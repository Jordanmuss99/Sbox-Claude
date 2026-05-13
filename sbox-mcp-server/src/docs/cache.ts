/**
 * Persistent cache for s&box doc pages crawled from sbox.game.
 *
 * Layout: one manifest file (`manifest.json`) that holds every cached page
 * keyed by canonical URL plus a `lastFullCrawl` epoch. Pages are stored
 * inline in the manifest — they're small (median ~2 KB Markdown), the
 * full set is ~500 KB, and one JSON read on startup is simpler than
 * managing per-page files.
 *
 * Environment overrides:
 *   SBOX_DOCS_CACHE_DIR  — override cache directory (default: <temp>/sbox-docs-cache)
 *   SBOX_DOCS_CACHE_TTL  — TTL in seconds (default: 86400 = 24h)
 */

import * as fs from "fs";
import * as path from "path";
import * as os from "os";

const MANIFEST_VERSION = 1;
const DEFAULT_TTL_SECONDS = 86400; // 24h

export interface CachedPage {
  url: string;
  pathOnly: string;
  title: string;
  category: string;
  markdown: string;
  fetchedAt: number;
}

interface CacheManifest {
  version: number;
  pages: Record<string, CachedPage>;
  lastFullCrawl: number;
}

export class DocCache {
  readonly cacheDir: string;
  readonly manifestPath: string;
  readonly ttlMs: number;
  private manifest: CacheManifest = { version: MANIFEST_VERSION, pages: {}, lastFullCrawl: 0 };
  private loaded = false;

  constructor() {
    this.cacheDir =
      process.env.SBOX_DOCS_CACHE_DIR ??
      path.join(os.tmpdir(), "sbox-docs-cache");
    this.manifestPath = path.join(this.cacheDir, "manifest.json");
    const ttlSec = parseInt(
      process.env.SBOX_DOCS_CACHE_TTL ?? String(DEFAULT_TTL_SECONDS),
      10
    );
    this.ttlMs = (isNaN(ttlSec) ? DEFAULT_TTL_SECONDS : ttlSec) * 1000;
  }

  init(): void {
    if (this.loaded) return;
    try {
      fs.mkdirSync(this.cacheDir, { recursive: true });
    } catch {
      /* ignore — failures surface on save */
    }
    if (fs.existsSync(this.manifestPath)) {
      try {
        const raw = fs.readFileSync(this.manifestPath, "utf8").replace(/^\uFEFF/, "");
        const parsed = JSON.parse(raw) as CacheManifest;
        if (parsed?.version === MANIFEST_VERSION && parsed.pages) {
          this.manifest = parsed;
        }
      } catch {
        // Corrupt manifest — start fresh, don't surface error
      }
    }
    this.loaded = true;
  }

  isFresh(): boolean {
    if (this.manifest.lastFullCrawl === 0) return false;
    return Date.now() - this.manifest.lastFullCrawl < this.ttlMs;
  }

  isPageFresh(url: string): boolean {
    const p = this.manifest.pages[url];
    if (!p) return false;
    return Date.now() - p.fetchedAt < this.ttlMs;
  }

  getPage(url: string): CachedPage | null {
    return this.manifest.pages[url] ?? null;
  }

  getAllPages(): CachedPage[] {
    return Object.values(this.manifest.pages);
  }

  getPageCount(): number {
    return Object.keys(this.manifest.pages).length;
  }

  setPage(page: CachedPage): void {
    this.manifest.pages[page.url] = page;
  }

  markFullCrawl(): void {
    this.manifest.lastFullCrawl = Date.now();
  }

  /** Drop cached pages whose URLs no longer appear in the canonical index. */
  pruneNotIn(validUrls: Set<string>): number {
    let pruned = 0;
    for (const url of Object.keys(this.manifest.pages)) {
      if (!validUrls.has(url)) {
        delete this.manifest.pages[url];
        pruned++;
      }
    }
    return pruned;
  }

  save(): void {
    try {
      fs.mkdirSync(this.cacheDir, { recursive: true });
      fs.writeFileSync(
        this.manifestPath,
        JSON.stringify(this.manifest, null, 2),
        "utf8"
      );
    } catch (err) {
      // Cache writes are best-effort; surface as stderr so tests/smoke can see
      // when something goes wrong, but never throw.
      console.error(
        `[sbox-mcp:docs] failed to save cache manifest: ${(err as Error).message}`
      );
    }
  }

  status(): {
    cacheDir: string;
    manifestPath: string;
    ttlSeconds: number;
    lastFullCrawl: number;
    lastFullCrawlIso: string | null;
    pageCount: number;
    fresh: boolean;
  } {
    return {
      cacheDir: this.cacheDir,
      manifestPath: this.manifestPath,
      ttlSeconds: this.ttlMs / 1000,
      lastFullCrawl: this.manifest.lastFullCrawl,
      lastFullCrawlIso:
        this.manifest.lastFullCrawl > 0
          ? new Date(this.manifest.lastFullCrawl).toISOString()
          : null,
      pageCount: this.getPageCount(),
      fresh: this.isFresh(),
    };
  }
}
