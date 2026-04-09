# CLAUDE.md — s&box MCP Bridge

## Architecture

```
Claude AI (MCP Client)
      ↕  stdio JSON-RPC
sbox-mcp-server  (Node.js / TypeScript)
      ↕  WebSocket  ws://localhost:8765
Claude Bridge Plugin  (C# / s&box Editor)
      ↕  s&box Engine APIs
Scene / GameObjects / Components
```

Two parts work together:

1. **MCP Server** (`sbox-mcp-server/`) — A Node.js process Claude connects to via
   stdio. It speaks the Model Context Protocol and forwards every tool call to s&box
   over WebSocket.

2. **s&box Plugin** (`sbox-plugin/`) — A C# editor plugin that runs a WebSocket
   server inside the s&box editor. It receives JSON commands and dispatches them to
   registered handlers that call the s&box engine APIs.

---

## Project Structure

```
sbox-plugin/
├── .addon                              # s&box addon metadata
├── sbox-plugin.csproj                  # C# project (Sandbox.Game.Sdk)
├── BridgeServer.cs                     # WebSocket server + request routing
├── LogCapture.cs                       # Captures s&box log messages
└── Handlers/
    ├── IToolHandler.cs                 # Handler interface
    ├── GetConsoleOutputHandler.cs      # get_console_output
    ├── CreateGameObjectHandler.cs      # create_gameobject
    ├── DeleteGameObjectHandler.cs      # delete_gameobject
    ├── SetTransformHandler.cs          # set_transform
    ├── GetSceneHierarchyHandler.cs     # get_scene_hierarchy
    ├── GetAllPropertiesHandler.cs      # get_all_properties
    └── AddComponentWithPropertiesHandler.cs  # add_component_with_properties

sbox-mcp-server/
├── package.json
├── tsconfig.json
├── jest.config.json
├── .eslintrc.json
├── .prettierrc
├── src/
│   ├── index.ts                        # Entry point + MCP server setup
│   ├── BridgeClient.ts                 # WebSocket client (reconnect + ping)
│   └── tools/
│       ├── console.ts                  # Console / logging tools
│       ├── gameobjects.ts              # Scene-building tools
│       └── status.ts                   # Bridge health / status tool
└── tests/
    └── BridgeClient.test.ts            # WebSocket reconnect tests

examples/
└── horror-game/
    ├── CLAUDE.md                       # Example project context file
    ├── PlayerController.cs             # Starter player controller
    └── horror-game.scene               # Minimal scene JSON

CLAUDE.md          ← you are here
CONTRIBUTING.md
README.md
```

---

## Wire Protocol

Every WebSocket message is UTF-8 JSON. Messages stay under 1 MB.

**Request** (MCP Server → s&box Plugin):

```json
{ "id": "550e8400-e29b-41d4-a716", "command": "create_gameobject", "params": { "name": "Cube" } }
```

**Success response** (s&box Plugin → MCP Server):

```json
{ "id": "550e8400-e29b-41d4-a716", "result": { "guid": "abc123", "name": "Cube" } }
```

**Error response**:

```json
{ "id": "550e8400-e29b-41d4-a716", "error": { "code": "UNKNOWN_COMMAND", "message": "Unknown command: foo" } }
```

Error codes used by `BridgeServer.cs`:

| Code | Meaning |
|------|---------|
| `INVALID_REQUEST` | Missing id/command, malformed JSON, or message >1 MB |
| `UNKNOWN_COMMAND` | No handler registered for the command name |
| `HANDLER_ERROR` | Handler threw an exception |

---

## How to Add a New Tool

### Step 1 — Create the C# handler

Create `sbox-plugin/Handlers/MyToolHandler.cs`:

```csharp
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxClaude;

public class MyToolHandler : IToolHandler
{
    // Must exactly match the MCP tool name used in TypeScript
    public string Command => "my_tool";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        // Read parameters safely
        var value = parameters.TryGetProperty("my_param", out var p)
            ? p.GetString()
            : "default";

        // Use s&box APIs here
        // Scene.Active, new GameObject(), TypeLibrary, etc.

        // Return any JSON-serializable object
        return Task.FromResult<object>(new { success = true, value });
    }
}
```

