import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BridgeClient, PROTOCOL_VERSION } from "../src/transport/bridge-client.js";
import { makeMockIpcDir, type MockIpcDir } from "./helpers/mock-ipc-dir.js";

/**
 * Phase 0.1 contract — protocol_version handshake.
 *
 * The C# bridge writes `protocol_version: N` to status.json. On connect the
 * TS client compares it to its own PROTOCOL_VERSION constant and:
 *   - matching:    silently accept
 *   - mismatched:  flag `hasProtocolMismatch()` true + log warning
 *   - missing:     soft-warn once (older bridge in compat mode)
 *
 * We do NOT hard-throw on mismatch: tool calls can still attempt to round-trip,
 * and `get_bridge_status` surfaces the flag for the caller to act on.
 */
describe("Phase 0.1 — protocol_version handshake", () => {
  let fixture: MockIpcDir;
  let restoreEnv: string | undefined;
  let warnSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    fixture = makeMockIpcDir();
    restoreEnv = process.env.SBOX_BRIDGE_IPC_DIR;
    process.env.SBOX_BRIDGE_IPC_DIR = fixture.ipcDir;
    warnSpy = vi.spyOn(console, "error").mockImplementation(() => {});
  });

  afterEach(() => {
    if (restoreEnv === undefined) delete process.env.SBOX_BRIDGE_IPC_DIR;
    else process.env.SBOX_BRIDGE_IPC_DIR = restoreEnv;
    warnSpy.mockRestore();
    fixture.cleanup();
  });

  it("matching protocol_version connects without mismatch flag", async () => {
    fixture.writeStatus({
      running: true,
      protocol_version: PROTOCOL_VERSION,
      addonVersion: "1.4.0",
    });
    const client = new BridgeClient();
    await client.connect();
    expect(client.isConnected()).toBe(true);
    expect(client.hasProtocolMismatch()).toBe(false);
  });

  it("mismatched protocol_version flags the client and logs warning", async () => {
    fixture.writeStatus({
      running: true,
      protocol_version: 999,
      addonVersion: "9.9.9",
    });
    const client = new BridgeClient();
    await client.connect();
    expect(client.isConnected()).toBe(true);
    expect(client.hasProtocolMismatch()).toBe(true);
    const warnings = warnSpy.mock.calls.flat().join("\n");
    expect(warnings).toMatch(/Protocol mismatch/i);
    expect(warnings).toContain("v999");
  });

  it("missing protocol_version (legacy bridge) connects with soft-warn", async () => {
    fixture.writeStatus({ running: true });
    const client = new BridgeClient();
    await client.connect();
    expect(client.isConnected()).toBe(true);
    expect(client.hasProtocolMismatch()).toBe(false);
  });

  it("getLastStatus() returns the parsed status payload", async () => {
    const payload = {
      running: true,
      protocol_version: PROTOCOL_VERSION,
      addonVersion: "1.4.0",
      editorPid: 12345,
      handlerCount: 121,
    };
    fixture.writeStatus(payload);
    const client = new BridgeClient();
    await client.connect();
    const status = client.getLastStatus();
    expect(status).not.toBeNull();
    expect(status?.protocol_version).toBe(PROTOCOL_VERSION);
    expect(status?.editorPid).toBe(12345);
    expect(status?.handlerCount).toBe(121);
  });

  it("PROTOCOL_VERSION is a positive integer", () => {
    expect(Number.isInteger(PROTOCOL_VERSION)).toBe(true);
    expect(PROTOCOL_VERSION).toBeGreaterThan(0);
  });
});
