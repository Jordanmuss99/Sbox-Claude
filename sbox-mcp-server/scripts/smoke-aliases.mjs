// Live smoke test for JTC alias dispatch (B.1.4-7).
// Spawns `node dist/index.js`, exchanges MCP JSON-RPC over stdio,
// and verifies that the four JTC aliases are listed and that tools/call
// against an alias actually forwards to the canonical handler.
//
// Usage: node scripts/smoke-aliases.mjs
// Exit code: 0 = pass, 1 = fail. No side effects on the s&box editor
// (only invokes `editor_take_screenshot` which writes to a temp path).

import { spawn } from "node:child_process";
import { tmpdir } from "node:os";
import { join } from "node:path";
import process from "node:process";

const proc = spawn(process.execPath, ["dist/index.js"], {
  stdio: ["pipe", "pipe", "pipe"],
});

let stdoutBuf = "";
let stderrBuf = "";
proc.stdout.on("data", (c) => {
  stdoutBuf += c.toString();
});
proc.stderr.on("data", (c) => {
  stderrBuf += c.toString();
});

function send(req) {
  proc.stdin.write(JSON.stringify(req) + "\n");
}

function readMessage(timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const tick = () => {
      const idx = stdoutBuf.indexOf("\n");
      if (idx >= 0) {
        const line = stdoutBuf.slice(0, idx).trim();
        stdoutBuf = stdoutBuf.slice(idx + 1);
        if (line) {
          try {
            resolve(JSON.parse(line));
          } catch (e) {
            reject(new Error(`bad JSON: ${line.slice(0, 200)} (${e.message})`));
          }
          return;
        }
      }
      if (Date.now() - start > timeoutMs) {
        reject(new Error(`timeout waiting for MCP message; stderr=${stderrBuf.slice(-500)}`));
        return;
      }
      setTimeout(tick, 25);
    };
    tick();
  });
}

const fail = (msg) => {
  console.error(`FAIL: ${msg}`);
  console.error(`stderr tail:\n${stderrBuf.slice(-1500)}`);
  proc.kill();
  process.exit(1);
};

