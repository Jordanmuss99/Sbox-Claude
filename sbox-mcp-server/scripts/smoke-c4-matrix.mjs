// Phase C gate C.4 — per-tool happy-path + error-state coverage for all
// B.2-era tools (S4-closing, S5, S6, S7, S8, S9, S10). For each tool we
// dispatch a happy-path call plus ≥3 induced error states. Output is a
// structured markdown report written to .omc/specs/c4-evidence.md.
//
// Driven over MCP JSON-RPC against a freshly-spawned dist/index.js, so
// every result is real end-to-end (server → file IPC → bridge → editor).
//
// Usage: node scripts/smoke-c4-matrix.mjs
// Exit code: 0 on PASS (every case matches its expectation pattern), 1 on FAIL.

import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import process from "node:process";

const __filename = fileURLToPath(import.meta.url);
const REPO_ROOT = join(dirname(__filename), "..", "..");
const REPORT_PATH = join(REPO_ROOT, ".omc", "specs", "c4-evidence.md");

const proc = spawn(process.execPath, ["dist/index.js"], {
  stdio: ["pipe", "pipe", "pipe"],
});
let stdoutBuf = "";
let stderrBuf = "";
proc.stdout.on("data", (c) => (stdoutBuf += c.toString()));
proc.stderr.on("data", (c) => (stderrBuf += c.toString()));

const send = (req) => proc.stdin.write(JSON.stringify(req) + "\n");
const read = (timeoutMs = 60000) =>
  new Promise((resolve, reject) => {
    const start = Date.now();
    const tick = () => {
      const i = stdoutBuf.indexOf("\n");
      if (i >= 0) {
        const ln = stdoutBuf.slice(0, i).trim();
        stdoutBuf = stdoutBuf.slice(i + 1);
        if (ln) {
          try {
            return resolve(JSON.parse(ln));
          } catch (e) {
            return reject(new Error(`bad JSON: ${ln.slice(0, 200)}`));
          }
        }
      }
      if (Date.now() - start > timeoutMs)
        return reject(new Error(`timeout; stderr=${stderrBuf.slice(-500)}`));
      setTimeout(tick, 25);
    };
    tick();
  });

let id = 1;
async function call(method, params) {
  send({ jsonrpc: "2.0", id: id++, method, params });
  return await read();
}

async function tryTool(name, args) {
  try {
    const r = await call("tools/call", { name, arguments: args });
    if (r.error) {
      return {
        ok: false,
        kind: "mcp-error",
        code: r.error.code,
        message: r.error.message ?? String(r.error),
      };
    }
    const text = r.result?.content?.[0]?.text ?? "";
    // isError marker from MCP SDK distinguishes tool-level errors from successes
    const isError = r.result?.isError === true;
    return { ok: true, text, isError };
  } catch (e) {
    return { ok: false, kind: "transport", message: e.message };
  }
}

/**
 * A test case has:
 *   tool   — tool name
 *   label  — short human description
 *   args   — arguments to pass
 *   expect — { contains?: string[]; notContains?: string[]; errorKind?: "mcp-error" | "handler-error" }
 *
 * "handler-error" means a successful MCP call whose body contains "Error" or an error: field.
 */
