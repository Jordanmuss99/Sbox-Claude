/**
 * Play-mode tools (Tasks 1 & 2).
 *
 * All five tools forward directly to the matching C# handler.
 * Every response includes a { state } field: "playing" | "paused" | "stopped".
 * This lets Claude always know the current mode without an extra is_playing call.
 */
import type { Tool } from '@modelcontextprotocol/sdk/types.js';
import type { BridgeClient } from '../BridgeClient.js';

export const PLAYMODE_TOOLS: Tool[] = [
  // ── Task 1 ──────────────────────────────────────────────────────────────
  {
    name: 'start_play',
    description:
      'Enter play mode in the s&box editor. ' +
      'Returns { state: "playing" | "stopped" }. ' +
      'Must be called before any runtime queries (get_runtime_property, etc.). ' +
      'No-op with a message if already playing.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'stop_play',
    description:
      'Exit play mode and return to editor mode. ' +
      'All runtime changes made during play mode are discarded by s&box. ' +
      'Returns { state: "stopped" }.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'is_playing',
    description:
      'Return the current play-mode state without changing it. ' +
      'Returns { state: "playing" | "paused" | "stopped" }. ' +
      'Use this before calling play-mode-only tools.',
    inputSchema: { type: 'object', properties: {} },
  },

  // ── Task 2 ──────────────────────────────────────────────────────────────
  {
    name: 'pause_play',
    description:
      'Pause a running play-mode session. ' +
      'Time stops and physics freeze, but the scene stays live — ' +
      'useful for inspecting state mid-game. ' +
      'Returns { state: "paused" }.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'resume_play',
    description:
      'Resume a paused play-mode session. ' +
      'Returns { state: "playing" }.',
    inputSchema: { type: 'object', properties: {} },
  },
];

const PLAYMODE_NAMES = new Set(PLAYMODE_TOOLS.map((t) => t.name));

export async function handlePlayModeTool(
  name: string,
  args: Record<string, unknown>,
  bridge: BridgeClient,
): Promise<unknown> {
  if (!PLAYMODE_NAMES.has(name)) {
    throw new Error(`Unknown playmode tool: ${name}`);
  }
  return bridge.send(name, args);
}
