import { describe, it, expect, beforeEach } from "vitest";
import {
  registerAlias,
  _resetAliasWarnings,
} from "../src/tools/aliases.js";

interface ToolReg {
  name: string;
  description: string;
  schema: Record<string, unknown>;
  handler: (args: Record<string, unknown>) => Promise<unknown>;
}

class StubServer {
  registrations: ToolReg[] = [];
  tool(
    name: string,
    description: string,
    schema: Record<string, unknown>,
    handler: (args: Record<string, unknown>) => Promise<unknown>,
  ): void {
    this.registrations.push({ name, description, schema, handler });
  }
  getHandler(name: string) {
    const r = this.registrations.find((reg) => reg.name === name);
    if (!r) throw new Error(`no handler registered for ${name}`);
    return r.handler;
  }
}

interface StubResponse {
  success: boolean;
  data?: unknown;
  error?: string;
}

class StubBridge {
  sendCalls: Array<{ name: string; args: Record<string, unknown> }> = [];
  responses: StubResponse[] = [];
  async send(
    name: string,
    args: Record<string, unknown>,
    _timeoutMs?: number,
  ): Promise<StubResponse> {
    this.sendCalls.push({ name, args });
    return this.responses.shift() ?? { success: true, data: { ok: true } };
  }
}

describe("B.1.2 — deprecation alias dispatch", () => {
  let server: StubServer;
  let bridge: StubBridge;
  let warnings: string[];
  const recordWarn = (m: string) => {
    warnings.push(m);
  };

  beforeEach(() => {
    server = new StubServer();
    bridge = new StubBridge();
    warnings = [];
    _resetAliasWarnings();
  });

  it("registers the alias as a TS tool with the JTC name", () => {
    registerAlias(
      server as never,
      bridge as never,
      "editor_save_scene",
      "save_scene",
      "Save the scene",
      {},
      30000,
      recordWarn,
    );
    expect(server.registrations.map((r) => r.name)).toEqual([
      "editor_save_scene",
    ]);
  });

  it("forwards bridge.send to the LOU canonical name, not the JTC name", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "editor_save_scene",
      "save_scene",
      "d",
      {},
      30000,
      recordWarn,
    );
    await server.getHandler("editor_save_scene")({});
    expect(bridge.sendCalls).toEqual([{ name: "save_scene", args: {} }]);
  });

  it("passes args through unchanged", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "scene_set_transform",
      "set_transform",
      "d",
      {},
      30000,
      recordWarn,
    );
    await server.getHandler("scene_set_transform")({
      id: "abc",
      position: { x: 1, y: 2, z: 3 },
    });
    expect(bridge.sendCalls[0].args).toEqual({
      id: "abc",
      position: { x: 1, y: 2, z: 3 },
    });
  });

  it("emits warning exactly once per alias per process", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "editor_undo",
      "undo",
      "d",
      {},
      30000,
      recordWarn,
    );
    const handler = server.getHandler("editor_undo");
    await handler({});
    await handler({});
    await handler({});
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain("editor_undo");
    expect(warnings[0]).toContain("undo");
    expect(warnings[0]).toContain("JTC-compat alias");
  });

  it("warns independently for different aliases", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "editor_undo",
      "undo",
      "d",
      {},
      30000,
      recordWarn,
    );
    registerAlias(
      server as never,
      bridge as never,
      "editor_redo",
      "redo",
      "d",
      {},
      30000,
      recordWarn,
    );
    await server.getHandler("editor_undo")({});
    await server.getHandler("editor_redo")({});
    await server.getHandler("editor_undo")({});
    expect(warnings).toHaveLength(2);
    expect(warnings[0]).toContain("editor_undo");
    expect(warnings[1]).toContain("editor_redo");
  });

  it("propagates bridge errors as isError responses", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "editor_play",
      "start_play",
      "d",
      {},
      30000,
      recordWarn,
    );
    bridge.responses = [{ success: false, error: "bridge offline" }];
    const res = (await server.getHandler("editor_play")({})) as {
      isError?: boolean;
      content: Array<{ text: string }>;
    };
    expect(res.isError).toBe(true);
    expect(res.content[0].text).toContain("bridge offline");
  });

  it("formats successful response data as JSON text", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "project_info",
      "get_project_info",
      "d",
      {},
      30000,
      recordWarn,
    );
    bridge.responses = [
      { success: true, data: { name: "rts_fps", version: "1.0.0" } },
    ];
    const res = (await server.getHandler("project_info")({})) as {
      content: Array<{ text: string }>;
    };
    expect(res.content[0].text).toContain("rts_fps");
    expect(res.content[0].text).toContain("1.0.0");
  });

  it("emits 'JTC-compat alias' warning when kind is jtc-compat (default)", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "editor_undo",
      "undo",
      "d",
      {},
      30000,
      recordWarn,
    );
    await server.getHandler("editor_undo")({});
    expect(warnings[0]).toContain("JTC-compat alias");
    expect(warnings[0]).not.toContain("was renamed to");
  });

  it("emits 'was renamed to' warning when kind is lou-rename", async () => {
    registerAlias(
      server as never,
      bridge as never,
      "install_asset",
      "asset_install_pinned",
      "d",
      {},
      30000,
      recordWarn,
      "lou-rename",
    );
    await server.getHandler("install_asset")({});
    expect(warnings[0]).toContain("was renamed to");
    expect(warnings[0]).toContain("install_asset");
    expect(warnings[0]).toContain("asset_install_pinned");
    expect(warnings[0]).not.toContain("JTC-compat alias");
  });
});
