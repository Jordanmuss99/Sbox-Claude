import { describe, it, expect } from "vitest";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
// @ts-expect-error -- .mjs sibling, untyped.
import { verifyAddonSync } from "../scripts/verify-addon-sync.mjs";

const __filename = fileURLToPath(import.meta.url);
const __dirname  = dirname(__filename);
const SERVER_ROOT = join(__dirname, "..");
const REPO_ROOT   = join(SERVER_ROOT, "..");

describe("Phase 0.2 — addon-sync infrastructure", () => {
  it("canonical addon source exists at expected location", () => {
    const canonical = join(REPO_ROOT, "sbox-bridge-addon", "Editor", "MyEditorMenu.cs");
    expect(existsSync(canonical), `canonical missing: ${canonical}`).toBe(true);
  });

  it("canonical addon source declares itself canonical", () => {
    const canonical = join(REPO_ROOT, "sbox-bridge-addon", "Editor", "MyEditorMenu.cs");
    const text = readFileSync(canonical, "utf8");
    // Must mention "canonical" so a grep tells the truth.
    expect(text.toLowerCase()).toMatch(/canonical/);
  });

  it("sync-addon.ps1 script exists", () => {
    expect(existsSync(join(SERVER_ROOT, "scripts", "sync-addon.ps1"))).toBe(true);
  });

  it("sync-addon.sh script exists", () => {
    expect(existsSync(join(SERVER_ROOT, "scripts", "sync-addon.sh"))).toBe(true);
  });

  it("verify-addon-sync.mjs script exists", () => {
    expect(existsSync(join(SERVER_ROOT, "scripts", "verify-addon-sync.mjs"))).toBe(true);
  });

  it("verifyAddonSync() returns 'skipped' when no SBOX_PROJECT_LIB", () => {
    const result = verifyAddonSync(undefined);
    // Either skipped (canonical found, no target) or match (CI accidentally has target set)
    expect(["skipped", "match"]).toContain(result.status);
    expect(typeof result.canonicalHash).toBe("string");
    expect(result.canonicalHash).toMatch(/^[0-9A-F]{64}$/);
  });

  it("verifyAddonSync() detects missing-target when path doesn't exist", () => {
    const result = verifyAddonSync("/path/that/definitely/does/not/exist/xyzzy");
    expect(result.status).toBe("missing-target");
  });
});
