import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { EventWatcher } from "../transport/event-watcher.js";

/**
 * Editor event query tool — reads from the events.json file written by the
 * C# BridgeEventDispatcher. This is a TS-only tool (no C# handler).
 *
 * Events are captured from editor lifecycle hooks (scene open/play/stop/save,
 * selection changes, asset/resource modifications).
 */
export function registerEditorEventsTools(
  server: McpServer,
  watcher: EventWatcher
): void {
  server.tool(
    "get_editor_events",
    "Query recent editor events (scene lifecycle, selection changes, asset modifications). Returns newest-first.",
    {
      sinceId: z
        .number()
        .int()
        .optional()
        .describe("Only return events with eventId greater than this (monotonic cursor)"),
      sessionId: z
        .string()
        .optional()
        .describe("Filter to events from a specific editor session (GUID)"),
      types: z
        .array(z.string())
        .optional()
        .describe("Filter by event type (e.g. ['scene.play', 'scene.stop', 'hammer.selection.changed'])"),
      limit: z
        .number()
        .int()
        .min(1)
        .max(200)
        .default(50)
        .describe("Maximum events to return"),
    },
    async ({ sinceId, sessionId, types, limit }) => {
      const events = watcher.getEvents(sinceId, sessionId, types, limit);

      if (events.length === 0) {
        return {
          content: [
            {
              type: "text",
              text: "No events found matching the specified filters.",
            },
          ],
        };
      }

      const latestId = events[0].eventId;
      const summary = `${events.length} event(s) returned (newest eventId: ${latestId})`;

      return {
        content: [
          {
            type: "text",
            text: `${summary}\n\n${JSON.stringify(events, null, 2)}`,
          },
        ],
      };
    }
  );
}
