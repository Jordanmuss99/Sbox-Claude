import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerGameStateTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "snapshot_scene",
    "Serialize all GameObjects' transforms from the active scene to JSON",
    {},
    async () => {
      const res = await bridge.send("snapshot_scene", {});
      if (!res.success)
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return {
        content: [
          { type: "text", text: JSON.stringify(res.data, null, 2) },
        ],
      };
    }
  );

  server.tool(
    "restore_scene",
    "Restore GameObject transforms from a scene snapshot",
    {
      objects: z
        .array(
          z.object({
            id: z.string().describe("GUID of the GameObject"),
            position__x: z.number().optional(),
            position__y: z.number().optional(),
            position__z: z.number().optional(),
            rotation__pitch: z.number().optional(),
            rotation__yaw: z.number().optional(),
            rotation__roll: z.number().optional(),
          })
        )
        .describe("Array of objects with transforms to restore"),
    },
    async ({ objects }) => {
      const transformed = objects.map((o) => {
        const entry: Record<string, unknown> = { id: o.id };
        if (o.position__x !== undefined) {
          entry.position = {
            x: o.position__x,
            y: o.position__y ?? 0,
            z: o.position__z ?? 0,
          };
        }
        if (o.rotation__pitch !== undefined) {
          entry.rotation = {
            pitch: o.rotation__pitch,
            yaw: o.rotation__yaw ?? 0,
            roll: o.rotation__roll ?? 0,
          };
        }
        return entry;
      });
      const res = await bridge.send("restore_scene", {
        objects: transformed,
      });
      if (!res.success)
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return {
        content: [
          { type: "text", text: JSON.stringify(res.data, null, 2) },
        ],
      };
    }
  );
}