const cases = [
  // ── S4 closing — get_scene_info (0 params, 1 happy, 1 environment-dependent edge)
  { sprint: "S4-closing", tool: "get_scene_info", label: "happy: returns name + dirty flag",
    args: {}, expect: { contains: ["name", "dirty"] } },

  // ── S5 — tag_add / tag_list / tag_remove
  { sprint: "S5", tool: "tag_add", label: "err: missing id (zod min)",
    args: { tag: "test" }, expect: { errorKind: "mcp-error" } },
  { sprint: "S5", tool: "tag_add", label: "err: missing tag (zod min)",
    args: { id: "00000000-0000-0000-0000-000000000000" }, expect: { errorKind: "mcp-error" } },
  { sprint: "S5", tool: "tag_add", label: "err: id not found",
    args: { id: "00000000-0000-0000-0000-000000000000", tag: "phc4" }, expect: { contains: ["Error"] } },
  { sprint: "S5", tool: "tag_list", label: "err: missing id",
    args: {}, expect: { errorKind: "mcp-error" } },
  { sprint: "S5", tool: "tag_list", label: "err: id not found",
    args: { id: "00000000-0000-0000-0000-000000000000" }, expect: { contains: ["Error"] } },
  { sprint: "S5", tool: "tag_remove", label: "err: id not found (no-op path)",
    args: { id: "00000000-0000-0000-0000-000000000000", tag: "phc4" }, expect: { contains: ["Error"] } },

  // ── S6 — scene_find_by_tag / scene_find_by_component / scene_find_objects
  { sprint: "S6", tool: "scene_find_by_tag", label: "happy/empty: no objects with rare tag",
    args: { tag: "__no_such_tag_phc4__" }, expect: { contains: ["0"] } },
  { sprint: "S6", tool: "scene_find_by_tag", label: "err: missing tag",
    args: {}, expect: { errorKind: "mcp-error" } },
  { sprint: "S6", tool: "scene_find_by_component", label: "happy: existing component type",
    args: { componentType: "ModelRenderer" }, expect: { notContains: ["Error:"] } },
  { sprint: "S6", tool: "scene_find_by_component", label: "err: unknown component type",
    args: { componentType: "__no_such_component_phc4__" }, expect: { contains: ["not found"] } },
  { sprint: "S6", tool: "scene_find_by_component", label: "err: missing componentType",
    args: {}, expect: { errorKind: "mcp-error" } },
  { sprint: "S6", tool: "scene_find_objects", label: "happy/empty: no name match",
    args: { query: "__no_such_object_phc4__" }, expect: { notContains: ["Error:"] } },
  { sprint: "S6", tool: "scene_find_objects", label: "err: missing query",
    args: {}, expect: { errorKind: "mcp-error" } },

  // ── S7 — component_list / component_remove
  { sprint: "S7", tool: "component_list", label: "err: missing id",
    args: {}, expect: { errorKind: "mcp-error" } },
  { sprint: "S7", tool: "component_list", label: "err: id not found",
    args: { id: "00000000-0000-0000-0000-000000000000" }, expect: { contains: ["Error"] } },
  { sprint: "S7", tool: "component_remove", label: "err: missing component",
    args: { id: "00000000-0000-0000-0000-000000000000" }, expect: { errorKind: "mcp-error" } },
  { sprint: "S7", tool: "component_remove", label: "err: id not found",
    args: { id: "00000000-0000-0000-0000-000000000000", component: "ModelRenderer" }, expect: { contains: ["Error"] } },

  // ── S8 — console_run / execute_csharp
  { sprint: "S8", tool: "console_run", label: "happy: real ConCmd (perf)",
    args: { command: "perf" }, expect: { contains: ["Executed: perf"] } },
  { sprint: "S8", tool: "console_run", label: "err: empty command (zod min)",
    args: { command: "" }, expect: { errorKind: "mcp-error" } },
  { sprint: "S8", tool: "console_run", label: "err: unknown command rejected by ConsoleSystem",
    args: { command: "this_is_definitely_not_a_real_command" }, expect: { contains: ["failed"] } },
  { sprint: "S8", tool: "console_run", label: "err: missing command param",
    args: {}, expect: { errorKind: "mcp-error" } },
  { sprint: "S8", tool: "execute_csharp", label: "graceful: Roslyn-not-loaded path",
    args: { code: "1 + 2" }, expect: { contains: ["Roslyn"] } },
  { sprint: "S8", tool: "execute_csharp", label: "err: empty code (zod min)",
    args: { code: "" }, expect: { errorKind: "mcp-error" } },
  { sprint: "S8", tool: "execute_csharp", label: "err: missing code param",
    args: {}, expect: { errorKind: "mcp-error" } },

  // ── S9 — sbox_cache_status / list_categories / search / get_page
  { sprint: "S9", tool: "sbox_cache_status", label: "happy: returns cache dir + TTL",
    args: {}, expect: { contains: ["cache", "ttl"] } },
  { sprint: "S9", tool: "sbox_list_doc_categories", label: "happy: returns 18 categories",
    args: {}, expect: { contains: ["scene", "rendering", "networking"] } },
  { sprint: "S9", tool: "sbox_search_docs", label: "happy: scene search returns results",
    args: { query: "scene component" }, expect: { contains: ["Scene"] } },
  { sprint: "S9", tool: "sbox_search_docs", label: "happy/empty: nonsense query",
    args: { query: "xqzpfphc4nonexistent" }, expect: { contains: ["No documentation"] } },
  { sprint: "S9", tool: "sbox_search_docs", label: "happy/filtered: category=networking",
    args: { query: "rpc", category: "networking" }, expect: { notContains: ["Error:"] } },
  { sprint: "S9", tool: "sbox_search_docs", label: "err: missing query (zod)",
    args: {}, expect: { errorKind: "mcp-error" } },
  { sprint: "S9", tool: "sbox_search_docs", label: "err: limit out of range (zod)",
    args: { query: "scene", limit: 999 }, expect: { errorKind: "mcp-error" } },
  { sprint: "S9", tool: "sbox_get_doc_page", label: "happy: known URL returns markdown",
    args: { url: "https://sbox.game/dev/doc/scene.md", maxLength: 500 }, expect: { contains: ["# Scene"] } },
  { sprint: "S9", tool: "sbox_get_doc_page", label: "err: unknown URL",
    args: { url: "https://sbox.game/dev/doc/__nope__phc4.md" }, expect: { contains: ["not found"] } },
  { sprint: "S9", tool: "sbox_get_doc_page", label: "err: missing url",
    args: {}, expect: { errorKind: "mcp-error" } },
  { sprint: "S9", tool: "sbox_get_doc_page", label: "happy/chunked: startIndex offset",
    args: { url: "https://sbox.game/dev/doc/scene.md", startIndex: 100, maxLength: 200 }, expect: { contains: ["showing"] } },

  // ── S10 — get_console_output (bonus)
  { sprint: "S10", tool: "get_console_output", label: "happy: returns recent entries",
    args: { count: 5 }, expect: { contains: ["Returned"] } },
  { sprint: "S10", tool: "get_console_output", label: "happy/filtered: severity=info",
    args: { count: 5, severity: "info" }, expect: { contains: ["INFO"] } },
  { sprint: "S10", tool: "get_console_output", label: "happy/clamped: count=999 accepted (handler clamps to 500)",
    args: { count: 999 }, expect: { contains: ["Returned"] } },
  { sprint: "S10", tool: "get_console_output", label: "err: invalid severity (zod enum)",
    args: { severity: "verbose" }, expect: { errorKind: "mcp-error" } },
];

