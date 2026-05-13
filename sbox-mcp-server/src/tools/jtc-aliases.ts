import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z, ZodRawShape } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";
import { registerAlias, ParamAdapter, ResponseAdapter, LocalHandler } from "./aliases.js";

/**
 * Complex alias entry — used when the JTC alias has a different param envelope
 * than the Lou canonical. The `adapter` transforms caller-side args before they
 * reach the canonical handler.
 *
 * `schema` MUST declare every JTC input key the adapter reads. Without it,
 * MCP's zod validation strips unknown keys and the adapter sees `{}`. The
 * default `schema = {}` in registerJtcAliasTools below is ONLY safe for simple
 * aliases whose canonical takes no caller-supplied params.
 */
export interface AliasSpec {
  canonical: string;
  adapter: ParamAdapter;
  schema?: ZodRawShape;
  /**
   * Optional response-shape adapter. Runs after `bridge.send` succeeds and
   * before the result is wrapped in the MCP envelope. Receives the ORIGINAL
   * caller args (JTC keys, pre-paramAdapter) so projection logic can use them.
   */
  responseAdapter?: ResponseAdapter;
  /**
   * Optional TS-local dispatch. When present, the alias COMPLETELY BYPASSES
   * `bridge.send` and calls `localHandler(args, bridge)` directly. Used when
   * the canonical is TS-only (no C# handler) and the alias would otherwise
   * fail with "unknown command". The localHandler returns raw response data;
   * envelope wrapping is done by registerAlias.
   */
  localHandler?: LocalHandler;
}

/**
 * Hierarchy node shape returned by `get_scene_hierarchy`. Used by the
 * scene_get_object / scene_list_objects responseAdapters.
 */
interface HierarchyNode {
  id: string;
  name: string;
  enabled?: boolean;
  components?: string[];
  children?: HierarchyNode[];
}

/**
 * Walk a `get_scene_hierarchy` response and return a flat list of nodes
 * (children stripped). Used by scene_list_objects.
 */
function flattenHierarchy(data: unknown): Array<Omit<HierarchyNode, "children">> {
  const out: Array<Omit<HierarchyNode, "children">> = [];
  const visit = (nodes: HierarchyNode[] | undefined): void => {
    if (!Array.isArray(nodes)) return;
    for (const n of nodes) {
      out.push({ id: n.id, name: n.name, enabled: n.enabled, components: n.components });
      visit(n.children);
    }
  };
  const root = (data as { hierarchy?: HierarchyNode[] } | null)?.hierarchy;
  visit(root);
  return out;
}

/**
 * Walk a `get_scene_hierarchy` response and return the node matching `id`,
 * or null if not found. Returns the node WITH its children intact.
 */
function findInHierarchy(data: unknown, targetId: string): HierarchyNode | null {
  const root = (data as { hierarchy?: HierarchyNode[] } | null)?.hierarchy;
  if (!Array.isArray(root)) return null;
  const stack: HierarchyNode[] = [...root];
  while (stack.length > 0) {
    const n = stack.pop()!;
    if (n.id === targetId) return n;
    if (Array.isArray(n.children)) stack.push(...n.children);
  }
  return null;
}

/**
 * Helper — declare an alias whose JTC and Lou input shapes match exactly.
 * Adapter is identity; schema declares JTC keys so zod doesn't strip them.
 */
function passthrough(canonical: string, schema: ZodRawShape): AliasSpec {
  return { canonical, schema, adapter: (args) => args };
}

/**
 * Helper — declare an alias whose JTC input keys must be renamed to Lou keys.
 * `mapping` is `{ jtcKey: louKey }`; any JTC key not listed is forwarded as-is.
 * Used when JTC consistently calls a GameObject id `objectId` but Lou calls it `id`.
 */
function remap(
  canonical: string,
  schema: ZodRawShape,
  mapping: Record<string, string>,
): AliasSpec {
  return {
    canonical,
    schema,
    adapter: (args) => {
      const out: Record<string, unknown> = { ...args };
      for (const [from, to] of Object.entries(mapping)) {
        if (from in out) {
          out[to] = out[from];
          delete out[from];
        }
      }
      return out;
    },
  };
}

/**
 * JTC-compat alias registry. A value can be:
 *   - `string` — simple alias whose canonical takes NO caller-supplied params.
 *     If the canonical takes params, use `passthrough()` or `remap()` instead,
 *     or declare an inline AliasSpec with an explicit schema. Without a schema,
 *     zod strips every key and the canonical sees `{}`.
 *   - `AliasSpec` — explicit schema + adapter. Use `passthrough()`/`remap()`
 *     helpers for the common cases; inline for non-trivial adapters.
 *
 * On first call per process per alias, a one-shot deprecation warning fires.
 *
 * Adding an alias:
 *   1. Ensure the canonical Lou tool exists (parity test enforces this).
 *   2. Add the entry below using the helper that fits.
 *   3. Add `<jtc_name>: <lou_name>` to the rename table in CLAUDE.md.
 */
