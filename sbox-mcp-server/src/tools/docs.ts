/**
 * S9 — s&box docs cluster (4 TS-only tools).
 *
 * Provides docs search + page retrieval against the live sbox.game docs.
 * Pure TS-only — no C# handler, no bridge dependency. Works even when the
 * s&box editor is closed.
 *
 * Tools:
 *   sbox_cache_status        — cache health, TTL, page count
 *   sbox_list_doc_categories — taxonomy: categories with page counts
 *   sbox_search_docs         — TF-IDF search across cached pages
 *   sbox_get_doc_page        — fetch one page as Markdown, chunked
 *
 * Source: sbox.game/llms.txt → individual /dev/doc/<path>.md pages.
 * Cache: <temp>/sbox-docs-cache/ (override SBOX_DOCS_CACHE_DIR).
 * TTL: 24 h (override SBOX_DOCS_CACHE_TTL in seconds).
 */

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { DocCache } from "../docs/cache.js";
import { crawlAll, type CrawlStats } from "../docs/fetcher.js";
import { DocSearch } from "../docs/search.js";

const cache = new DocCache();
const search = new DocSearch();

/** Async-singleton: re-uses the in-flight crawl across concurrent callers. */
let indexingTask: Promise<CrawlStats> | null = null;
let lastError: string | null = null;

async function ensureIndexed(): Promise<CrawlStats | null> {
  cache.init();

  // If cache is fresh AND already in the in-memory search index, no work.
  if (cache.isFresh() && search.pageCount > 0) return null;

  // Build in-memory index from the on-disk cache immediately so first
  // search requests don't block on the network crawl when stale-but-usable
  // data exists.
  if (search.pageCount === 0 && cache.getPageCount() > 0) {
    search.buildIndex(cache.getAllPages());
  }

  if (cache.isFresh() && search.pageCount > 0) return null;

  if (indexingTask) return indexingTask;

  indexingTask = (async () => {
    try {
      const stats = await crawlAll(cache);
      search.buildIndex(cache.getAllPages());
      lastError = null;
      return stats;
    } catch (err) {
      lastError = (err as Error).message;
      throw err;
    } finally {
      indexingTask = null;
    }
  })();
  return indexingTask;
}

function asText(text: string): { content: { type: "text"; text: string }[] } {
  return { content: [{ type: "text", text }] };
}

