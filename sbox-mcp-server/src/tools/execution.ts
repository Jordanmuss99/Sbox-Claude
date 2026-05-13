/**
 * S8 (2026-05-13) — execute_csharp + console_run.
 *
 * Both tools dispatch to C# handlers in MyEditorMenu.cs. Synchronous on the
 * editor side (Roslyn awaits internally for execute_csharp, ConsoleSystem.Run
 * is fire-and-forget for console_run) so no long-running-handler protocol is
 * needed — the standard 30 s bridge timeout accommodates Roslyn cold-start.
 *
 * Originally gated by gate2 G2.3 "execute_csharp + console_run deferred until
 * S1-S7 green; treat as a separate spike with its own user re-approval gate."
 * Re-approved 2026-05-13.
 */

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerExecutionTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── console_run ──────────────────────────────────────────────────
  server.tool(
    "console_run",
    "Run a console command in the s&box editor console. Fire-and-forget — output (if any) appears in the editor console, not in the tool response.",
    {
      command: z
        .string()
        .min(1)
        .describe("Console command to run (e.g. 'sv_cheats 1', 'echo hello')"),
    },
    async ({ command }) => {
      const res = await bridge.send("console_run", { command });
      if (!res.success) {
        return {
          content: [
            { type: "text", text: `Error: ${res.error ?? "console_run failed"}` },
          ],
        };
      }
      const data = res.data as {
        executed?: boolean;
        command?: string;
        note?: string;
        error?: string;
      };
      // ConsoleSystem.Run throws for unknown commands; handler returns { error } in that case.
      if (data.error) {
        return {
          content: [
            { type: "text", text: `console_run failed: ${data.error}` },
          ],
        };
      }
      const lines = [`Executed: ${data.command ?? command}`];
      if (data.note) lines.push("", `Note: ${data.note}`);
      return { content: [{ type: "text", text: lines.join("\n") }] };
    }
  );

  // ── execute_csharp ───────────────────────────────────────────────
  server.tool(
    "execute_csharp",
    "Evaluate a C# expression or statement block in the s&box editor context using Roslyn scripting. Returns the result or a structured error if the scripting assembly isn't loaded.",
    {
      code: z
        .string()
        .min(1)
        .describe("C# expression or statement block to evaluate"),
      imports: z
        .string()
        .optional()
        .describe(
          "Optional comma-separated namespaces to import (e.g. 'Sandbox.UI, Editor.Inspector'). Default imports: System, System.Linq, System.Collections.Generic, Sandbox, Editor."
        ),
    },
    async ({ code, imports }) => {
      const res = await bridge.send(
        "execute_csharp",
        imports ? { code, imports } : { code },
        30000
      );
      if (!res.success) {
        return {
          content: [
            { type: "text", text: `Error: ${res.error ?? "execute_csharp failed"}` },
          ],
        };
      }
      const data = res.data as {
        executed: boolean;
        result: string;
        type?: string;
        error?: string;
        note?: string;
        stack?: string;
      };
      if (!data.executed) {
        const lines = [`execute_csharp failed:`, ``, data.error ?? "(no error message)"];
        if (data.note) lines.push("", `Note: ${data.note}`);
        if (data.stack) lines.push("", `Stack:`, data.stack);
        return { content: [{ type: "text", text: lines.join("\n") }] };
      }
      const lines = [
        `Result: ${data.result}`,
        data.type && data.type !== "void" ? `Type: ${data.type}` : "",
      ].filter(Boolean);
      return { content: [{ type: "text", text: lines.join("\n") }] };
    }
  );
}
