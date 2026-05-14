import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerVfxTools(server: McpServer, bridge: BridgeClient): void {
  server.tool(
    "create_particle_effect",
    "Add a ParticleEffect component to a GameObject",
    {
      id: z.string().describe("GUID of the GameObject"),
    },
    async (p) => {
      const res = await bridge.send("create_particle_effect", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}