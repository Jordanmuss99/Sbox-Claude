import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
  JTC_ALIASES,
  LOU_RENAMES,
} from "../src/tools/jtc-aliases.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const REPO_ROOT = join(__dirname, "..", "..");
const SERVER_ROOT = join(REPO_ROOT, "sbox-mcp-server");
const TS_TOOLS_DIR = join(SERVER_ROOT, "src", "tools");
const INDEX_TS = join(SERVER_ROOT, "src", "index.ts");
const CS_HANDLERS_FILE = join(
  REPO_ROOT,
  "sbox-bridge-addon",
  "Editor",
  "MyEditorMenu.cs",
);
const TS_ONLY_ALLOWLIST_FILE = join(
  SERVER_ROOT,
  "src",
  "ts-only-tools.json",
);

function collectCanonicalTsTools(): Set<string> {
  const tools = new Set<string>();
  const files = readdirSync(TS_TOOLS_DIR).filter((f) => f.endsWith(".ts"));
  const re = /server\.tool\s*\(\s*["']([a-z0-9_]+)["']/g;
  for (const f of files) {
    const src = readFileSync(join(TS_TOOLS_DIR, f), "utf8");
    let m: RegExpExecArray | null;
    while ((m = re.exec(src)) !== null) {
      tools.add(m[1]);
    }
  }
  return tools;
}

function collectCsHandlers(): Set<string> {
  const src = readFileSync(CS_HANDLERS_FILE, "utf8");
  const re = /Register\s*\(\s*"([a-z0-9_]+)"/g;
  const keys = new Set<string>();
  let m: RegExpExecArray | null;
  while ((m = re.exec(src)) !== null) {
    keys.add(m[1]);
  }
  return keys;
}

function loadTsOnlyAllowlist(): Set<string> {
  const data = JSON.parse(readFileSync(TS_ONLY_ALLOWLIST_FILE, "utf8"));
  return new Set(data.tools as string[]);
}

function aliasCanonical(v: string | { canonical: string }): string {
  return typeof v === "string" ? v : v.canonical;
}

const ALL_ALIASES: Record<string, string> = {
  ...Object.fromEntries(
    Object.entries(JTC_ALIASES).map(([k, v]) => [k, aliasCanonical(v)]),
  ),
  ...LOU_RENAMES,
};

describe("B.1.1 — canonical TS<->C# handler-set parity", () => {
  const ts = collectCanonicalTsTools();
  const cs = collectCsHandlers();
  const tsOnly = loadTsOnlyAllowlist();

  it("every canonical TS tool has a C# handler OR is in TS-only allowlist", () => {
    const unmatched = [...ts].filter((t) => !cs.has(t) && !tsOnly.has(t));
    expect(
      unmatched,
      `Canonical TS tools without C# handler and not in TS-only allowlist:\n  ${unmatched.join("\n  ")}`,
    ).toEqual([]);
  });

  it("every C# handler has a corresponding canonical TS tool", () => {
    const orphans = [...cs].filter((c) => !ts.has(c));
    expect(
      orphans,
      `C# handlers with no TS tool (orphans):\n  ${orphans.join("\n  ")}`,
    ).toEqual([]);
  });

  it("no entry in TS-only allowlist also has a C# handler", () => {
    const conflicting = [...tsOnly].filter((t) => cs.has(t));
    expect(
      conflicting,
      `Tools in TS-only allowlist that also have a C# handler:\n  ${conflicting.join("\n  ")}`,
    ).toEqual([]);
  });

  it("every entry in TS-only allowlist is actually a TS tool", () => {
    const stale = [...tsOnly].filter((t) => !ts.has(t));
    expect(
      stale,
      `Tools in TS-only allowlist that are NOT registered as TS tools:\n  ${stale.join("\n  ")}`,
    ).toEqual([]);
  });
});

describe("B.1.4-7 + B.1.11 + S3 — alias registry integrity (JTC + Lou-rename)", () => {
  const ts = collectCanonicalTsTools();
  const cs = collectCsHandlers();
  const tsOnly = loadTsOnlyAllowlist();
  const aliasKeys = new Set(Object.keys(ALL_ALIASES));
  const aliasVals = new Set(Object.values(ALL_ALIASES));

  it("alias registry is wired into index.ts", () => {
    const indexSrc = readFileSync(INDEX_TS, "utf8");
    expect(indexSrc).toMatch(
      /import\s*\{\s*registerJtcAliasTools\s*\}\s*from\s*["']\.\/tools\/jtc-aliases\.js["']/,
    );
    expect(indexSrc).toMatch(
      /registerJtcAliasTools\s*\(\s*server\s*,\s*bridge\s*\)/,
    );
  });

  it("every alias canonical resolves to a real TS tool (with C# handler OR in TS-only allowlist)", () => {
    const dangling = [...aliasVals].filter(
      (v) => !ts.has(v) || (!cs.has(v) && !tsOnly.has(v)),
    );
    expect(
      dangling,
      `Alias canonical names that don't resolve to a Lou tool (need C# handler or TS-only allowlist entry):\n  ${dangling.join("\n  ")}`,
    ).toEqual([]);
  });

  it("no alias key collides with a canonical TS tool, C# handler, or TS-only entry", () => {
    const collisions = [...aliasKeys].filter(
      (k) => ts.has(k) || cs.has(k) || tsOnly.has(k),
    );
    expect(
      collisions,
      `Alias keys that ALSO exist as canonical TS tools, C# handlers, or TS-only entries:\n  ${collisions.join("\n  ")}`,
    ).toEqual([]);
  });

  it("no alias maps to another alias (no dispatch chains)", () => {
    const chained = [...aliasVals].filter((v) => aliasKeys.has(v));
    expect(
      chained,
      `Alias values that are themselves alias keys (would create chains):\n  ${chained.join("\n  ")}`,
    ).toEqual([]);
  });

  it("no key appears in BOTH JTC_ALIASES and LOU_RENAMES", () => {
    const jtcSet = new Set(Object.keys(JTC_ALIASES));
    const louSet = new Set(Object.keys(LOU_RENAMES));
    const dupes = [...jtcSet].filter((k) => louSet.has(k));
    expect(
      dupes,
      `Names registered in both JTC_ALIASES and LOU_RENAMES (ambiguous):\n  ${dupes.join("\n  ")}`,
    ).toEqual([]);
  });

  it("at least one entry exists in JTC_ALIASES", () => {
    expect(Object.keys(JTC_ALIASES).length).toBeGreaterThan(0);
  });

  it("at least one entry exists in LOU_RENAMES", () => {
    expect(Object.keys(LOU_RENAMES).length).toBeGreaterThan(0);
  });
});

describe("parity inventory sanity", () => {
  const ts = collectCanonicalTsTools();
  const cs = collectCsHandlers();
  const tsOnly = loadTsOnlyAllowlist();
  const jtcKeys = new Set(Object.keys(JTC_ALIASES));
  const louKeys = new Set(Object.keys(LOU_RENAMES));

  it("canonical TS count == C# handler count + TS-only allowlist count", () => {
    expect(ts.size).toBe(cs.size + tsOnly.size);
  });

  it("inventory totals match expected post-v1.4.1 (compile/build observability added)", () => {
    expect({
      canonical_ts: ts.size,
      cs_handlers: cs.size,
      ts_only: tsOnly.size,
      jtc_aliases: jtcKeys.size,
      lou_renames: louKeys.size,
      runtime_registered_total: ts.size + jtcKeys.size + louKeys.size,
    }).toEqual({
      canonical_ts: 140,
      cs_handlers: 127,
      ts_only: 13,
      jtc_aliases: 34,
      lou_renames: 1,
      runtime_registered_total: 175,
    });
  });
});
