import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerNavMeshTools(server: McpServer, bridge: BridgeClient): void {
  server.tool(
    "build_navmesh",
    "Trigger navmesh generation for the active scene",
    {},
    async () => {
      const res = await bridge.send("build_navmesh", {}, 60000);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "query_navmesh",
    "Find the nearest reachable point on the navmesh from a world position",
    {
      position: z.object({ x: z.number(), y: z.number(), z: z.number() }).describe("World position"),
      radius: z.number().optional().describe("Search radius. Defaults to 1000"),
    },
    async (p) => {
      const res = await bridge.send("query_navmesh", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}