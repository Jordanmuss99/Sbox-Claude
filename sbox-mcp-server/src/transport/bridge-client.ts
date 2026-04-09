import WebSocket from "ws";

export interface BridgeRequest {
  id: string;
  command: string;
  params: Record<string, unknown>;
}

export interface BridgeResponse {
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

/**
 * WebSocket client that connects to the s&box Bridge Addon.
 * The Bridge runs inside the s&box editor on port 29015.
 */
export class BridgeClient {
  private ws: WebSocket | null = null;
  private pendingRequests = new Map<
    string,
    {
      resolve: (value: BridgeResponse) => void;
      reject: (reason: Error) => void;
      timer: ReturnType<typeof setTimeout>;
    }
  >();
  private requestCounter = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private url: string;
  private connected = false;

  constructor(host = "127.0.0.1", port = 29015) {
    this.url = `ws://${host}:${port}`;
  }

  async connect(): Promise<void> {
    return new Promise((resolve, reject) => {
      try {
        this.ws = new WebSocket(this.url);

        this.ws.on("open", () => {
          this.connected = true;
          resolve();
        });

        this.ws.on("message", (data: WebSocket.RawData) => {
          try {
            const response = JSON.parse(data.toString()) as BridgeResponse;
            const pending = this.pendingRequests.get(response.id);
            if (pending) {
              clearTimeout(pending.timer);
              this.pendingRequests.delete(response.id);
              pending.resolve(response);
            }
          } catch {
            // Ignore malformed messages
          }
        });

        this.ws.on("close", () => {
          this.connected = false;
          this.rejectAllPending("Connection closed");
          this.scheduleReconnect();
        });

        this.ws.on("error", (err: Error) => {
          if (!this.connected) {
            reject(
              new Error(
                `Cannot connect to s&box Bridge at ${this.url}. Is s&box running with the Bridge Addon? (${err.message})`
              )
            );
          }
        });
      } catch (err) {
        reject(err);
      }
    });
  }

  async send(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    if (!this.ws || !this.connected) {
      // Try to reconnect once
      try {
        await this.connect();
      } catch {
        return {
          id: "",
          success: false,
          error:
            "Not connected to s&box Bridge. Make sure s&box is running with the Bridge Addon installed.",
        };
      }
    }

    const id = `req_${++this.requestCounter}_${Date.now()}`;
    const request: BridgeRequest = { id, command, params };

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        resolve({
          id,
          success: false,
          error: `Request timed out after ${timeoutMs}ms`,
        });
      }, timeoutMs);

      this.pendingRequests.set(id, { resolve, reject, timer });
      this.ws!.send(JSON.stringify(request));
    });
  }

  isConnected(): boolean {
    return this.connected;
  }

  disconnect(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.rejectAllPending("Client disconnecting");
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
    this.connected = false;
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect().catch(() => {
        // Will retry on next send()
      });
    }, 3000);
  }

  private rejectAllPending(reason: string): void {
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.resolve({ id, success: false, error: reason });
    }
    this.pendingRequests.clear();
  }
}
