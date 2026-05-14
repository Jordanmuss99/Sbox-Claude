import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * v1.5.2 — Library Manager observability tools.
 *
 * Backed by Editor.AssetSystem static methods (GetReferencedPackages,
 * GetInstalledPackages, GetPackageFiles, IsCloudInstalled) and Sandbox.Package
 * instance properties (Title, Ident, Revision, IsMounted, etc).
 *
 * `package_uninstall` mutates the .sbproj JSON directly (mirrors the install
 * handler) - the runtime mount persists until the editor restarts.
 */
export function registerLibraryTools(server: McpServer, bridge: BridgeClient): void {
  server.tool(
    "list_installed_packages",
    "List all packages referenced (pinned in .sbproj) and/or cloud-installed in the current project. De-duped by ident. Returns title, org, version, mount status, file size, and update timestamps.",
    {},
    async () => {
      const res = await bridge.send("list_installed_packages", {});
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "list_package_files",
    "List the files inside an installed package. Use ident like 'org.name' or the full ident reported by list_installed_packages.",
    {
      ident: z.string().describe("Package identifier (e.g. 'facepunch.flatgrass', 'org.name#version')"),
    },
    async (p) => {
      const res = await bridge.send("list_package_files", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "get_installed_package_info",
    "Full metadata for one installed/referenced package: title, description, dependencies (its PackageReferences), tags, revision, screenshots-url-context, vote counts. Pass fetchRemote=true to do a network call if the package isn't locally cached.",
    {
      ident: z.string().describe("Package identifier"),
      fetchRemote: z.boolean().optional().describe("If true, query asset.party when not locally cached. Default false."),
    },
    async (p) => {
      const res = await bridge.send("get_installed_package_info", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "package_uninstall",
    "Remove a package ident from ProjectConfig.PackageReferences in the .sbproj. Optionally delete the Libraries/<ident>/ folder. NOTE: the runtime mount may persist until the editor restarts.",
    {
      ident: z.string().describe("Package identifier to remove from PackageReferences"),
      deleteFolder: z.boolean().optional().describe("If true, also delete Libraries/<ident>/ from disk. Default false (just unpin)."),
    },
    async (p) => {
      const res = await bridge.send("package_uninstall", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  server.tool(
    "library_manager_state",
    "Check the Library Manager state — currently just 'updates available' for each referenced package (compares local Revision.VersionId against latest remote). Recently-viewed is not exposed by the s&box editor API. Pass checkUpdates=false to skip the network calls.",
    {
      checkUpdates: z.boolean().optional().describe("Whether to fetch latest revisions from asset.party. Default true."),
    },
    async (p) => {
      const res = await bridge.send("library_manager_state", p);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
