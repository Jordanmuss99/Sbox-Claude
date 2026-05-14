import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerCameraTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "get_editor_camera",
    "Get the current editor camera position, rotation, and FOV",
    {},
    async () => {
      const res = await bridge.send("get_editor_camera", {});
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
    "set_editor_camera",
    "Set the editor camera position and/or rotation",
    {
      position__x: z.number().optional().describe("X coordinate"),
      position__y: z.number().optional().describe("Y coordinate"),
      position__z: z.number().optional().describe("Z coordinate"),
      rotation__pitch: z.number().optional().describe("Pitch angle in degrees"),
      rotation__yaw: z.number().optional().describe("Yaw angle in degrees"),
      rotation__roll: z.number().optional().describe("Roll angle in degrees"),
    },
    async (params) => {
      const p: Record<string, unknown> = {};
      if (params.position__x !== undefined) {
        p.position = {
          x: params.position__x,
          y: params.position__y ?? 0,
          z: params.position__z ?? 0,
        };
      }
      if (params.rotation__pitch !== undefined) {
        p.rotation = {
          pitch: params.rotation__pitch,
          yaw: params.rotation__yaw ?? 0,
          roll: params.rotation__roll ?? 0,
        };
      }
      const res = await bridge.send("set_editor_camera", p);
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
