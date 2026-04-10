# s&box + Claude Code MCP Integration

> Let non-coders build s&box games through conversation with Claude Code.

## Status: 78 of 89 Tools Working

**Last updated:** 2026-04-10
**Bridge:** File-based IPC ✅ working on main thread
**Handlers:** 78 compiled and registered (of 89 MCP tools defined)
**Tested & passing:** 44 handlers across all categories
**Not implementable:** 11 tools (no s&box API exists)

---

## Architecture

```
Claude Code → (stdio) → MCP Server → (file IPC) → Bridge Addon → s&box Editor
                          Node.js        %TEMP%/sbox-bridge-ipc/     C# in Editor
```

**NOT WebSocket.** s&box's sandboxed C# environment does not allow `System.Net` (HttpListener, WebSocket, TcpListener). Communication uses **file-based IPC**:

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

### API Schema
- The full s&box type schema can be downloaded as JSON from `sbox.game/api`
- It contains all types, methods, properties, and fields
- Use this as the source of truth, NOT reverse engineering from the tools addon

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
- [ ] `start_play` triggers but `is_playing` returns false — SetPlaying API may need different approach
- [ ] `add_sync_property` can't add new properties, only annotate existing ones
- [ ] `set_material_property` requires MaterialOverride to be set first
- [ ] Install process could be simplified (single-file copy)
- [ ] Bridge addon is project-specific — needs packaging for distribution
- [ ] 11 tools not implementable: pause_play, resume_play, get_console_output, get_compile_errors, clear_console, build_project, get_build_status, clean_build, export_project, prepare_publish (no s&box API)
- [ ] Consider publishing addon to s&box Asset Library

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
