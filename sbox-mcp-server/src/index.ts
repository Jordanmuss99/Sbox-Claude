/**
 * sbox-mcp-server entry point.
 *
 * Implements Tasks 12 (--help / --version flags) and wires together the full
 * MCP server, BridgeClient, and all registered tools.
 *
 * Usage:
 *   node dist/index.js [--help | --version]
 *
 * Environment:
 *   SBOX_HOST  (default: localhost)
 *   SBOX_PORT  (default: 8765)
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  type Tool,
} from '@modelcontextprotocol/sdk/types.js';

import { BridgeClient } from './BridgeClient.js';
import { CONSOLE_TOOLS, handleConsoleTool } from './tools/console.js';
import { GAMEOBJECT_TOOLS, handleGameObjectTool } from './tools/gameobjects.js';
import { PLAYMODE_TOOLS, handlePlayModeTool } from './tools/playmode.js';
import { COMPONENT_TOOLS, handleComponentTool } from './tools/components.js';
import { EDITOR_TOOLS, handleEditorTool } from './tools/editor.js';
import { STATUS_TOOLS, handleStatusTool } from './tools/status.js';

// ── CLI flags (Task 12) ───────────────────────────────────────────────────────

const __dirname = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);

if (args.includes('--version')) {
  const pkg = JSON.parse(
    readFileSync(join(__dirname, '../package.json'), 'utf8'),
  ) as { version: string };
  process.stdout.write(pkg.version + '\n');
  process.exit(0);
}

if (args.includes('--help')) {
  process.stdout.write(`sbox-mcp-server — MCP bridge for Claude AI ↔ s&box

USAGE
  node dist/index.js [options]

OPTIONS
  --help      Print this help message and exit
  --version   Print the version from package.json and exit

DESCRIPTION
  Connects to the Claude Bridge plugin running inside the s&box editor via
  WebSocket and exposes s&box engine capabilities as Model Context Protocol
  tools that Claude can call.

  The server communicates with Claude via stdin/stdout (stdio transport).

ENVIRONMENT VARIABLES
  SBOX_HOST   Hostname where s&box is running  (default: localhost)
  SBOX_PORT   WebSocket port of the bridge      (default: 8765)

TOOLS EXPOSED
  get_console_output              Read s&box editor log output
  create_gameobject               Create a new scene object
  delete_gameobject               Remove a scene object
  set_transform                   Move/rotate/scale an object
  get_scene_hierarchy             Inspect the full scene tree
  get_all_properties              List component properties
  add_component_with_properties   Add a component and set its fields
  set_property                    Write a component property (edit mode)
  start_play / stop_play          Enter and exit play mode
  pause_play / resume_play        Pause and resume play mode
  is_playing                      Query current play-mode state
  get_runtime_property            Read a property while playing
  set_runtime_property            Write a property while playing
  take_screenshot                 Capture the editor viewport
  undo / redo                     Undo/redo editor actions
  get_bridge_status               Check connection health and latency

EXAMPLE
  SBOX_HOST=localhost SBOX_PORT=8765 node dist/index.js

SETUP
  1. Copy sbox-plugin/ into your s&box addons folder and restart the editor.
  2. Look for "[Claude Bridge] Listening on port 8765" in the s&box console.
  3. Run this server and configure Claude Code to use it:
       claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
`);
  process.exit(0);
}

// ── All registered MCP tools ──────────────────────────────────────────────────

const allTools: Tool[] = [
  ...CONSOLE_TOOLS,
  ...GAMEOBJECT_TOOLS,
  ...PLAYMODE_TOOLS,
  ...COMPONENT_TOOLS,
  ...EDITOR_TOOLS,
  ...STATUS_TOOLS,
];

// ── Main ──────────────────────────────────────────────────────────────────────

async function main(): Promise<void> {
  const bridge = new BridgeClient();
  bridge.connect();

  bridge.on('connected', () =>
    process.stderr.write('[Bridge] Connected to s&box\n'),
  );
  bridge.on('disconnected', () =>
    process.stderr.write('[Bridge] Disconnected — will retry\n'),
  );
  bridge.on('ping_timeout', () =>
    process.stderr.write('[Bridge] Ping timeout — reconnecting\n'),
  );
  bridge.on('error', (err: Error) =>
    process.stderr.write(`[Bridge] Error: ${err.message}\n`),
  );

  const server = new Server(
    { name: 'sbox-mcp-server', version: '1.0.0' },
    { capabilities: { tools: {} } },
  );

  // List tools
  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: allTools }));

  // Call tool — dispatch to the right module
  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: rawArgs } = request.params;
    const toolArgs = (rawArgs ?? {}) as Record<string, unknown>;

    try {
      let result: unknown;

      if (CONSOLE_TOOLS.some((t) => t.name === name)) {
        result = await handleConsoleTool(name, toolArgs, bridge);
      } else if (GAMEOBJECT_TOOLS.some((t) => t.name === name)) {
        result = await handleGameObjectTool(name, toolArgs, bridge);
      } else if (PLAYMODE_TOOLS.some((t) => t.name === name)) {
        result = await handlePlayModeTool(name, toolArgs, bridge);
      } else if (COMPONENT_TOOLS.some((t) => t.name === name)) {
        result = await handleComponentTool(name, toolArgs, bridge);
      } else if (EDITOR_TOOLS.some((t) => t.name === name)) {
        result = await handleEditorTool(name, toolArgs, bridge);
      } else if (STATUS_TOOLS.some((t) => t.name === name)) {
        result = await handleStatusTool(name, toolArgs, bridge);
      } else {
        return {
          content: [{ type: 'text' as const, text: `Unknown tool: ${name}` }],
          isError: true,
        };
      }

      return {
        content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }],
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [{ type: 'text' as const, text: `Error: ${message}` }],
        isError: true,
      };
    }
  });

  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err: unknown) => {
  process.stderr.write(`Fatal: ${String(err)}\n`);
  process.exit(1);
});