export function registerDocsTools(server: McpServer): void {
  // ── sbox_cache_status ──────────────────────────────────────────────
  server.tool(
    "sbox_cache_status",
    "Report s&box docs-cache health: directory, TTL, last-refresh timestamp, page count, freshness. Crawl is triggered lazily on first search/list call.",
    {},
    async () => {
      cache.init();
      const status = cache.status();
      const text = [
        `s&box docs cache`,
        `  dir: ${status.cacheDir}`,
        `  manifest: ${status.manifestPath}`,
        `  ttl: ${status.ttlSeconds}s`,
        `  pages cached: ${status.pageCount}`,
        `  last full crawl: ${status.lastFullCrawlIso ?? "never"}`,
        `  fresh: ${status.fresh ? "yes" : "no (next call will refresh)"}`,
        `  in-memory index: ${search.pageCount} page(s)`,
        lastError ? `  last error: ${lastError}` : "",
      ]
        .filter(Boolean)
        .join("\n");
      return asText(`${text}\n\n${JSON.stringify({ ...status, indexedPageCount: search.pageCount, lastError }, null, 2)}`);
    }
  );

  // ── sbox_list_doc_categories ───────────────────────────────────────
  server.tool(
    "sbox_list_doc_categories",
    "List s&box documentation categories with page counts. Useful for narrowing sbox_search_docs with a `category` filter.",
    {},
    async () => {
      try {
        await ensureIndexed();
      } catch (err) {
        return asText(
          `Error: failed to index docs: ${(err as Error).message}\n\nThe cache may have stale data — try again or check sbox_cache_status.`
        );
      }
      const cats = search.getCategories();
      if (cats.length === 0) {
        return asText(
          `No doc categories indexed yet. The cache may be empty or the network crawl failed. Check sbox_cache_status.`
        );
      }
      const lines: string[] = [`## s&box doc categories (${cats.length})`, ""];
      for (const c of cats) {
        lines.push(`- **${c.name}** — ${c.pageCount} page(s)`);
      }
      lines.push("");
      lines.push(
        `_${search.pageCount} page(s) total. Use sbox_search_docs with category="<name>" to filter._`
      );
      return asText(lines.join("\n"));
    }
  );

  // ── sbox_search_docs ───────────────────────────────────────────────
  server.tool(
    "sbox_search_docs",
    "Search s&box documentation for guides, tutorials, and concepts. Returns matching pages with titles, URLs, and relevant snippets.",
    {
      query: z.string().describe("Search terms"),
      limit: z
        .number()
        .int()
        .min(1)
        .max(25)
        .optional()
        .describe("Max number of results (1-25, default 10)"),
      category: z
        .string()
        .optional()
        .describe(
          "Optional category filter (e.g. 'scene', 'rendering', 'networking') — case-insensitive"
        ),
    },
    async ({ query, limit, category }) => {
      try {
        await ensureIndexed();
      } catch (err) {
        return asText(
          `Error: failed to index docs: ${(err as Error).message}`
        );
      }

      const actualLimit = Math.min(Math.max(limit ?? 10, 1), 25);
      const results = search.search(query, actualLimit, category ?? null);

      if (results.length === 0) {
        const hint = category
          ? ` Try without the category filter "${category}".`
          : "";
        return asText(
          `No documentation found for "${query}".${hint}\n\nUse sbox_list_doc_categories to see what's available.`
        );
      }

      const lines: string[] = [`## Search results for "${query}"`, ""];
      for (let i = 0; i < results.length; i++) {
        const r = results[i];
        lines.push(`${i + 1}. **[${r.title}](${r.url})** — _${r.category}_`);
        if (r.snippet) lines.push(`   > ${r.snippet}`);
        lines.push("");
      }
      lines.push(
        `_${results.length} result(s). Use sbox_get_doc_page to read full content._`
      );
      return asText(lines.join("\n"));
    }
  );

  // ── sbox_get_doc_page ──────────────────────────────────────────────
  server.tool(
    "sbox_get_doc_page",
    "Fetch a specific s&box documentation page and return its content as Markdown. Supports chunked reading via start_index and max_length.",
    {
      url: z
        .string()
        .describe(
          "Full URL of the documentation page (e.g. from sbox_search_docs)"
        ),
      startIndex: z
        .number()
        .int()
        .min(0)
        .optional()
        .describe("Character offset to start reading from (default 0)"),
      maxLength: z
        .number()
        .int()
        .min(100)
        .max(20000)
        .optional()
        .describe("Maximum content length in characters (100-20000, default 5000)"),
    },
    async ({ url, startIndex, maxLength }) => {
      try {
        await ensureIndexed();
      } catch (err) {
        return asText(
          `Error: failed to index docs: ${(err as Error).message}`
        );
      }

      const page = search.getPage(url);
      if (!page) {
        return asText(
          `Page not found in cache: ${url}\n\nUse sbox_search_docs to find a valid URL. If you believe this URL is correct, the cache may need a refresh — check sbox_cache_status.`
        );
      }

      const start = Math.max(0, startIndex ?? 0);
      const len = Math.min(Math.max(maxLength ?? 5000, 100), 20000);
      const body = page.markdown;
      const total = body.length;
      const end = Math.min(total, start + len);
      const slice = body.slice(start, end);

      const header = [
        `# ${page.title}`,
        `URL: ${page.url}`,
        `Category: ${page.category}`,
        `Length: ${total} chars (showing ${start}-${end})`,
        "",
        "---",
        "",
      ].join("\n");

      const footer =
        end < total
          ? `\n\n---\n_Showing chars ${start}-${end} of ${total}. Call sbox_get_doc_page again with startIndex=${end} to continue._`
          : "";

      return asText(`${header}${slice}${footer}`);
    }
  );
}
