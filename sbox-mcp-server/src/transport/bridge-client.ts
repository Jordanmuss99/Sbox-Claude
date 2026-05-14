import { EventEmitter } from "node:events";
import * as fs from "fs";
import * as path from "path";
import * as os from "os";

/**
 * Resolve the IPC dir by probing where the .NET bridge actually writes status.json.
 *
 * On Windows, `Path.GetTempPath()` follows TMP > TEMP > USERPROFILE\AppData\Local\Temp.
 * The Node side used to do the same via `os.tmpdir()`, but that prefers TEMP first,
 * and parent processes (opencode, Claude Code, VS Code) sometimes override only ONE
 * of TMP/TEMP or override BOTH to a sandboxed scratch dir. Either way, env-based
 * resolution alone can land on a temp dir that has no bridge.
 *
 * Strategy: build the candidate list in .NET's priority order, and probe each for
 * `sbox-bridge-ipc/status.json`. Return whichever has it. Falls back to TMP-first
 * order when none have status.json (so a freshly-started client still picks a sane
 * dir for the addon to write into).
 *
 * Honors an explicit `SBOX_BRIDGE_IPC_DIR` override for the rare case where the user
 * runs the addon out of a non-standard location.
 */
function getNetCompatibleTempPath(): string {
  if (process.env.SBOX_BRIDGE_IPC_DIR) {
    const override = process.env.SBOX_BRIDGE_IPC_DIR;
    return path.basename(override) === "sbox-bridge-ipc"
      ? path.dirname(override)
      : override;
  }

  if (process.platform !== "win32") return os.tmpdir();

  const candidates = [
    process.env.TMP,
    process.env.TEMP,
    path.join(os.homedir(), "AppData", "Local", "Temp"),
  ].filter((p): p is string => !!p);

  for (const dir of candidates) {
    const status = path.join(dir, "sbox-bridge-ipc", "status.json");
    if (fs.existsSync(status)) return dir;
  }

  return candidates[0] ?? os.tmpdir();
}

/**
 * File-based IPC transport for communicating with the s&box Bridge Addon.
 *
 * Instead of WebSocket, this uses a shared temp directory where:
 * - MCP server writes request files (req_*.json)
 * - s&box addon polls for them, processes, and writes response files (res_*.json)
 * - MCP server reads the response files via either an fs.watch push notification
 *   (Phase A.C.3.11 demux) OR a 50 ms polling fallback (for Windows tmpdirs where
 *   fs.watch can silently miss events).
 *
 * Concurrent calls share a single fs.watch + a `pending` map keyed by request id.
 * This eliminates the per-call setInterval thrash and lets `Promise.all([send, …])`
 * naturally pipeline through one C#-frame-per-request without dead locking on the
 * Node side. (BatchHandler was dropped in v1.4.0 — see CHANGELOG for the rationale.)
 */

/** A single command request sent to the s&box Bridge. */
export interface BridgeRequest {
  id: string;
  command: string;
  params: Record<string, unknown>;
}

/** Response from the s&box Bridge. Check `success` before reading `data`. */
export interface BridgeResponse {
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
  errorCode?: string;  // One of: BRIDGE_DISCONNECTED, BRIDGE_TIMEOUT, HANDLER_NOT_FOUND, HANDLER_ERROR, INVALID_PARAMS, PROTOCOL_MISMATCH
}

/**
 * Phase 0.1 — wire-protocol version asserted on connect.
 * Bumped only on breaking IPC changes (envelope shape, status.json key renames, etc).
 * Connecting to a bridge with mismatched version sets `protocolMismatch` on the client
 * and logs a warning. v1 = post-Phase-0 envelope (success/errorCode at top level).
 */
export const PROTOCOL_VERSION = 1;

/** Status payload written by the C# bridge to status.json. */
export interface BridgeStatus {
  running: boolean;
  protocol_version?: number;
  addonVersion?: string;
  editorPid?: number;
  startedAt?: string;
  handlerCount?: number;
}

/** Snapshot of the heartbeat / demux state for `get_bridge_status`. */
export interface HeartbeatState {
  heartbeatActive: boolean;
  watchMode: "fs.watch" | "poll-only";
  pingMissCount: number;
  lastPongAgeMs: number | null;
  pendingRequestCount: number;
  reconnecting: boolean;
}

