import * as fs from "fs";
import * as path from "path";

/** A single event from the s&box editor event stream. */
export interface EditorEvent {
  eventId: number;
  sessionId: string;
  timestamp: string;
  type: string;
  data: unknown;
}

/**
 * Watches events.json (written by the C# BridgeEventDispatcher) for new events.
 * Uses fs.watch for push detection, with a 2s poll fallback for reliability on Windows.
 */
export class EventWatcher {
  private eventsPath: string;
  private cached: EditorEvent[] = [];
  private watcher: fs.FSWatcher | null = null;
  private pollInterval: ReturnType<typeof setInterval> | null = null;
  private started = false;

  constructor(ipcDir: string) {
    this.eventsPath = path.join(ipcDir, "events.json");
  }

  start(): void {
    if (this.started) return;
    this.started = true;

    // Initial read
    this.readEvents();

    // fs.watch for push detection (Windows may be unreliable on temp dirs)
    try {
      this.watcher = fs.watch(
        path.dirname(this.eventsPath),
        (_eventType, filename) => {
          if (filename === "events.json" || filename === null) {
            this.readEvents();
          }
        }
      );
    } catch {
      // fs.watch fails on some Windows temp dirs — poll fallback works
    }

    // Poll fallback at 2s interval
    this.pollInterval = setInterval(() => {
      this.readEvents();
    }, 2000);
  }

  stop(): void {
    this.started = false;
    if (this.watcher) {
      try { this.watcher.close(); } catch {}
      this.watcher = null;
    }
    if (this.pollInterval) {
      clearInterval(this.pollInterval);
      this.pollInterval = null;
    }
  }

  getEvents(
    sinceId?: number,
    sessionId?: string,
    types?: string[],
    limit = 50
  ): EditorEvent[] {
    let filtered = this.cached;

    if (sinceId !== undefined) {
      filtered = filtered.filter((e) => e.eventId > sinceId);
    }
    if (sessionId !== undefined) {
      filtered = filtered.filter((e) => e.sessionId === sessionId);
    }
    if (types && types.length > 0) {
      filtered = filtered.filter((e) => types.includes(e.type));
    }

    // Newest first, limited
    return filtered.slice(-limit).reverse();
  }

  private readEvents(): void {
    try {
      if (!fs.existsSync(this.eventsPath)) return;
      const raw = fs.readFileSync(this.eventsPath, "utf8");
      const lines = raw.split("\n");
      const events: EditorEvent[] = [];
      for (const line of lines) {
        if (!line.trim()) continue;
        try {
          events.push(JSON.parse(line));
        } catch {
          // Skip unparseable lines (partial writes, compaction artifacts)
        }
      }
      this.cached = events;
    } catch {
      // File may be locked during compaction — use cached data
    }
  }
}
