/**
 * Reconnect & resilience stress tests — Task 6.
 *
 * Tests the tricky edge cases around connection loss:
 *   • Auto-reconnect after server drop
 *   • In-flight requests rejected with DISCONNECTED on disconnect
 *   • Ping timeout fires after MAX_MISSED_PINGS * PING_INTERVAL
 *   • Concurrent sends during reconnect window
 *   • connect() is idempotent (safe to call while already connected)
 */

import { AddressInfo } from 'node:net';
import { WebSocketServer, WebSocket } from 'ws';
import { BridgeClient, BridgeClientError } from '../src/BridgeClient.js';

// ── Helpers ───────────────────────────────────────────────────────────────────

function startServer(): Promise<{ server: WebSocketServer; port: number }> {
  return new Promise((resolve) => {
    const server = new WebSocketServer({ port: 0 }, () => {
      resolve({ server, port: (server.address() as AddressInfo).port });
    });
  });
}

/** Client with short timers so tests complete quickly. */
function makeClient(
  port: number,
  opts: {
    requestTimeout?: number;
    reconnectDelay?: number;
    pingInterval?: number;
    maxMissedPings?: number;
  } = {},
): BridgeClient {
  return new BridgeClient(
    'localhost',
    port,
    opts.requestTimeout  ?? 1_000,
    opts.reconnectDelay  ?? 80,
    opts.pingInterval    ?? 200,
    opts.maxMissedPings  ?? 3,
  );
}

function waitFor(emitter: BridgeClient, event: string): Promise<void> {
  return new Promise((r) => emitter.once(event, r));
}

function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

// ── Auto-reconnect ────────────────────────────────────────────────────────────

