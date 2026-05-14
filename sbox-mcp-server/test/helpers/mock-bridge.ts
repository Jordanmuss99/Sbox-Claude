import * as fs from "node:fs";
import * as path from "node:path";
import type { MockIpcDir } from "./mock-ipc-dir.js";

/**
 * Phase 0.3 — minimal stand-in for the C# bridge for transport tests.
 *
 * Polls the fixture's ipcDir on a short interval, reads any req_*.json file,
 * and writes a res_*.json envelope. The default handler echoes back a
 * `{ success: true, data: { command, params } }` envelope. Tests can supply
 * a custom handler to simulate errors, slow responses, or selective drops.
 *
 * `start()` returns an `unsubscribe` function to call in `afterEach`.
 *
 * The mock-bridge does NOT enforce one-request-per-frame ordering like the
 * real C# bridge. Tests that need ordering should serialize via Promise.all
 * with await checkpoints, or use the `dropFn` to drop specific requests.
 */
export interface MockBridgeOptions {
  /** Poll interval. Default 5ms (much faster than the real 20ms bridge). */
  pollMs?: number;
  /** Per-request handler. Defaults to echo. Return `undefined` to drop the request. */
  handler?: (req: { id: string; command: string; params: Record<string, unknown> }) => Record<string, unknown> | undefined;
  /**
   * Artificial delay between reading the req file and writing the res file.
   * Useful for tests that want to observe pending state.
   */
  responseDelayMs?: number;
}

export interface MockBridgeHandle {
  stop(): void;
  /** Number of requests handled so far. */
  handledCount(): number;
  /** Number of requests intentionally dropped. */
  droppedCount(): number;
}

export function startMockBridge(
  fixture: MockIpcDir,
  options: MockBridgeOptions = {}
): MockBridgeHandle {
  const pollMs = options.pollMs ?? 5;
  const handler = options.handler ?? defaultEchoHandler;
  const responseDelayMs = options.responseDelayMs ?? 0;

  let handled = 0;
  let dropped = 0;
  let stopped = false;

  const timer = setInterval(() => {
    if (stopped) return;
    let files: string[];
    try { files = fs.readdirSync(fixture.ipcDir); }
    catch { return; }
    for (const f of files) {
      const m = f.match(/^req_(.+)\.json$/);
      if (!m) continue;
      const reqPath = path.join(fixture.ipcDir, f);
      let raw: string;
      try { raw = fs.readFileSync(reqPath, "utf8"); }
      catch { continue; }
      try { fs.unlinkSync(reqPath); } catch { /* ignore */ }
      let request: { id: string; command: string; params: Record<string, unknown> };
      try { request = JSON.parse(raw); }
      catch { continue; }
      const envelope = handler(request);
      if (envelope === undefined) {
        dropped++;
        continue;
      }
      handled++;
      const resPath = path.join(fixture.ipcDir, `res_${m[1]}.json`);
      const fullEnvelope = { id: request.id, ...envelope };
      const writeRes = (): void => {
        try { fs.writeFileSync(resPath, JSON.stringify(fullEnvelope)); }
        catch { /* fixture cleaned up */ }
      };
      if (responseDelayMs > 0) setTimeout(writeRes, responseDelayMs);
      else writeRes();
    }
  }, pollMs);
  timer.unref?.();

  return {
    stop() {
      stopped = true;
      clearInterval(timer);
    },
    handledCount: () => handled,
    droppedCount: () => dropped,
  };
}

function defaultEchoHandler(req: { command: string; params: Record<string, unknown> }): Record<string, unknown> {
  return { success: true, data: { echoed: true, command: req.command, params: req.params } };
}
