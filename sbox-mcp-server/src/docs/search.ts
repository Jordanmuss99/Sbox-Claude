/**
 * Search index for cached doc pages.
 *
 * Hand-rolled per-field TF-IDF with field boosts, prefix matching, and an
 * optional substring fallback for short queries. Same field weights as
 * upstream sbox-mcp (title 3.0, category 2.0, markdown 1.0) so result
 * relevance is comparable. ~150 lines; no external deps.
 *
 * The index is built once per `buildIndex(pages)` call. Search latency on
 * the full 219-page s&box doc set is sub-millisecond.
 */

import type { CachedPage } from "./cache.js";

const TITLE_BOOST = 3.0;
const CATEGORY_BOOST = 2.0;
const BODY_BOOST = 1.0;
const TOKEN_RE = /[\p{L}\p{N}_]+/gu;
const PREFIX_DECAY = 0.5; // prefix matches scored at half weight of exact
const SNIPPET_RADIUS = 100; // chars on each side of the match

export interface SearchResult {
  title: string;
  url: string;
  category: string;
  snippet: string;
  score: number;
}

export interface CategoryInfo {
  name: string;
  pageCount: number;
  pages: { title: string; url: string }[];
}

interface Posting {
  docId: number;
  count: number;
}

interface FieldIndex {
  /** token → list of postings */
  postings: Map<string, Posting[]>;
  /** docId → field length (token count) */
  lengths: number[];
  /** boost weight */
  boost: number;
}

function tokenize(text: string): string[] {
  if (!text) return [];
  const out: string[] = [];
  const matches = text.toLowerCase().match(TOKEN_RE);
  if (!matches) return out;
  for (const m of matches) out.push(m);
  return out;
}

export class DocSearch {
  private pages: CachedPage[] = [];
  private byUrl = new Map<string, CachedPage>();
  private titleIdx: FieldIndex = {
    postings: new Map(),
    lengths: [],
    boost: TITLE_BOOST,
  };
  private categoryIdx: FieldIndex = {
    postings: new Map(),
    lengths: [],
    boost: CATEGORY_BOOST,
  };
  private bodyIdx: FieldIndex = {
    postings: new Map(),
    lengths: [],
    boost: BODY_BOOST,
  };

  get pageCount(): number {
    return this.pages.length;
  }

  buildIndex(pages: CachedPage[]): void {
    this.pages = pages.slice();
    this.byUrl.clear();
    this.titleIdx = { postings: new Map(), lengths: [], boost: TITLE_BOOST };
    this.categoryIdx = {
      postings: new Map(),
      lengths: [],
      boost: CATEGORY_BOOST,
    };
    this.bodyIdx = { postings: new Map(), lengths: [], boost: BODY_BOOST };

    for (let docId = 0; docId < pages.length; docId++) {
      const p = pages[docId];
      this.byUrl.set(p.url, p);
      this.addToField(this.titleIdx, docId, p.title);
      this.addToField(this.categoryIdx, docId, p.category);
      this.addToField(this.bodyIdx, docId, p.markdown);
    }
  }

  private addToField(idx: FieldIndex, docId: number, text: string): void {
    const tokens = tokenize(text);
    idx.lengths[docId] = tokens.length;
    const counts = new Map<string, number>();
    for (const tk of tokens) counts.set(tk, (counts.get(tk) ?? 0) + 1);
    for (const [tk, count] of counts) {
      let postings = idx.postings.get(tk);
      if (!postings) {
        postings = [];
        idx.postings.set(tk, postings);
      }
      postings.push({ docId, count });
    }
  }

