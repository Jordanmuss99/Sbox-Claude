import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerAnimationTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "list_animations",
    "List animation clips available on a SkinnedModelRenderer",
    {
      id: z.string().describe("GUID of the GameObject with a SkinnedModelRenderer"),
    },
    async ({ id }) => {
      const res = await bridge.send("list_animations", { id });
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
    "play_animation",
    "Play an animation by name on a SkinnedModelRenderer",
    {
      id: z.string().describe("GUID of the GameObject"),
      name: z.string().describe("Animation clip name to play"),
    },
    async ({ id, name }) => {
      const res = await bridge.send("play_animation", { id, name });
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
