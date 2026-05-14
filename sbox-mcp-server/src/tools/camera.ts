import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

export function registerCameraTools(server: McpServer, bridge: BridgeClient): void {
  server.tool(
    "get_editor_camera",
    "Get the current editor camera position, rotation, and FOV",
    {},
    async () => {
      const res = await bridge.send("get_editor_camera", {});
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "set_editor_camera",
    "Set the editor camera position and/or rotation (may be reset by editor on next frame)",
    {
      position: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional().describe("World position"),
      rotation: z.object({ pitch: z.number(), yaw: z.number(), roll: z.number() }).optional().describe("Rotation in degrees"),
      fieldOfView: z.number().optional().describe("Field of view in degrees"),
    },
    async (p) => {
      const res = await bridge.send("set_editor_camera", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // Phase B.3 — v1.4.0 — camera framing.
  server.tool(
    "camera_focus_object",
    "Frame the editor camera on a GameObject's bounds (real FrameTo, not just selection)",
    {
      id: z.string().describe("GUID of the GameObject"),
    },
    async (p) => {
      const res = await bridge.send("camera_focus_object", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "camera_frame_bounds",
    "Frame the editor camera on an explicit world-space bounding box",
    {
      min: z.object({ x: z.number(), y: z.number(), z: z.number() }).describe("BBox mins"),
      max: z.object({ x: z.number(), y: z.number(), z: z.number() }).describe("BBox maxs"),
    },
    async (p) => {
      const res = await bridge.send("camera_frame_bounds", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}