### Step 2 — Register the handler in BridgeServer.cs

In `RegisterHandlers()`, add one line:

```csharp
private void RegisterHandlers()
{
    var handlers = new IToolHandler[]
    {
        new GetConsoleOutputHandler(),
        new CreateGameObjectHandler(),
        // ... existing handlers ...
        new MyToolHandler(),   // <-- add here
    };
    foreach (var h in handlers)
        _handlers[h.Command] = h;
}
```

### Step 3 — Add the MCP tool definition

In `sbox-mcp-server/src/tools/` (add to an existing file or create a new one):

```typescript
import type { Tool } from '@modelcontextprotocol/sdk/types.js';
import type { BridgeClient } from '../BridgeClient.js';

export const MY_TOOL: Tool = {
  name: 'my_tool',
  description:
    'One sentence. Be specific — Claude uses this to decide when to call the tool.',
  inputSchema: {
    type: 'object',
    properties: {
      my_param: {
        type: 'string',
        description: 'What this parameter does',
      },
    },
    required: ['my_param'],
  },
};

export async function handleMyTool(
  args: Record<string, unknown>,
  bridge: BridgeClient
): Promise<unknown> {
  return bridge.send('my_tool', args);
}
```

### Step 4 — Wire it into index.ts

```typescript
// At the top of src/index.ts, add the import:
import { MY_TOOL, handleMyTool } from './tools/mytools.js';

// Add to the allTools array:
const allTools: Tool[] = [
  ...CONSOLE_TOOLS,
  ...GAMEOBJECT_TOOLS,
  ...STATUS_TOOLS,
  MY_TOOL,          // <-- add here
];

// Add a case to the dispatch switch:
} else if (name === 'my_tool') {
  result = await handleMyTool(args, bridge);
}
```

That's it. Four files touched, usually only one new file created.

---

## s&box API Quick Reference

```csharp
// ── Scene ─────────────────────────────────────────────────────────────────
Scene.Active                            // the running scene
Scene.Active.GetAllObjects(false)       // flat list of all GOs (false = include inactive)
Scene.Active.Children                   // root-level GameObjects only

// ── GameObjects ───────────────────────────────────────────────────────────
var go = new GameObject(true, "Name");  // create enabled GO
go.Parent = Scene.Active;               // attach to scene root
go.Parent = otherGo;                    // or to another GO
go.Destroy();                           // remove from scene
go.Id                                   // Guid (use .ToString() for JSON)
go.Enabled                              // bool
go.Name                                 // string

// ── Transform ─────────────────────────────────────────────────────────────
go.WorldPosition = new Vector3(x, y, z);
go.WorldRotation = Rotation.From(pitch, yaw, roll);  // degrees
go.WorldScale    = new Vector3(x, y, z);
go.LocalPosition / go.LocalRotation / go.LocalScale  // parent-relative

// ── Components ────────────────────────────────────────────────────────────
go.Components.Create(typeDesc);         // add component by TypeDescription
go.Components.GetAll();                 // IEnumerable<Component>
go.Components.Get<T>();                 // first component of type T

// ── TypeLibrary ───────────────────────────────────────────────────────────
var td = TypeLibrary.GetType("Rigidbody");   // TypeDescription by name
td.Properties                                // IEnumerable<PropertyDescription>
pd.GetValue(instance)                        // read a property value
pd.SetValue(instance, value)                 // write a property value
pd.HasAttribute<PropertyAttribute>()        // check for [Property]

// ── Logging ───────────────────────────────────────────────────────────────
Log.Info("message");
Log.Warning("message");
Log.Error("message");
```

---

## Environment Variables

| Variable    | Default     | Description                |
|-------------|-------------|----------------------------|
| `SBOX_HOST` | `localhost` | Host where s&box is running |
| `SBOX_PORT` | `8765`      | Bridge WebSocket port       |

---

## Running Locally

