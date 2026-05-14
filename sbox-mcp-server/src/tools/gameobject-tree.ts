import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Phase B.2 — v1.4.0 — subtree snapshot/instantiate.
 *
 * Narrower than full scene-level snapshot/restore: operates on a single
 * GameObject subtree (rooted at `rootId`), round-trips name, enabled, tags,
 * transform, and primitive-typed component properties. Reference-typed
 * properties (GameObject refs, Component refs, Resource handles) are not
 * round-tripped — caller must wire those with `set_prefab_ref` etc.
 *
 * On `instantiate_gameobject_tree` partial failure, every newly-created
 * GameObject is destroyed before the error is returned (atomic-or-nothing).
 */
export function registerGameObjectTreeTools(
  server: McpServer,
  bridge: BridgeClient,
): void {
  server.tool(
    "snapshot_gameobject_tree",
    "Serialize a GameObject subtree to a JSON file at a project-relative path. Captures structure, transforms, tags, and primitive component properties. NOTE: resource references (Model, Material, Sound, prefab GameObjects) are NOT round-tripped — they appear in each component's `_skippedProperties` array. An instantiated cube will have its ModelRenderer reattached but with no Model assigned, so it will be visually invisible. For full clones, use prefabs + set_prefab_ref, not this tool.",
    {
      rootId: z.string().describe("GUID of the subtree root GameObject"),
      outputPath: z.string().describe("Project-relative file path to write (e.g. 'snapshots/player.json')"),
    },
    async (p) => {
      const res = await bridge.send("snapshot_gameobject_tree", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    },
  );

  server.tool(
    "instantiate_gameobject_tree",
    "Recreate a GameObject subtree from a snapshot JSON file. New GameObjects get fresh GUIDs. Atomic: partial failure destroys every newly-created GO before returning the error.",
    {
      jsonPath: z.string().describe("Project-relative path to the snapshot JSON"),
      parentId: z.string().optional().describe("GUID of parent GameObject. Omit for scene root."),
      position: z.object({ x: z.number(), y: z.number(), z: z.number() })
        .optional()
        .describe("Override world position of the new root (after parenting)"),
    },
    async (p) => {
      const res = await bridge.send("instantiate_gameobject_tree", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    },
  );
}
