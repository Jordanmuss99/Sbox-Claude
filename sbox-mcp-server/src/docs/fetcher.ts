/**
 * llms.txt-driven crawler for s&box docs at sbox.game.
 *
 * Strategy:
 *   1. Fetch sbox.game/llms.txt — an AI-discovery manifest declared in
 *      sbox.game/robots.txt as `Llms-Txt:`. Lists every doc page as a
 *      Markdown link `[Title](/dev/doc/category/page.md)`.
 *   2. For each entry, fetch `<base><path>` which sbox.game serves as
 *      `text/markdown; charset=utf-8`. No HTML→Markdown conversion needed.
 *
 * No Outline API, no auth, no HTML scraping, no extra deps. Uses Node 18+
 * global `fetch` with a per-request AbortController for timeout.
 *
 * Why not the older docs.facepunch.com / Outline share-id approach: as of
 * 2026-05, the `sbox-dev` share on docs.facepunch.com returns 404 on
 * `shares.info`. The Llms-Txt route is the canonical replacement.
 */

import type { CachedPage, DocCache } from "./cache.js";

const BASE_URL = "https://sbox.game";
const LLMS_TXT_URL = `${BASE_URL}/llms.txt`;
const USER_AGENT =
  "sbox-mcp-claude/1.x (+https://github.com/lousputthole/sbox-claude)";
const REQUEST_DELAY_MS = 150;
const REQUEST_TIMEOUT_MS = 10000;
const MIN_PAGE_BYTES = 8;

export interface IndexEntry {
  title: string;
  path: string;
  url: string;
  category: string;
}

export interface CrawlStats {
  total: number;
  crawled: number;
  fromCache: number;
  failed: number;
}

/** Fetch a URL with a hard timeout, returning the body text on 2xx. */
async function fetchText(url: string): Promise<string | null> {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), REQUEST_TIMEOUT_MS);
  try {
    const res = await fetch(url, {
      method: "GET",
      signal: ctrl.signal,
      headers: { "User-Agent": USER_AGENT, Accept: "text/markdown,text/plain,*/*" },
    });
    if (!res.ok) return null;
    return await res.text();
  } catch {
    return null;
  } finally {
    clearTimeout(t);
  }
}

/** Extract the category from a doc path like `/dev/doc/scene/components.md`. */
export function extractCategory(pathOnly: string): string {
  // /dev/doc/<category>/<...>.md  ->  category
  // /dev/doc/<single>.md          ->  strip ".md" => "single"
  const parts = pathOnly.split("/").filter((p) => p.length > 0);
  // Expect ["dev","doc",...]
  if (parts.length < 3) return "root";
  if (parts.length === 3) {
    const last = parts[2];
    return last.endsWith(".md") ? last.slice(0, -3) : last;
  }
  return parts[2];
}

/**
 * Parse llms.txt and return doc-page entries (only `/dev/doc/*.md` links
 * under the "## Documentation" section). API-reference links and the
 * navigational `/api` link are skipped.
 */
export function parseLlmsTxt(text: string): IndexEntry[] {
  const entries: IndexEntry[] = [];
  const linkRe = /\[([^\]]+)\]\((\/dev\/doc\/[^)]+\.md)\)/g;
  let m: RegExpExecArray | null;
  while ((m = linkRe.exec(text)) !== null) {
    const title = m[1].trim();
    const pathOnly = m[2];
    entries.push({
      title,
      path: pathOnly,
      url: `${BASE_URL}${pathOnly}`,
      category: extractCategory(pathOnly),
    });
  }
  return entries;
}

/** Fetch the master index. Returns parsed entries (empty array on failure). */
export async function fetchIndex(): Promise<IndexEntry[]> {
  const body = await fetchText(LLMS_TXT_URL);
  if (!body) return [];
  return parseLlmsTxt(body);
}

/** Fetch one doc page as Markdown. */
export async function fetchPage(url: string): Promise<string | null> {
  const md = await fetchText(url);
  if (!md || md.length < MIN_PAGE_BYTES) return null;
  return md;
}

const sleep = (ms: number): Promise<void> =>
  new Promise((r) => setTimeout(r, ms));

/**
 * Refresh the cache from sbox.game.
 *
 * Cold-path behaviour: when cache.isFresh() is true, returns immediately
 * with the cached page count. Otherwise: fetches llms.txt, then walks the
 * index sequentially with REQUEST_DELAY_MS between page fetches. Per-page
 * fresh checks short-circuit re-downloads inside the same crawl.
 */
export async function crawlAll(
  cache: DocCache,
  onProgress?: (s: CrawlStats) => void
): Promise<CrawlStats> {
  cache.init();

  if (cache.isFresh()) {
    const count = cache.getPageCount();
    const stats: CrawlStats = {
      total: count,
      crawled: 0,
      fromCache: count,
      failed: 0,
    };
    onProgress?.(stats);
    return stats;
  }

  const entries = await fetchIndex();
  const stats: CrawlStats = {
    total: entries.length,
    crawled: 0,
    fromCache: 0,
    failed: 0,
  };

  if (entries.length === 0) {
    // Network failure: surface what's in the cache so existing tools keep
    // working, but don't markFullCrawl — next call will retry.
    stats.fromCache = cache.getPageCount();
    onProgress?.(stats);
    return stats;
  }

  for (const entry of entries) {
    if (cache.isPageFresh(entry.url)) {
      stats.fromCache++;
      onProgress?.(stats);
      continue;
    }

    const md = await fetchPage(entry.url);
    if (md === null) {
      stats.failed++;
      onProgress?.(stats);
      await sleep(REQUEST_DELAY_MS);
      continue;
    }

    const page: CachedPage = {
      url: entry.url,
      pathOnly: entry.path,
      title: entry.title,
      category: entry.category,
      markdown: md,
      fetchedAt: Date.now(),
    };
    cache.setPage(page);
    stats.crawled++;
    onProgress?.(stats);
    await sleep(REQUEST_DELAY_MS);
  }

  const validUrls = new Set(entries.map((e) => e.url));
  cache.pruneNotIn(validUrls);
  cache.markFullCrawl();
  cache.save();
  return stats;
}
