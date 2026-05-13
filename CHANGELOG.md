# Changelog

All notable changes to the s&box Claude Bridge.

## [Unreleased]

**JTC parity sprints (B.2): 8 new canonical tools + 29 JTC-compat aliases. Coverage of JTC `sbox-mcp` tool surface: 37/48 (77.1%).**

### Added — Tag management (S5)

- `tag_add` / `tag_list` / `tag_remove` — operate on `GameObject.Tags`. Idempotent: `tag_add` reports `alreadyHad=true` if the tag was present; `tag_remove` reports `removed=false` if it wasn't.

### Added — Scene search (S6)

- `scene_find_by_tag` — active-scene lookup via `Scene.FindAllWithTag`.
- `scene_find_by_component` — enumerate all GameObjects that have a given component type (resolved via `Game.TypeLibrary`).
- `scene_find_objects` — glob-pattern name search (`*` wildcards, case-insensitive). Walks `Scene.GetAllObjects(recursive=true)`.

### Added — Component lifecycle (S7)

- `component_list` — enumerate components on a GameObject. Returns each component's type name, full name, and enabled state.
- `component_remove` — destroy a component on a GameObject by type name. Idempotent: returns `removed=false` if no component of that type was present.

### Added — JTC compatibility aliases

- 29 `JTC_ALIASES` registered — every alias prints a one-shot console warning naming the canonical Lou tool, then forwards transparently. Coverage includes `editor_*` (undo / redo / save_scene / take_screenshot / play / stop / is_playing / select_object / get_selection / console_output), `file_*` (list / read / write), `scene_*` (clone / create / delete / get_hierarchy / load / reparent / set_transform), `project_info`, `asset_mount`, `asset_search`, **`asset_fetch`**, **`asset_browse_local`**, **`component_get`** (S4a), `component_add`, `component_set`, and `sbox_search_api`.
- 1 `LOU_RENAMES` entry: `install_asset` → `asset_install_pinned` (the canonical was renamed in B.1.11 for clarity).

### Added — Adjacent wrappers (S4a)

