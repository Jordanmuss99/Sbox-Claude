import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { BridgeClient } from "../transport/bridge-client.js";
import { registerAlias, ParamAdapter } from "./aliases.js";

/**
 * Complex alias entry — used when the JTC alias has a different param envelope
 * than the Lou canonical. The `adapter` transforms caller-side args before they
 * reach the canonical handler.
 */
export interface AliasSpec {
  canonical: string;
  adapter: ParamAdapter;
}

/**
 * JTC-compat alias registry. A value can be:
 *   - `string` — simple alias: canonical name; args forwarded verbatim.
 *   - `AliasSpec` — complex alias with a param adapter for envelope mismatches.
 *
 * The forwarding mechanism is `bridge.send(canonical, adapter?(args) ?? args)`.
 * On first call per process per alias, a one-shot deprecation warning fires.
 *
 * Adding an alias:
 *   1. Ensure the canonical Lou tool exists (parity test enforces this).
 *   2. Add the entry below.
 *   3. Add `<jtc_name>: <lou_name>` to the rename table in CLAUDE.md.
 */
export const JTC_ALIASES: Record<string, string | AliasSpec> = {
  // B.1.4-7 (Phase B.1 proof-point cluster)
  editor_undo: "undo",
  editor_redo: "redo",
  editor_save_scene: "save_scene",
  editor_take_screenshot: "take_screenshot",
  // S1 (Phase B.2 — editor_* alias fan-out, 2026-05-12)
  editor_get_selection: "get_selected_objects",
  editor_is_playing: "is_playing",
  editor_play: "start_play",
  editor_select_object: "select_object",
  editor_stop: "stop_play",
  // S2 (Phase B.2 — scene_*/file_*/project_info alias fan-out, 2026-05-12)
  file_read: "read_file",
  file_write: "write_file",
  project_info: "get_project_info",
  scene_clone_object: "duplicate_gameobject",
  scene_create_object: "create_gameobject",
  scene_delete_object: "delete_gameobject",
  scene_get_hierarchy: "get_scene_hierarchy",
  scene_load: "load_scene",
  scene_reparent_object: "set_parent",
  scene_set_transform: "set_transform",
  // S3 simple (Phase B.2 — equivalent aliases with matching envelopes, 2026-05-12)
  asset_mount: "asset_install_pinned",
  // S3 with adapters (Phase B.2 — envelope mismatches, 2026-05-12)
  asset_search: {
    canonical: "list_asset_library",
    adapter: (args) => {
      // JTC `amount` (string) → Lou `maxResults` (number); default 10 per B.1.13
      const out: Record<string, unknown> = { ...args };
      const amount = out.amount;
      delete out.amount;
      if (amount === undefined || amount === null) {
        out.maxResults = 10;
      } else if (typeof amount === "string") {
        const n = parseInt(amount, 10);
        out.maxResults = Number.isFinite(n) ? n : 10;
      } else if (typeof amount === "number") {
        out.maxResults = amount;
      }
      return out;
    },
  },
  component_add: {
    canonical: "add_component_with_properties",
    adapter: (args) => {
      // JTC `objectId`/`componentType` → Lou `id`/`component`
      const out: Record<string, unknown> = { ...args };
      if ("objectId" in out) {
        out.id = out.objectId;
        delete out.objectId;
      }
      if ("componentType" in out) {
        out.component = out.componentType;
        delete out.componentType;
      }
      return out;
    },
  },
  component_set: {
    canonical: "set_property",
    adapter: (args) => {
      const out: Record<string, unknown> = { ...args };
      if ("objectId" in out) {
        out.id = out.objectId;
        delete out.objectId;
      }
      if ("componentType" in out) {
        out.component = out.componentType;
        delete out.componentType;
      }
      return out;
    },
  },
  sbox_search_api: {
    canonical: "search_types",
    adapter: (args) => {
      // JTC `query` → Lou `pattern`
      const out: Record<string, unknown> = { ...args };
      if ("query" in out) {
        out.pattern = out.query;
        delete out.query;
      }
      return out;
    },
  },
  // S2 catch-up (2026-05-12) — file_list closed via paramAdapter infrastructure
  file_list: {
    canonical: "list_project_files",
    adapter: (args) => {
      // JTC `dir` → Lou `path`
      const out: Record<string, unknown> = { ...args };
      if ("dir" in out) {
        out.path = out.dir;
        delete out.dir;
      }
      return out;
    },
  },
  // S1 catch-up (2026-05-12) — editor_console_output forwards via bridge.send.
  // The canonical `get_console_output` currently lacks a C# handler (Batch 14
  // omitted per LogCapture availability), so BOTH this alias and the canonical
  // return an "unknown command" error at runtime. When a C# handler is added
  // for get_console_output, this alias starts working automatically.
  editor_console_output: "get_console_output",
  // NOTE deferrals (see PENDING_ALIAS_REQUIREMENTS below):
  //   - get_server_status → get_bridge_status (genuinely TS-local; needs localHandler infra)
  //   - sbox_get_api_type → describe_type (needs Lou-side schema verification)
};

