#!/usr/bin/env node
// inject-status.mjs — read .omc/status.json and replace BEGIN/END STATUS blocks
// in target markdown files.
//
// Phase 0.4 deliverable. Target files declare templated sections:
//
//   <!-- BEGIN STATUS:counts -->
//     anything in here is regenerated
//   <!-- END STATUS:counts -->
//
// Multiple named blocks are supported. The block name maps to a generator in
// `RENDERERS` below. Unknown names are left untouched (forward-compat).
//
// Run via:
//   node sbox-mcp-server/scripts/inject-status.mjs            # dry-run, prints diffs
//   node sbox-mcp-server/scripts/inject-status.mjs --write    # actually writes
//   npm run docs:inject-status -- --write                     # via npm
//
// Exit code 0 on success, 1 on file/parse error.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname  = dirname(__filename);
const SERVER_ROOT = resolve(__dirname, "..");
const REPO_ROOT   = resolve(SERVER_ROOT, "..");
const STATUS_IN   = join(REPO_ROOT, ".omc", "status.json");

// Targets: repo files always considered, plus optional local skill files at
//   ~/.claude/skills/sbox-game-dev/SKILL.md   (Claude Code skill location)
//   ~/.agents/skills/sbox-game-dev/SKILL.md   (opencode / ohmyopencode-agent skill location)
// if a developer has installed the skill locally. Missing targets are skipped
// silently, not an error.
const TARGETS = [
  join(REPO_ROOT, "README.md"),
  join(REPO_ROOT, "CLAUDE.md"),
  join(SERVER_ROOT, "README.md"),
  join(homedir(), ".claude", "skills", "sbox-game-dev", "SKILL.md"),
  join(homedir(), ".agents", "skills", "sbox-game-dev", "SKILL.md"),
];

const RENDERERS = {
  counts(status) {
    return [
      `**${status.canonical_ts} canonical TS tools** / ${status.cs_handlers} C# handlers / ${status.ts_only} TS-only / ${status.jtc_aliases} JTC-compat aliases + ${status.lou_renames} Lou-rename = **${status.runtime_registered_total} runtime-registered total**.`,
      ``,
      `MCP server: \`${status.mcp_version}\`. Addon protocol: \`v${status.addon_protocol_version}\`. Generated: \`${status.generated_at}\`.`,
    ].join("\n");
  },
  inventory(status) {
    return [
      `| metric | count |`,
      `|---|---:|`,
      `| canonical TS tools | ${status.canonical_ts} |`,
      `| C# handlers | ${status.cs_handlers} |`,
      `| TS-only allowlist | ${status.ts_only} |`,
      `| JTC-compat aliases | ${status.jtc_aliases} |`,
      `| Lou renames | ${status.lou_renames} |`,
      `| runtime-registered total | ${status.runtime_registered_total} |`,
    ].join("\n");
  },
};

function injectInto(content, status) {
  const re = /(<!--\s*BEGIN STATUS:([a-z_-]+)\s*-->)([\s\S]*?)(<!--\s*END STATUS:\2\s*-->)/g;
  let unknownBlocks = [];
  const updated = content.replace(re, (full, begin, name, _body, end) => {
    const renderer = RENDERERS[name];
    if (!renderer) {
      unknownBlocks.push(name);
      return full;
    }
    return `${begin}\n${renderer(status)}\n${end}`;
  });
  return { updated, unknownBlocks };
}

function main() {
  const writeFlag = process.argv.includes("--write");

  if (!existsSync(STATUS_IN)) {
    console.error(`Missing ${STATUS_IN}. Run \`node scripts/gen-status.mjs\` first.`);
    process.exit(1);
  }
  const status = JSON.parse(readFileSync(STATUS_IN, "utf8"));

  let anyChange = false;
  for (const target of TARGETS) {
    if (!existsSync(target)) {
      console.log(`skip (missing): ${target}`);
      continue;
    }
    const original = readFileSync(target, "utf8");
    const { updated, unknownBlocks } = injectInto(original, status);
    if (unknownBlocks.length > 0) {
      console.log(`note: ${target}: ${unknownBlocks.length} unknown block(s) (left untouched): ${unknownBlocks.join(", ")}`);
    }
    if (updated === original) {
      console.log(`unchanged: ${target}`);
      continue;
    }
    anyChange = true;
    if (writeFlag) {
      writeFileSync(target, updated);
      console.log(`WROTE: ${target}`);
    } else {
      console.log(`DRIFT: ${target} (re-run with --write to apply)`);
    }
  }

  if (!writeFlag && anyChange) {
    console.log("");
    console.log("Drift detected. Re-run with --write to apply, or commit before running docs:sync.");
    process.exit(2); // distinct exit code so CI / git hooks can detect drift
  }
  process.exit(0);
}

main();
