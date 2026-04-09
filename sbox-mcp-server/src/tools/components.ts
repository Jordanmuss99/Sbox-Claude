/**
 * Component property tools.
 *
 * Task 7  — set_property:          write a component property in edit mode
 * Task 10 — get_runtime_property:  read  a component property during play mode
 * Task 10 — set_runtime_property:  write a component property during play mode
 */
import type { Tool } from '@modelcontextprotocol/sdk/types.js';
import type { BridgeClient } from '../BridgeClient.js';

// ── Shared sub-schemas ────────────────────────────────────────────────────────

const guidParam = {
  guid: {
    type: 'string' as const,
    description: 'GUID of the GameObject that owns the component.',
  },
};

const componentTypeParam = {
  component_type: {
    type: 'string' as const,
    description: 'Type name of the component (e.g. "Rigidbody", "PlayerController").',
  },
};

const propertyNameParam = {
  property_name: {
    type: 'string' as const,
    description:
      'Name of the [Property]-annotated field to target. ' +
      'Use get_all_properties to discover available names.',
  },
};

// ── Tool definitions ──────────────────────────────────────────────────────────

export const COMPONENT_TOOLS: Tool[] = [
  // ── Task 7 ──────────────────────────────────────────────────────────────
  {
    name: 'set_property',
    description:
      'Write a single [Property]-annotated field on a component. ' +
      'The write counterpart to get_all_properties. ' +
      'Returns { guid, component, property, new_value } with the value read back after the write. ' +
      'Only works in editor mode — use set_runtime_property when the game is running.',
    inputSchema: {
      type: 'object',
      properties: {
        ...guidParam,
        ...componentTypeParam,
        ...propertyNameParam,
        value: {
          description:
            'New value for the property. ' +
            'Must be JSON-compatible with the property CLR type ' +
            '(number for float/int, string for string, object for structs, etc.).',
        },
      },
      required: ['guid', 'component_type', 'property_name', 'value'],
    },
  },

  // ── Task 10 ─────────────────────────────────────────────────────────────
  {
    name: 'get_runtime_property',
    description:
      'Read a component property while the game is in play mode. ' +
      'Throws if the editor is not currently playing. ' +
      'Use is_playing first to confirm play mode is active. ' +
      'Returns { guid, component, property, value }.',
    inputSchema: {
      type: 'object',
      properties: {
        ...guidParam,
        ...componentTypeParam,
        ...propertyNameParam,
      },
      required: ['guid', 'component_type', 'property_name'],
    },
  },
  {
    name: 'set_runtime_property',
    description:
      'Write a component property while the game is in play mode — ' +
      'lets Claude tweak live values without stopping play. ' +
      'Changes made here are lost when play mode ends. ' +
      'Throws if not currently playing. ' +
      'Returns { guid, component, property, new_value }.',
    inputSchema: {
      type: 'object',
      properties: {
        ...guidParam,
        ...componentTypeParam,
        ...propertyNameParam,
        value: {
          description: 'New value for the property.',
        },
      },
      required: ['guid', 'component_type', 'property_name', 'value'],
    },
  },
];

const COMPONENT_NAMES = new Set(COMPONENT_TOOLS.map((t) => t.name));

export async function handleComponentTool(
  name: string,
  args: Record<string, unknown>,
  bridge: BridgeClient,
): Promise<unknown> {
  if (!COMPONENT_NAMES.has(name)) {
    throw new Error(`Unknown component tool: ${name}`);
  }
  return bridge.send(name, args);
}