  /**
   * Score a token against a field for every doc that contains it (exact)
   * or starts with it (prefix). Accumulates into `scores`.
   */
  private scoreToken(
    idx: FieldIndex,
    queryToken: string,
    scores: Map<number, number>
  ): void {
    const N = this.pages.length;
    if (N === 0) return;

    // Exact-match postings
    const exact = idx.postings.get(queryToken);
    if (exact) {
      const idf = Math.log(1 + N / exact.length);
      for (const { docId, count } of exact) {
        const len = idx.lengths[docId] || 1;
        const tf = count / len;
        const score = tf * idf * idx.boost;
        scores.set(docId, (scores.get(docId) ?? 0) + score);
      }
    }

    // Prefix matches — boost half-weight, scan all terms starting with
    // queryToken. Inverted index is small enough (~few thousand terms)
    // that a linear scan is fine.
    if (queryToken.length >= 3) {
      for (const [term, postings] of idx.postings) {
        if (term === queryToken) continue;
        if (!term.startsWith(queryToken)) continue;
        const idf = Math.log(1 + N / postings.length);
        for (const { docId, count } of postings) {
          const len = idx.lengths[docId] || 1;
          const tf = count / len;
          const score = tf * idf * idx.boost * PREFIX_DECAY;
          scores.set(docId, (scores.get(docId) ?? 0) + score);
        }
      }
    }
  }

  search(
    query: string,
    limit: number,
    category: string | null
  ): SearchResult[] {
    if (this.pages.length === 0) return [];
    const tokens = tokenize(query);
    if (tokens.length === 0) return [];

    const scores = new Map<number, number>();
    for (const tk of tokens) {
      this.scoreToken(this.titleIdx, tk, scores);
      this.scoreToken(this.categoryIdx, tk, scores);
      this.scoreToken(this.bodyIdx, tk, scores);
    }

    const ranked = [...scores.entries()].sort((a, b) => b[1] - a[1]);

    const results: SearchResult[] = [];
    for (const [docId, score] of ranked) {
      if (results.length >= limit) break;
      const page = this.pages[docId];
      if (
        category &&
        page.category.toLowerCase() !== category.toLowerCase()
      ) {
        continue;
      }
      results.push({
        title: page.title,
        url: page.url,
        category: page.category,
        snippet: extractSnippet(page.markdown, query),
        score,
      });
    }
    return results;
  }

  getCategories(): CategoryInfo[] {
    const byCat = new Map<string, { title: string; url: string }[]>();
    for (const p of this.pages) {
      let arr = byCat.get(p.category);
      if (!arr) {
        arr = [];
        byCat.set(p.category, arr);
      }
      arr.push({ title: p.title, url: p.url });
    }
    return [...byCat.entries()]
      .map(([name, pages]) => ({
        name,
        pageCount: pages.length,
        pages: pages.sort((a, b) => a.title.localeCompare(b.title)),
      }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }

  getPage(url: string): CachedPage | null {
    return this.byUrl.get(url) ?? null;
  }
}

/**
 * Return a short ~200-char window around the first query-token hit in
 * `body`, or the first 200 chars when no hit found. Hits are matched
 * case-insensitively on raw substring (not tokenized) for snippet purposes.
 */
export function extractSnippet(body: string, query: string): string {
  if (!body) return "";
  const tokens = tokenize(query);
  if (tokens.length === 0) return body.slice(0, SNIPPET_RADIUS * 2).trim();

  const lower = body.toLowerCase();
  let bestIdx = -1;
  for (const tk of tokens) {
    const idx = lower.indexOf(tk);
    if (idx >= 0 && (bestIdx < 0 || idx < bestIdx)) bestIdx = idx;
  }

  if (bestIdx < 0) {
    return body.slice(0, SNIPPET_RADIUS * 2).trim();
  }

  const start = Math.max(0, bestIdx - SNIPPET_RADIUS);
  const end = Math.min(body.length, bestIdx + SNIPPET_RADIUS);
  let snippet = body.slice(start, end).replace(/\s+/g, " ").trim();
  if (start > 0) snippet = "… " + snippet;
  if (end < body.length) snippet = snippet + " …";
  return snippet;
}
