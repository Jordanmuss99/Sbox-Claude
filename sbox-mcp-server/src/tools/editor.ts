/**
 * Editor utility tools (Tasks 3 & 4).
 *
 * take_screenshot — capture the current viewport to a PNG file
 * undo            — perform an editor undo
 * redo            — perform an editor redo
 */
import type { Tool } from '@modelcontextprotocol/sdk/types.js';
import type { BridgeClient } from '../BridgeClient.js';

export const EDITOR_TOOLS: Tool[] = [
  // ── Task 3 ──────────────────────────────────────────────────────────────
  {
    name: 'take_screenshot',
    description:
      'Capture the current s&box editor viewport to a PNG file. ' +
      'Returns { path, width, height, timestamp }. ' +
      'Use this to visually verify scene changes or to document a build.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description:
            'Destination file path relative to the project root. ' +
            'Default: "screenshots/screenshot_{timestamp}.png".',
        },
      },
    },
  },

  // ── Task 4 ──────────────────────────────────────────────────────────────
  {
    name: 'undo',
    description:
      'Undo the last editor action — safety net for when Claude makes a bad change. ' +
      'Returns { success, description? }.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'redo',
    description:
      'Redo the last undone editor action. ' +
      'Returns { success, description? }.',
    inputSchema: { type: 'object', properties: {} },
  },
];

const EDITOR_NAMES = new Set(EDITOR_TOOLS.map((t) => t.name));

export async function handleEditorTool(
  name: string,
  args: Record<string, unknown>,
  bridge: BridgeClient,
): Promise<unknown> {
  if (!EDITOR_NAMES.has(name)) {
    throw new Error(`Unknown editor tool: ${name}`);
  }
  return bridge.send(name, args);
}
