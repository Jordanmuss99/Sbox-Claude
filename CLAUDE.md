# s&box + Claude Code MCP Integration

> Let non-coders build s&box games through conversation with Claude Code.

## Status: 146 canonical TS tools / 133 C# handlers / 13 TS-only / 34 JTC-compat aliases + 1 Lou rename (= 181 runtime-registered total)

**Last updated:** 2026-05-15 (v1.5.2)
**Bridge:** File-based IPC ✅ working on main thread
**Wire protocol:** v1 (asserted on connect via status.json `protocol_version`)
**Handlers:** 133 compiled and registered (TS canonicals minus 13 TS-only = 133 paired with C#)
**JTC parity:** **48 / 48 (100%)** 🎉 — full parity 2026-05-13 (S8 + S9). **S10 bonus**: real `get_console_output` (NLog capture beyond JTC's manual-buffer approach).
**Not implementable:** 6 tools (no s&box API exists — see "Known Issues"). These overlap with the TS-only allowlist. S10 (2026-05-13) moved `get_console_output` out of this list (NLog `MemoryTarget` via reflection). v1.4.1 (2026-05-14) moved `get_compile_errors` and `get_build_status` out by parsing Roslyn diagnostics out of the same NLog buffer.

### What's new in v1.4.0 (2026-05-14)

**Transport hardening + 4 Phase 3 v3 tools + addon-sync infrastructure + sbox-game-dev skill.** See `CHANGELOG.md` for the full per-phase rundown.

- **Phase 0 (cross-cutting)**: wire-protocol versioning (`PROTOCOL_VERSION = 1`), canonical addon-sync scripts + drift detection, mock-ipc-dir test fixtures, docs:sync templating (`scripts/gen-status.mjs` + `scripts/inject-status.mjs`).
- **Phase A (transport)**: 3-strike heartbeat with `disconnect`/`reconnect` events, shared `fs.watch` demux (replaces per-call setInterval thrash; Promise.all pipelines naturally), scoped startup orphan sweep, new diagnostics fields on `get_bridge_status`, dropped dead `sendBatch()` method.
- **Phase B (tools, +4)**: `snapshot_gameobject_tree` / `instantiate_gameobject_tree` (subtree round-trip with primitive property values; atomic-or-nothing instantiate); `camera_focus_object` / `camera_frame_bounds` (reflection-invoked `SceneEditorSession.FrameTo`).
- **Phase D (skill)**: `~/.claude/skills/sbox-game-dev/SKILL.md` auto-loads on s&box prompts with decision tree + pitfalls + addon-sync workflow.
- **Deferred**: `list_animations` (Phase B.1) — see `.omc/research/phase3v3-list-animations.md` for reopen criteria.

Counts: 136 → 140 canonical TS tools, 121 → 125 C# handlers, total 171 → 175 runtime-registered.

### What's new in v1.3.0 (2026-05-14)

**Editor responsiveness release.** Three coordinated efforts shipped:

- **Phase 0 - Response envelope migration**. `ProcessRequest()` now inspects handler results and propagates `success: false` to the top-level response. Previously handler failures were silently buried in `data`, making error detection unreliable across all 112 handlers. New `IsHandlerFailure()` shared method handles null/bare-error/JsonElement/reflection cases. New `errorCode` field on all error envelopes (`BRIDGE_DISCONNECTED`/`BRIDGE_TIMEOUT`/`HANDLER_NOT_FOUND`/`HANDLER_ERROR`/`INVALID_PARAMS`). New `ExecuteCommand()` internal dispatch seam. New `PingHandler` + `bridge.measureLatency()` for real round-trip latency.
- **Phase 2 - Push-based event capture**. New `BridgeEventDispatcher` hooks 5 editor events (`scene.open`, `scene.play`, `scene.stop`, `scene.saved`, `hammer.selection.changed`) and writes to `events.json` JSON-lines file. Atomic ring buffer with `File.Move(overwrite:true)` compaction. Session ID + monotonic eventId for cross-restart isolation. New TS `get_editor_events` tool reads via `fs.watch` with 2s poll fallback.
- **Phase 3 v2 - 8 API-probe-verified tools**. After v1 (4b01e98) authored handlers against non-existent APIs, v2 probes every API via `sbox_describe_type` BEFORE writing handlers. Ships: `create_light` / `set_light_properties` (PointLight/SpotLight/DirectionalLight - not `*Component`), `create_particle_effect` (ParticleEffect - not ParticleSystem), `play_animation` (SkinnedModelRenderer.Set), `build_navmesh` / `query_navmesh` (scene.NavMesh), `get_editor_camera` / `set_editor_camera` (with fallback CameraComponent scan).

**Latency**: 60-120 ms baseline -> avg 29 ms, min 14 ms. C# timer 50 ms -> 20 ms. `get_bridge_status` now reports real IPC ping (was local `status.json` read).

**Phase 3 v2 API probe findings** are committed to `.omc/research/phase3-api-probes.md`. The corrected APIs vs v1 attempts:

| v1 (broken) | v2 (verified) |
|---|---|
| `PointLightComponent` etc. | `Sandbox.PointLight` etc. (Light base class) |
| `ParticleSystem` (as component) | `Sandbox.ParticleEffect` |
| `SceneView.Camera` | `Scene.Camera` (+ fallback scan for `CameraComponent`) |
| `Game.Save`/`Game.Load` | doesn't exist; v3 will use `Component.Serialize/Deserialize` |
| `InputSystem.GetActions` | abstract, no API; use `read_file` on `.inputactions` |
| `NavMesh.Build()` | `scene.NavMesh.Generate(scene.PhysicsWorld)` |
| `BridgeLogTarget.GetEntries()` | (deferred - use `get_console_output {severity:"error"}`) |
| `MapBuilder.PaintMaterial` | project-specific - use `invoke_button` + `set_property` |

---
### What's new in this update

- **S10 — Real console capture (bonus, beyond JTC parity)**: `get_console_output` now works for real. Subclasses `NLog.Targets.MemoryTarget` via runtime reflection on `AppDomain.CurrentDomain.GetAssemblies()` (NLog namespace is loaded at runtime but not compile-visible in s&box's sandbox — same blocker JTC hit). Surfaces the full editor log stream including `[engine/MaterialSystem]`, `[MCP Docs]`, `[SboxBridge]`, controller-detect lines. Parses NLog's default `${longdate}|${level}|${logger}|${message}` layout back into structured records. JTC's equivalent (`ConsoleCapture`) is a manual `AddEntry()` buffer and shows only lines they explicitly logged.
- **S8 — Execution tools (2 canonical, hit 100% JTC parity)**: `console_run` wraps `Sandbox.ConsoleSystem.Run`; `execute_csharp` evaluates a C# expression/block via Roslyn-via-reflection (`Microsoft.CodeAnalysis.CSharp.Scripting`). Graceful degradation when Roslyn isn't loaded in the build (returns structured `{ executed: false, error, note }`). Note: `ConsoleSystem.Run` in editor context is gated by the s&box sandbox — most user-input commands fail with "Can't run '<x>'". This is an upstream restriction shared by JTC's identical implementation. Originally deferred per gate2 G2.3 pending B.1.8 long-running-handler protocol; Phase-1 inspection revealed both tools are synchronous and fit inside the bridge's 30 s timeout, so B.1.8 was not actually required.
- **S9 — s&box docs cluster (4 TS-only tools)**: `sbox_search_docs`, `sbox_get_doc_page`, `sbox_list_doc_categories`, `sbox_cache_status`. Crawls `sbox.game/llms.txt` (219 doc pages) and caches them as raw Markdown at `<temp>/sbox-docs-cache/` with a 24 h TTL. Hand-rolled TF-IDF search, zero new npm deps. Works even when the s&box editor is closed — pure Node server-side. See CHANGELOG "Added — s&box docs cluster (S9)" and `.omc/specs/s9-spec.md` for the post-mortem (JTC's Outline-share-based docs implementation is non-functional today, prompting the pivot to `sbox.game/llms.txt`).
- **Generic component-button driver** — `invoke_button` + `list_component_buttons` work on any component with a `[Button]` attribute. Plus `set_prefab_ref` for assigning prefab GameObjects to component properties.
- **Map editing primitives** — `add_terrain_hill`, `add_terrain_clearing`, `add_terrain_trail`, `clear_terrain_features` drive a `MapBuilder` component by mutating its editable `Hills`/`Clearings`/`Trails`/`CavePath` lists and rebuilding.
- **Sculpt brushes** — `sculpt_terrain` modifies the heightmap directly with raise/lower/flatten/smooth modes.
- **Cave + forest editing** — `add_cave_waypoint`, `clear_cave_path`, `add_forest_poi`, `add_forest_trail`, `set_forest_seed`, `clear_forest_pois`, `paint_forest_density`.
- **Path placement** — `place_along_path` drops asset instances along a curve.
- **Heightmap raycast** — `raycast_terrain` returns surface height at a world XY.
- **Type discovery** — `describe_type`, `search_types`, `get_method_signature`, `find_in_project` expose `Game.TypeLibrary` reflection so Claude can look up real APIs instead of guessing.
- **Better play-mode tracking** — `start_play` uses `EditorScene.Play` with `SetPlaying` fallback; `is_playing` tracks state via `PlayState` static + Game flag + active-scene divergence (fixes the prior "start_play triggers but is_playing returns false" issue).

---

## Architecture

```
Claude Code → (stdio) → MCP Server → (file IPC) → Bridge Addon → s&box Editor
                          Node.js        %TEMP%/sbox-bridge-ipc/     C# in Editor
```

**File-based IPC** (not WebSocket/HTTP). This MCP server uses a directory-based message-passing protocol via `%TEMP%\sbox-bridge-ipc\`. File IPC was chosen for simplicity and to avoid port-conflict issues; the s&box editor process IS capable of running an `HttpListener` (as demonstrated by JTC's MCP server on port 29015), so the choice is architectural — not mandated by the sandbox. Communication uses **file-based IPC**:

1. MCP Server writes `req_<id>.json` to the temp directory
2. Bridge addon polls for request files via `System.Threading.Timer` (50ms)
3. Requests are queued and processed on the **main editor thread** (required for scene APIs)
4. Bridge writes `res_<id>.json` back
5. MCP Server polls for response files

IPC directory: `%TEMP%/sbox-bridge-ipc/` (typically `C:\Users\<user>\AppData\Local\Temp\sbox-bridge-ipc\`)

Two components:
1. **MCP Server** (`sbox-mcp-server/`) — TypeScript/Node.js, stdio transport, talks to Claude Code
2. **Bridge Addon** — C# editor library, lives in the s&box **project's Libraries folder**

---

## Critical Lessons Learned

### Addon Location
- **DO NOT** put addons in the global `sbox/addons/` folder — those are built-in only and won't compile custom code
- **DO** put addons in the project's `Libraries/` folder (e.g., `bigfoot/Libraries/claudebridge/`)
- s&box auto-scaffolds `Editor/`, `Code/`, `UnitTests/` with proper `.csproj` files when you create a library through the editor

### Compilation
- s&box compiles addons silently — if there are errors, **no log output appears** unless you check the full log
- Check `logs/sbox-dev.log` or the console for `Compile of 'local.X.editor' Failed:` messages
- The `.csproj` file is required and must reference s&box DLLs with absolute paths
- s&box generates the `.csproj` automatically when you create a library through the Library Manager

### Main Thread Requirement
- All scene manipulation APIs (`CreateObject`, `AddComponent`, `Destroy`, etc.) **must run on the main editor thread**
- `System.Threading.Timer` callbacks run on thread pool threads — NOT safe for scene APIs
- Solution: Timer reads files from disk (thread-safe), queues them, and a `[EditorEvent.Frame]` handler on a `[Dock]` widget processes them on the main thread

### Class Discovery
- s&box discovers classes via attributes like `[Menu]`, `[Dock]`, `[EditorEvent.Frame]`
- Static constructors fire when the type scanner discovers the class
- `[Event("editor.created")]` fires BEFORE custom addons load — don't rely on it
- `[Event("editor.loaded")]` does NOT exist

### s&box API Key Differences (vs. what was originally coded)
- `SceneEditorSession.Active.Scene` — the editor scene (NOT `Game.ActiveScene` which is for play mode)
- `go.AddComponent<T>()` — add component (NOT `go.Components.Create<T>()`, though ComponentList.Create also exists)
- `go.GetOrAddComponent<T>()` — get existing or add
- `go.GetComponent<T>()` — get existing
- `SceneEditorSession.Active.Selection` — editor selection (NOT `EditorScene.Selection`)
- `SceneEditorSession.Active.SetPlaying(scene)` / `.StopPlaying()` — play mode
- `SceneEditorSession.Active.FrameTo(bbox)` — focus camera on object
- `SceneEditorSession.Active.Save()` — save scene
- `Game.TypeLibrary.GetType("name")` — find types
- `Game.TypeLibrary.GetTypes<Component>()` — list all component types
- `MeshCollider` does NOT exist — use `HullCollider` instead
- `Rotation.Pitch()`, `.Yaw()`, `.Roll()` are methods, not properties

### Math & Events (s&box sandbox specifics)
- `MathX.Clamp(value, min, max)` — NOT `System.Math` or `MathF` (neither exists in s&box sandbox)
- `System.MathF` does NOT exist in s&box's C# sandbox
- `IGameEvent` / `GameObject.Dispatch()` / `Scene.Dispatch()` are from `facepunch.libevents` package, NOT base s&box
- `Networking.MaxPlayers` is **read-only** — set via lobby config, not direct assignment
- `Networking.IsHost` may throw if networking is not active — guard with try/catch or check `Networking.IsActive` first

### UTF-8 BOM (Critical IPC Bug — Fixed)
- C#'s `Encoding.UTF8` writes a BOM prefix (`EF BB BF`) at the start of files
- Node.js `JSON.parse` rejects the BOM: `Unexpected token '﻿'` — but the `catch` block in the polling loop swallowed this silently, causing every response to time out
- **Bridge fix**: Use `new UTF8Encoding(false)` for all IPC file writes (status.json, res_*.json)
- **MCP server fix**: Strip BOM with `.replace(/^\uFEFF/, "")` before `JSON.parse` as a safety net
- Both fixes are applied — belt and suspenders

### Bridge Behavior Notes
- Bridge processes **one request per editor frame** — sending many requests rapidly causes some to be consumed without response
- If game code fails to compile, the editor code (bridge) also fails (`Broken Reference: package.local.X`)
- Bridge Status menu item always works even when frame processing is broken (it's a sync call)
- The `[Dock]` widget must be **visible** for `[EditorEvent.Frame]` to fire — if closed, no requests process
- `Org` in `.sbproj` must be `"local"` for local development — only set to your org name when publishing

### API Schema
- The full s&box type schema can be downloaded as JSON from `sbox.game/api`
- It contains all types, methods, properties, and fields
- Use this as the source of truth, NOT reverse engineering from the tools addon
- Key types verified from schema: `MathX.Clamp`, `SceneEditorSession`, `NetworkHelper`, `Package.FetchAsync`, `AssetSystem.InstallAsync`, `UndoSystem.Undo/Redo`

---

## Project Structure

```
sbox-claude/
├── CLAUDE.md                          ← YOU ARE HERE
├── README.md                          ← User-facing docs
├── INSTALL.md                         ← Installation guide
├── LICENSE                            ← MIT
├── install.ps1 / install.sh           ← Legacy installers (need updating)
│
├── sbox-mcp-server/                   # MCP Server (TypeScript)
│   ├── package.json
│   ├── tsconfig.json
│   ├── src/
│   │   ├── index.ts                   # Entry point — registers all 88 tools
│   │   ├── transport/
│   │   │   └── bridge-client.ts       # File-based IPC client
│   │   └── tools/
│   │       ├── project.ts             # get_project_info, list_project_files, read_file, write_file
│   │       ├── scripts.ts             # create_script, edit_script, delete_script, trigger_hotload
│   │       ├── console.ts             # get_console_output, get_compile_errors, clear_console
│   │       ├── scenes.ts              # list_scenes, load_scene, save_scene, create_scene
│   │       ├── gameobjects.ts         # CRUD, hierarchy, selection
│   │       ├── components.ts          # get/set properties, list components, add components
│   │       ├── assets.ts              # search, list, install, info
│   │       ├── materials.ts           # assign_model, create_material, assign_material
│   │       ├── audio.ts               # list_sounds, create_sound_event, assign_sound
│   │       ├── playmode.ts            # play/stop/pause, runtime properties, screenshot, undo
│   │       ├── prefabs.ts             # create/instantiate/list/info
│   │       ├── physics.ts             # add_physics, add_collider, add_joint, raycast
│   │       ├── ui.ts                  # create_razor_ui, screen/world panels
│   │       ├── templates.ts           # player/npc/game_manager/trigger templates
│   │       ├── networking.ts          # network helpers, spawn, sync, RPC, templates
│   │       ├── publishing.ts          # project config, build, export, publish
│   │       └── status.ts              # get_bridge_status
│   └── dist/                          # Compiled JS
│
└── sbox-bridge-addon/                 # Legacy location (DO NOT USE)
    └── ...                            # Old WebSocket-based addon (non-functional)

# ACTUAL working addon location (per-project):
<s&box project>/Libraries/claudebridge/
├── claudebridge.sbproj               # Auto-generated by s&box
├── Editor/
│   ├── claudebridge.editor.csproj    # Auto-generated by s&box
│   └── MyEditorMenu.cs               # ALL bridge code — server + handlers
├── Code/
│   └── claudebridge.csproj           # Auto-generated
└── UnitTests/
    └── claudebridge.unittest.csproj  # Auto-generated
```

---

## How to Install (Current Working Method)

### Prerequisites
- s&box installed via Steam
- Node.js 18+ installed
- Claude Code installed

### Step 1: Create the Library in s&box
1. Open s&box with your project
2. Go to Library Manager
3. Create a new library called "claudebridge"
4. s&box will scaffold the folder structure

### Step 2: Copy the Bridge Code
Copy `MyEditorMenu.cs` into the `Editor/` folder of the library.

### Step 3: Build the MCP Server
```bash
cd sbox-mcp-server
npm install
npm run build
```

### Step 4: Register with Claude Code
```bash
claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
```

### Step 5: Restart s&box
- Open the "Claude Bridge" dock from View menu
- Check status: Editor → Claude Bridge → Status

---

## Verified s&box APIs (from schema + testing)

### Scene Access
```csharp
var scene = SceneEditorSession.Active?.Scene;  // Editor scene
var scene = Game.ActiveScene;                   // Play mode scene
```

### GameObject
```csharp
var go = scene.CreateObject(true);
go.Name = "My Object";
go.WorldPosition = new Vector3(x, y, z);
go.WorldRotation = Rotation.From(pitch, yaw, roll);
go.WorldScale = new Vector3(sx, sy, sz);
go.SetParent(parent, keepWorldPosition: true);
go.Enabled = false;
go.Destroy();
var clone = go.Clone();
scene.Directory.FindByGuid(guid);
scene.Directory.FindByName("name");
```

### Components
```csharp
go.AddComponent<ModelRenderer>();
go.GetComponent<ModelRenderer>();
go.GetOrAddComponent<ModelRenderer>();
go.Components.GetAll();
go.Components.Create(typeDescription);  // Dynamic type
```

### Models & Materials
```csharp
var renderer = go.GetOrAddComponent<ModelRenderer>();
renderer.Model = Model.Load("models/dev/box.vmdl");
renderer.MaterialOverride = Material.Load("path.vmat");
renderer.Tint = Color.Red;
```

### Physics
```csharp
go.AddComponent<Rigidbody>();       // Has: Gravity, MassOverride, LinearDamping, etc.
go.AddComponent<BoxCollider>();      // Has: Scale, Center, IsTrigger
go.AddComponent<SphereCollider>();   // Has: Radius, Center, IsTrigger
go.AddComponent<CapsuleCollider>(); // Has: Radius, Start, End, IsTrigger
go.AddComponent<HullCollider>();     // (NOT MeshCollider — doesn't exist)
```

### Play Mode
```csharp
Game.IsPlaying   // bool
Game.IsPaused    // bool
SceneEditorSession.Active.SetPlaying(scene);
SceneEditorSession.Active.StopPlaying();
```

### Editor Selection
```csharp
SceneEditorSession.Active.Selection.Set(go);
SceneEditorSession.Active.Selection.Add(go);
SceneEditorSession.Active.Selection.Clear();
SceneEditorSession.Active.FrameTo(go.GetBounds());  // Focus camera
```

### TypeLibrary
```csharp
Game.TypeLibrary.GetType("ModelRenderer");          // TypeDescription
Game.TypeLibrary.GetTypes<Component>();              // All component types
// TypeDescription: .Name, .Title, .Description, .Properties, .IsAbstract, .FullName
```

### Project
```csharp
Project.Current.GetRootPath();
Project.Current.GetAssetsPath();
Project.Current.Config.Title / .Org / .Ident / .Type
```

---

## Known Issues / TODO

- [x] ~~Parameter name alignment~~ — Fixed, all 78 handlers use correct MCP param names
- [x] ~~get_scene_hierarchy empty~~ — Fixed, removed erroneous Parent==null filter
- [x] ~~Old WebSocket code~~ — Removed, ws dependency dropped
- [x] ~~`start_play` triggers but `is_playing` returns false~~ — Fixed via `EditorScene.Play` + manual `PlayState` tracking
- [ ] `add_sync_property` can't add new properties, only annotate existing ones
- [ ] `set_material_property` requires MaterialOverride to be set first
- [ ] Install process could be simplified (single-file copy)
- [ ] Bridge addon is project-specific — needs packaging for distribution
- [ ] 9 tools not implementable: pause_play, resume_play, get_console_output, get_compile_errors, clear_console, build_project, get_build_status, clean_build, prepare_publish (no s&box API)
- [ ] Consider publishing addon to s&box Asset Library
- [ ] Map-edit tools assume the project has `MapBuilder`/`CaveBuilder`/`ForestGenerator`-shaped components. `invoke_button` works on any project; the named convenience tools require those components or compatible ones.

---

## Development

```bash
# Build MCP Server
cd sbox-mcp-server && npm install && npm run build

# The Bridge Addon is compiled automatically by s&box
# Just edit MyEditorMenu.cs and restart s&box

# Test IPC manually:
echo '{"id":"test","command":"get_project_info","params":{}}' > %TEMP%/sbox-bridge-ipc/req_test.json
# Check response:
cat %TEMP%/sbox-bridge-ipc/res_test.json
```

## Alias / Rename Migration Tables

This MCP server registers two categories of alias names that forward to canonical tools via `bridge.send(<canonical>, args)`. Each alias emits a one-shot deprecation warning to stderr on first call per process. The wire-level command name sent to the C# bridge is ALWAYS the canonical name — the C# side has no knowledge of aliases.

### JTC-Compat Aliases

JTC tool names (from `sbox.game/jtc/mcp-server`) registered as aliases that forward to Lou canonical tools. Warning text: `[sbox-mcp] tool '<jtc>' is a JTC-compat alias; canonical name is '<lou>'`.

| JTC alias (deprecated) | Canonical Lou tool | Added |
|---|---|---|
| `editor_undo` | `undo` | B.1.4 (2026-05-12) |
| `editor_redo` | `redo` | B.1.5 (2026-05-12) |
| `editor_save_scene` | `save_scene` | B.1.6 (2026-05-12) |
| `editor_take_screenshot` | `take_screenshot` | B.1.7 (2026-05-12) |
| `editor_get_selection` | `get_selected_objects` | S1 (2026-05-12) |
| `editor_is_playing` | `is_playing` | S1 (2026-05-12) |
| `editor_play` | `start_play` | S1 (2026-05-12) |
| `editor_select_object` | `select_object` | S1 (2026-05-12) |
| `editor_stop` | `stop_play` | S1 (2026-05-12) |
| `file_read` | `read_file` | S2 (2026-05-12) |
| `file_write` | `write_file` | S2 (2026-05-12) |
| `project_info` | `get_project_info` | S2 (2026-05-12) |
| `scene_clone_object` | `duplicate_gameobject` | S2 (2026-05-12) |
| `scene_create_object` | `create_gameobject` | S2 (2026-05-12) |
| `scene_delete_object` | `delete_gameobject` | S2 (2026-05-12) |
| `scene_get_hierarchy` | `get_scene_hierarchy` | S2 (2026-05-12) |
| `scene_load` | `load_scene` | S2 (2026-05-12) |
| `scene_reparent_object` | `set_parent` | S2 (2026-05-12) |
| `scene_set_transform` | `set_transform` | S2 (2026-05-12) |
| `asset_mount` | `asset_install_pinned` | S3 (2026-05-12) |
| `asset_search` | `list_asset_library` *(adapter: `amount`→`maxResults`, default 10)* | S3 (2026-05-12) |
| `component_add` | `add_component_with_properties` *(adapter: `objectId`→`id`, `componentType`→`component`)* | S3 (2026-05-12) |
| `component_set` | `set_property` *(adapter: `objectId`→`id`, `componentType`→`component`)* | S3 (2026-05-12) |
| `sbox_search_api` | `search_types` *(adapter: `query`→`pattern`)* | S3 (2026-05-12) |
| `file_list` | `list_project_files` *(adapter: `dir`→`path`)* | S2 catch-up (2026-05-12) |
| `editor_console_output` | `get_console_output` *(forwards via bridge; canonical lacks C# handler today — both broken until LogCapture lands)* | S1 catch-up (2026-05-12) |

*Deferred*: `get_server_status → get_bridge_status` (canonical is genuinely TS-local; needs `localHandler` infra in registerAlias); `sbox_get_api_type → describe_type` (JTC adds `startIndex`/`maxLength`; needs Lou-side Zod schema verification before deciding adapter vs alias). See `PENDING_ALIAS_REQUIREMENTS` in `jtc-aliases.ts`.

Registry: `sbox-mcp-server/src/tools/jtc-aliases.ts` → `JTC_ALIASES`.

### Lou-Internal Renames

Old Lou canonical names that were renamed in a B.1.11 / D5 inversion. The old names continue to resolve via alias for a deprecation cycle. Warning text: `[sbox-mcp] tool '<old>' was renamed to '<new>'; please update your callers`.

| Old name (deprecated) | New canonical name | Renamed in |
|---|---|---|
| `install_asset` | `asset_install_pinned` | B.1.11 (2026-05-12) |

Registry: `sbox-mcp-server/src/tools/jtc-aliases.ts` → `LOU_RENAMES`.

### Adding a new alias

1. Decide kind: JTC-compat (foreign name) or lou-rename (internal rename).
2. Add entry to the matching registry in `jtc-aliases.ts`.
3. Add a row to the appropriate table above.
4. Run `npm test` — the parity test enforces (a) canonical target exists and has a C# handler, (b) no name collisions, (c) no dispatch chains, (d) no key in both registries.
5. Run `npm run smoke:aliases` — verifies the alias registers at runtime via the spawned MCP server.

### Removing an alias

Do NOT delete without a deprecation cycle. Downstream consumers may still depend on the old name. Document the planned removal in this file with a target date first.

See `.omc/specs/jtc-vs-lousputthole-matrix.v2.md` for the full implementation status of all 48 JTC tools.

