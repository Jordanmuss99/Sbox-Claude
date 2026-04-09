import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Game logic template tools: create_player_controller, create_npc_controller,
 * create_game_manager, create_trigger_zone.
 *
 * These generate fully functional C# scripts with configurable boilerplate,
 * saving non-coders from writing game logic from scratch.
 */
export function registerTemplateTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_player_controller ──────────────────────────────────────
  server.tool(
    "create_player_controller",
    "Generate a player controller script with WASD movement, mouse look, jumping, and sprint. Supports first-person and third-person camera modes",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'PlayerController'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      type: z
        .enum(["first_person", "third_person"])
        .optional()
        .describe(
          "Camera mode: 'first_person' or 'third_person'. Defaults to 'first_person'"
        ),
      moveSpeed: z
        .number()
        .optional()
        .describe("Movement speed in units/sec. Defaults to 300"),
      jumpForce: z
        .number()
        .optional()
        .describe("Jump force. Defaults to 350"),
      sprintMultiplier: z
        .number()
        .optional()
        .describe("Sprint speed multiplier. Defaults to 1.5"),
    },
    async (params) => {
      const res = await bridge.send("create_player_controller", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_npc_controller ─────────────────────────────────────────
  server.tool(
    "create_npc_controller",
    "Generate an NPC controller script with NavMeshAgent pathfinding. Supports patrol, chase, and patrol-chase behaviors",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'NpcController'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      behavior: z
        .enum(["patrol", "chase", "patrol_chase"])
        .optional()
        .describe(
          "AI behavior: 'patrol' (follow waypoints), 'chase' (follow player), 'patrol_chase' (patrol until player nearby). Defaults to 'patrol'"
        ),
      moveSpeed: z
        .number()
        .optional()
        .describe("Movement speed. Defaults to 150"),
      chaseRange: z
        .number()
        .optional()
        .describe(
          "Detection range for chase behavior. Defaults to 500"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_npc_controller", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_game_manager ───────────────────────────────────────────
  server.tool(
    "create_game_manager",
    "Generate a game manager script with configurable game loop features: score tracking, round timer, player spawning, and game state machine",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'GameManager'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      includeScore: z
        .boolean()
        .optional()
        .describe("Include score tracking. Defaults to true"),
      includeTimer: z
        .boolean()
        .optional()
        .describe("Include round timer with countdown. Defaults to false"),
      includeSpawning: z
        .boolean()
        .optional()
        .describe(
          "Include player spawning from prefab at spawn point. Defaults to false"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_game_manager", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_trigger_zone ───────────────────────────────────────────
  server.tool(
    "create_trigger_zone",
    "Generate a trigger zone script that detects when GameObjects enter/exit a collider volume. Supports teleport, damage, spawn, and log actions",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'TriggerZone'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/ for the file"),
      action: z
        .enum(["log", "teleport", "damage", "spawn"])
        .optional()
        .describe(
          "What happens on trigger: 'log' (print message), 'teleport' (move to destination), 'damage' (apply damage), 'spawn' (create prefab). Defaults to 'log'"
        ),
      filterTag: z
        .string()
        .optional()
        .describe(
          "Only trigger for objects with this tag. Defaults to 'player'"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_trigger_zone", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