const RES_FILE_RE = /^res_(.+)\.json$/;

/**
 * File-based IPC client that communicates with the s&box Bridge Addon.
 *
 * Emits the following events:
 *   - `disconnect`  — heartbeat has missed `maxMissBeforeDisconnect` pings in a row.
 *   - `reconnect`   — status.json reports a running+compatible bridge after a disconnect.
 *   - `mismatch`    — protocol_version reported by the bridge does not match `PROTOCOL_VERSION`.
 */
export class BridgeClient extends EventEmitter {
  static readonly POLL_INTERVAL_MS = 50; // 50ms polling for responses (fallback)
  static readonly STATUS_CHECK_INTERVAL_MS = 5000;
  static readonly HEARTBEAT_INTERVAL_MS = 5000;
  static readonly HEARTBEAT_TIMEOUT_MS = 3000;
  static readonly RECONNECT_INTERVAL_MS = 2000;
  // Phase A.C.3.12 — startup sweep age threshold. Older req_* files are
  // considered orphans (process crashed before reading the response).
  static readonly STARTUP_ORPHAN_AGE_MS = 600_000;

  private static _warnedNoProtoVer = false;

  private requestCounter = 0;
  private ipcDir: string;
  private connected = false;
  private lastPongTime = 0;
  private host: string;
  private port: number;
  private protocolMismatch = false;
  private lastStatus: BridgeStatus | null = null;

  // Phase A.C.3.11 — shared fs.watch + demux pending map.
  private pending = new Map<string, (r: BridgeResponse) => void>();
  private watcher: fs.FSWatcher | null = null;
  private watcherFailed = false;
  private watcherStarted = false;

  // Phase A.C.3.10 — heartbeat state machine.
  // We use setTimeout (not setInterval) + self-rescheduling so we never have
  // two heartbeat pings in flight at once. setInterval would queue up calls
  // when individual pings hit their timeout, causing pingMissCount to lag.
  private heartbeatTimer: ReturnType<typeof setTimeout> | null = null;
  private heartbeatInterval = BridgeClient.HEARTBEAT_INTERVAL_MS;
  private heartbeatTimeout = BridgeClient.HEARTBEAT_TIMEOUT_MS;
  private reconnectTimer: ReturnType<typeof setInterval> | null = null;
  private pingMissCount = 0;
  private maxMissBeforeDisconnect = 3;
  private heartbeatActive = false;
  private reconnecting = false;

  constructor(host = "127.0.0.1", port = 29015) {
    super();
    this.host = host;
    this.port = port;
    this.ipcDir = path.join(getNetCompatibleTempPath(), "sbox-bridge-ipc");
  }

  /**
   * Check if the s&box Bridge is running by looking for the status file.
   * Performs a scoped orphan sweep on first successful connect.
   */
  async connect(): Promise<void> {
    // Ensure IPC directory exists
    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    const statusPath = path.join(this.ipcDir, "status.json");
    if (fs.existsSync(statusPath)) {
      try {
        const status = JSON.parse(fs.readFileSync(statusPath, "utf8")) as BridgeStatus;
        if (status.running) {
          this.lastStatus = status;
          this.assertProtocolVersion(status);
          if (!this.connected) {
            // First successful connect for this client instance — sweep orphans
            // and start the demux watcher. Both are idempotent.
            this.sweepStartupOrphans();
            this.startWatcher();
          }
          this.connected = true;
          this.lastPongTime = Date.now();
          return;
        }
      } catch {
        // Status file exists but is malformed
      }
    }

    throw new Error(
      `Cannot connect to s&box Bridge. No status file found at ${statusPath}. Is s&box running with the Bridge Addon?`
    );
  }

  /**
   * Phase 0.1 — assert the bridge's wire-protocol version matches ours.
   * Sets `protocolMismatch` flag and logs a warning when it doesn't.
   * Emits `mismatch` so callers / index.ts can react.
   */
  private assertProtocolVersion(status: BridgeStatus): void {
    const remote = status.protocol_version;
    if (remote === undefined) {
      if (!BridgeClient._warnedNoProtoVer) {
        BridgeClient._warnedNoProtoVer = true;
        console.error(
          `[sbox-mcp] Bridge addon did not report protocol_version. Expected v${PROTOCOL_VERSION}. Continuing in compat mode — upgrade the addon to silence this warning.`
        );
      }
      this.protocolMismatch = false;
      return;
    }
    if (remote !== PROTOCOL_VERSION) {
      this.protocolMismatch = true;
      console.error(
        `[sbox-mcp] Protocol mismatch: bridge reports v${remote}, MCP server expects v${PROTOCOL_VERSION}. Tool calls may fail. Update the bridge addon and/or the MCP server to matching versions.`
      );
      this.emit("mismatch", { remote, expected: PROTOCOL_VERSION });
      return;
    }
    this.protocolMismatch = false;
  }

