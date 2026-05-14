import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerLightingTools(server: McpServer, bridge: BridgeClient): void {
  server.tool(
    "create_light",
    "Add a light component (point, spot, or directional) to a GameObject",
    {
      id: z.string().describe("GUID of the GameObject"),
      lightType: z.enum(["point", "spot", "directional"]).optional().describe("Light type. Defaults to 'point'"),
      color: z.string().optional().describe("Color as hex (e.g. '#ff0000')"),
      shadows: z.boolean().optional().describe("Whether the light casts shadows"),
    },
    async (p) => {
      const res = await bridge.send("create_light", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "set_light_properties",
    "Modify properties on an existing Light component (point/spot/directional)",
    {
      id: z.string().describe("GUID of the GameObject with a Light component"),
      color: z.string().optional().describe("Color as hex"),
      shadows: z.boolean().optional().describe("Whether the light casts shadows"),
      shadowBias: z.number().optional().describe("Shadow bias value"),
    },
    async (p) => {
      const res = await bridge.send("set_light_properties", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}