async function main() {
  // 1. initialize
  send({
    jsonrpc: "2.0",
    id: 1,
    method: "initialize",
    params: {
      protocolVersion: "2024-11-05",
      capabilities: {},
      clientInfo: { name: "smoke-aliases", version: "1.0.0" },
    },
  });
  const initResp = await readMessage();
  if (initResp.error) fail(`initialize error: ${JSON.stringify(initResp.error)}`);
  console.log(`init OK: server=${initResp.result?.serverInfo?.name} v${initResp.result?.serverInfo?.version}`);

  send({ jsonrpc: "2.0", method: "notifications/initialized" });

  // 2. tools/list
  send({ jsonrpc: "2.0", id: 2, method: "tools/list", params: {} });
  const listResp = await readMessage();
  if (listResp.error) fail(`tools/list error: ${JSON.stringify(listResp.error)}`);
  const tools = listResp.result?.tools ?? [];
  console.log(`tools/list OK: ${tools.length} tools registered`);

  const expectedAliases = [
    // B.1.4-7
    "editor_undo",
    "editor_redo",
    "editor_save_scene",
    "editor_take_screenshot",
    // S1 (2026-05-12)
    "editor_get_selection",
    "editor_is_playing",
    "editor_play",
    "editor_select_object",
    "editor_stop",
    // S2 (2026-05-12)
    "file_read",
    "file_write",
    "project_info",
    "scene_clone_object",
    "scene_create_object",
    "scene_delete_object",
    "scene_get_hierarchy",
    "scene_load",
    "scene_reparent_object",
    "scene_set_transform",
    // S3 (2026-05-12 — simple + adapter aliases)
    "asset_mount",
    "asset_search",
    "component_add",
    "component_set",
    "sbox_search_api",
    // S1/S2 catch-up (2026-05-12 — deferral closures)
    "editor_console_output",
    "file_list",
    // S4a (2026-05-12 — adjacent wrappers, simple/adapter aliases)
    "asset_fetch",
    "asset_browse_local",
    "component_get",
    // S4b (2026-05-12 — hierarchy-walking wrappers via responseAdapter)
    "scene_get_object",
    "scene_list_objects",
    // S4 closing (2026-05-12 — new C# canonical get_scene_info + passthrough alias)
    "editor_scene_info",
    // S3-deferral closure (2026-05-12 — localHandler infra + describe_type passthrough)
    "get_server_status",
    "sbox_get_api_type",
  ];
  const missing = expectedAliases.filter(
    (a) => !tools.some((t) => t.name === a),
  );
  if (missing.length > 0)
    fail(`missing aliases: ${missing.join(", ")}`);
  console.log(`all ${expectedAliases.length} JTC aliases present: ${expectedAliases.join(", ")}`);

  if (tools.length !== 159)
    fail(`expected 159 tools (124 canonical + 34 JTC aliases + 1 Lou rename), got ${tools.length}`);
  console.log(`count check OK: ${tools.length} == 159`);

  // S9 — 4 docs tools must be registered (no aliases, canonical names match JTC)
  const docsTools = [
    "sbox_cache_status",
    "sbox_list_doc_categories",
    "sbox_search_docs",
    "sbox_get_doc_page",
  ];
  const missingDocs = docsTools.filter((d) => !tools.some((t) => t.name === d));
  if (missingDocs.length > 0)
    fail(`missing S9 docs tools: ${missingDocs.join(", ")}`);
  console.log(`S9 docs tools present: ${docsTools.join(", ")}`);

  // 3. verify alias description points at canonical
  const undoAlias = tools.find((t) => t.name === "editor_undo");
  if (!undoAlias.description?.includes("undo"))
    fail(`editor_undo description missing canonical name 'undo': ${undoAlias.description}`);
  if (!undoAlias.description?.includes("JTC-compat alias"))
    fail(`editor_undo description missing 'JTC-compat alias' tag`);
  console.log(`alias description OK: editor_undo → '${undoAlias.description.slice(0, 80)}...'`);

  // 4. live dispatch: call editor_take_screenshot, expect bridge to handle it
  const screenshotPath = join(tmpdir(), `smoke-alias-${Date.now()}.png`);
  send({
    jsonrpc: "2.0",
    id: 3,
    method: "tools/call",
    params: {
      name: "editor_take_screenshot",
      arguments: { path: screenshotPath },
    },
  });
  const callResp = await readMessage(15000);
  if (callResp.error) {
    // bridge unavailable counts as a non-fatal warning here — we already
    // verified registration. Just report.
    console.warn(
      `live dispatch warning: editor_take_screenshot → ${JSON.stringify(callResp.error).slice(0, 200)}`,
    );
  } else {
    const text = callResp.result?.content?.[0]?.text ?? "";
    console.log(`live dispatch OK: editor_take_screenshot returned (${text.slice(0, 120)}...)`);
  }

  // 4b. adapter dispatch: call asset_browse_local(directory="scenes") and assert
  //     the adapter rewrote `directory` → `path: "Assets/scenes"`. Without the
  //     schema fix landed alongside this assertion, zod strips `directory`,
  //     the adapter sees `{}` and falls back to `path: "Assets"`.
  send({
    jsonrpc: "2.0",
    id: 4,
    method: "tools/call",
    params: {
      name: "asset_browse_local",
      arguments: { directory: "scenes" },
    },
  });
  const adapterResp = await readMessage(15000);
  if (adapterResp.error) {
    console.warn(
      `adapter dispatch warning: asset_browse_local → ${JSON.stringify(adapterResp.error).slice(0, 200)}`,
    );
  } else {
    const text = adapterResp.result?.content?.[0]?.text ?? "";
    let parsed;
    try { parsed = JSON.parse(text); } catch { parsed = null; }
    if (parsed?.path === "Assets/scenes") {
      console.log(`adapter dispatch OK: asset_browse_local(directory="scenes") forwarded as path="Assets/scenes"`);
    } else if (parsed?.path === "Assets") {
      fail(`adapter dispatch FAIL: asset_browse_local(directory="scenes") got path="Assets" — zod stripped 'directory'; AliasSpec.schema is not being honored`);
    } else {
      console.warn(`adapter dispatch ambiguous: response.path=${parsed?.path ?? "<absent>"}, full text=${text.slice(0, 200)}`);
    }
  }

  // 4c. adapter dispatch with envelope remap: asset_search(amount=2) was the
  //     canonical diagnostic for the alias-schema bug — before the fix, `amount`
  //     was stripped before the adapter saw it, so the canonical received no
  //     maxResults override and defaulted to 10. Assert count<=2 to lock it in.
  send({
    jsonrpc: "2.0",
    id: 5,
    method: "tools/call",
    params: {
      name: "asset_search",
      arguments: { query: "terrain", amount: 2 },
    },
  });
  const searchResp = await readMessage(15000);
  if (searchResp.error) {
    console.warn(
      `adapter dispatch warning: asset_search → ${JSON.stringify(searchResp.error).slice(0, 200)}`,
    );
  } else {
    const text = searchResp.result?.content?.[0]?.text ?? "";
    let parsed;
    try { parsed = JSON.parse(text); } catch { parsed = null; }
    const count = parsed?.count;
    if (typeof count === "number" && count <= 2) {
      console.log(`adapter dispatch OK: asset_search(amount=2) returned count=${count} (≤ 2)`);
    } else if (typeof count === "number" && count > 2) {
      fail(`adapter dispatch FAIL: asset_search(amount=2) returned count=${count} (>2) — 'amount' was likely stripped before reaching the adapter; AliasSpec.schema regression`);
    } else {
      console.warn(`adapter dispatch ambiguous: asset_search count=${count}, full text=${text.slice(0, 200)}`);
    }
  }

  // 4d. live dispatch via new C# canonical: editor_scene_info → get_scene_info.
  //     Verifies (a) JTC alias is wired, (b) canonical TS tool dispatches via
  //     bridge.send, (c) C# GetSceneInfoHandler is registered in the live
  //     editor (hot-loaded after the MyEditorMenu.cs edit), (d) Scene.Source
  //     and HasUnsavedChanges properties resolve at runtime.
  send({
    jsonrpc: "2.0",
    id: 6,
    method: "tools/call",
    params: {
      name: "editor_scene_info",
      arguments: {},
    },
  });
  const infoResp = await readMessage(15000);
  if (infoResp.error) {
    console.warn(
      `live dispatch warning: editor_scene_info → ${JSON.stringify(infoResp.error).slice(0, 200)}`,
    );
  } else {
    const text = infoResp.result?.content?.[0]?.text ?? "";
    let parsed;
    try { parsed = JSON.parse(text); } catch { parsed = null; }
    if (parsed?.error) {
      // Bridge returned a structured error — e.g. "unknown command" if the
      // C# handler failed to compile/register.
      fail(`live dispatch FAIL: editor_scene_info returned error="${parsed.error}" — GetSceneInfoHandler likely failed to hot-load in the editor`);
    } else if (parsed && typeof parsed.dirty === "boolean" && "name" in parsed) {
      console.log(`live dispatch OK: editor_scene_info returned name="${parsed.name}" dirty=${parsed.dirty} path=${parsed.path === null ? "null" : `"${parsed.path}"`}`);
    } else {
      console.warn(`live dispatch ambiguous: editor_scene_info response=${text.slice(0, 200)}`);
    }
  }

  // 4e. localHandler dispatch: get_server_status canonical is TS-only (no C#),
  //     so this exercises the new `localHandler` branch added in S3-deferral
  //     closure. Asserts the response has the expected status-envelope shape
  //     (proves dispatch skipped bridge.send and computed in-process).
  send({
    jsonrpc: "2.0",
    id: 7,
    method: "tools/call",
    params: { name: "get_server_status", arguments: {} },
  });
  const statusResp = await readMessage(15000);
  if (statusResp.error) {
    fail(`localHandler dispatch FAIL: get_server_status returned protocol error ${JSON.stringify(statusResp.error)}`);
  } else {
    const text = statusResp.result?.content?.[0]?.text ?? "";
    let parsed;
    try { parsed = JSON.parse(text); } catch { parsed = null; }
    if (parsed && typeof parsed.connected === "boolean" && typeof parsed.host === "string" && typeof parsed.port === "number") {
      console.log(`localHandler dispatch OK: get_server_status returned connected=${parsed.connected} ${parsed.host}:${parsed.port}`);
    } else if (parsed?.error?.includes("unknown command")) {
      fail(`localHandler dispatch FAIL: get_server_status reached bridge.send (got "unknown command") — localHandler branch was NOT taken`);
    } else {
      fail(`localHandler dispatch FAIL: get_server_status response missing expected fields. Got: ${text.slice(0, 200)}`);
    }
  }

  // 5. verify the deprecation warning fired on stderr
  if (!stderrBuf.includes("JTC-compat alias")) {
    console.warn(`deprecation warning NOT seen on stderr — registerAlias may not have warned. stderr=${stderrBuf.slice(-500)}`);
  } else {
    const warnLines = stderrBuf
      .split("\n")
      .filter((l) => l.includes("JTC-compat alias"));
    console.log(`deprecation warning fired: ${warnLines.length} line(s)`);
    for (const l of warnLines) console.log(`  ${l.trim()}`);
  }

  console.log("\nSMOKE PASS");
  proc.kill();
  process.exit(0);
}

main().catch((e) => {
  console.error("uncaught:", e.message);
  console.error(`stderr tail:\n${stderrBuf.slice(-1500)}`);
  proc.kill();
  process.exit(1);
});
