import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerInputTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "list_input_actions",
    "List input action configuration files from the project",
    {},
    async () => {
      const res = await bridge.send("list_input_actions", {});
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
