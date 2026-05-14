# Changelog

All notable changes to the s&box Claude Bridge.

## [Unreleased]

## [1.4.0] - 2026-05-14

**Transport hardening + Phase 3 v3 tools + addon-sync infrastructure + sbox-game-dev skill.**

140 canonical TS tools / 125 C# handlers / 15 TS-only / 34 JTC-compat aliases + 1 Lou-rename = **175 runtime-registered total**. JTC parity: still 48/48 (100%). Addon wire protocol: **v1** (new this release).

### Added — wire-protocol versioning (Phase 0.1)

The IPC envelope between the C# bridge and the Node MCP server is now versioned. status.json carries `protocol_version: 1`, `addonVersion`, and `editorPid`; the `ping` handler echoes the bridge's protocol version too. On connect, `BridgeClient` asserts the version matches its own `PROTOCOL_VERSION` constant:

- **match**: silent accept.
- **mismatch**: sets `hasProtocolMismatch()` flag, emits a `mismatch` event, logs a warning. Tool calls can still attempt to round-trip.
- **missing** (pre-1.4.0 bridge): one-shot soft-warn, continue in compat mode.

`PROTOCOL_VERSION` will only bump on breaking IPC changes (envelope shape, status.json key renames). v1 = the post-Phase-0 envelope shipped in v1.3.0.

### Added — canonical addon sync infrastructure (Phase 0.2)

The C# bridge addon now has ONE canonical source: `sbox-bridge-addon/Editor/MyEditorMenu.cs`. The live runtime copy lives at `<sbox-project>/Libraries/claudebridge/Editor/MyEditorMenu.cs`. Two scripts copy the canonical into the live location:

- `sbox-mcp-server/scripts/sync-addon.ps1` (PowerShell)
- `sbox-mcp-server/scripts/sync-addon.sh` (Bash, POSIX-portable)

Both refuse to run without an explicit `-Target` / `$SBOX_PROJECT_LIB`, compute SHA256 before and after, and bail no-op when already in sync.

Drift detection: `node scripts/verify-addon-sync.mjs` returns `match` / `drift` / `skipped` / `missing-target` / `missing-canonical`. The vitest `test/addon-sync.test.ts` smoke-tests the infrastructure and — when `SBOX_PROJECT_LIB` is set in the env — fails on drift. In a clean clone with no target configured, it skips cleanly (CI safe).

### Added — test scaffolding (Phase 0.3)

New `test/helpers/` directory:

- `mock-ipc-dir.ts` — disposable IPC-dir fixture (`makeMockIpcDir()`). Returns helpers for `writeStatus`, `writeResponse`, `writeRequest`, `list`, `cleanup`. Each test gets its own tmpdir.
- `mock-bridge.ts` — minimal stand-in for the C# bridge. Polls the fixture's ipcDir, echoes back `res_*.json` envelopes. Custom handlers + artificial delays supported. Used by `test/transport.test.ts` for the heartbeat / demux / orphan-sweep tests.

Live integration tests now opt-in via `SBOX_BRIDGE_LIVE=1` so default `npm test` runs entirely offline.

### Added — docs templating (Phase 0.4)

Single source of truth for tool-inventory counts:

- `scripts/gen-status.mjs` — regex-scans `src/tools/*.ts` (`server.tool("X", ...)`), `MyEditorMenu.cs` (`Register("X", ...)`), `ts-only-tools.json`, and `jtc-aliases.ts` (top-level keys only — nested schema keys are correctly excluded). Writes `.omc/status.json`.
- `scripts/inject-status.mjs` — reads `.omc/status.json` and replaces `<!-- BEGIN STATUS:counts -->` / `<!-- BEGIN STATUS:inventory -->` blocks in target markdown files. Exit code 2 on drift (CI hook ready). `--write` to apply.

New npm scripts: `docs:gen-status`, `docs:inject-status`, `docs:sync`, `docs:check`.

### Added — transport hardening (Phase A)

