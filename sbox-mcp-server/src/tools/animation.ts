import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerAnimationTools(server: McpServer, bridge: BridgeClient): void {
  server.tool(
    "play_animation",
    "Set an animation parameter on a SkinnedModelRenderer (drives the animation graph)",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
      name: z.string().describe("Animation parameter name (e.g. 'b_jump', 'move_x', 'attack')"),
      value: z.union([z.number(), z.boolean()]).optional().describe("Parameter value (number, bool, or int). Defaults to setting numeric 1.0"),
    },
    async (p) => {
      const res = await bridge.send("play_animation", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}