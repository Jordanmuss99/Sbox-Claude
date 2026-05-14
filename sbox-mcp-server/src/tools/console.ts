import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Console and error feedback tools: get_console_output, get_compile_errors, clear_console.
 * Reads from the Bridge's circular log buffer (LogCapture) to surface editor output.
 */
export function registerConsoleTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_console_output ───────────────────────────────────────────
  server.tool(
    "get_console_output",
    "Read recent console log entries from s&box. Returns log messages, warnings, and errors with timestamps",
    {
      count: z
        .number()
        .optional()
        .describe("Maximum number of log entries to return. Defaults to 50"),
      severity: z
        .enum(["all", "info", "warning", "error"])
        .optional()
        .describe("Filter by severity level. Defaults to 'all'"),
    },
    async (params) => {
      const res = await bridge.send("get_console_output", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      const data = res.data as {
        total?: number;
        returned?: number;
        severity?: string;
        entries?: Array<{
          timestamp: string;
          level: string;
          loggerName: string;
          message: string;
          exception?: string | null;
        }>;
        error?: string;
      };
      if (data.error) {
        return { content: [{ type: "text", text: `Error: ${data.error}` }] };
      }
      const entries = data.entries ?? [];
      if (entries.length === 0) {
        return {
          content: [
            {
              type: "text",
              text: `No console entries (severity=${data.severity ?? "all"}, total captured=${data.total ?? 0}).`,
            },
          ],
        };
      }
      const lines = entries.map((e) => {
        const head = `[${e.timestamp}] ${e.level.toUpperCase()}${e.loggerName ? ` <${e.loggerName}>` : ""}: ${e.message}`;
        return e.exception ? `${head}\n  ${e.exception.replace(/\n/g, "\n  ")}` : head;
      });
      const header = `Returned ${data.returned ?? entries.length} of ${data.total ?? "?"} captured entries (severity=${data.severity ?? "all"}, newest first):`;
      return {
        content: [{ type: "text", text: `${header}\n\n${lines.join("\n")}` }],
      };
    }
  );

  // ── get_compile_errors ───────────────────────────────────────────
  server.tool(
    "get_compile_errors",
    "Get current C# compilation errors and warnings from s&box. Returns file path, line number, column, error code, and message for each diagnostic",
    {},
    async () => {
      const res = await bridge.send("get_compile_errors");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }

      const data = res.data as { errors?: unknown[]; warnings?: unknown[] } | undefined;
      const errors = data?.errors ?? [];
      const warnings = data?.warnings ?? [];

      let text: string;
      if (errors.length === 0 && warnings.length === 0) {
        text = "No compilation errors or warnings. Code is clean!";
      } else {
        text = JSON.stringify(res.data, null, 2);
      }

      return {
        content: [{ type: "text", text }],
      };
    }
  );

  // ── clear_console ────────────────────────────────────────────────
  server.tool(
    "clear_console",
    "Clear all console log entries in the s&box editor",
    {},
    async () => {
      const res = await bridge.send("clear_console");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: "Console cleared" }],
      };
    }
  );

  // ── get_runtime_errors (Phase 3) ────────────────────────────────
  server.tool(
    "get_runtime_errors",
    "Query runtime errors from the console during play mode (filters NLog to Error/Fatal)",
    {
      count: z
        .number()
        .int()
        .min(1)
        .max(500)
        .default(50)
        .describe("Maximum number of error entries to return"),
    },
    async ({ count }) => {
      const res = await bridge.send("get_runtime_errors", { count });
      if (!res.success)
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      const data = res.data as { count: number; entries: Array<{ timestamp: string; level: string; logger: string; message: string }> };
      if (data.entries.length === 0)
        return { content: [{ type: "text", text: "No runtime errors found." }] };
      const lines = [`${data.count} runtime error(s):`, ""];
      for (const e of data.entries) {
        lines.push(`[${e.timestamp}] ${e.level} <${e.logger}>: ${e.message}`);
      }
      return { content: [{ type: "text", text: lines.join("\n") }] };
    }
  );
}
