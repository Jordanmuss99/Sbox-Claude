# Sbox-Claude

Let non-coders build s&box games through conversation with Claude Code.

## What This Does

Claude Code connects to the s&box editor in real-time. You describe what you want — Claude writes the C# scripts, builds the scenes, reads the console errors, and iterates until it works. s&box hotloads everything instantly.

```
You: "Make me a horror game where I explore an abandoned hospital with a flashlight"
Claude: *creates scripts, builds scene, configures lighting, adds player controller*
```

## Architecture

```
┌──────────────┐     stdio      ┌───────────────┐   WebSocket    ┌──────────────┐
│  Claude Code │ ◄────────────► │  MCP Server   │ ◄────────────► │ Bridge Addon │
│              │                │  (Node.js)    │    :29015      │  (in s&box)  │
└──────────────┘                └───────────────┘                └──────┬───────┘
                                                                       │
                                                                       ▼
                                                                ┌──────────────┐
                                                                │ s&box Editor │
                                                                │  (Source 2)  │
                                                                └──────────────┘
```

## Setup

### 1. Install the Bridge Addon in s&box

Copy the `sbox-bridge-addon/` folder into your s&box addons directory. When s&box loads, it will compile the addon and start the WebSocket server on port 29015.

### 2. Build the MCP Server

```bash
cd sbox-mcp-server
npm install
npm run build
```

### 3. Connect Claude Code

```bash
claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
```

Or add to your Claude Code MCP config:

```json
{
  "mcpServers": {
    "sbox": {
      "command": "node",
      "args": ["/path/to/sbox-mcp-server/dist/index.js"],
      "env": {
        "SBOX_BRIDGE_HOST": "127.0.0.1",
        "SBOX_BRIDGE_PORT": "29015"
      }
    }
  }
}
```

### 4. Start Building

Open s&box, open a project, and start talking to Claude:

```
"Create a first-person player controller with WASD movement and mouse look"
"Add a flashlight to the player that toggles with F"
"What compile errors are there? Fix them"
"Create a new scene called level_01 with a camera and lights"
```

## Available Tools (Phase 1)

| Category | Tools |
|----------|-------|
| **Project** | `get_project_info`, `list_project_files`, `read_file`, `write_file` |
| **Scripts** | `create_script`, `edit_script`, `delete_script`, `trigger_hotload` |
| **Console** | `get_console_output`, `get_compile_errors`, `clear_console` |
| **Scenes** | `list_scenes`, `load_scene`, `save_scene`, `create_scene` |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `SBOX_BRIDGE_HOST` | `127.0.0.1` | Bridge WebSocket host |
| `SBOX_BRIDGE_PORT` | `29015` | Bridge WebSocket port |

## Roadmap

- **Phase 1** ✅ Foundation — project awareness, scripts, console, scenes
- **Phase 2** 🔲 Scene Building — GameObject lifecycle, components, hierarchy
- **Phase 3** 🔲 Assets — asset browser, materials, models, audio
- **Phase 4** 🔲 Play & Test — play mode, runtime debugging, screenshots
- **Phase 5** 🔲 Game Logic — prefabs, template generators
- **Phase 6** 🔲 Multiplayer — networking, RPCs, local testing
- **Phase 7** 🔲 Publishing — build, export, Steam Workshop

## License

MIT