Three JTC tools landed as paramAdapter-only wrappers (no new canonicals, no new C# work):

- `asset_fetch` → `get_package_details` (pure rename).
- `asset_browse_local` → `list_project_files` (adds `Assets/` prefix to caller's `directory`, forces `recursive=true`).
- `component_get` → `get_all_properties` (maps `objectId`/`componentType` → `id`/`component`).

### Added — Hierarchy-walking wrappers (S4b)

Two of the three remaining ADJACENT tools landed via a new `responseAdapter` extension to `AliasSpec`:

- `scene_get_object` — wraps `get_scene_hierarchy` and projects the matching node by id. Responds with the node and its subtree, or `{ error: "GameObject not found: ..." }`.
- `scene_list_objects` — wraps `get_scene_hierarchy` and flattens the tree to `{ sceneName, count, objects: [{ id, name, enabled, components }] }`.

### Added — `localHandler` infra + S3-deferral closure

Closed both S3-deferred aliases by adding a TS-local dispatch primitive to `AliasSpec`. No new C# work needed.

- `aliases.ts` exports new type `LocalHandler = (args, bridge) => Promise<unknown>`. When present on an `AliasSpec`, the alias bypasses `bridge.send` entirely and computes the response in-process from `BridgeClient` state (or any TS-side data).
- `registerAlias` gained a 12th optional param `localHandler`. Mutually exclusive in practice with the bridge.send path; `responseAdapter` still applies to localHandler results.
- New entries:
  - `get_server_status` — canonical `get_bridge_status` is TS-only (no C# handler). localHandler probes `bridge.isConnected()`, `bridge.ping()`, optionally fetches editor version via `get_project_info`, and returns `{ connected, host, port, latencyMs, lastPong, editorVersion }`. Same shape as the canonical's response.
  - `sbox_get_api_type` — canonical `describe_type` only takes `name`. JTC's schema adds optional `startIndex` and `maxLength` (pagination) that Lou ignores. Schema declares all three so zod doesn't strip `name`; paramAdapter drops the pagination params before forwarding.
- Smoke now dispatches `get_server_status` and asserts the response has `{ connected: boolean, host: string, port: number }`. Fails loudly if the localHandler branch isn't taken (e.g., if dispatch falls through to bridge.send and gets `unknown command`).

### Added — Scene info canonical (S4 closing)

The last S4 wrapper, `editor_scene_info`, landed as a new canonical + passthrough alias rather than a response-reshape wrapper. The honest exposure of `SceneEditorSession.Active.Scene.{Name, Source.ResourcePath, HasUnsavedChanges}` requires C# work, not just TS:

- New canonical `get_scene_info` — returns `{ name, title, path, resourceName, dirty, isPrefabSession, isPlaying }`. Path is null for unsaved scenes; dirty=true when the session has unsaved changes.
- New C# handler `GetSceneInfoHandler` in `MyEditorMenu.cs`.
- JTC alias `editor_scene_info → get_scene_info` (passthrough).
- Smoke now dispatches `editor_scene_info` against the live editor and asserts `dirty` is a boolean and `name` is present — catches both registration failures and C# hot-reload misses.

**Note on hot-reload latency:** when the live `claudebridge.editor.dll` doesn't pick up a `MyEditorMenu.cs` change, call `trigger_hotload` and allow ~15s for s&box to recompile the bridge addon. The smoke script's `live dispatch FAIL` branch will name the missing handler if recompile didn't land.

### Tool count update (S4b + S4 closing)

| | Before | After |
|---|---|---|
| Canonical TS tools | 119 | **120** |
| C# handlers | 108 | **109** |
| JTC-compat aliases | 29 | **34** |
| Runtime-registered total | 149 | **155** |
| JTC parity coverage | 37/48 (77.1%) | **42/48 (87.5%)** |

All 34 JTC aliases verified working end-to-end via 4 positive smoke assertions covering identity (`editor_take_screenshot`), paramAdapter (`asset_browse_local`, `asset_search`), new-canonical (`editor_scene_info`), and localHandler (`get_server_status`).

### Added — `responseAdapter` infra

- `aliases.ts` exports new type `ResponseAdapter = (args, data) => unknown`. Receives the ORIGINAL caller args (JTC keys, pre-paramAdapter) so projections can reference them.
- `registerAlias` gained an optional 11th param `responseAdapter`. Applied after a successful `bridge.send`, before MCP-envelope wrapping.
- `AliasSpec` extended with optional `responseAdapter` field; `registerJtcAliasTools` plumbs it through.
- Wrapper-style aliases get a distinct description text ("wrapper around" instead of "alias for") so callers see that responses are reshaped, not 1:1 forwarded.

### Fixed — Alias schema strip (system-wide; S4-fix sprint)

`registerJtcAliasTools` was passing `schema = {}` to every `registerAlias(...)` call, which caused MCP's zod validation to strip every JTC-input key before the adapter or canonical handler saw it. **All 14 affected aliases now fixed.**

Live diagnosis: `asset_browse_local(directory="scenes")` was forwarded as `list_project_files()` with no path (returned the full Assets tree). `asset_search(amount=2)` was returning 10 results because `amount` was stripped and the adapter defaulted to `maxResults=10`. Both cases now verified working post-fix.

**Infra changes:**
- `AliasSpec` extended with optional `schema?: ZodRawShape`.
- `registerJtcAliasTools` now forwards `entry.schema ?? {}` to `registerAlias`.
- Two helpers added to `jtc-aliases.ts`: `passthrough(canonical, schema)` for identity adapters and `remap(canonical, schema, mapping)` for JTC→Lou key renames. Keep per-entry boilerplate minimal.

**Entries fixed (14 total):**
- 6 simple-string aliases promoted to `passthrough()`: `editor_take_screenshot`, `file_read`, `file_write`, `scene_load`, `asset_mount`, `asset_fetch`.
- 6 simple-string aliases promoted to `remap()` with `objectId→id` (and similar): `editor_select_object`, `scene_clone_object`, `scene_create_object`, `scene_delete_object`, `scene_reparent_object`, `scene_set_transform`.
- 5 existing `AliasSpec` entries gained explicit schemas: `asset_search`, `component_add`, `component_set`, `sbox_search_api`, `file_list`.
- `file_list` additionally fixed a pre-existing bug: adapter read JTC key `dir` but actual JTC schema uses `directory`. Now correctly remaps `directory→path`.

**Regression guards:** `scripts/smoke-aliases.mjs` now asserts `asset_browse_local(directory="scenes")` round-trips to `path="Assets/scenes"` AND `asset_search(amount=2)` returns `count<=2`. Both fail loudly if the schema-strip bug ever returns.

### Added — Parity test infrastructure

- `test/parity.test.ts` — 22 assertions enforcing the TS-tool ↔ C#-handler contract via regex scan of source. Includes inventory snapshot, alias-integrity, no-collision, and no-chain checks.
- `test/aliases.test.ts` — 9 assertions covering `registerAlias` dispatch (forwarding to canonical, one-shot warning, kind-specific warning text).
- `src/ts-only-tools.json` — reviewer-gated allowlist of canonical TS tools with no C# handler.
- `scripts/smoke-aliases.mjs` — live MCP JSON-RPC smoke against the built server (alias dispatch + handler reachability).

### Tool Count

| | Before | After |
|---|---|---|
| Canonical TS tools | 111 | **119** |
| C# handlers | 100 | **108** |
| JTC-compat aliases | 0 | **29** |
| Lou-rename aliases | 0 | **1** |
| Runtime-registered total | 111 | **149** |

JTC parity coverage: **37/48 (77.1%)**.

## [1.1.0] — 2026-04-27

**21 new tools, 109 total. Major focus: world editing and code discovery.**

### Added — Map & World Editing

The bridge now drives map-building components that follow a `[Property] List<Feature>` + `[Button]` pattern. Works with any project structured this way; no special integration required.

- `invoke_button` — press any `[Button]` on any component (the keystone tool)
- `list_component_buttons` — discover buttons available on a component
- `add_terrain_hill` / `add_terrain_clearing` / `add_terrain_trail` — sculpt the heightmap by adding features
- `clear_terrain_features` — wipe Hills / Clearings / Trails / CavePath / all
- `raycast_terrain` — sample surface height at world XY (place props on the surface)
- `add_cave_waypoint` / `clear_cave_path` — edit cave tunnel paths
- `add_forest_poi` / `add_forest_trail` — add clearing zones and trail gaps to procedural forests
- `set_forest_seed` / `clear_forest_pois` — re-roll layouts, reset

### Added — Terrain Sculpting & Painting

- `sculpt_terrain` — direct heightmap brush with raise / lower / flatten / smooth modes
- `paint_forest_density` — paint circular biome regions with density multipliers (0 = clearing, 2 = dense)
- `place_along_path` — drop instances of any model along a curve with spacing, jitter, and scale variation

### Added — Code Discovery

Stops Claude from guessing s&box APIs by exposing `Game.TypeLibrary` reflection.

- `describe_type` — full surface of any type: properties, methods, events, attributes
- `search_types` — find types by name pattern, optionally filter to Components only
- `get_method_signature` — formal signature with all overloads, parameter types, defaults
- `find_in_project` — grep the project for a symbol to find usage examples

### Added — Component Reference

- `set_prefab_ref` — assign a prefab GameObject to a component property (the case `set_property` couldn't handle because prefab references are GameObjects, not primitives)

### Added — Standalone Terrain Builder

- `build_terrain_mesh` — build a heightmap terrain mesh from a JSON spec (hills + clearings) without needing a `MapBuilder` component in the scene

### Fixed

- **`is_playing` always returning false** after `start_play` succeeded. Now uses `EditorScene.Play` with `SetPlaying` fallback, plus a `PlayState` tracker that combines multiple signals (manual flag + `Game.IsPlaying` + active-scene divergence).
- **`MeshComponent.Mesh` NullReferenceException** in `build_terrain_mesh`. `MeshComponent.Mesh` is `null` on a freshly-added component and must be assigned `new PolygonMesh()`. Latent in the previous build; surfaced by live testing.
- **`invoke_button` reporting misleading "Button not found" errors.** The reflection helper was catching `TargetInvocationException`, logging a warning, and returning `false` — which masked the actual exception thrown by the invoked method. Now unwraps and rethrows, so callers see the real inner error (e.g. `NullReferenceException: ... at MyComponent.Build()`) directly.

### Removed

- Legacy hardcoded `build_map` inline command (~150 lines of grey-box scene generator). Superseded by the new component-driver pattern.

### Tool Count

| | Before | After |
|---|---|---|
| Defined | 89 | **109** |
| Implemented | 78 | **100** |
| Not implementable (no s&box API) | 11 | **9** |

### Compatibility

- All 78 existing tools unchanged. Drop-in upgrade.
- The new map-edit tools (`add_terrain_hill`, etc.) work on any project with components shaped like `MapBuilder` / `CaveBuilder` / `ForestGenerator` (a `[Property] List<FeatureClass>` plus `[Button]` to rebuild).
- `invoke_button`, `list_component_buttons`, `raycast_terrain`, `set_prefab_ref`, and all four discovery tools work on any project, no specific component required.

### For Game Developers

To make your own components driveable by the named map tools, follow this convention:

```csharp
public class MyHill {
    [Property] public Vector2 Position { get; set; }
    [Property] public float Radius { get; set; } = 500f;
    [Property] public float Height { get; set; } = 100f;
}

public class MyTerrain : Component {
    [Property] public List<MyHill> Hills { get; set; } = new();

    [Button("Build Terrain")]
    public void Build() {
        var go = Scene.CreateObject(true);
        var mesh = go.AddComponent<MeshComponent>();
        if ( mesh.Mesh == null ) mesh.Mesh = new PolygonMesh();  // ← required, easy to miss
        // ... read Hills, generate vertices/faces ...
    }
}
```

The bridge tools find your component via `Game.TypeLibrary`, mutate the `List<>` via reflection, and re-press the `[Button]` — no per-project bridge changes required.

---

## [1.0.0] — 2026-04-10

Initial public release.

- 78 working tools across 18 categories: project, scenes, GameObjects, components, assets, materials, audio, physics, prefabs, play mode, UI, templates, networking, publishing, status.
- File-based IPC transport via `%TEMP%/sbox-bridge-ipc/` (replaced earlier WebSocket attempt — s&box's sandboxed C# blocks `System.Net`).
- Bridge addon as project-local Library at `Libraries/claudebridge/Editor/MyEditorMenu.cs`.
- BOM-less UTF-8 fix on both sides of the IPC channel (C# `new UTF8Encoding(false)` writes, MCP server strips `﻿` reads).
- 11 tools defined-but-not-implementable due to missing s&box APIs: `pause_play`, `resume_play`, `get_console_output`, `get_compile_errors`, `clear_console`, `build_project`, `get_build_status`, `clean_build`, `export_project`, `prepare_publish`.
