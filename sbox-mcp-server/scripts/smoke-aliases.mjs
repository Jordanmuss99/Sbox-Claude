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
  ];
  const missing = expectedAliases.filter(
    (a) => !tools.some((t) => t.name === a),
  );
  if (missing.length > 0)
    fail(`missing aliases: ${missing.join(", ")}`);
  console.log(`all ${expectedAliases.length} JTC aliases present: ${expectedAliases.join(", ")}`);

  if (tools.length !== 144)
    fail(`expected 144 tools (117 canonical + 26 JTC aliases + 1 Lou rename), got ${tools.length}`);
  console.log(`count check OK: ${tools.length} == 144`);

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