The `BridgeClient` got a substantial rewrite:

- **3-strike heartbeat** (A.C.3.10) — `startHeartbeat(intervalMs, timeoutMs)` pings via `measureLatency()` on a self-rescheduling timer (NOT `setInterval`, so we never have two pings in flight at once). After `maxMissBeforeDisconnect` (default 3) consecutive misses, the client transitions to disconnected, emits a `disconnect` event, and starts a reconnect loop that polls status.json every 2 s. When the bridge comes back, the next heartbeat tick emits `reconnect`.
- **Shared `fs.watch` + demux** (A.C.3.11) — a single watcher on the IPC dir wakes up the right pending request by id (`pending: Map<id, resolver>`). Concurrent `bridge.send()` calls no longer thrash with per-call setInterval polls. Race fixes: pending registered BEFORE `fs.writeFileSync(req)`; filename filter ignores `events.json` / `status.json`; Windows null-filename falls back to readdirSync scan; envelope-id assertion catches mismatched res files. The 50 ms polling is kept as a fallback for flaky Windows tmpdirs.
- **Scoped orphan sweep** (A.C.3.12) — first successful connect sweeps `req_*.json` / `res_*.json` older than `STARTUP_ORPHAN_AGE_MS` (default 10 min). Nothing outside the `req|res_*.json` pattern is touched. Concurrent MCP-server instances keep their in-flight requests.
- **New status fields** (A.C.3.13) — C# writes `protocol_version`, `addonVersion`, `editorPid`. TS `get_bridge_status` adds `watchMode`, `pingMissCount`, `lastPongAgeMs`, `pendingRequestCount`, `heartbeatActive`, `reconnecting`, `protocolMismatch`.
- **`sendBatch` dropped** (A.C.3.14) — dead code (no callers, non-atomic, redundant with `Promise.all([send, send, ...])` once the demux landed). The shared comment on `IsHandlerFailure` in C# was updated to note the historical BatchHandler caller is gone.

Graceful shutdown: `bridge.disconnect()` clears timers, closes the watcher, resolves in-flight requests with `BRIDGE_DISCONNECTED`. `index.ts` wires SIGINT/SIGTERM to call it.

### Added — 4 Phase 3 v3 tools (Phase B)

- **`snapshot_gameobject_tree(rootId, outputPath)`** — serialize a subtree to JSON. Round-trips name, enabled, tags, transform, and primitive component property values (bool / string / numbers / `Vector3` / `Rotation` / `Color` / enums). Reference-typed properties land in a per-component `_skippedProperties` array. Intentionally narrower than full scene serialization — see `.omc/research/snapshot-restore-deferred.md` (forthcoming) for the rationale.
- **`instantiate_gameobject_tree(jsonPath, parentId?, position?)`** — reverse direction. Creates new GameObjects with fresh GUIDs under `parentId` (or scene root). Property values feed back through `TypeLibrary.SetValue` with the same type coercion as `set_property`. **Atomic-or-nothing**: every newly-created GameObject is tracked, and on any failure they're all destroyed before the error returns.
- **`camera_focus_object(id)`** — frames the editor camera on a GameObject's bounds via reflection-invoked `SceneEditorSession.FrameTo(BBox)`. Falls back to selection-only on builds where `FrameTo` isn't exposed. Distinct from the existing `focus_object` which is selection-only.
- **`camera_frame_bounds({min, max})`** — same, but with an explicit world-space `BBox`.

### Added — sbox-game-dev skill (Phase D)

New local skill at `~/.claude/skills/sbox-game-dev/SKILL.md`. Triggers on `s&box`, `sbox`, `gameobject`, `editor`, `scene`, `sbox-mcp`, `claudebridge`. Contains the decision tree, 8 unimplementable tools list, common pitfalls, addon sync workflow, response envelope shape, and the discovery-before-authoring pattern.

### Deferred — `list_animations` (Phase B.1)