export const JTC_ALIASES: Record<string, string | AliasSpec> = {
  // B.1.4-7 — proof-point cluster. All canonical handlers take NO required args.
  editor_undo: "undo",
  editor_redo: "redo",
  editor_save_scene: "save_scene",
  // take_screenshot accepts optional path; JTC additionally documents width/height
  // which Lou's take_screenshot ignores (default 1920x1080). Schema declares
  // them so callers passing those keys don't get them stripped silently.
  editor_take_screenshot: passthrough("take_screenshot", {
    path: z.string().optional().describe("Output PNG path"),
    width: z.number().optional().describe("(unused by Lou; default 1920)"),
    height: z.number().optional().describe("(unused by Lou; default 1080)"),
  }),

  // S1 — editor_* alias fan-out (Phase B.2, 2026-05-12).
  editor_get_selection: "get_selected_objects",
  editor_is_playing: "is_playing",
  editor_play: "start_play",
  editor_select_object: remap(
    "select_object",
    { objectId: z.string().describe("GUID of the GameObject") },
    { objectId: "id" },
  ),
  editor_stop: "stop_play",

  // S2 — scene_*/file_*/project_info alias fan-out (Phase B.2, 2026-05-12).
  file_read: passthrough("read_file", {
    path: z.string().describe("Project-relative file path"),
  }),
  file_write: passthrough("write_file", {
    path: z.string().describe("Project-relative file path"),
    content: z.string().describe("File content to write"),
  }),
  project_info: "get_project_info",
  scene_clone_object: remap(
    "duplicate_gameobject",
    { objectId: z.string().describe("GUID of the GameObject to clone") },
    { objectId: "id" },
  ),
  scene_create_object: remap(
    "create_gameobject",
    {
      name: z.string().optional().describe("Display name"),
      parentId: z.string().optional().describe("GUID of parent GameObject"),
      position: z.unknown().optional().describe("{ x, y, z } position"),
    },
    { parentId: "parent" },
  ),
  scene_delete_object: remap(
    "delete_gameobject",
    { objectId: z.string().describe("GUID of the GameObject to delete") },
    { objectId: "id" },
  ),
  scene_get_hierarchy: "get_scene_hierarchy",
  scene_load: passthrough("load_scene", {
    path: z.string().describe("Path to the .scene file"),
  }),
  scene_reparent_object: remap(
    "set_parent",
    {
      objectId: z.string().describe("GUID of the GameObject to reparent"),
      parentId: z.string().optional().describe("GUID of new parent (omit for root)"),
    },
    { objectId: "id" },
  ),
  scene_set_transform: remap(
    "set_transform",
    {
      objectId: z.string().describe("GUID of the GameObject"),
      position: z.unknown().optional().describe("{ x, y, z } position"),
      rotation: z.unknown().optional().describe("{ pitch, yaw, roll } rotation"),
      scale: z.unknown().optional().describe("Uniform scale or { x, y, z }"),
    },
    { objectId: "id" },
  ),

  // S3 simple — equivalent alias, matching envelope (Phase B.2, 2026-05-12).
  asset_mount: passthrough("asset_install_pinned", {
    ident: z.string().describe("Package identifier"),
  }),

  // S3 with adapters — envelope mismatches (Phase B.2, 2026-05-12).
  asset_search: {
    canonical: "list_asset_library",
    schema: {
      query: z.string().optional().describe("Search query"),
      amount: z.union([z.string(), z.number()]).optional().describe("Result cap (JTC name for maxResults)"),
      type: z.string().optional().describe("Asset type filter"),
    },
    adapter: (args) => {
      // JTC `amount` (string|number) → Lou `maxResults` (number); default 10 per B.1.13
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
    schema: {
      objectId: z.string().describe("GUID of the GameObject"),
      componentType: z.string().describe("Component type name (e.g. 'ModelRenderer')"),
      properties: z.record(z.unknown()).optional().describe("Optional property overrides"),
    },
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
    schema: {
      objectId: z.string().describe("GUID of the GameObject"),
      componentType: z.string().describe("Component type name"),
      property: z.string().describe("Property name to set"),
      value: z.unknown().describe("New value (any type)"),
    },
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
    schema: {
      query: z.string().describe("Substring to match against type name"),
      limit: z.number().optional().describe("Max results"),
    },
    adapter: (args) => {
      // JTC `query` → Lou `pattern`. `limit` forwards unchanged (Lou also names it `limit`).
      const out: Record<string, unknown> = { ...args };
      if ("query" in out) {
        out.pattern = out.query;
        delete out.query;
      }
      return out;
    },
  },

  // S2 catch-up — file_list. Two bugs fixed:
  //   1. Adapter previously expected JTC key `dir`; actual JTC key is `directory`.
  //   2. Schema was missing entirely so zod stripped any key passed.
  // JTC `directory` → Lou `path`. JTC `pattern` is not a Lou concept (Lou takes
  // `extension` only); pattern is forwarded as-is so the canonical can ignore it.
  file_list: {
    canonical: "list_project_files",
    schema: {
      directory: z.string().optional().describe("Project-relative directory"),
      pattern: z.string().optional().describe("Glob pattern (unused by Lou; forwarded for forward-compat)"),
    },
    adapter: (args) => {
      const out: Record<string, unknown> = { ...args };
      if ("directory" in out) {
        out.path = out.directory;
        delete out.directory;
      }
      return out;
    },
  },

  // S1 catch-up — editor_console_output. The canonical `get_console_output`
  // currently lacks a C# handler (Batch 14 omitted per LogCapture availability),
  // so BOTH this alias and the canonical return an "unknown command" error at
  // runtime. When a C# handler is added for get_console_output, this alias
  // starts working automatically. Schema empty: canonical takes no required args.
  editor_console_output: "get_console_output",

  // S4a — ADJACENT bucket adjacent wrappers (Phase B.2, 2026-05-12).
  asset_fetch: passthrough("get_package_details", {
    ident: z.string().describe("Package identifier"),
  }),
  asset_browse_local: {
    canonical: "list_project_files",
    schema: {
      directory: z.string().optional().describe("Subdirectory under Assets/"),
      extension: z.string().optional().describe("File extension filter (e.g. '.prefab')"),
    },
    adapter: (args) => {
      // JTC `directory` (optional, relative to Assets/) → Lou `path` (relative to project root).
      // Force recursive=true to match JTC's flat-listing semantics.
      const out: Record<string, unknown> = {};
      const dir = args.directory;
      if (typeof dir === "string" && dir.length > 0) {
        out.path = dir.startsWith("Assets/") || dir === "Assets" ? dir : `Assets/${dir}`;
      } else {
        out.path = "Assets";
      }
      if (args.extension !== undefined) out.extension = args.extension;
      out.recursive = true;
      return out;
    },
  },
  component_get: remap(
    "get_all_properties",
    {
      objectId: z.string().describe("GUID of the GameObject"),
      componentType: z.string().describe("Component type name (e.g. 'ModelRenderer')"),
    },
    { objectId: "id", componentType: "component" },
  ),

  // S4 closing — editor_scene_info (2026-05-12).
  // Added canonical `get_scene_info` (new C# GetSceneInfoHandler reading
  // SceneEditorSession.Active.Scene.{Name, Source.ResourcePath, HasUnsavedChanges}).
  // JTC alias is now a simple passthrough.
  editor_scene_info: passthrough("get_scene_info", {}),

  // S3-deferral closure (2026-05-12) — unblocked via new `localHandler` infra.
  // `get_server_status` canonical is `get_bridge_status` (TS-only, no C#),
  // so the alias dispatches in-process from BridgeClient metadata.
  get_server_status: {
    canonical: "get_bridge_status",
    schema: {},
    adapter: (args) => args,
    localHandler: async (_args, bridge) => {
      // Same logic as the `get_bridge_status` canonical in status.ts — kept
      // inline here to keep the alias self-contained. If status.ts ever grows
      // additional probes, factor `getBridgeStatusData(bridge)` and import.
      if (!bridge.isConnected()) {
        try { await bridge.connect(); } catch { /* leave disconnected */ }
      }
      const connected = bridge.isConnected();
      let latencyMs = -1;
      let editorVersion: string | null = null;
      if (connected) {
        latencyMs = await bridge.ping();
        try {
          const res = await bridge.send("get_project_info", {}, 5000);
          if (res.success && res.data) {
            const data = res.data as Record<string, unknown>;
            editorVersion = (data.editorVersion as string) ?? null;
          }
        } catch { /* non-fatal */ }
      }
      return {
        connected,
        host: bridge.getHost(),
        port: bridge.getPort(),
        latencyMs: connected ? latencyMs : null,
        lastPong: connected ? new Date(bridge.getLastPongTime()).toISOString() : null,
        editorVersion,
      };
    },
  },

  // `sbox_get_api_type` canonical is `describe_type`. JTC's schema adds
  // pagination params (`startIndex`, `maxLength`) that Lou's describe_type
  // does not honor — they're declared optional so callers passing them get
  // through validation, then ignored at the canonical layer (which only reads
  // `name`). Both sides use the same `name` key, so adapter is identity.
  sbox_get_api_type: {
    canonical: "describe_type",
    schema: {
      name: z.string().describe("Type name (short or fully-qualified)"),
      startIndex: z.number().optional().describe("(unused by Lou; ignored)"),
      maxLength: z.number().optional().describe("(unused by Lou; ignored)"),
    },
    adapter: (args) => {
      // Strip the pagination params so they don't sit in the bridge.send payload.
      const out: Record<string, unknown> = { ...args };
      delete out.startIndex;
      delete out.maxLength;
      return out;
    },
  },

  // S4b — hierarchy-walking wrappers (Phase B.2, 2026-05-12).
  // Both wrap `get_scene_hierarchy` and reshape its response via responseAdapter.
  scene_get_object: {
    canonical: "get_scene_hierarchy",
    schema: {
      objectId: z.string().describe("GUID of the GameObject to fetch"),
    },
    // Canonical takes no required args; force a deep walk so any GO can be found.
    adapter: () => ({ maxDepth: 100 }),
    responseAdapter: (args, data) => {
      const id = typeof args.objectId === "string" ? args.objectId : null;
      if (!id) return { error: "objectId is required" };
      const node = findInHierarchy(data, id);
      if (!node) return { error: `GameObject not found: ${id}` };
      return node;
    },
  },
  scene_list_objects: {
    canonical: "get_scene_hierarchy",
    schema: {},
    adapter: () => ({ maxDepth: 100 }),
    responseAdapter: (_args, data) => {
      const objects = flattenHierarchy(data);
      const sceneName = (data as { sceneName?: string } | null)?.sceneName ?? null;
      return { sceneName, count: objects.length, objects };
    },
  },

  // All S3 deferrals and S4 wrappers closed (2026-05-12).
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
  // file_list: CLOSED (2026-05-12 / 2026-05-12 S4-fix) — schema declared,
  //   `directory→path` rewrite corrected (previously bug: adapter read `dir`).

  // get_server_status: CLOSED (2026-05-12 / S3-deferral closure via localHandler).
  // sbox_get_api_type: CLOSED (2026-05-12 / S3-deferral closure with paramAdapter
  //   stripping pagination params).
  // scene_get_object: CLOSED (2026-05-12 / S4b) — responseAdapter walks hierarchy.
  // scene_list_objects: CLOSED (2026-05-12 / S4b) — responseAdapter flattens hierarchy.
  // editor_scene_info: CLOSED (2026-05-12 / S4 closing) — new C# canonical + passthrough alias.
} as const;

/** Wire all aliases (JTC-compat + Lou-renames) into the MCP server. */
export function registerJtcAliasTools(
  server: McpServer,
  bridge: BridgeClient,
): void {
  for (const [aliasName, entry] of Object.entries(JTC_ALIASES)) {
    const canonical = typeof entry === "string" ? entry : entry.canonical;
    const adapter = typeof entry === "string" ? undefined : entry.adapter;
    const schema = typeof entry === "string" ? {} : (entry.schema ?? {});
    const responseAdapter = typeof entry === "string" ? undefined : entry.responseAdapter;
    const localHandler = typeof entry === "string" ? undefined : entry.localHandler;
    const desc =
      typeof entry === "string"
        ? `JTC-compat alias for '${canonical}'. Forwards to the canonical tool — see '${canonical}' for parameters and behavior. This alias is deprecated; migrate callers to '${canonical}'.`
        : localHandler
          ? `JTC-compat alias for '${canonical}' (TS-local dispatch). The canonical is TS-only; this alias computes the same response in-process. This alias is deprecated; migrate callers to '${canonical}'.`
          : responseAdapter
            ? `JTC-compat wrapper around '${canonical}' (reshapes response). See '${canonical}' for the underlying data. This alias is deprecated; migrate callers to '${canonical}' if you can consume its richer envelope directly.`
            : `JTC-compat alias for '${canonical}' (with param-envelope adapter). Forwards to the canonical tool; see '${canonical}' for full semantics. This alias is deprecated; migrate callers to '${canonical}'.`;
    registerAlias(
      server,
      bridge,
      aliasName,
      canonical,
      desc,
      schema,
      30000,
      undefined,
      "jtc-compat",
      adapter,
      responseAdapter,
      localHandler,
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