```bash
# 1. Install and load the s&box plugin
#    Copy sbox-plugin/ into your s&box addons folder and restart the editor.
#    The bridge starts automatically and logs: [Claude Bridge] Listening on port 8765

# 2. Build and start the MCP server
cd sbox-mcp-server
npm install
npm run build
npm start

# 3. Connect Claude Code
#    Add the MCP server to your Claude Code config:
#    claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
```

## Testing Without s&box

The test suite uses a mock WebSocket server (no s&box required):

```bash
cd sbox-mcp-server
npm test
```

See `tests/BridgeClient.test.ts` and `tests/tools.test.ts` for examples of
how to mock the bridge for integration tests of your own handlers.

---

## Troubleshooting

### Bridge won't connect

**Symptom:** `get_bridge_status` returns `{ connected: false }` or the MCP server
logs `[Bridge] Error: connect ECONNREFUSED`.

Checklist:
1. Is the s&box editor open? The bridge only starts when the editor is running.
2. Is the Claude Bridge addon installed?
   - Drop `sbox-plugin/` into your s&box addons folder.
   - Restart the editor.
   - Check the editor console for `[Claude Bridge] Listening on port 8765`.
3. Is the port free? Run `lsof -i :8765` (macOS/Linux) or `netstat -an | findstr 8765`
   (Windows) to check if something else is bound there. Override with `SBOX_PORT`.
4. Is the host right? If s&box is on a different machine, set `SBOX_HOST`.
5. Is a firewall or VPN blocking `localhost:8765`?

---

### Commands time out

**Symptom:** Tool calls return `Error: Request timed out after 10000ms`.

Checklist:
1. Run `get_bridge_status` — if `connected: false`, see "Bridge won't connect".
2. Is the s&box editor frozen or showing a blocking dialog? Dismiss it.
3. Is the handler throwing silently? Check the editor console for
   `[Claude Bridge] Handler error` lines.
4. For long-running operations (large scenes), increase `requestTimeout` in
   `BridgeClient` constructor or via a wrapper.

---

### Compile errors after a script edit

**Symptom:** Claude adds or changes a C# file, then s&box shows compile errors in the
console and components stop working.

Workflow:
1. Call `get_console_output` with `{ "severity": "error" }` to read the errors.
2. Fix the errors in the affected `.cs` file(s).
3. s&box recompiles automatically on save — watch for
   `[Compiler] Compilation succeeded` in the console.
4. If the error is in generated TypeLibrary metadata, try restarting the editor.

Common mistakes Claude makes:
- Using a type that doesn't exist in the current SDK version.
- Calling `new GameObject()` without setting `Parent` (object is created but
  immediately garbage-collected).
- Missing `using Sandbox;` at the top of the file.

---

### Scene hierarchy is empty

**Symptom:** `get_scene_hierarchy` returns `{ objects: [] }`.

Checklist:
1. Is a scene actually open? Check the s&box Scene panel. If no scene is loaded,
   open one via `File > Open Scene`.
2. Is the scene saved? Unsaved new scenes may not be recognized as "active".
3. Is `Scene.Active` null? This can happen if you query before the editor has fully
   loaded the scene. Wait a moment and retry.

---

### Play-mode tools fail immediately

**Symptom:** `start_play`, `get_runtime_property`, etc. return
`HANDLER_ERROR: No active scene` or crash the editor.

Checklist:
1. Ensure a scene is open *and* saved before entering play mode.
2. Check that your scene has a `GameManager` or equivalent boot object.
   Without one, s&box may fail to initialize.
3. If `get_runtime_property` throws `"This tool only works in play mode"`,
   call `is_playing` first — the editor may have stopped due to a compile error.

---

### Screenshot is a 1×1 placeholder

**Symptom:** `take_screenshot` succeeds but the PNG is tiny.

The `TakeScreenshotHandler.cs` ships with a placeholder PNG until the real
`EditorScene.Camera.RenderToTexture()` call is wired up (see `TODO` comment in
that file). Follow the API NOTE in the handler and replace the stub with the
correct render call for your SDK version.