  /** Whether the last connect saw a wire-protocol mismatch. Reported by get_bridge_status. */
  hasProtocolMismatch(): boolean {
    return this.protocolMismatch;
  }

  /** Last successfully-parsed status.json payload. May be null if never connected. */
  getLastStatus(): BridgeStatus | null {
    return this.lastStatus;
  }

  // ─── Phase A.C.3.11 — shared fs.watch + demux ──────────────────────────
  /**
   * Start the shared fs.watch on the IPC dir. Watcher events are filtered to
   * `res_*.json` only — status.json + events.json (Phase 2 watcher) are passed
   * over. On Windows, `filename` is sometimes null; we fall back to a readdir
   * scan in that case (was C.3.15 in the original AC).
   *
   * If fs.watch throws (e.g. some Windows tmpdirs), we mark `watcherFailed`
   * and rely entirely on the 50ms poll fallback inside `send()`.
   */
  private startWatcher(): void {
    if (this.watcherStarted) return;
    this.watcherStarted = true;
    try {
      this.watcher = fs.watch(this.ipcDir, (_eventType, filename) => {
        if (filename === null) {
          // Windows: filename omitted. Scan for any res_* file we own.
          this.drainResponses();
          return;
        }
        // Race B fix: only act on res_*.json. Ignore status.json, events.json,
        // req_*.json, and anything else (lets Phase 2's events.json watcher own
        // that file without cross-talk).
        const m = filename.match(RES_FILE_RE);
        if (!m) return;
        const resPath = path.join(this.ipcDir, filename);
        this.tryResolveResponseFile(resPath, m[1]);
      });
      this.watcher.unref();
    } catch {
      this.watcher = null;
      this.watcherFailed = true;
    }
  }

  /**
   * Scan the IPC dir for any res_* files matching pending request ids and
   * resolve them. Used as the Windows-null-filename fallback path AND as a
   * one-shot drain after `startWatcher()` to catch responses that arrived
   * BETWEEN the `send()`-side registration and the watcher attaching.
   */
  private drainResponses(): void {
    let entries: string[];
    try {
      entries = fs.readdirSync(this.ipcDir);
    } catch {
      return;
    }
    for (const filename of entries) {
      const m = filename.match(RES_FILE_RE);
      if (!m) continue;
      const id = m[1];
      if (!this.pending.has(id)) continue;
      const resPath = path.join(this.ipcDir, filename);
      this.tryResolveResponseFile(resPath, id);
    }
  }

  /**
   * Read+parse a res_*.json file, validate its envelope, and resolve the
   * pending request. Idempotent: if no pending entry exists for the id (e.g.
   * the poll-fallback already drained it), this is a no-op.
   *
   * Race C fix: assert `parsed.id === capturedId`. Drops responses whose
   * envelope id doesn't match the filename id (would indicate disk corruption
   * or a misbehaving addon).
   */
  private tryResolveResponseFile(resPath: string, capturedId: string): void {
    if (!this.pending.has(capturedId)) return;
    let response: BridgeResponse;
    try {
      const responseJson = fs.readFileSync(resPath, "utf8").replace(/^\uFEFF/, "");
      const parsed = JSON.parse(responseJson) as BridgeResponse;
      if (parsed.id !== capturedId) {
        // Filename id and envelope id disagree — drop and let the polling
        // fallback retry on the next tick (file may have been overwritten
        // during a partial write).
        return;
      }
      response = parsed;
    } catch {
      // Partial write or transient parse error — retry on next event.
      return;
    }
    const resolver = this.pending.get(capturedId);
    if (!resolver) return;
    this.pending.delete(capturedId);
    try { fs.unlinkSync(resPath); } catch { /* already gone */ }
    this.lastPongTime = Date.now();
    resolver(response);
  }

