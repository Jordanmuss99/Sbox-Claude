import * as fs from "node:fs";
import * as path from "node:path";
import * as os from "node:os";

/**
 * Phase 0.3 — disposable IPC-dir fixture for unit tests.
 *
 * Each call returns a fresh tmpdir + helper functions for writing fake
 * status.json / request / response files. `cleanup()` removes the dir.
 *
 * Used by transport tests (heartbeat, demux, orphan sweep) to exercise the
 * BridgeClient against a deterministic on-disk state instead of a live editor.
 *
 * Live integration tests (those that actually spawn s&box) should opt-in via
 * `SBOX_BRIDGE_LIVE=1` and skip otherwise:
 *
 *   const isLive = process.env.SBOX_BRIDGE_LIVE === "1";
 *   (isLive ? describe : describe.skip)("live IPC", () => { ... });
 */
export interface MockIpcDir {
  /** Absolute path to the fixture root. Pass to `SBOX_BRIDGE_IPC_DIR`. */
  root: string;
  /** Path to the sbox-bridge-ipc subdir (what BridgeClient actually reads). */
  ipcDir: string;
  /** Write a status.json with arbitrary fields. */
  writeStatus(status: Record<string, unknown>): void;
  /** Delete status.json (simulates bridge offline). */
  removeStatus(): void;
  /** Write res_<id>.json with the given envelope. */
  writeResponse(id: string, envelope: Record<string, unknown>): void;
  /** Drop a stray req_<id>.json (e.g. orphan sweep tests). */
  writeRequest(id: string, age_ms?: number): void;
  /** List the files currently in ipcDir (filenames only). */
  list(): string[];
  /** Remove the entire fixture tree. */
  cleanup(): void;
}

export function makeMockIpcDir(): MockIpcDir {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sbox-mcp-test-"));
  const ipcDir = path.join(root, "sbox-bridge-ipc");
  fs.mkdirSync(ipcDir, { recursive: true });

  const utf8NoBom = (obj: unknown): string => JSON.stringify(obj);

  return {
    root,
    ipcDir,
    writeStatus(status) {
      fs.writeFileSync(path.join(ipcDir, "status.json"), utf8NoBom(status));
    },
    removeStatus() {
      const p = path.join(ipcDir, "status.json");
      if (fs.existsSync(p)) fs.unlinkSync(p);
    },
    writeResponse(id, envelope) {
      fs.writeFileSync(path.join(ipcDir, `res_${id}.json`), utf8NoBom(envelope));
    },
    writeRequest(id, age_ms = 0) {
      const reqPath = path.join(ipcDir, `req_${id}.json`);
      fs.writeFileSync(reqPath, utf8NoBom({ id, command: "noop", params: {} }));
      if (age_ms > 0) {
        const past = Date.now() - age_ms;
        fs.utimesSync(reqPath, past / 1000, past / 1000);
      }
    },
    list() {
      return fs.readdirSync(ipcDir);
    },
    cleanup() {
      try {
        fs.rmSync(root, { recursive: true, force: true });
      } catch {
        // Race with watcher closing; ignore.
      }
    },
  };
}
