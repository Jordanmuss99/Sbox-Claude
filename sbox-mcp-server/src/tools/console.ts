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
  // v1.4.1: backed by C# handler that parses BridgeLogTarget NLog buffer for
  // Roslyn-style "error CS####:" / "warning CS####:" diagnostics. The bridge
  // can only surface entries written AFTER BridgeLogTarget attached (S10).
  server.tool(
    "get_compile_errors",
    "Get current C# compilation errors and warnings from s&box. Returns file path, line number, column, error code, and message for each diagnostic. Best-effort: only sees diagnostics logged via NLog after the bridge attached its capture target.",
    {
      since: z.string().optional().describe("ISO timestamp; only return diagnostics newer than this"),
      severity: z.enum(["all", "error", "warning", "info"]).optional().describe("Filter by severity (default 'error')"),
      limit: z.number().int().optional().describe("Max diagnostics to return (1-1000, default 100)"),
    },
    async (params) => {
      const res = await bridge.send("get_compile_errors", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }

      const data = res.data as {
        count?: number;
        totalMatching?: number;
        diagnostics?: unknown[];
        errors?: unknown[];
        warnings?: unknown[];
        note?: string;
      } | undefined;
      const errors = data?.errors ?? [];
      const warnings = data?.warnings ?? [];

      let text: string;
      if (errors.length === 0 && warnings.length === 0) {
        text = data?.note ?? "No compilation errors or warnings. Code is clean!";
      } else {
        text = JSON.stringify(res.data, null, 2);
      }

      return { content: [{ type: "text", text }] };
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
}
