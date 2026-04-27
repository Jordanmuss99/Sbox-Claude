import * as fs from "fs";
import * as path from "path";
import * as os from "os";

/**
 * File-based IPC transport for communicating with the s&box Bridge Addon.
 *
 * Instead of WebSocket, this uses a shared temp directory where:
 * - MCP server writes request files (req_*.json)
 * - s&box addon polls for them, processes, and writes response files (res_*.json)
 * - MCP server polls for response files
 */

/** A single command request sent to the s&box Bridge. */
export interface BridgeRequest {
  id: string;
  command: string;
  params: Record<string, unknown>;
}

/** Response from the s&box Bridge. Check `success` before reading `data`. */
export interface BridgeResponse {
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

/**
 * File-based IPC client that communicates with the s&box Bridge Addon.
 */
export class BridgeClient {
  private requestCounter = 0;
  private ipcDir: string;
  private connected = false;
  private lastPongTime = 0;
  private host: string;
  private port: number;

  static readonly POLL_INTERVAL_MS = 50; // 50ms polling for responses
  static readonly STATUS_CHECK_INTERVAL_MS = 5000;

  constructor(host = "127.0.0.1", port = 29015) {
    this.host = host;
    this.port = port;
    this.ipcDir = path.join(os.tmpdir(), "sbox-bridge-ipc");
  }

  /**
   * Check if the s&box Bridge is running by looking for the status file.
   */
  async connect(): Promise<void> {
    // Ensure IPC directory exists
    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    const statusPath = path.join(this.ipcDir, "status.json");
    if (fs.existsSync(statusPath)) {
      try {
        const status = JSON.parse(fs.readFileSync(statusPath, "utf8"));
        if (status.running) {
          this.connected = true;
          this.lastPongTime = Date.now();
          return;
        }
      } catch {
        // Status file exists but is malformed
      }
    }

    throw new Error(
      `Cannot connect to s&box Bridge. No status file found at ${statusPath}. Is s&box running with the Bridge Addon?`
    );
  }

  /**
   * Send a command to the s&box Bridge and wait for its response.
   */
  async send(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    // Try to connect if not connected
    if (!this.connected) {
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

    const id = `${++this.requestCounter}_${Date.now()}`;
    const request: BridgeRequest = { id, command, params };

    // Ensure IPC directory exists
    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    // Write request file
    const reqPath = path.join(this.ipcDir, `req_${id}.json`);
    const resPath = path.join(this.ipcDir, `res_${id}.json`);
    try {
      fs.writeFileSync(reqPath, JSON.stringify(request), "utf8");
    } catch (err) {
      return {
        id,
        success: false,
        error: `Failed to write request file: ${err}`,
      };
    }

    // Poll for response file
    const startTime = Date.now();

    return new Promise((resolve) => {
      const poll = setInterval(() => {
        // Check timeout
        if (Date.now() - startTime > timeoutMs) {
          clearInterval(poll);
          // Clean up request file if still there
          try {
            if (fs.existsSync(reqPath)) fs.unlinkSync(reqPath);
          } catch {}
          resolve({
            id,
            success: false,
            error: `Request timed out after ${timeoutMs}ms`,
          });
          return;
        }

        // Check for response file
        if (fs.existsSync(resPath)) {
          try {
            // Strip UTF-8 BOM that C#'s File.WriteAllText prepends
            const responseJson = fs.readFileSync(resPath, "utf8").replace(/^\uFEFF/, "");
            const response = JSON.parse(responseJson) as BridgeResponse;

            // Clean up response file
            try {
              fs.unlinkSync(resPath);
            } catch {}

            clearInterval(poll);
            this.lastPongTime = Date.now();
            resolve(response);
          } catch {
            // Response file might be partially written, try again next poll
          }
        }
      }, BridgeClient.POLL_INTERVAL_MS);
    });
  }

  /**
   * Send multiple commands as a batch.
   */
  async sendBatch(
    commands: Array<{ command: string; params?: Record<string, unknown> }>,
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    if (!this.connected) {
      try {
        await this.connect();
      } catch {
        return {
          id: "",
          success: false,
          error: "Not connected to s&box Bridge.",
        };
      }
    }

    const id = `batch_${++this.requestCounter}_${Date.now()}`;
    const request = { id, commands };

    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    const reqPath = path.join(this.ipcDir, `req_${id}.json`);
    try {
      fs.writeFileSync(reqPath, JSON.stringify(request), "utf8");
    } catch (err) {
      return {
        id,
        success: false,
        error: `Failed to write request file: ${err}`,
      };
    }

    const resPath = path.join(this.ipcDir, `res_${id}.json`);
    const startTime = Date.now();

    return new Promise((resolve) => {
      const poll = setInterval(() => {
        if (Date.now() - startTime > timeoutMs) {
          clearInterval(poll);
          try {
            if (fs.existsSync(reqPath)) fs.unlinkSync(reqPath);
          } catch {}
          resolve({
            id,
            success: false,
            error: `Batch request timed out after ${timeoutMs}ms`,
          });
          return;
        }

        if (fs.existsSync(resPath)) {
          try {
            // Strip UTF-8 BOM that C#'s File.WriteAllText prepends
            const responseJson = fs.readFileSync(resPath, "utf8").replace(/^\uFEFF/, "");
            const response = JSON.parse(responseJson) as BridgeResponse;
            try {
              fs.unlinkSync(resPath);
            } catch {}
            clearInterval(poll);
            this.lastPongTime = Date.now();
            resolve(response);
          } catch {}
        }
      }, BridgeClient.POLL_INTERVAL_MS);
    });
  }

  /**
   * Check if bridge is alive by looking for status file.
   */
  async ping(): Promise<number> {
    const statusPath = path.join(this.ipcDir, "status.json");
    const start = Date.now();
    try {
      if (fs.existsSync(statusPath)) {
        const status = JSON.parse(fs.readFileSync(statusPath, "utf8"));
        if (status.running) {
          this.lastPongTime = Date.now();
          return Date.now() - start;
        }
      }
    } catch {}
    return -1;
  }

  isConnected(): boolean {
    // Re-check status file
    const statusPath = path.join(this.ipcDir, "status.json");
    try {
      if (fs.existsSync(statusPath)) {
        const status = JSON.parse(fs.readFileSync(statusPath, "utf8"));
        this.connected = !!status.running;
      } else {
        this.connected = false;
      }
    } catch {
      this.connected = false;
    }
    return this.connected;
  }

  getHost(): string {
    return this.host;
  }

  getPort(): number {
    return this.port;
  }

  getLastPongTime(): number {
    return this.lastPongTime;
  }

  disconnect(): void {
    this.connected = false;
  }
}
