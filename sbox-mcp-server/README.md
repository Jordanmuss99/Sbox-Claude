# sbox-mcp-server

MCP Server for the s&box game engine. Lets Claude Code build s&box games through conversation — 88 tools for scenes, scripts, GameObjects, components, assets, materials, audio, physics, UI, networking, publishing, and more.

## Quick Start

### 1. Install the Bridge Addon in s&box

The Bridge Addon runs inside the s&box editor and receives commands from this MCP server.

**From the s&box Asset Library (easiest):**
- Open s&box editor > Asset Library > search "Claude Bridge" > Install

**From source:**
```bash
git clone https://github.com/lousputthole/sbox-claude.git
cd sbox-claude
# Windows:
.\install.ps1
# Linux:
./install.sh
```

### 2. Connect to Claude Code

```bash
claude mcp add sbox -- npx sbox-mcp-server
```

That's it. Start s&box, open Claude Code, and start building.

## How It Works

```
Claude Code --> (stdio) --> sbox-mcp-server --> (WebSocket :29015) --> Bridge Addon --> s&box Editor
```

This package is the middle piece. It translates Claude's tool calls into WebSocket messages that the Bridge Addon (running inside s&box) executes against the engine APIs.

## Tools (88 total)

| Category | Tools |
|----------|-------|
| **Project** | get_project_info, list_project_files, read_file, write_file |
| **Scripts** | create_script, edit_script, delete_script, trigger_hotload |
| **Console** | get_console_output, get_compile_errors, clear_console |
| **Scenes** | list_scenes, load_scene, save_scene, create_scene |
| **GameObjects** | create/delete/duplicate/rename, set_parent/enabled/transform |
| **Components** | get/set_property, get_all_properties, list_available, add_component |
| **Hierarchy** | get_scene_hierarchy, get/select/focus_object |
| **Assets** | search_assets, list_asset_library, install_asset, get_asset_info |
| **Materials** | assign_model, create/assign_material, set_material_property |
| **Audio** | list_sounds, create_sound_event, assign_sound, play_sound_preview |
| **Play Mode** | start/stop/pause/resume_play, is_playing |
| **Runtime** | get/set_runtime_property, take_screenshot |
| **Editor** | undo, redo |
| **Prefabs** | create/instantiate_prefab, list_prefabs, get_prefab_info |
| **Physics** | add_physics, add_collider, add_joint, raycast |
| **UI** | create_razor_ui, add_screen_panel, add_world_panel |
| **Templates** | create_player/npc_controller, create_game_manager, create_trigger_zone |
| **Networking** | network_helper, configure/status, spawn, ownership, sync, RPCs, templates |
| **Publishing** | project_config, validate, build, clean_build, export, thumbnail, publish |
| **Status** | get_bridge_status |

## Configuration

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `SBOX_BRIDGE_HOST` | `127.0.0.1` | Bridge WebSocket host |
| `SBOX_BRIDGE_PORT` | `29015` | Bridge WebSocket port |

Custom port example:
```bash
claude mcp add sbox --env SBOX_BRIDGE_PORT=29016 -- npx sbox-mcp-server
```

## Requirements

- **Node.js 18+**
- **s&box** with the Bridge Addon installed
- **Claude Code**

## License

MIT
