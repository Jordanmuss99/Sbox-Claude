import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Diagnostic and health-check tool (get_bridge_status).
 * Reports connection state, latency, host/port, and editor version.
 */
export function registerStatusTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_bridge_status ────────────────────────────────────────────
  server.tool(
    "get_bridge_status",
    "Check the connection status to the s&box Bridge — whether it's connected, latency, host/port, and editor info. Useful for debugging",
    {},
    async () => {
      // Cold-start probe: attempt a connection so a fresh `get_bridge_status` call
      // accurately reflects reachability. `connect()` is idempotent and silently
      // no-ops when already connected; on failure the catch leaves `connected=false`.
      if (!bridge.isConnected()) {
        try { await bridge.connect(); } catch { /* leave disconnected */ }
      }
      const connected = bridge.isConnected();
      let latencyMs = -1;
      let editorVersion: string | null = null;

      if (connected) {
        // Measure round-trip ping
        latencyMs = await bridge.measureLatency();

        // Try to get editor version via project info
        try {
          const res = await bridge.send("get_project_info", {}, 5000);
          if (res.success && res.data) {
            const data = res.data as Record<string, unknown>;
            editorVersion = (data.editorVersion as string) ?? null;
          }
        } catch {
          // Non-fatal
        }
      }

      // Phase A.C.3.13 — surface heartbeat / demux / protocol state.
      const hb = bridge.getHeartbeatState();
      const lastStatus = bridge.getLastStatus();
      const status = {
        connected,
        host: bridge.getHost(),
        port: bridge.getPort(),
        latencyMs: connected ? latencyMs : null,
        lastPong: connected
          ? new Date(bridge.getLastPongTime()).toISOString()
          : null,
        editorVersion,
        protocol_version: lastStatus?.protocol_version ?? null,
        addonVersion: lastStatus?.addonVersion ?? null,
        editorPid: lastStatus?.editorPid ?? null,
        protocolMismatch: bridge.hasProtocolMismatch(),
        watchMode: hb.watchMode,
        pingMissCount: hb.pingMissCount,
        lastPongAgeMs: hb.lastPongAgeMs,
        pendingRequestCount: hb.pendingRequestCount,
        heartbeatActive: hb.heartbeatActive,
        reconnecting: hb.reconnecting,
      };

      const text = connected
        ? `Bridge connected (${bridge.getHost()}:${bridge.getPort()}, ${latencyMs}ms latency)`
        : `Bridge NOT connected (${bridge.getHost()}:${bridge.getPort()}). Is s&box running?`;

      return {
        content: [
          {
            type: "text",
            text: `${text}\n\n${JSON.stringify(status, null, 2)}`,
          },
        ],
      };
    }
  );

  // ── ping (internal — real IPC round-trip measurement) ──────────────
  server.tool(
    "ping",
    "Internal: measure real IPC round-trip latency to the s&box Bridge",
    {},
    async () => {
      const ms = await bridge.measureLatency();
      if (ms < 0) return { content: [{ type: "text", text: "ping failed: bridge unreachable" }] };
      return { content: [{ type: "text", text: `pong: ${ms}ms` }] };
    }
  );
}
