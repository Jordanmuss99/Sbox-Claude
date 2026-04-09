# s&box + Claude Code MCP Integration

## What This Is
A two-part system that lets Claude Code interact with the s&box game engine in real-time:
- **MCP Server** (`sbox-mcp-server/`) — TypeScript, runs as stdio MCP server for Claude Code
- **Bridge Addon** (`sbox-bridge-addon/`) — C#, runs inside the s&box editor, exposes WebSocket API

## Architecture
```
Claude Code → (stdio) → MCP Server → (WebSocket :29015) → Bridge Addon → s&box Editor
```

## Project Structure
```
sbox-mcp-server/           # MCP Server (TypeScript/Node.js)
  src/
    index.ts               # Entry point — registers tools, starts stdio transport
    transport/
      bridge-client.ts     # WebSocket client to s&box Bridge
    tools/
      project.ts           # get_project_info, list_project_files, read_file, write_file
      scripts.ts           # create_script, edit_script, delete_script, trigger_hotload
      console.ts           # get_console_output, get_compile_errors, clear_console
      scenes.ts            # list_scenes, load_scene, save_scene, create_scene

sbox-bridge-addon/         # s&box Bridge Addon (C#)
  Code/
    Core/
      BridgeAddon.cs       # Entry point — registers handlers, starts server
      BridgeServer.cs      # WebSocket server on port 29015
      ICommandHandler.cs   # Interface for all command handlers
      LogCapture.cs        # Hooks into s&box logger for console capture
    Commands/
      *Handler.cs          # One handler per MCP tool (15 total in Phase 1)
```

## Available MCP Tools (Phase 1)

### Project Awareness
- `get_project_info` — project path, name, type, dependencies
- `list_project_files` — browse file tree with extension/directory filter
- `read_file` — read any project file
- `write_file` — create/overwrite project files

### Script Management
- `create_script` — generate C# component with boilerplate or raw content
- `edit_script` — find/replace, insert, append, delete lines
- `delete_script` — remove script files
- `trigger_hotload` — force s&box to recompile scripts

### Console & Errors
- `get_console_output` — read log entries (info/warning/error)
- `get_compile_errors` — get compilation diagnostics with file/line info
- `clear_console` — reset log buffer

### Scene Operations
- `list_scenes` — all .scene files in project
- `load_scene` — open scene in editor
- `save_scene` — save current scene
- `create_scene` — new scene with optional default objects

## Development Commands
```bash
cd sbox-mcp-server
npm install        # install dependencies
npm run build      # compile TypeScript
npm start          # run MCP server
```

## Conventions
- Bridge Addon handlers: one class per command in `Code/Commands/`
- MCP tools: grouped by domain in `src/tools/`
- All file paths in tools are relative to the s&box project root
- Security: all file operations validate paths stay within project directory
- WebSocket default port: 29015