describe('auto-reconnect', () => {
  test('reconnects after server terminates the connection', async () => {
    const { server, port } = await startServer();
    let connectionCount = 0;

    const secondConnection = new Promise<void>((resolve) => {
      server.on('connection', (ws: WebSocket) => {
        connectionCount++;
        if (connectionCount === 1) ws.terminate();
        else resolve();
      });
    });

    const client = makeClient(port, { reconnectDelay: 50 });
    client.connect();

    await secondConnection;
    expect(connectionCount).toBeGreaterThanOrEqual(2);

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 5_000);

  test('emits disconnected then connected on reconnect', async () => {
    const { server, port } = await startServer();
    const events: string[] = [];

    let serverWs!: WebSocket;
    server.on('connection', (ws: WebSocket) => { serverWs = ws; });

    const client = makeClient(port, { reconnectDelay: 50 });
    client.on('connected',    () => events.push('connected'));
    client.on('disconnected', () => events.push('disconnected'));
    client.connect();

    await waitFor(client, 'connected');
    serverWs.terminate();
    await waitFor(client, 'disconnected');
    await waitFor(client, 'connected');

    expect(events).toEqual(['connected', 'disconnected', 'connected']);

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 5_000);

  test('does not queue duplicate reconnect timers', async () => {
    const { server, port } = await startServer();
    let count = 0;
    server.on('connection', () => count++);

    const client = makeClient(port, { reconnectDelay: 200 });
    client.connect();
    await waitFor(client, 'connected');

    // Simulate rapid close events (shouldn't schedule multiple reconnects)
    (client as unknown as { scheduleReconnect: () => void }).scheduleReconnect?.();
    (client as unknown as { scheduleReconnect: () => void }).scheduleReconnect?.();

    await sleep(300);
    expect(count).toBe(1); // still only the original connection

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 3_000);
});

// ── In-flight rejection on disconnect ─────────────────────────────────────────

describe('in-flight rejection', () => {
  test('all pending requests rejected when connection drops', async () => {
    const { server, port } = await startServer();
    let serverWs!: WebSocket;
    server.on('connection', (ws: WebSocket) => { serverWs = ws; });

    const client = makeClient(port);
    await new Promise<void>((r) => { client.once('connected', r); client.connect(); });

    // Fire 3 requests that the server will never answer
    const results = await Promise.allSettled([
      client.send('cmd1'),
      client.send('cmd2'),
      client.send('cmd3'),
    ].map((p, i) => {
      // Kill the connection after the first send is queued
      if (i === 0) setImmediate(() => serverWs.terminate());
      return p;
    }));

    for (const r of results) {
      expect(r.status).toBe('rejected');
    }

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 5_000);

  test('rejected errors have code DISCONNECTED', async () => {
    const { server, port } = await startServer();
    let serverWs!: WebSocket;
    server.on('connection', (ws: WebSocket) => { serverWs = ws; });

    const client = makeClient(port);
    await new Promise<void>((r) => { client.once('connected', r); client.connect(); });

    const inflight = client.send('slow');
    setImmediate(() => serverWs.terminate());

    const err = await inflight.catch((e: unknown) => e);
    expect(err).toBeInstanceOf(BridgeClientError);
    expect((err as BridgeClientError).code).toBe('DISCONNECTED');

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 3_000);
});

// ── Ping timeout ──────────────────────────────────────────────────────────────

describe('ping timeout', () => {
  test('emits ping_timeout after maxMissedPings intervals', async () => {
    // Server that never responds to pings
    const noPongServer = new WebSocketServer({ port: 0 });
    const port = await new Promise<number>((r) =>
      noPongServer.on('listening', () =>
        r((noPongServer.address() as AddressInfo).port),
      ),
    );

    const client = makeClient(port, {
      pingInterval:   100,
      maxMissedPings: 3,
      reconnectDelay: 10_000, // don't reconnect during this test
    });

    const start = Date.now();
    client.connect();
    await waitFor(client, 'ping_timeout');
    const elapsed = Date.now() - start;

    // Should fire after ~3 intervals = ~300 ms (allow generous window)
    expect(elapsed).toBeGreaterThan(200);
    expect(elapsed).toBeLessThan(1_500);

    client.disconnect();
    await new Promise<void>((r) => noPongServer.close(r));
  }, 5_000);

  test('ping_timeout triggers reconnect (connected fires again)', async () => {
    // First server: never sends pong; second server (same port reused via new server)
    const { server, port } = await startServer();
    let connections = 0;

    server.on('connection', () => {
      connections++;
      // First connection: don't respond to pings → trigger timeout + reconnect
      // Second connection: normal (ws library auto-pongs)
    });

    const client = makeClient(port, {
      pingInterval:   100,
      maxMissedPings: 3,
      reconnectDelay: 50,
    });

    client.connect();
    await waitFor(client, 'connected');
    // wait for ping_timeout and subsequent reconnect
    await waitFor(client, 'ping_timeout');
    await waitFor(client, 'connected');

    expect(connections).toBeGreaterThanOrEqual(2);

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 8_000);
});

// ── Concurrent sends during reconnect window ──────────────────────────────────

describe('concurrent sends during reconnect window', () => {
  test('send() during reconnect window rejects with NOT_CONNECTED', async () => {
    const { server, port } = await startServer();
    let serverWs!: WebSocket;
    server.on('connection', (ws: WebSocket) => { serverWs = ws; });

    const client = makeClient(port, { reconnectDelay: 2_000 }); // long delay
    await new Promise<void>((r) => { client.once('connected', r); client.connect(); });

    // Drop the connection
    serverWs.terminate();
    await waitFor(client, 'disconnected');

    // client is now in the reconnect wait window
    await expect(client.send('anything')).rejects.toMatchObject({ code: 'NOT_CONNECTED' });

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 5_000);
});

// ── connect() idempotency ─────────────────────────────────────────────────────

describe('connect() idempotency', () => {
  test('calling connect() twice does not open two sockets', async () => {
    const { server, port } = await startServer();
    let count = 0;
    server.on('connection', () => count++);

    const client = makeClient(port);
    client.connect();
    client.connect(); // second call should be a no-op

    await waitFor(client, 'connected');
    await sleep(100); // let any spurious second connection arrive

    expect(count).toBe(1);

    client.disconnect();
    await new Promise<void>((r) => server.close(r));
  }, 3_000);
});
