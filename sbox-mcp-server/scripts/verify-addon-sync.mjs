#!/usr/bin/env node
// verify-addon-sync.mjs — SHA256 drift check between canonical addon and live copy.
//
// Phase 0.2 deliverable. Runnable two ways:
//
//   1. Standalone:  `node scripts/verify-addon-sync.mjs`
//      Reads SBOX_PROJECT_LIB env or first CLI arg, compares SHA256.
//      Exit code 0 = match (or no target configured), 1 = drift detected, 2 = missing files.
//
//   2. Inside vitest: imported by test/addon-sync.test.ts which calls
//      `verifyAddonSync()` and asserts.
//
// Intentional design: when no sync target is configured, this is a no-op
// (exit 0) so `npm test` works in CI / on a clean clone. Local developers who
// have run `sync-addon.{ps1,sh}` and set SBOX_PROJECT_LIB get the drift check
// for free.

import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname  = dirname(__filename);
const REPO_ROOT  = resolve(__dirname, "..", "..");
const CANONICAL  = join(REPO_ROOT, "sbox-bridge-addon", "Editor", "MyEditorMenu.cs");

function sha256(path) {
  const h = createHash("sha256");
  h.update(readFileSync(path));
  return h.digest("hex").toUpperCase();
}

/**
 * @returns {{ status: "match"|"drift"|"skipped"|"missing-canonical"|"missing-target",
 *             canonicalHash: string|null, targetHash: string|null,
 *             targetPath: string|null, message: string }}
 */
export function verifyAddonSync(targetLib = process.env.SBOX_PROJECT_LIB) {
  if (!existsSync(CANONICAL)) {
    return {
      status: "missing-canonical",
      canonicalHash: null,
      targetHash: null,
      targetPath: null,
      message: `Canonical addon not found: ${CANONICAL}`,
    };
  }

  const canonicalHash = sha256(CANONICAL);

  if (!targetLib) {
    return {
      status: "skipped",
      canonicalHash,
      targetHash: null,
      targetPath: null,
      message: "No sync target configured (set SBOX_PROJECT_LIB to enable drift detection).",
    };
  }

  const targetPath = join(targetLib, "Editor", "MyEditorMenu.cs");
  if (!existsSync(targetPath)) {
    return {
      status: "missing-target",
      canonicalHash,
      targetHash: null,
      targetPath,
      message: `Target file not found: ${targetPath}. Run scripts/sync-addon.{ps1,sh} first.`,
    };
  }

  const targetHash = sha256(targetPath);
  if (canonicalHash !== targetHash) {
    return {
      status: "drift",
      canonicalHash,
      targetHash,
      targetPath,
      message:
        `Drift detected.\n` +
        `  Canonical (${CANONICAL}): ${canonicalHash}\n` +
        `  Target    (${targetPath}): ${targetHash}\n` +
        `Run scripts/sync-addon.{ps1,sh} to resync.`,
    };
  }

  return {
    status: "match",
    canonicalHash,
    targetHash,
    targetPath,
    message: `In sync: ${canonicalHash}`,
  };
}

// Standalone entrypoint
if (process.argv[1] === __filename) {
  const argTarget = process.argv[2] && !process.argv[2].startsWith("-")
    ? process.argv[2]
    : undefined;
  const result = verifyAddonSync(argTarget);
  console.log(result.message);
  switch (result.status) {
    case "match":
    case "skipped":
      process.exit(0);
    case "drift":
    case "missing-target":
      process.exit(1);
    case "missing-canonical":
      process.exit(2);
    default:
      process.exit(3);
  }
}