Dropped from v1.4.0 per the plan's probe-first criterion. Without a live editor probe of `SkinnedModelRenderer` / `Model` / `AnimGraphResource` we cannot verify a readable-names API exists. Documented in `.omc/research/phase3v3-list-animations.md` with explicit reopen criteria.

### Tool surface

- Canonical TS tools: 136 → **140** (+4: snapshot_gameobject_tree, instantiate_gameobject_tree, camera_focus_object, camera_frame_bounds)
- C# handlers: 121 → **125** (+4)
- TS-only allowlist: 15 (unchanged)
- Runtime-registered total: 171 → **175**
- JTC parity: still **48/48 (100%)**
- Wire protocol version: **v1** (new)

### Heartbeat / latency

5-sample heartbeat latency holds at v1.3.0 levels (avg ~29 ms, min ~14 ms). The shared `fs.watch` removes the per-call setInterval thrash but ping latency is dominated by the C# 20 ms timer, not Node-side polling.

### Dropped — `sendBatch` + BatchHandler

The TS `BridgeClient.sendBatch()` method is gone. Callers should use `Promise.all([bridge.send(...), bridge.send(...)])` instead — the C# bridge processes one request per editor frame, so concurrent `send`s naturally pipeline through the queue. The shared `fs.watch` demuxes responses by id without needing a special batch envelope. The `IsHandlerFailure` helper on the C# side no longer has a BatchHandler caller (it never had one in production — the comment was aspirational).

## [1.3.0] - 2026-05-14

**Editor responsiveness release: top-level error envelope migration, push-based event capture, 8 new game-dev tools across lighting/VFX/animation/navmesh/camera, ~3x lower latency.**

### Added - Top-level response envelope (Phase 0, CRITICAL)

`ProcessRequest()` now inspects handler results for failure state and propagates them to the top-level response. Previously, handlers returning `{ success: false, error: "..." }` were silently wrapped as `{ success: true, data: { success: false, ... } }`, hiding failures from the TS layer. This was a system-wide silent-failure-generator across 112 handlers.