  /** How many requests are currently in-flight (waiting for a response). */
  getPendingRequestCount(): number {
    return this.pending.size;
  }

  /** Whether the shared fs.watch is active or we're on poll-only fallback. */
  getWatchMode(): "fs.watch" | "poll-only" {
    return this.watcher !== null ? "fs.watch" : "poll-only";
  }

  // ─── Phase A.C.3.12 — scoped startup orphan sweep ──────────────────────
  /**
   * Sweep stale req_*.json + res_*.json files older than STARTUP_ORPHAN_AGE_MS.
   * Runs ONCE on the first successful connect per client instance.
   *
   * Scope is intentionally narrow:
   *   - Only touches files matching `^(req|res)_.+\.json$`.
   *   - Never touches status.json, events.json, events_compact.tmp, or anything
   *     that doesn't match the pattern.
   *   - Only deletes files OLDER than the age threshold (default 10 min) so a
   *     concurrent second MCP-server instance keeps its in-flight requests.
   *   - If a req_* has a matching res_*, both are removed together (the client
   *     that issued the request is presumed dead).
   */
  private sweepStartupOrphans(): void {
    let entries: string[];
    try {
      entries = fs.readdirSync(this.ipcDir);
    } catch {
      return;
    }
    const now = Date.now();
    const oldReqs: string[] = [];
    const oldRess: Set<string> = new Set();
    for (const f of entries) {
      const reqMatch = f.match(/^req_(.+)\.json$/);
      const resMatch = f.match(RES_FILE_RE);
      if (!reqMatch && !resMatch) continue; // never touch non-req/res files
      const full = path.join(this.ipcDir, f);
      let stat: fs.Stats;
      try {
        stat = fs.statSync(full);
      } catch {
        continue;
      }
      if (now - stat.mtimeMs < BridgeClient.STARTUP_ORPHAN_AGE_MS) continue;
      if (reqMatch) oldReqs.push(reqMatch[1]);
      else if (resMatch) oldRess.add(resMatch[1]);
    }
    let removed = 0;
    for (const id of oldReqs) {
      const reqPath = path.join(this.ipcDir, `req_${id}.json`);
      const resPath = path.join(this.ipcDir, `res_${id}.json`);
      try { fs.unlinkSync(reqPath); removed++; } catch { /* ignore */ }
      if (oldRess.has(id)) {
        try { fs.unlinkSync(resPath); removed++; } catch { /* ignore */ }
        oldRess.delete(id);
      }
    }
    for (const id of oldRess) {
      // Orphan res_* with no matching req_* (req cleaned up but res survived).
      const resPath = path.join(this.ipcDir, `res_${id}.json`);
      try { fs.unlinkSync(resPath); removed++; } catch { /* ignore */ }
    }
    if (removed > 0) {
      console.error(`[sbox-mcp] Swept ${removed} orphan IPC file(s) older than ${BridgeClient.STARTUP_ORPHAN_AGE_MS} ms`);
    }
  }

  // ─── Phase A.C.3.10 — 3-strike heartbeat + reconnect loop ──────────────
  /**
   * Start the heartbeat loop. Pings the bridge every `intervalMs` (with a
   * `timeoutMs` per ping) and tracks consecutive misses. After 3 misses the
   * client transitions to disconnected state and emits `disconnect`. A
   * reconnect loop then polls status.json every RECONNECT_INTERVAL_MS for a
   * running+compatible bridge.
   *
   * Implementation: self-rescheduling setTimeout (NOT setInterval) so we
   * never have two heartbeat pings in flight at once — critical when the
   * bridge is unresponsive and pings hit their timeout. setInterval would
   * queue up calls and pingMissCount would lag wall-clock by `timeoutMs`.
   */
  startHeartbeat(
    intervalMs = BridgeClient.HEARTBEAT_INTERVAL_MS,
    timeoutMs = BridgeClient.HEARTBEAT_TIMEOUT_MS
  ): void {
    if (this.heartbeatActive) return;
    this.heartbeatActive = true;
    this.heartbeatInterval = intervalMs;
    this.heartbeatTimeout = timeoutMs;
    this.scheduleNextHeartbeat(intervalMs);
  }

