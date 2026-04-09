/**
 * BridgeClient — WebSocket client for the s&box Claude Bridge plugin.
 *
 * Features:
 *   • Promise-based request/response with per-request timeout
 *   • sendBatch() — execute multiple commands in one round-trip (Task 8)
 *   • Automatic reconnect after disconnect (configurable delay)
 *   • Periodic ping every 15 s; terminates and reconnects if 3 pings go unanswered
 *   • Rejects all in-flight requests on disconnect
 */
import { EventEmitter } from 'node:events';
import WebSocket from 'ws';
import { v4 as uuidv4 } from 'uuid';

export interface BridgeError {
  code: string;
  message: string;
}

export class BridgeClientError extends Error {
  constructor(
    message: string,
    public readonly code: string,
  ) {
    super(message);
    this.name = 'BridgeClientError';
  }
}

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

interface BridgeResponse {
  id: string;
  result?: unknown;
  error?: BridgeError;
  /** Present on batch responses instead of result */
  results?: BatchResultItem[];
}

/** One command in a batch request. */
export interface BatchCommand {
  command: string;
  params?: Record<string, unknown>;
}

/** One result item in a batch response. */
export interface BatchResultItem {
  command: string;
  result?: unknown;
  error?: BridgeError;
}

export class BridgeClient extends EventEmitter {
  private ws: WebSocket | null = null;
  private readonly pending = new Map<string, PendingRequest>();
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private pingTimer: ReturnType<typeof setInterval> | null = null;
  private pongReceived = true;
  private missedPings = 0;
  private _connected = false;

  constructor(
    public readonly host: string = process.env['SBOX_HOST'] ?? 'localhost',
    public readonly port: number = parseInt(process.env['SBOX_PORT'] ?? '8765', 10),
    /** Milliseconds before a request is considered timed out */
    private readonly requestTimeout: number = 10_000,
    /** Delay before a reconnect attempt after disconnect */
    private readonly reconnectDelay: number = 3_000,
    /** Interval between pings in milliseconds */
    private readonly pingInterval: number = 15_000,
    /** Number of unanswered pings before the connection is considered dead */
    private readonly maxMissedPings: number = 3,
  ) {
    super();
  }

  get connected(): boolean {
    return this._connected && this.ws?.readyState === WebSocket.OPEN;
  }

  get url(): string {
    return `ws://${this.host}:${this.port}`;
  }

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  connect(): void {
    if (
      this.ws?.readyState === WebSocket.OPEN ||
      this.ws?.readyState === WebSocket.CONNECTING
    ) {
      return;
    }

    this.ws = new WebSocket(this.url);

    this.ws.on('open', () => {
      this._connected = true;
      this.missedPings = 0;
      this.pongReceived = true;
      this.startPing();
      this.emit('connected');
    });

    this.ws.on('message', (data: WebSocket.RawData) => {
      this.handleMessage(data.toString());
    });

    this.ws.on('pong', () => {
      this.pongReceived = true;
      this.missedPings = 0;
    });

    this.ws.on('close', () => {
      this._connected = false;
      this.stopPing();
      this.rejectAllPending(new BridgeClientError('WebSocket connection closed', 'DISCONNECTED'));
      this.emit('disconnected');
      this.scheduleReconnect();
    });

    this.ws.on('error', (err: Error) => {
      // 'close' fires after 'error', so reconnect logic lives there
      this.emit('error', err);
    });
  }

  disconnect(): void {
    this.cancelReconnect();
    this.stopPing();
    this.rejectAllPending(new BridgeClientError('Client disconnected', 'DISCONNECTED'));
    this.ws?.terminate();
    this.ws = null;
    this._connected = false;
  }

  // ── Send / receive ────────────────────────────────────────────────────────

  async send(command: string, params: Record<string, unknown> = {}): Promise<unknown> {
    if (!this.connected) {
      throw new BridgeClientError(
        'Not connected to the s&box bridge. Is the plugin running?',
        'NOT_CONNECTED',
      );
    }

    const id = uuidv4();
    const message = JSON.stringify({ id, command, params });

    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(
          new BridgeClientError(`Request timed out after ${this.requestTimeout}ms: ${command}`, 'TIMEOUT'),
        );
      }, this.requestTimeout);

      this.pending.set(id, { resolve, reject, timer });

      this.ws!.send(message, (err?: Error) => {
        if (err) {
          clearTimeout(timer);
          this.pending.delete(id);
          reject(new BridgeClientError(`Send failed: ${err.message}`, 'SEND_ERROR'));
        }
      });
    });
  }

  /**
   * Send multiple commands to the bridge in one WebSocket round-trip (Task 8).
   *
   * Commands are executed sequentially on the C# side. Even if one fails, the
   * remaining commands still run. Inspect each item's `.error` to check for
   * per-command failures.
   *
   * @example
   * const [goResult, compResult] = await bridge.sendBatch([
   *   { command: 'create_gameobject', params: { name: 'Cube' } },
   *   { command: 'add_component_with_properties', params: { guid: '...', component_type: 'Rigidbody' } },
   * ]);
   */
  async sendBatch(commands: BatchCommand[]): Promise<BatchResultItem[]> {
    if (!this.connected) {
      throw new BridgeClientError(
        'Not connected to the s&box bridge. Is the plugin running?',
        'NOT_CONNECTED',
      );
    }

    if (commands.length === 0) return [];

    const id = uuidv4();
    const message = JSON.stringify({ id, commands });

    return new Promise<BatchResultItem[]>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(
          new BridgeClientError(
            `Batch timed out after ${this.requestTimeout}ms (${commands.length} commands)`,
            'TIMEOUT',
          ),
        );
      }, this.requestTimeout);

      this.pending.set(id, {
        resolve: (v) => resolve(v as BatchResultItem[]),
        reject,
        timer,
      });

      this.ws!.send(message, (err?: Error) => {
        if (err) {
          clearTimeout(timer);
          this.pending.delete(id);
          reject(new BridgeClientError(`Send failed: ${err.message}`, 'SEND_ERROR'));
        }
      });
    });
  }

  // ── Internal ──────────────────────────────────────────────────────────────

  private handleMessage(raw: string): void {
    let response: BridgeResponse;
    try {
      response = JSON.parse(raw) as BridgeResponse;
    } catch {
      // Ignore malformed messages
      return;
    }

    const pending = this.pending.get(response.id);
    if (!pending) return;

    clearTimeout(pending.timer);
    this.pending.delete(response.id);

    if (response.error) {
      pending.reject(new BridgeClientError(response.error.message, response.error.code));
    } else if (response.results !== undefined) {
      // Batch response
      pending.resolve(response.results);
    } else {
      pending.resolve(response.result);
    }
  }

  /** Ping every `pingInterval` ms. Terminate + reconnect if `maxMissedPings` accumulate. */
  private startPing(): void {
    this.pingTimer = setInterval(() => {
      if (!this.pongReceived) {
        this.missedPings++;
        if (this.missedPings >= this.maxMissedPings) {
          this.emit('ping_timeout');
          this.ws?.terminate();
          return;
        }
      }
      this.pongReceived = false;
      this.ws?.ping();
    }, this.pingInterval);
  }

  private stopPing(): void {
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return; // already scheduled
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect();
    }, this.reconnectDelay);
  }

  private cancelReconnect(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private rejectAllPending(err: Error): void {
    for (const [id, req] of this.pending) {
      clearTimeout(req.timer);
      req.reject(err);
      this.pending.delete(id);
    }
  }
}