- New shared `IsHandlerFailure(result)` method on `ClaudeBridge`. Handles 4 cases that the naive code missed: (a) `null` handler results -> `NullReferenceException`, (b) bare `{ error = "..." }` objects without `success` field, (c) `JsonElement`-typed returns, (d) anonymous-typed returns via reflection. Used by both `ProcessRequest` AND `BatchHandler` to guarantee consistent error detection.
- New `ExecuteCommand(command, params)` internal dispatch seam on `ClaudeBridge`. Single canonical path for handler lookup + exception catching. Used by both `ProcessRequest` (single requests) and the planned batch path.
- New `errorCode` field on `BridgeResponse` (TS) and all error envelopes (C#). Values: `BRIDGE_DISCONNECTED`, `BRIDGE_TIMEOUT`, `HANDLER_NOT_FOUND`, `HANDLER_ERROR`, `INVALID_PARAMS`.
- New `PingHandler` for real IPC round-trip latency measurement. Replaces the local-`status.json`-read `ping()` method (now deprecated, kept for back-compat). `measureLatency()` sends a `ping` command through the bridge and times the actual response.

**112-handler audit**: every `return Task.FromResult<object>(new { error = ... })` updated to include `success = false, errorCode = "HANDLER_ERROR"`. After Phase 0 envelope migration, these become visible top-level failures. 261 `success = false` instances now in `MyEditorMenu.cs`.

### Added - Push-based event capture (Phase 2)

New event system that surfaces editor events to the TS side via a JSON-lines file with `fs.watch` push detection.

- New `BridgeEventDispatcher` static class hooks 5 editor events: `scene.open`, `scene.play`, `scene.stop`, `scene.saved`, `hammer.selection.changed`. Each writes a JSON line to `<ipc-dir>/events.json`.
- Atomic ring buffer at 1000 entries / 500 KB. Compaction writes to `events_compact.tmp` then `File.Move(..., overwrite: true)` for atomic replacement. Node side tolerates partial reads / unparseable lines.
- Session isolation via `sessionId` GUID (regenerated on every `Initialize()` call) and monotonic `eventId` counter. Survives bridge restarts without stale-event pollution.
- New `src/transport/event-watcher.ts` reads events.json via `fs.watch` with 2s poll fallback. Tolerates Windows temp-dir `fs.watch` unreliability.
- New `get_editor_events` MCP tool (TS-only) - filter by `sinceId` / `sessionId` / `types`. Returns newest-first with limit.

### Added - 8 game-dev tools (Phase 3 v2)

All authored after API probes via `sbox_describe_type` / `sbox_search_types` confirmed the real APIs. Replaces a v1 attempt that used non-existent APIs (`SceneView.Camera`, `Game.Save/Load`, `PointLightComponent`, etc.).

- **Lighting**: `create_light` (PointLight / SpotLight / DirectionalLight via `Sandbox.Light` hierarchy - not `*LightComponent`), `set_light_properties` (color, shadows, shadowBias).
- **VFX**: `create_particle_effect` (uses `Sandbox.ParticleEffect`, NOT `ParticleSystem` which is a resource).
- **Animation**: `play_animation` (uses `SkinnedModelRenderer.Set(name, value)` to drive named anim-graph parameters; type-detects bool/int/float).
- **NavMesh**: `build_navmesh` (calls `scene.NavMesh.Generate(scene.PhysicsWorld)` async), `query_navmesh` (calls `scene.NavMesh.GetClosestPoint(position, radius)`, returns nullable Vector3).
- **Camera**: `get_editor_camera`, `set_editor_camera` (uses `scene.Camera` with fallback to `scene.GetAllComponents<CameraComponent>()` scanning - the editor scene's `Camera` property is null but an `editor_camera` GO with a `CameraComponent` exists).

**Probe research**: live findings in `.omc/research/phase3-api-probes.md`. v1 (4b01e98) was 13 tools using wrong APIs; v2 ships 8 verified tools. 5 v1 tools dropped from scope (Input - no exposed API, snapshot_scene - complex graph serialization, terrain material - project-specific, list_animations - needs deeper probe, get_runtime_errors - just use `get_console_output {severity:"error"}`).

### Changed - Transport latency

- `Timer( ReadRequestFiles, null, 500, 50 )` -> `( ..., 500, 20 )`. C# poll interval reduced from 50 ms to 20 ms.
- `bridge.measureLatency()` replaces `bridge.ping()` in `get_bridge_status`. Returns real IPC round-trip time (was local filesystem stat).

**Measured**: 5-sample ping latency goes from 60-120 ms baseline to **min 14 ms / avg 29 ms** (target was <40 ms). Driven by 20 ms C# timer + tighter request/response handling.

### Fixed - OnSelectionChanged hook

The `hammer.selection.changed` event hook crashed because `Selection` returns `IEnumerable<object>` not `IEnumerable<GameObject>`. Fixed: `sel?.OfType<GameObject>().Select(g => g.Id.ToString())`.

### Tool surface

- Canonical TS tools: 126 -> 136 (+10: 8 Phase 3 v2 + 1 ping + 1 get_editor_events)
- C# handlers: 112 -> 121 (+9: 8 Phase 3 v2 + 1 ping; get_editor_events is TS-only)
- TS-only allowlist: 14 -> 15 (+1: get_editor_events)
- Total runtime-registered: 161 -> 171
- JTC parity: still 48/48 (100%)

### Reverted - Phase 3 v1 (4b01e98)

Stripped 13 v1 tool implementations and 13 Phase 3 TS files. v1 used the following non-existent or wrong APIs:
- `PointLightComponent`/`SpotLightComponent`/`DirectionalLightComponent` (real: `PointLight`/`SpotLight`/`DirectionalLight`, no Component suffix)
- `SceneView.Camera` (not in TypeLibrary - editor camera lives in scene's `editor_camera` GameObject)
- `Game.Save`/`Game.Load` (don't exist; would need custom serialization)
- `InputSystem.GetActions` (`NativeEngine.InputSystem` is abstract with no exposed properties)
- `MapBuilder.PaintMaterial` (`MapBuilder` is project-specific, not in core engine)
- `NavMesh.Build()` (real: `scene.NavMesh.Generate(physicsWorld)`)
- `ParticleSystem` as a component (real: `ParticleEffect`; `ParticleSystem` is a resource)
- `BridgeLogTarget.GetEntries()` (wrong method name)

Lesson: any Phase 3-style "broad tool surface expansion" must follow Phase 3.0 (the "API Probe Gate") in the original plan - probe APIs live BEFORE authoring handlers. See `.omc/specs/sbox-claude-improvements-ralplan.md` for the hyperplan-audited plan structure.

## [1.2.0] - 2026-05-13

**JTC parity sprints (B.2): 14 new canonical tools + 34 JTC-compat aliases. Coverage of JTC `sbox-mcp` tool surface: 48/48 (100%). 🎉 Full parity achieved 2026-05-13. Bonus: S10 lands real console capture beyond JTC's manual-buffer approach.**

### Added — Real console capture (S10, bonus)

`get_console_output` moved from TS-only stub (one of the 9 "not implementable" tools) to a working C# handler. Captures the full editor log stream — game code, addon code, Facepunch internals — not just lines our bridge logged itself.

**Approach**: subclass `NLog.Targets.MemoryTarget` via runtime reflection on `AppDomain.CurrentDomain.GetAssemblies()` (the `NLog` namespace isn't compile-visible from sandbox code — same blocker JTC hit). Reach `LogFactory.Configuration` via `LogManager.Setup().LogFactory`, attach the target, call `ReconfigExistingLoggers()`. Mirror of our `ExecuteCSharpHandler` ScriptRunner pattern, applied to logging.

**Beyond JTC**: JTC's `ConsoleCapture` is a manual `AddEntry()` buffer (their note: "s&box Logger does not expose an OnEntry event") — it only shows lines they explicitly logged. Our `MemoryTarget` attachment intercepts every NLog entry: live-verified surfacing `[MCP Docs]`, `[engine/MaterialSystem]`, `[SboxBridge]`, controller-detect lines, etc.

**Schema**: `{ count?: number (1-500, default 50), severity?: "all"|"info"|"warning"|"error" (default "all") }`. Default NLog Layout `${longdate}|${level:uppercase=true}|${logger}|${message}` is parsed back into structured `{ timestamp, level, loggerName, message }` records.

### Fixed — Hotload zombie (cumulative discovery during S10)

`OnHotload` now calls `RegisterHandlers()` before `StartBridge()` so the static `_handlers` dict gets repopulated even when the static cctor doesn't fire. This was discovered during S10 verification: a fresh compile registered all 112 handlers correctly, then a stale assembly's `[EditorEvent.Hotload]` callback overwrote `status.json` with 111. Belt-and-suspenders defense against the zombie pattern.

### Added — Execution tools (S8)

Two new canonical tools matching JTC's `console_run` and `execute_csharp`. Mirror JTC's `ExecutionHandler` semantics exactly, including Roslyn-via-reflection scripting and graceful degradation when `Microsoft.CodeAnalysis.CSharp.Scripting` isn't loaded.

- `console_run(command)` — invokes `Sandbox.ConsoleSystem.Run`. Fire-and-forget; returns success/error synchronously. Note: s&box's editor-context sandbox gates which commands are accepted — this is an upstream restriction shared by JTC.
- `execute_csharp(code, imports?)` — Roslyn scripting via reflection on `Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript`. The assembly is loaded into the editor's AppDomain on builds where it ships; on builds where it isn't, the handler returns a structured `{ executed: false, error, note }` instead of crashing. Default imports: `System, System.Linq, System.Collections.Generic, Sandbox, Editor`. Extra imports passed comma-separated.

**Why deferred until now**: gate2 G2.3 deferred this cluster pending B.1.8 long-running-handler protocol and explicit user re-approval. Phase-1 inspection of JTC's reference implementation revealed both tools are actually synchronous (Roslyn awaits internally, ConsoleSystem.Run is fire-and-forget) and fit comfortably inside the bridge's existing 30 s timeout. **B.1.8 protocol turned out to be unnecessary for S8** — a scope reduction confirmed against JTC's `ExecutionHandler.cs`. User re-approved S8 2026-05-13.

**Implementation**: 2 new C# handlers (`ConsoleRunHandler`, `ExecuteCSharpHandler`) plus a private `ScriptRunner` reflection adapter in `MyEditorMenu.cs`. 1 new TS module `src/tools/execution.ts` registering both tools. ~200 lines C# + 100 lines TS.


### Added — s&box docs cluster (S9)

Four new canonical TS-only tools that mirror JTC's `sbox_*` docs surface. All operate server-side via Node global `fetch` against `sbox.game` — no C# handler, no bridge dependency, work even when the editor is closed.

- `sbox_search_docs(query, limit?, category?)` — TF-IDF search across cached doc pages. Field boosts match JTC: title (3.0), category (2.0), markdown body (1.0). Includes prefix matching and snippet extraction.
- `sbox_get_doc_page(url, startIndex?, maxLength?)` — fetch a cached page as Markdown, chunked for context-window safety (100–20000 chars per call, default 5000).
- `sbox_list_doc_categories` — 18 categories with page counts, derived from URL path-segment-2.
- `sbox_cache_status` — cache directory, TTL, last-refresh timestamp, page count, freshness flag.

**Source pivot from JTC**: the docs system on `docs.facepunch.com` (Outline share-id `sbox-dev`) returned 404 on `shares.info` as of 2026-05-13 — JTC's installed C# crawler is non-functional today. The new home is `sbox.game/dev/doc/*`, a Blazor Server SPA. The canonical AI-discovery file at `sbox.game/llms.txt` (declared via `Llms-Txt:` in `robots.txt`) lists all 219 doc pages with their titles. **Every doc page is fetchable as raw Markdown by appending `.md` to its URL** — no HTML scraping, no Outline API, no auth.

**Cache**: `<temp>/sbox-docs-cache/manifest.json`. Override with env `SBOX_DOCS_CACHE_DIR`. TTL 24 h (override with `SBOX_DOCS_CACHE_TTL` in seconds). First call after expiry triggers a lazy crawl (~30 s for the full 219 pages, 150 ms between fetches).

**Implementation**: ~600 lines TS across `src/docs/{cache,fetcher,search}.ts` + `src/tools/docs.ts`. Zero new dependencies — hand-rolled TF-IDF index, native `fetch`, no HTML-to-Markdown library, no `cheerio`. Coverage verified end-to-end: **219/219 pages indexed**, 18 categories, sub-millisecond search latency once cached.


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

### Tool count update (S4b + S4 closing + S9 + S8 + S10 = full B.2 + bonus)

| | Before B.2 | After S4 closing | After S9 | After S8 | After S10 |
|---|---|---|---|---|---|
| Canonical TS tools | 119 | 120 | 124 | 126 | 126 (unchanged) |
| C# handlers | 108 | 109 | 109 | 111 | **112** |
| TS-only allowlist | 11 | 11 | 15 | 15 | **14** |
| JTC-compat aliases | 29 | 34 | 34 | 34 | 34 (unchanged) |
| Runtime-registered total | 149 | 155 | 159 | 161 | 161 (unchanged) |
| JTC parity coverage | 37/48 (77.1%) | 42/48 (87.5%) | 46/48 (95.8%) | 48/48 (100%) | **48/48 (100%)** |
| Working tools beyond JTC | 0 | 0 | 0 | 0 | **1** (real log capture) |

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
