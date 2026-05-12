import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Scene search tools (S6 — 2026-05-12). Walk the active scene and filter
 * GameObjects by component type, tag, or name pattern. Canonical names match
 * JTC's scene_find_by_component / scene_find_by_tag / scene_find_objects.
 */
export function registerSceneSearchTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── scene_find_by_tag ──────────────────────────────────────────
  server.tool(
    "scene_find_by_tag",
    "Find all GameObjects in the active scene that have a specific tag. Uses Scene.FindAllWithTag.",
    {
      tag: z.string().describe("Tag name to search for"),
    },
    async (params) => {
      const res = await bridge.send("scene_find_by_tag", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── scene_find_by_component ────────────────────────────────────
  server.tool(
    "scene_find_by_component",
    "Find all GameObjects in the active scene that have a specific component type. Type name is resolved via Game.TypeLibrary.",
    {
      componentType: z
        .string()
        .describe(
          "Component type name (e.g. 'ModelRenderer', 'Rigidbody', 'PlayerController')"
        ),
    },
    async (params) => {
      const res = await bridge.send("scene_find_by_component", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── scene_find_objects ─────────────────────────────────────────
  server.tool(
    "scene_find_objects",
    "Search for GameObjects in the active scene by name pattern. Supports * wildcards (e.g. 'Player*', '*Spawn*', 'Enemy_*'). Case-insensitive.",
    {
      query: z
        .string()
        .describe("Name pattern. Use * as a wildcard (case-insensitive)."),
    },
    async (params) => {
      const res = await bridge.send("scene_find_objects", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