/**
 * Lou-internal rename registry. Each entry maps an OLD Lou tool name → its
 * NEW canonical name (post B.1.11/D5 rename). Same dispatch path as JTC
 * aliases but uses a different ("was renamed to") deprecation warning.
 */
export const LOU_RENAMES: Record<string, string> = {
  install_asset: "asset_install_pinned",
};

/**
 * Per-alias requirements deferred to future sprints. Captured here so the
 * constraints are not lost between phases.
 */
export const PENDING_ALIAS_REQUIREMENTS = {
  // B.1.13 was folded into the asset_search adapter above (default take=10).
  // B.1.14 was implemented as the canonical `asset_install_pinned` handler.

  // editor_console_output: CLOSED (2026-05-12) — registered as simple alias.
  //   It forwards via bridge.send to the canonical, which currently has no
  //   C# handler. Both alias and canonical return "unknown command" at
  //   runtime; both start working when a C# get_console_output handler is added.
  // file_list: CLOSED (2026-05-12) — added with paramAdapter (`dir`→`path`).

  // S3 deferral (2026-05-12)
  get_server_status: {
    canonical: "get_bridge_status",
    blocker: "canonical is TS-only (no C# handler); bridge.send dispatch fails",
    suggestedFix: "same as editor_console_output — needs localHandler callback infra",
  },
  sbox_get_api_type: {
    canonical: "describe_type",
    blocker: "JTC schema adds startIndex/maxLength pagination; Lou's describe_type may not support these. Need to verify Lou's exact schema before deciding if extra params are ignored (safe to alias) or rejected (need adapter).",
    suggestedFix: "probe describe_type Zod schema; if extra props rejected, add adapter to strip startIndex/maxLength",
  },
} as const;

/** Wire all aliases (JTC-compat + Lou-renames) into the MCP server. */
export function registerJtcAliasTools(
  server: McpServer,
  bridge: BridgeClient,
): void {
  for (const [aliasName, entry] of Object.entries(JTC_ALIASES)) {
    const canonical = typeof entry === "string" ? entry : entry.canonical;
    const adapter = typeof entry === "string" ? undefined : entry.adapter;
    const desc =
      typeof entry === "string"
        ? `JTC-compat alias for '${canonical}'. Forwards to the canonical tool — see '${canonical}' for parameters and behavior. This alias is deprecated; migrate callers to '${canonical}'.`
        : `JTC-compat alias for '${canonical}' (with param-envelope adapter). Forwards to the canonical tool; see '${canonical}' for full semantics. This alias is deprecated; migrate callers to '${canonical}'.`;
    registerAlias(
      server,
      bridge,
      aliasName,
      canonical,
      desc,
      {},
      30000,
      undefined,
      "jtc-compat",
      adapter,
    );
  }
  for (const [oldName, newName] of Object.entries(LOU_RENAMES)) {
    registerAlias(
      server,
      bridge,
      oldName,
      newName,
      `Renamed to '${newName}'. This alias is deprecated; please update your callers — the canonical tool is '${newName}'.`,
      {},
      30000,
      undefined,
      "lou-rename",
    );
  }
}
