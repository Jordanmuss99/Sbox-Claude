#!/usr/bin/env node

/**
 * Entry point for the sbox-mcp MCP server.
 *
 * Creates an MCP server (stdio transport), connects to the s&box Bridge Addon
 * via file IPC, and registers all tool handlers. Each tool domain (project,
 * scripts, console, scenes, etc.) has its own register function in src/tools/.
 *
 * CLI flags: --version / -v, --help / -h
 * Environment: SBOX_BRIDGE_HOST, SBOX_BRIDGE_PORT
 */

import { readFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { BridgeClient } from "./transport/bridge-client.js";
import { registerProjectTools } from "./tools/project.js";
import { registerScriptTools } from "./tools/scripts.js";
import { registerConsoleTools } from "./tools/console.js";
import { registerSceneTools } from "./tools/scenes.js";
import { registerGameObjectTools } from "./tools/gameobjects.js";
import { registerComponentTools } from "./tools/components.js";
import { registerAssetTools } from "./tools/assets.js";
import { registerMaterialTools } from "./tools/materials.js";
import { registerAudioTools } from "./tools/audio.js";
import { registerStatusTools } from "./tools/status.js";
import { registerPlayModeTools } from "./tools/playmode.js";
import { registerPrefabTools } from "./tools/prefabs.js";
import { registerPhysicsTools } from "./tools/physics.js";
import { registerUITools } from "./tools/ui.js";
import { registerTemplateTools } from "./tools/templates.js";
import { registerNetworkingTools } from "./tools/networking.js";
import { registerPublishingTools } from "./tools/publishing.js";
import { registerWorldTools } from "./tools/world.js";
import { registerDiscoveryTools } from "./tools/discovery.js";
import { registerTagTools } from "./tools/tags.js";
import { registerSceneSearchTools } from "./tools/scene-search.js";
import { registerJtcAliasTools } from "./tools/jtc-aliases.js";
import { registerDocsTools } from "./tools/docs.js";
import { registerExecutionTools } from "./tools/execution.js";
import { registerEditorEventsTools } from "./tools/editor-events.js";
import { registerLightingTools } from "./tools/lighting.js";
import { registerVfxTools } from "./tools/vfx.js";
import { registerAnimationTools } from "./tools/animation.js";
import { registerNavMeshTools } from "./tools/navmesh.js";
import { registerCameraTools } from "./tools/camera.js";
import { registerGameObjectTreeTools } from "./tools/gameobject-tree.js";
import { registerLibraryTools } from "./tools/library.js";
import { EventWatcher } from "./transport/event-watcher.js";

// ── CLI flags ──────────────────────────────────────────────────────
const args = process.argv.slice(2);

/** Read the package version from package.json, or return "unknown" on failure. */
function getVersion(): string {
  try {
    const __filename = fileURLToPath(import.meta.url);
    const __dirname = dirname(__filename);
    const pkg = JSON.parse(
      readFileSync(join(__dirname, "..", "package.json"), "utf-8")
    );
    return pkg.version ?? "unknown";
  } catch {
    return "unknown";
  }
}

if (args.includes("--version") || args.includes("-v")) {
  console.log(`sbox-mcp ${getVersion()}`);
  process.exit(0);
}

if (args.includes("--help") || args.includes("-h")) {
  console.log(`sbox-mcp ${getVersion()} — MCP Server for s&box game engine

USAGE
  node dist/index.js              Start the MCP server (stdio transport)
  node dist/index.js --help       Show this help
  node dist/index.js --version    Show version

ENVIRONMENT VARIABLES
  SBOX_BRIDGE_HOST    Bridge host (legacy — file IPC is the active transport; default: 127.0.0.1)
  SBOX_BRIDGE_PORT    Bridge port (legacy — file IPC is the active transport; default: 29015)
  SBOX_BRIDGE_IPC_DIR Override the auto-probed IPC directory (default: <temp>/sbox-bridge-ipc)

CONNECT TO CLAUDE CODE
  claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js

TOOLS (109 total)
  Project:     get_project_info, list_project_files, read_file, write_file
  Scripts:     create_script, edit_script, delete_script, trigger_hotload
  Console:     get_console_output, get_compile_errors, clear_console
  Scenes:      list_scenes, load_scene, save_scene, create_scene
  GameObjects: create/delete/duplicate/rename_gameobject, set_parent/enabled/transform
  Components:  get/set_property, get_all_properties, list_available_components, add_component_with_properties, set_prefab_ref
  Hierarchy:   get_scene_hierarchy, get_selected_objects, select_object, focus_object
  Assets:      search_assets, list_asset_library, install_asset, get_asset_info
  Materials:   assign_model, create_material, assign_material, set_material_property
  Audio:       list_sounds, create_sound_event, assign_sound, play_sound_preview
  Play Mode:   start/stop/pause/resume_play, is_playing
  Runtime:     get/set_runtime_property, take_screenshot
  Editor:      undo, redo
  Prefabs:     create_prefab, instantiate_prefab, list_prefabs, get_prefab_info
  Physics:     add_physics, add_collider, add_joint, raycast
  UI:          create_razor_ui, add_screen_panel, add_world_panel
  Templates:   create_player_controller, create_npc_controller, create_game_manager, create_trigger_zone
  Networking:  add_network_helper, configure_network, get_network_status, network_spawn, set_ownership
  Net Scripts: add_sync_property, add_rpc_method, create_networked_player, create_lobby_manager, create_network_events
  Publishing:  get_project_config, set_project_config, validate_project, build_project, get_build_status, clean_build
  Export:      export_project, set_project_thumbnail, get_package_details, prepare_publish
  World Gen:   invoke_button, list_component_buttons, raycast_terrain, build_terrain_mesh
  Map Edit:    add_terrain_hill/clearing/trail, clear_terrain_features, sculpt_terrain
  Caves:       add_cave_waypoint, clear_cave_path
  Forest:      add_forest_poi/trail, set_forest_seed, clear_forest_pois, paint_forest_density
  Placement:   place_along_path
  Discovery:   describe_type, search_types, get_method_signature, find_in_project
  Status:      get_bridge_status
`);
  process.exit(0);
}

// ── Server setup ───────────────────────────────────────────────────
const server = new McpServer({
  name: "sbox-mcp",
  version: getVersion(),
});

// Bridge client connects to s&box editor via file IPC (see transport/bridge-client.ts)
const bridge = new BridgeClient(
  process.env.SBOX_BRIDGE_HOST ?? "127.0.0.1",
  parseInt(process.env.SBOX_BRIDGE_PORT ?? "29015", 10)
);
const eventWatcher = new EventWatcher( bridge.getIpcDir() );
eventWatcher.start();


// Register all tools
registerProjectTools(server, bridge);
registerScriptTools(server, bridge);
registerConsoleTools(server, bridge);
registerSceneTools(server, bridge);
registerGameObjectTools(server, bridge);
registerComponentTools(server, bridge);
registerAssetTools(server, bridge);
registerMaterialTools(server, bridge);
registerAudioTools(server, bridge);
registerStatusTools(server, bridge);
registerPlayModeTools(server, bridge);
registerPrefabTools(server, bridge);
registerPhysicsTools(server, bridge);
registerUITools(server, bridge);
registerTemplateTools(server, bridge);
registerNetworkingTools(server, bridge);
registerPublishingTools(server, bridge);
registerWorldTools(server, bridge);
registerDiscoveryTools(server, bridge);
registerTagTools(server, bridge);
registerSceneSearchTools(server, bridge);
registerDocsTools(server);
registerExecutionTools(server, bridge);
registerJtcAliasTools(server, bridge);
registerEditorEventsTools(server, eventWatcher);
registerLightingTools(server, bridge);
registerVfxTools(server, bridge);
registerAnimationTools(server, bridge);
registerNavMeshTools(server, bridge);
registerCameraTools(server, bridge);
registerGameObjectTreeTools(server, bridge);
registerLibraryTools(server, bridge);

/** Start the MCP server on stdio and attempt initial Bridge connection. */
async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);

  console.error("");
  console.error("  ╔═══════════════════════════════════════════════════╗");
  console.error("  ║  s&box Claude Bridge — MCP Server                ║");
  console.error("  ║  Build s&box games through conversation          ║");
  console.error("  ║                                                   ║");
  console.error("  ║  A project by sboxskins.gg                       ║");
  console.error("  ║  https://sboxskins.gg                            ║");
  console.error("  ╚═══════════════════════════════════════════════════╝");
  console.error("");

  // Attempt initial connection to s&box (non-fatal if it fails)
  try {
    await bridge.connect();
    console.error("[sbox-mcp] Connected to s&box Bridge");
  } catch {
    console.error(
      "[sbox-mcp] Warning: Could not connect to s&box Bridge. Will retry on first tool call."
    );
  }

  // Phase A.C.3.10 — start the heartbeat loop. Pings every 5s, transitions to
  // disconnected after 3 consecutive misses, and reconnects when the bridge
  // comes back. Logs disconnect/reconnect to stderr for operator visibility.
  bridge.startHeartbeat();
  bridge.on("disconnect", (info: { missCount: number }) => {
    console.error(`[sbox-mcp] Bridge disconnected after ${info.missCount} missed pings. Reconnect loop active.`);
  });
  bridge.on("reconnect", (info: { latencyMs: number }) => {
    console.error(`[sbox-mcp] Bridge reconnected (latency ${info.latencyMs} ms).`);
  });

  // Graceful shutdown — stop timers and watcher so the process exits cleanly.
  const shutdown = (signal: string): void => {
    console.error(`[sbox-mcp] Received ${signal} — shutting down.`);
    bridge.disconnect();
    eventWatcher.stop();
    process.exit(0);
  };
  process.on("SIGINT", () => shutdown("SIGINT"));
  process.on("SIGTERM", () => shutdown("SIGTERM"));
}

main().catch((err) => {
  console.error("[sbox-mcp] Fatal error:", err);
  process.exit(1);
});
