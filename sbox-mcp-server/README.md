# sbox-mcp-server

MCP Server for the s&box game engine. Lets Claude Code build s&box games through conversation — 78 working tools for scenes, scripts, GameObjects, components, assets, materials, audio, physics, UI, networking, publishing, and more.

## Quick Start

### 1. Install the Bridge Addon in s&box

The Bridge Addon runs inside the s&box editor and receives commands from this MCP server.

**From source:**
```bash
git clone https://github.com/lousputthole/sbox-claude.git
```

Then in s&box:
1. Open your project in the s&box editor
2. Go to **Library Manager** and create a new library called **"claudebridge"**
3. Copy `sbox-bridge-addon/Editor/MyEditorMenu.cs` into the library's `Editor/` folder
4. Restart s&box

### 2. Build the MCP Server

```bash
cd sbox-claude/sbox-mcp-server
npm install
npm run build
```

### 3. Connect to Claude Code

```bash
claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
```

### 4. Open the Bridge Dock

In s&box, go to **View > Claude Bridge** to open the dock panel. The dock **must be visible** for commands to be processed.

That's it. Start Claude Code and start building.

## How It Works

```
Claude Code --> (stdio) --> sbox-mcp-server --> (file IPC) --> Bridge Addon --> s&box Editor
```

Communication uses **file-based IPC** through `%TEMP%/sbox-bridge-ipc/`. The MCP server writes request JSON files, the Bridge Addon (running inside s&box) polls for them, processes on the main editor thread, and writes response files back.

WebSocket is not used — s&box's sandboxed C# environment does not allow `System.Net`.

## Tools (78 working, 89 defined)

| Category | Tools |
|----------|-------|
| **Project** | get_project_info, list_project_files, read_file, write_file |
| **Scripts** | create_script, edit_script, delete_script, trigger_hotload |
| **Scenes** | list_scenes, load_scene, save_scene, create_scene |
| **GameObjects** | create/delete/duplicate/rename, set_parent/enabled/transform |
| **Components** | get/set_property, get_all_properties, list_available, add_component |
| **Hierarchy** | get_scene_hierarchy, get/select/focus_object |
| **Assets** | search_assets, list_asset_library, install_asset, get_asset_info |
| **Materials** | assign_model, create/assign_material, set_material_property |
| **Audio** | list_sounds, create_sound_event, assign_sound, play_sound_preview |
| **Play Mode** | start/stop_play, is_playing |
| **Runtime** | get/set_runtime_property, take_screenshot |
| **Editor** | undo, redo |
| **Prefabs** | create/instantiate_prefab, list_prefabs, get_prefab_info |
| **Physics** | add_physics, add_collider, add_joint, raycast |
| **UI** | create_razor_ui, add_screen_panel, add_world_panel |
| **Templates** | create_player/npc_controller, create_game_manager, create_trigger_zone |
| **Networking** | network_helper, configure/status, spawn, ownership, sync, RPCs, templates |
| **Publishing** | project_config, validate, thumbnail, package_details, install_asset |
| **Status** | get_bridge_status |

### Not implementable (no s&box API)

pause_play, resume_play, get_console_output, get_compile_errors, clear_console, build_project, get_build_status, clean_build, export_project, prepare_publish

## Requirements

- **Node.js 18+**
- **s&box** with the Bridge Addon installed
- **Claude Code**

## License

**GPL-3.0** — see [LICENSE](../LICENSE) for details.

Copyright (c) 2026 [sboxskins.gg](https://sboxskins.gg)
