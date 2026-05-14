import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerNavMeshTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "build_navmesh",
    "Trigger navmesh generation for the active scene",
    {},
    async () => {
      const res = await bridge.send("build_navmesh", {});
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
    "query_navmesh",
    "Find the nearest reachable point on the navmesh from a world position",
    {
      position__x: z.number().describe("X coordinate"),
      position__y: z.number().describe("Y coordinate"),
      position__z: z.number().describe("Z coordinate"),
    },
    async ({ position__x, position__y, position__z }) => {
      const res = await bridge.send("query_navmesh", {
        position: { x: position__x, y: position__y, z: position__z },
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