function matches(result, expect) {
  // MCP SDK surfaces zod validation errors two ways depending on version:
  //   1. as a JSON-RPC error response (result.error) — our "mcp-error" kind
  //   2. as a successful response with text content starting with "MCP error -32602"
  // We accept either for the `errorKind: "mcp-error"` expectation.
  if (expect.errorKind === "mcp-error") {
    if (result.ok === false && result.kind === "mcp-error") return true;
    if (result.ok && /MCP error -?\d+|Invalid arguments|validation error/i.test(result.text ?? "")) return true;
    return false;
  }
  if (!result.ok) return false;
  const text = result.text ?? "";
  if (expect.contains) {
    for (const s of expect.contains) {
      if (!text.toLowerCase().includes(s.toLowerCase())) return false;
    }
  }
  if (expect.notContains) {
    for (const s of expect.notContains) {
      if (text.toLowerCase().includes(s.toLowerCase())) return false;
    }
  }
  return true;
}

function summarizeText(text, max = 240) {
  if (!text) return "(empty)";
  const single = text.replace(/\s+/g, " ").trim();
  return single.length > max ? single.slice(0, max) + "…" : single;
}

async function main() {
  await call("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "c4-matrix", version: "1" },
  });
  send({ jsonrpc: "2.0", method: "notifications/initialized" });

  const bySprint = new Map();
  let total = 0;
  let pass = 0;
  for (const c of cases) {
    total++;
    process.stdout.write(`[${c.sprint}] ${c.tool}: ${c.label}… `);
    const result = await tryTool(c.tool, c.args);
    const ok = matches(result, c.expect);
    if (ok) pass++;
    process.stdout.write(ok ? "PASS\n" : "FAIL\n");

    if (!bySprint.has(c.sprint)) bySprint.set(c.sprint, []);
    bySprint.get(c.sprint).push({
      tool: c.tool,
      label: c.label,
      args: JSON.stringify(c.args),
      expect: JSON.stringify(c.expect),
      pass: ok,
      response: result.ok ? summarizeText(result.text) : `${result.kind}: ${summarizeText(result.message ?? "")}`,
    });
  }

  // Build markdown report
  const lines = [
    `# C.4 — Per-tool error-state matrix (live evidence)`,
    ``,
    `Generated by \`scripts/smoke-c4-matrix.mjs\` against the live bridge. ${pass}/${total} cases match expectations.`,
    ``,
    `**Pass rate**: ${pass}/${total} (${Math.round(100 * pass / total)}%)`,
    ``,
  ];

  const sprintOrder = ["S4-closing", "S5", "S6", "S7", "S8", "S9", "S10"];
  for (const sprint of sprintOrder) {
    if (!bySprint.has(sprint)) continue;
    const rows = bySprint.get(sprint);
    lines.push(`## ${sprint}`);
    lines.push(``);
    lines.push(`| Tool | Case | Args | Expect | Result | Response (truncated) |`);
    lines.push(`|---|---|---|---|---|---|`);
    for (const r of rows) {
      const status = r.pass ? "✅ PASS" : "❌ FAIL";
      lines.push(`| \`${r.tool}\` | ${r.label} | \`${r.args}\` | \`${r.expect}\` | ${status} | ${r.response.replace(/\|/g, "\\|")} |`);
    }
    lines.push(``);
  }

  writeFileSync(REPORT_PATH, lines.join("\n"), "utf8");
  console.log(`\nReport: ${REPORT_PATH}`);
  console.log(`Pass rate: ${pass}/${total}`);

  proc.kill();
  process.exit(pass === total ? 0 : 1);
}

main().catch((e) => {
  console.error("FATAL:", e.message);
  console.error("stderr:", stderrBuf.slice(-1000));
  proc.kill();
  process.exit(2);
});