  private scheduleNextHeartbeat(delayMs: number): void {
    if (!this.heartbeatActive) return;
    this.heartbeatTimer = setTimeout(() => this.runHeartbeatTick(), delayMs);
    this.heartbeatTimer.unref?.();
  }

  private async runHeartbeatTick(): Promise<void> {
    if (!this.heartbeatActive) return;
    try {
      const ms = await this.measureLatency(this.heartbeatTimeout);
      if (ms >= 0) {
        if (this.pingMissCount > 0 || this.reconnecting) {
          this.pingMissCount = 0;
          if (this.reconnecting) {
            this.reconnecting = false;
            this.stopReconnectLoop();
            this.emit("reconnect", { latencyMs: ms });
          }
        }
      } else {
        this.pingMissCount++;
        if (this.pingMissCount >= this.maxMissBeforeDisconnect && this.connected) {
          this.connected = false;
          this.emit("disconnect", { missCount: this.pingMissCount });
          this.startReconnectLoop();
        }
      }
    } catch {
      this.pingMissCount++;
    }
    this.scheduleNextHeartbeat(this.heartbeatInterval);
  }

  /** Stop the heartbeat loop (idempotent). Call on SIGINT/SIGTERM. */
  stopHeartbeat(): void {
    this.heartbeatActive = false;
    if (this.heartbeatTimer) {
      clearTimeout(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
    this.stopReconnectLoop();
  }

  private startReconnectLoop(): void {
    if (this.reconnectTimer) return;
    this.reconnecting = true;
    this.reconnectTimer = setInterval(() => {
      // Re-read status.json. If the bridge is back and protocol matches, the
      // next heartbeat tick will see latency >= 0 and emit "reconnect".
      const statusPath = path.join(this.ipcDir, "status.json");
      try {
        if (fs.existsSync(statusPath)) {
          const status = JSON.parse(fs.readFileSync(statusPath, "utf8")) as BridgeStatus;
          if (status.running) {
            this.lastStatus = status;
            this.assertProtocolVersion(status);
            this.connected = true;
            // Don't emit "reconnect" here — wait for a real round-trip from
            // the heartbeat loop to confirm the bridge is actually responsive.
          }
        }
      } catch { /* malformed file, try again */ }
    }, BridgeClient.RECONNECT_INTERVAL_MS);
    this.reconnectTimer.unref?.();
  }

  private stopReconnectLoop(): void {
    if (this.reconnectTimer) {
      clearInterval(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.reconnecting = false;
  }

  /** Snapshot of the heartbeat + demux state for diagnostics tools. */
  getHeartbeatState(): HeartbeatState {
    return {
      heartbeatActive: this.heartbeatActive,
      watchMode: this.getWatchMode(),
      pingMissCount: this.pingMissCount,
      lastPongAgeMs: this.lastPongTime > 0 ? Date.now() - this.lastPongTime : null,
      pendingRequestCount: this.pending.size,
      reconnecting: this.reconnecting,
    };
  }

  // ─── Core send path ────────────────────────────────────────────────────
  /**
   * Send a command to the s&box Bridge and wait for its response.
   *
   * Concurrency model: Promise.all([send, send, …]) naturally pipelines once
   * the C# bridge drains the request queue (one per frame). The shared watcher
   * + per-id pending map demuxes responses, so there's no per-call setInterval
   * thrash. The 50 ms poll fallback inside this method covers fs.watch misses
   * on flaky Windows tmpdirs.
   */
  async send(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    if (!this.connected) {
      try {
        await this.connect();
      } catch {
        return {
          id: "",
          success: false,
          error:
            "Not connected to s&box Bridge. Make sure s&box is running with the Bridge Addon installed.",
          errorCode: "BRIDGE_DISCONNECTED",
        };
      }
    }

    const id = `${++this.requestCounter}_${Date.now()}`;
    const request: BridgeRequest = { id, command, params };

    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    const reqPath = path.join(this.ipcDir, `req_${id}.json`);
    const resPath = path.join(this.ipcDir, `res_${id}.json`);

    return new Promise<BridgeResponse>((resolve) => {
      let settled = false;
      const settle = (r: BridgeResponse): void => {
        if (settled) return;
        settled = true;
        this.pending.delete(id);
        if (poll) clearInterval(poll);
        if (timeoutHandle) clearTimeout(timeoutHandle);
        resolve(r);
      };

      // Race A fix: register the pending resolver BEFORE writing the request
      // file. If we wrote first, the bridge could process+respond before
      // pending.set() ran, and the watcher would fire with no resolver.
      this.pending.set(id, settle);

      // Ensure the shared watcher is alive. Idempotent; safe to call repeatedly.
      if (!this.watcherStarted && !this.watcherFailed) this.startWatcher();

      try {
        fs.writeFileSync(reqPath, JSON.stringify(request), "utf8");
      } catch (err) {
        settle({
          id,
          success: false,
          error: `Failed to write request file: ${err}`,
          errorCode: "BRIDGE_DISCONNECTED",
        });
        return;
      }

      // 50 ms polling fallback. The watcher should resolve first when it works;
      // this catches the cases where fs.watch silently misses an event (some
      // Windows tmpdirs, some network-mounted dirs, etc).
      const poll: ReturnType<typeof setInterval> | null = setInterval(() => {
        if (fs.existsSync(resPath)) {
          this.tryResolveResponseFile(resPath, id);
          // tryResolveResponseFile already called settle() via the pending map.
        }
      }, BridgeClient.POLL_INTERVAL_MS);
      poll.unref?.();

      // Timeout
      const timeoutHandle = setTimeout(() => {
        if (settled) return;
        // Best-effort cleanup of the request file. The response (if any later
        // arrives) will be picked up by the startup orphan sweep on next connect.
        try { if (fs.existsSync(reqPath)) fs.unlinkSync(reqPath); } catch { /* ignore */ }
        settle({
          id,
          success: false,
          error: `Request timed out after ${timeoutMs}ms`,
          errorCode: "BRIDGE_TIMEOUT",
        });
      }, timeoutMs);
      timeoutHandle.unref?.();
    });
  }

  /**
   * Measure real IPC round-trip latency by sending a "ping" command to the C# bridge.
   * This is an actual file-IPC round-trip, not a local filesystem stat.
   * Returns latency in ms, or -1 if unreachable.
   */
  async measureLatency(timeoutMs = 5000): Promise<number> {
    const start = Date.now();
    try {
      const res = await this.send("ping", {}, timeoutMs);
      if (res.success) {
        this.lastPongTime = Date.now();
        return Date.now() - start;
      }
    } catch {}
    return -1;
  }

  /**
   * @deprecated Use measureLatency() for real IPC round-trip measurement.
   * This method only reads status.json locally and does not contact the bridge.
   */
  async ping(): Promise<number> {
    const statusPath = path.join(this.ipcDir, "status.json");
    const start = Date.now();
    try {
      if (fs.existsSync(statusPath)) {
        const status = JSON.parse(fs.readFileSync(statusPath, "utf8"));
        if (status.running) {
          this.lastPongTime = Date.now();
          return Date.now() - start;
        }
      }
    } catch {}
    return -1;
  }

  isConnected(): boolean {
    const statusPath = path.join(this.ipcDir, "status.json");
    try {
      if (fs.existsSync(statusPath)) {
        const status = JSON.parse(fs.readFileSync(statusPath, "utf8")) as BridgeStatus;
        this.lastStatus = status;
        if (status.running) this.assertProtocolVersion(status);
        this.connected = !!status.running;
      } else {
        this.connected = false;
      }
    } catch {
      this.connected = false;
    }
    return this.connected;
  }

  getHost(): string {
    return this.host;
  }

  getPort(): number {
    return this.port;
  }

  getLastPongTime(): number {
    return this.lastPongTime;
  }

  getIpcDir(): string {
    return this.ipcDir;
  }

  /**
   * Tear down all timers + watchers. Call on SIGINT/SIGTERM.
   * Safe to call multiple times.
   */
  disconnect(): void {
    this.connected = false;
    this.stopHeartbeat();
    if (this.watcher) {
      try { this.watcher.close(); } catch { /* ignore */ }
      this.watcher = null;
    }
    this.watcherStarted = false;
    // Resolve any in-flight requests with a disconnect error so awaiters don't hang.
    for (const [id, resolver] of this.pending.entries()) {
      try {
        resolver({
          id,
          success: false,
          error: "Bridge disconnected during shutdown",
          errorCode: "BRIDGE_DISCONNECTED",
        });
      } catch { /* listener threw — keep going */ }
    }
    this.pending.clear();
  }
}
