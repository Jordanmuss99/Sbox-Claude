import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as fs from "node:fs";
import * as path from "node:path";
import { BridgeClient, PROTOCOL_VERSION } from "../src/transport/bridge-client.js";
import { makeMockIpcDir, type MockIpcDir } from "./helpers/mock-ipc-dir.js";
import { startMockBridge, type MockBridgeHandle } from "./helpers/mock-bridge.js";

/**
 * Phase A.C.3.10/11/12/13 — transport-hardening integration tests.
 *
 * Uses the mock-ipc-dir + mock-bridge fixtures. Each test spins up an isolated
 * tmpdir, points the BridgeClient at it via SBOX_BRIDGE_IPC_DIR, and tears
 * down the fixture + any timers in afterEach.
 */
describe("Phase A — transport hardening", () => {
  let fixture: MockIpcDir;
  let mockBridge: MockBridgeHandle | null = null;
  let restoreEnv: string | undefined;
  let client: BridgeClient | null = null;
  let warnSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    fixture = makeMockIpcDir();
    restoreEnv = process.env.SBOX_BRIDGE_IPC_DIR;
    process.env.SBOX_BRIDGE_IPC_DIR = fixture.ipcDir;
    warnSpy = vi.spyOn(console, "error").mockImplementation(() => {});
  });

  afterEach(() => {
    if (mockBridge) { mockBridge.stop(); mockBridge = null; }
    if (client) { client.disconnect(); client = null; }
    warnSpy.mockRestore();
    if (restoreEnv === undefined) delete process.env.SBOX_BRIDGE_IPC_DIR;
    else process.env.SBOX_BRIDGE_IPC_DIR = restoreEnv;
    fixture.cleanup();
  });

  // ─── A.C.3.11 — shared fs.watch + demux ────────────────────────────────
  describe("demux (A.C.3.11)", () => {
    it("send() resolves when mock bridge writes res_<id>.json", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      mockBridge = startMockBridge(fixture);
      client = new BridgeClient();
      await client.connect();
      const res = await client.send("echo", { ping: 1 });
      expect(res.success).toBe(true);
      expect(res.data).toMatchObject({ echoed: true, command: "echo", params: { ping: 1 } });
    });

    it("concurrent sends are demuxed by id (no cross-talk)", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      mockBridge = startMockBridge(fixture);
      client = new BridgeClient();
      await client.connect();
      const [a, b, c] = await Promise.all([
        client.send("cmd_a", { which: "a" }),
        client.send("cmd_b", { which: "b" }),
        client.send("cmd_c", { which: "c" }),
      ]);
      expect(a.success && b.success && c.success).toBe(true);
      expect((a.data as { params: { which: string } }).params.which).toBe("a");
      expect((b.data as { params: { which: string } }).params.which).toBe("b");
      expect((c.data as { params: { which: string } }).params.which).toBe("c");
    });

    it("send() times out cleanly when bridge never responds", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      // No mock bridge → no res files written.
      client = new BridgeClient();
      await client.connect();
      const res = await client.send("never_answers", {}, 150);
      expect(res.success).toBe(false);
      expect(res.errorCode).toBe("BRIDGE_TIMEOUT");
      expect(res.error).toMatch(/timed out/i);
    });

    it("getPendingRequestCount() decrements after response", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      mockBridge = startMockBridge(fixture, { responseDelayMs: 30 });
      client = new BridgeClient();
      await client.connect();
      const promise = client.send("delayed");
      // Brief wait so the request file has been written and registered.
      await new Promise((r) => setTimeout(r, 10));
      const inFlight = client.getHeartbeatState().pendingRequestCount;
      expect(inFlight).toBeGreaterThanOrEqual(1);
      await promise;
      expect(client.getHeartbeatState().pendingRequestCount).toBe(0);
    });

    it("Race B — ignores non-res_*.json files in the IPC dir", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      mockBridge = startMockBridge(fixture);
      client = new BridgeClient();
      await client.connect();
      // Manually drop status.json + events.json files while a request is in-flight.
      // The watcher must NOT treat them as responses.
      fs.writeFileSync(path.join(fixture.ipcDir, "events.json"), '{"eventId":1}\n');
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION, handlerCount: 999 });
      const res = await client.send("echo");
      expect(res.success).toBe(true);
    });

    it("Race C — rejects res_*.json whose envelope id mismatches the filename", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      // Plant a malformed response with mismatched id BEFORE the client sends.
      // (Race C path: bad file present at the moment send() registers pending.)
      // Real id ordering: client.send() will issue id like `1_<ts>`. We can't
      // predict it, so install a confounder with id="bogus" and verify a
      // properly-named res_<id>.json wins.
      mockBridge = startMockBridge(fixture);
      client = new BridgeClient();
      await client.connect();
      const stray = path.join(fixture.ipcDir, "res_bogus.json");
      fs.writeFileSync(stray, JSON.stringify({ id: "different", success: false, error: "stray" }));
      const res = await client.send("echo");
      expect(res.success).toBe(true);
      // The stray file should still be sitting there (not our id, so client ignored it).
      expect(fs.existsSync(stray)).toBe(true);
      // Manual cleanup so fixture.cleanup() doesn't fight us.
      try { fs.unlinkSync(stray); } catch { /* ignore */ }
    });
  });

  // ─── A.C.3.12 — scoped orphan sweep ────────────────────────────────────
  describe("orphan sweep (A.C.3.12)", () => {
    it("removes req_*.json older than STARTUP_ORPHAN_AGE_MS on first connect", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      fixture.writeRequest("stale", BridgeClient.STARTUP_ORPHAN_AGE_MS + 1000);
      fixture.writeRequest("recent", 0);
      client = new BridgeClient();
      await client.connect();
      const after = fixture.list();
      expect(after).not.toContain("req_stale.json");
      expect(after).toContain("req_recent.json");
    });

    it("never deletes status.json, events.json, or anything not matching req/res", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      fs.writeFileSync(path.join(fixture.ipcDir, "events.json"), '{"eventId":1}\n');
      fs.writeFileSync(path.join(fixture.ipcDir, "events_compact.tmp"), "x");
      fs.writeFileSync(path.join(fixture.ipcDir, "unrelated.txt"), "y");
      fixture.writeRequest("ancient", BridgeClient.STARTUP_ORPHAN_AGE_MS + 1000);
      client = new BridgeClient();
      await client.connect();
      const after = fixture.list();
      expect(after).toContain("status.json");
      expect(after).toContain("events.json");
      expect(after).toContain("events_compact.tmp");
      expect(after).toContain("unrelated.txt");
      expect(after).not.toContain("req_ancient.json");
    });

    it("removes paired old req+res together when both are stale", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      fixture.writeRequest("paired", BridgeClient.STARTUP_ORPHAN_AGE_MS + 1000);
      // Plant a stale response too.
      const resPath = path.join(fixture.ipcDir, "res_paired.json");
      fs.writeFileSync(resPath, JSON.stringify({ id: "paired", success: true, data: {} }));
      const past = Date.now() - (BridgeClient.STARTUP_ORPHAN_AGE_MS + 1000);
      fs.utimesSync(resPath, past / 1000, past / 1000);
      client = new BridgeClient();
      await client.connect();
      const after = fixture.list();
      expect(after).not.toContain("req_paired.json");
      expect(after).not.toContain("res_paired.json");
    });
  });

  // ─── A.C.3.10 — heartbeat ──────────────────────────────────────────────
  describe("heartbeat (A.C.3.10)", () => {
    it("emits disconnect after maxMissBeforeDisconnect consecutive ping failures", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      // No mock bridge → every ping times out.
      client = new BridgeClient();
      await client.connect();
      const disconnects: Array<{ missCount: number }> = [];
      client.on("disconnect", (info) => disconnects.push(info));
      // 50ms interval, 100ms timeout → ~3 misses in ~450ms total.
      client.startHeartbeat(50, 100);
      await new Promise((r) => setTimeout(r, 1500));
      expect(disconnects.length).toBeGreaterThanOrEqual(1);
      expect(disconnects[0].missCount).toBeGreaterThanOrEqual(3);
      expect(client.getHeartbeatState().reconnecting).toBe(true);
      client.stopHeartbeat();
    }, 5000);

    it("emits reconnect after bridge becomes responsive again", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      client = new BridgeClient();
      await client.connect();
      const events: string[] = [];
      client.on("disconnect", () => events.push("disconnect"));
      client.on("reconnect", () => events.push("reconnect"));
      client.startHeartbeat(50, 100);
      await new Promise((r) => setTimeout(r, 1500));
      expect(events).toContain("disconnect");
      // Now bring the bridge back up. Next heartbeat tick should succeed.
      mockBridge = startMockBridge(fixture);
      await new Promise((r) => setTimeout(r, 800));
      client.stopHeartbeat();
      expect(events).toContain("reconnect");
      expect(client.getHeartbeatState().pingMissCount).toBe(0);
    }, 8000);

    it("getHeartbeatState() reports complete shape", async () => {
      fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
      client = new BridgeClient();
      await client.connect();
      const state = client.getHeartbeatState();
      expect(state).toHaveProperty("heartbeatActive");
      expect(state).toHaveProperty("watchMode");
      expect(state).toHaveProperty("pingMissCount");
      expect(state).toHaveProperty("lastPongAgeMs");
      expect(state).toHaveProperty("pendingRequestCount");
      expect(state).toHaveProperty("reconnecting");
      expect(state.heartbeatActive).toBe(false); // not started yet
      expect(state.pendingRequestCount).toBe(0);
      expect(["fs.watch", "poll-only"]).toContain(state.watchMode);
    });
  });

  // ─── A.C.3.14 — sendBatch dropped ──────────────────────────────────────
  it("A.C.3.14 — sendBatch is no longer exported on BridgeClient", () => {
    fixture.writeStatus({ running: true, protocol_version: PROTOCOL_VERSION });
    client = new BridgeClient();
    expect((client as unknown as Record<string, unknown>).sendBatch).toBeUndefined();
  });

  // ─── A.C.3.13 — status fields ──────────────────────────────────────────
  it("A.C.3.13 — lastStatus exposes protocol_version + addonVersion + editorPid", async () => {
    fixture.writeStatus({
      running: true,
      protocol_version: PROTOCOL_VERSION,
      addonVersion: "1.4.0",
      editorPid: 4242,
      handlerCount: 121,
    });
    client = new BridgeClient();
    await client.connect();
    const status = client.getLastStatus();
    expect(status?.protocol_version).toBe(PROTOCOL_VERSION);
    expect(status?.addonVersion).toBe("1.4.0");
    expect(status?.editorPid).toBe(4242);
    expect(status?.handlerCount).toBe(121);
  });
});
