import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { BridgeClient } from "../transport/bridge-client.js";
import { ZodRawShape } from "zod";

/**
 * Process-local set of alias names that have already emitted their
 * one-shot deprecation warning. Test-only `_resetAliasWarnings()` clears it.
 */
const warnedAliases = new Set<string>();

/** Test-only escape hatch: clear the warned set so a re-import isn't required. */
export function _resetAliasWarnings(): void {
  warnedAliases.clear();
}

/**
 * Alias category — determines the wording of the one-shot deprecation warning.
 *   - `jtc-compat`: the alias name came from JTC's MCP server (sbox-mcp v2.0.0).
 *   - `lou-rename`: the alias name was the OLD Lou canonical name before an
 *     internal D5/B.1.11 rename.
 */
export type AliasKind = "jtc-compat" | "lou-rename";

/**
 * Optional argument-shape adapter: transforms caller-side args before they
 * reach the canonical handler. Used when the alias takes a different param
 * envelope than the canonical (e.g., JTC `amount` → Lou `maxResults`).
 */
export type ParamAdapter = (
  args: Record<string, unknown>,
) => Record<string, unknown>;

/**
 * Optional response-shape adapter: transforms `bridge.send` response data
 * before it leaves the alias. Used when the alias projects/filters/flattens
 * a canonical's response (e.g., JTC `scene_get_object` extracts one node from
 * `get_scene_hierarchy`'s tree). Receives the ORIGINAL caller args (pre-
 * paramAdapter) so the projection can use JTC-named keys.
 */
export type ResponseAdapter = (
  args: Record<string, unknown>,
  data: unknown,
) => unknown;

/**
 * Optional TS-local dispatch: when an alias's canonical is TS-only (no C#
 * handler), `bridge.send` will fail with "unknown command". A localHandler
 * bypasses the bridge entirely and computes the response in-process from
 * `BridgeClient` metadata (e.g., connection state) or other TS-side state.
 *
 * Returns the raw response DATA. The wrapper applies `responseAdapter` (if
 * any) and wraps in the MCP envelope. Throw to signal failure — the wrapper
 * converts to `{ isError: true, ... }`.
 */
export type LocalHandler = (
  args: Record<string, unknown>,
  bridge: BridgeClient,
) => Promise<unknown>;

function formatWarning(
  kind: AliasKind,
  aliasName: string,
  canonicalName: string,
): string {
  switch (kind) {
    case "jtc-compat":
      return `[sbox-mcp] tool '${aliasName}' is a JTC-compat alias; canonical name is '${canonicalName}'`;
    case "lou-rename":
      return `[sbox-mcp] tool '${aliasName}' was renamed to '${canonicalName}'; please update your callers`;
  }
}

/**
 * Register an alias that forwards to a canonical tool via `bridge.send`.
 *
 * On first call per process per alias, emits a one-shot deprecation warning.
 * If `paramAdapter` is provided, caller-side args are transformed before
 * dispatch (used for envelope mismatches). The wire-level command sent to the
 * C# bridge is ALWAYS the canonical name.
 */
export function registerAlias(
  server: McpServer,
  bridge: BridgeClient,
  aliasName: string,
  canonicalName: string,
  description: string,
  schema: ZodRawShape,
  timeoutMs: number = 30000,
  warn: (msg: string) => void = (m) => console.error(m),
  kind: AliasKind = "jtc-compat",
  paramAdapter?: ParamAdapter,
  responseAdapter?: ResponseAdapter,
  localHandler?: LocalHandler,
): void {
  server.tool(
    aliasName,
    description,
    schema,
    async (args: Record<string, unknown>) => {
      if (!warnedAliases.has(aliasName)) {
        warnedAliases.add(aliasName);
        warn(formatWarning(kind, aliasName, canonicalName));
      }

      let rawData: unknown;
      if (localHandler) {
        // TS-local dispatch (canonical is TS-only). Skip bridge.send.
        try {
          rawData = await localHandler(args, bridge);
        } catch (e: unknown) {
          const msg = e instanceof Error ? e.message : String(e);
          return {
            content: [
              { type: "text" as const, text: `Error: ${msg}` },
            ],
            isError: true,
          };
        }
      } else {
        const finalArgs = paramAdapter ? paramAdapter(args) : args;
        const res = await bridge.send(canonicalName, finalArgs, timeoutMs);
        if (!res.success) {
          return {
            content: [
              {
                type: "text" as const,
                text: `Error: ${res.error ?? "unknown error"}`,
              },
            ],
            isError: true,
          };
        }
        rawData = res.data;
      }
      const finalData = responseAdapter ? responseAdapter(args, rawData) : rawData;
      return {
        content: [
          {
            type: "text" as const,
            text:
              typeof finalData === "string"
                ? finalData
                : JSON.stringify(finalData, null, 2),
          },
        ],
      };
    },
  );
}
