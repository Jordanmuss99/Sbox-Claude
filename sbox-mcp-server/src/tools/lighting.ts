import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerLightingTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "create_light",
    "Add a light component (point, spot, or directional) to a GameObject",
    {
      id: z.string().describe("GUID of the GameObject"),
      lightType: z
        .enum(["point", "spot", "directional"])
        .default("point")
        .describe("Type of light to create"),
      color: z.string().optional().describe("Color as hex (e.g. '#ff0000')"),
      intensity: z.number().optional().describe("Light intensity multiplier"),
      range: z.number().optional().describe("Light range in world units"),
    },
    async (params) => {
      const res = await bridge.send("create_light", params);
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
    "set_light_properties",
    "Modify properties on an existing LightComponent",
    {
      id: z.string().describe("GUID of the GameObject with a LightComponent"),
      color: z.string().optional().describe("Color as hex"),
      intensity: z.number().optional().describe("Light intensity"),
      range: z.number().optional().describe("Light range"),
    },
    async (params) => {
      const res = await bridge.send("set_light_properties", params);
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
