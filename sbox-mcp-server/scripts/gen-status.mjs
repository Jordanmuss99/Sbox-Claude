#!/usr/bin/env node
// gen-status.mjs — scan tools/handlers/aliases and emit .omc/status.json.
//
// Phase 0.4 deliverable. Single source of truth for the "tool inventory"
// counts that appear in CLAUDE.md / README.md / CHANGELOG.md / SKILL.md.
//
// Run via:
//   node sbox-mcp-server/scripts/gen-status.mjs
//   npm run docs:gen-status     (from sbox-mcp-server/)
//
// Inputs (all scanned the same way the parity test does — regex on source):
//   - sbox-mcp-server/src/tools/*.ts  → `server.tool("X", ...)`  → canonical TS
//   - sbox-bridge-addon/Editor/MyEditorMenu.cs → `Register("X", ...)` → C# handlers
//   - sbox-mcp-server/src/ts-only-tools.json   → TS-only allowlist
//   - sbox-mcp-server/src/tools/jtc-aliases.ts → JTC_ALIASES, LOU_RENAMES
//   - sbox-mcp-server/package.json             → mcp_version
//   - sbox-bridge-addon/Editor/MyEditorMenu.cs → addon protocol_version
//
// Output: <repo>/.omc/status.json
//   {
//     "generated_at": "...",
//     "mcp_version": "1.4.0",
//     "addon_protocol_version": 1,
//     "canonical_ts": 136, "cs_handlers": 121, "ts_only": 15,
//     "jtc_aliases": 34, "lou_renames": 1,
//     "runtime_registered_total": 171
//   }
//
// Exit code 0 on success, 1 on input read failure.

import { readFileSync, readdirSync, mkdirSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname  = dirname(__filename);
const SERVER_ROOT = resolve(__dirname, "..");
const REPO_ROOT   = resolve(SERVER_ROOT, "..");
const STATUS_OUT  = join(REPO_ROOT, ".omc", "status.json");

const TS_TOOLS_DIR    = join(SERVER_ROOT, "src", "tools");
const CS_HANDLERS_FILE = join(REPO_ROOT, "sbox-bridge-addon", "Editor", "MyEditorMenu.cs");
const TS_ONLY_FILE     = join(SERVER_ROOT, "src", "ts-only-tools.json");
const ALIASES_FILE     = join(SERVER_ROOT, "src", "tools", "jtc-aliases.ts");
const PACKAGE_JSON     = join(SERVER_ROOT, "package.json");

function collectCanonicalTsTools() {
  const tools = new Set();
  const files = readdirSync(TS_TOOLS_DIR).filter(f => f.endsWith(".ts"));
  const re = /server\.tool\s*\(\s*["']([a-z0-9_]+)["']/g;
  for (const f of files) {
    const src = readFileSync(join(TS_TOOLS_DIR, f), "utf8");
    let m;
    while ((m = re.exec(src)) !== null) tools.add(m[1]);
  }
  return tools;
}

function collectCsHandlers() {
  const src = readFileSync(CS_HANDLERS_FILE, "utf8");
  const re = /Register\s*\(\s*"([a-z0-9_]+)"/g;
  const keys = new Set();
  let m;
  while ((m = re.exec(src)) !== null) keys.add(m[1]);
  return keys;
}

function readTsOnlyAllowlist() {
  const data = JSON.parse(readFileSync(TS_ONLY_FILE, "utf8"));
  return new Set(data.tools);
}

function countAliases() {
  const src = readFileSync(ALIASES_FILE, "utf8");
  // Count TOP-LEVEL keys in the JTC_ALIASES / LOU_RENAMES object literals.
  // The naive `^\s*ident:` regex over-counts because helper calls like
  // passthrough("x", { path: z.string(), width: z.number() }) contain nested
  // keys at deeper indentation. We constrain to EXACTLY 2-space indent, which
  // is the indent style used for top-level entries in both objects.
  function countBlock(name) {
    const re = new RegExp(
      String.raw`export const ${name}[^=]*=\s*\{([\s\S]*?)\n\}\s*;`,
      "m"
    );
    const match = src.match(re);
    if (!match) return 0;
    const body = match[1];
    // Top-level keys are at exactly 2-space indent. Anything deeper is nested
    // (inside AliasSpec / passthrough / remap call args).
    const keyRe = /^  (?!\s)([a-z][a-z0-9_]*)\s*:/gm;
    let n = 0;
    while (keyRe.exec(body) !== null) n++;
    return n;
  }
  return {
    jtc_aliases: countBlock("JTC_ALIASES"),
    lou_renames: countBlock("LOU_RENAMES"),
  };
}

function readMcpVersion() {
  const pkg = JSON.parse(readFileSync(PACKAGE_JSON, "utf8"));
  return pkg.version ?? "unknown";
}

function readAddonProtocolVersion() {
  const src = readFileSync(CS_HANDLERS_FILE, "utf8");
  const m = src.match(/public\s+const\s+int\s+PROTOCOL_VERSION\s*=\s*(\d+)/);
  return m ? parseInt(m[1], 10) : null;
}

function main() {
  const ts      = collectCanonicalTsTools();
  const cs      = collectCsHandlers();
  const tsOnly  = readTsOnlyAllowlist();
  const aliases = countAliases();

  const status = {
    generated_at: new Date().toISOString(),
    mcp_version: readMcpVersion(),
    addon_protocol_version: readAddonProtocolVersion(),
    canonical_ts: ts.size,
    cs_handlers: cs.size,
    ts_only: tsOnly.size,
    jtc_aliases: aliases.jtc_aliases,
    lou_renames: aliases.lou_renames,
    runtime_registered_total: ts.size + aliases.jtc_aliases + aliases.lou_renames,
  };

  mkdirSync(dirname(STATUS_OUT), { recursive: true });
  writeFileSync(STATUS_OUT, JSON.stringify(status, null, 2) + "\n");

  console.log(`Wrote ${STATUS_OUT}`);
  console.log(JSON.stringify(status, null, 2));
}

main();
