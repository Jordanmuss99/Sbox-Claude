import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Tag management tools (S5 — 2026-05-12). Operate on `GameObject.Tags`
 * (Sandbox.GameTags) for the GameObject identified by GUID. Canonical names
 * match JTC's tag_add / tag_list / tag_remove since no Lou tool by these
 * names previously existed.
 */
export function registerTagTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── tag_add ────────────────────────────────────────────────────
  server.tool(
    "tag_add",
    "Add a tag to a GameObject. Idempotent: returns alreadyHad=true if the tag was already present.",
    {
      id: z.string().describe("GUID of the GameObject"),
      tag: z.string().describe("Tag name to add"),
    },
    async (params) => {
      const res = await bridge.send("tag_add", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── tag_list ───────────────────────────────────────────────────
  server.tool(
    "tag_list",
    "List all tags on a GameObject. Returns the tag array plus a count.",
    {
      id: z.string().describe("GUID of the GameObject"),
    },
    async (params) => {
      const res = await bridge.send("tag_list", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── tag_remove ─────────────────────────────────────────────────
  server.tool(
    "tag_remove",
    "Remove a tag from a GameObject. Returns removed=true if the tag was present, false if it wasn't.",
    {
      id: z.string().describe("GUID of the GameObject"),
      tag: z.string().describe("Tag name to remove"),
    },
    async (params) => {
      const res = await bridge.send("tag_remove", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
