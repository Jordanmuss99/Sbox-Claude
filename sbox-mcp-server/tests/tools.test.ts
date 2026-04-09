/**
 * Integration test harness — Task 5.
 *
 * Validates that each MCP tool module sends the correct command name to the
 * bridge, that error responses propagate as BridgeClientErrors, and that
 * timeouts fire after the configured duration.
 *
 * Uses a real WebSocketServer on a random port (no mocking library needed).
 * The s&box editor is never involved.
 */

import { AddressInfo } from 'node:net';
import { WebSocketServer, WebSocket } from 'ws';
import { BridgeClient, BridgeClientError } from '../src/BridgeClient.js';
import { handleConsoleTool } from '../src/tools/console.js';
import { handleGameObjectTool } from '../src/tools/gameobjects.js';
import { handlePlayModeTool } from '../src/tools/playmode.js';
import { handleComponentTool } from '../src/tools/components.js';
import { handleStatusTool } from '../src/tools/status.js';

// ── Helpers ───────────────────────────────────────────────────────────────────

interface ReceivedMsg {
  id: string;
  command: string;
  params: Record<string, unknown>;
}

/** Start a server that echoes back a fixed result for every request. */
function startEchoServer(
  fixedResult: unknown = { ok: true },
): Promise<{ server: WebSocketServer; port: number; messages: ReceivedMsg[] }> {
  const messages: ReceivedMsg[] = [];
  return new Promise((resolve) => {
    const server = new WebSocketServer({ port: 0 }, () => {
      const port = (server.address() as AddressInfo).port;
      server.on('connection', (ws: WebSocket) => {
        ws.on('message', (data: Buffer) => {
          const msg = JSON.parse(data.toString()) as ReceivedMsg;
          messages.push(msg);
          ws.send(JSON.stringify({ id: msg.id, result: fixedResult }));
        });
      });
      resolve({ server, port, messages });
    });
  });
}

/** Start a server that always responds with an error. */
function startErrorServer(
  code = 'HANDLER_ERROR',
  message = 'simulated error',
): Promise<{ server: WebSocketServer; port: number }> {
  return new Promise((resolve) => {
    const server = new WebSocketServer({ port: 0 }, () => {
      const port = (server.address() as AddressInfo).port;
      server.on('connection', (ws: WebSocket) => {
        ws.on('message', (data: Buffer) => {
          const { id } = JSON.parse(data.toString()) as { id: string };
          ws.send(JSON.stringify({ id, error: { code, message } }));
        });
      });
      resolve({ server, port });
    });
  });
}

/** Start a server that never responds (for timeout tests). */
function startSilentServer(): Promise<{ server: WebSocketServer; port: number }> {
  return new Promise((resolve) => {
    const server = new WebSocketServer({ port: 0 }, () => {
      const port = (server.address() as AddressInfo).port;
      server.on('connection', () => {}); // accept but never reply
      resolve({ server, port });
    });
  });
}

function makeClient(port: number, timeoutMs = 500): BridgeClient {
  return new BridgeClient('localhost', port, timeoutMs, 100, 500, 3);
}

async function connectedClient(port: number, timeoutMs = 500): Promise<BridgeClient> {
  const client = makeClient(port, timeoutMs);
  await new Promise<void>((r) => { client.once('connected', r); client.connect(); });
  return client;
}

function closeAll(client: BridgeClient, server: WebSocketServer): Promise<void> {
  client.disconnect();
  return new Promise((r) => server.close(r));
}

// ── Console tools ─────────────────────────────────────────────────────────────

describe('console tools', () => {
  let server: WebSocketServer;
  let messages: ReceivedMsg[];
  let client: BridgeClient;

  beforeEach(async () => {
    ({ server, messages } = await startEchoServer({ count: 0, entries: [] }));
    client = await connectedClient((server.address() as AddressInfo).port);
  });

  afterEach(() => closeAll(client, server));

  test('get_console_output sends correct command', async () => {
    await handleConsoleTool('get_console_output', { severity: 'error', limit: 10 }, client);
    expect(messages[0].command).toBe('get_console_output');
    expect(messages[0].params).toMatchObject({ severity: 'error', limit: 10 });
  });
});

// ── GameObject tools ──────────────────────────────────────────────────────────

describe('gameobject tools', () => {
  const GUID = '00000000-0000-0000-0000-000000000001';
  let server: WebSocketServer;
  let messages: ReceivedMsg[];
  let client: BridgeClient;

  beforeEach(async () => {
    ({ server, messages } = await startEchoServer({ guid: GUID, name: 'Cube' }));
    client = await connectedClient((server.address() as AddressInfo).port);
  });

  afterEach(() => closeAll(client, server));

  test.each([
    ['create_gameobject',           { name: 'Cube' }],
    ['delete_gameobject',           { guid: GUID }],
    ['set_transform',               { guid: GUID, position: { x: 1, y: 2, z: 3 } }],
    ['get_scene_hierarchy',         {}],
    ['get_all_properties',          { guid: GUID, component_type: 'Rigidbody' }],
    ['add_component_with_properties', { guid: GUID, component_type: 'Rigidbody' }],
  ])('%s sends correct command name', async (toolName, args) => {
    await handleGameObjectTool(toolName, args, client);
    expect(messages.at(-1)!.command).toBe(toolName);
  });

  test('create_gameobject forwards params to bridge', async () => {
    await handleGameObjectTool('create_gameobject', { name: 'MyObj', parent: GUID }, client);
    expect(messages[0].params).toMatchObject({ name: 'MyObj', parent: GUID });
  });

  test('unknown tool name throws', async () => {
    await expect(handleGameObjectTool('nonexistent', {}, client))
      .rejects.toThrow('Unknown gameobject tool');
  });
});

// ── Play-mode tools ───────────────────────────────────────────────────────────

describe('playmode tools', () => {
  let server: WebSocketServer;
  let messages: ReceivedMsg[];
  let client: BridgeClient;

  beforeEach(async () => {
    ({ server, messages } = await startEchoServer({ state: 'playing' }));
    client = await connectedClient((server.address() as AddressInfo).port);
  });

  afterEach(() => closeAll(client, server));

  test.each([
    'start_play', 'stop_play', 'is_playing', 'pause_play', 'resume_play',
  ])('%s sends correct command name', async (toolName) => {
    await handlePlayModeTool(toolName, {}, client);
    expect(messages.at(-1)!.command).toBe(toolName);
  });

  test('unknown play-mode tool name throws', async () => {
    await expect(handlePlayModeTool('fly_mode', {}, client))
      .rejects.toThrow('Unknown playmode tool');
  });
});

// ── Component tools ───────────────────────────────────────────────────────────

describe('component tools', () => {
  const GUID = '00000000-0000-0000-0000-000000000002';
  let server: WebSocketServer;
  let messages: ReceivedMsg[];
  let client: BridgeClient;

  beforeEach(async () => {
    ({ server, messages } = await startEchoServer({ property: 'WalkSpeed', new_value: 200 }));
    client = await connectedClient((server.address() as AddressInfo).port);
  });

  afterEach(() => closeAll(client, server));

  test.each([
    ['set_property',          { guid: GUID, component_type: 'PlayerController', property_name: 'WalkSpeed', value: 200 }],
    ['get_runtime_property',  { guid: GUID, component_type: 'PlayerController', property_name: 'WalkSpeed' }],
    ['set_runtime_property',  { guid: GUID, component_type: 'PlayerController', property_name: 'WalkSpeed', value: 300 }],
  ])('%s sends correct command name', async (toolName, args) => {
    await handleComponentTool(toolName, args, client);
    expect(messages.at(-1)!.command).toBe(toolName);
  });
});

// ── Status tools ──────────────────────────────────────────────────────────────

describe('status tools', () => {
  let server: WebSocketServer;
  let messages: ReceivedMsg[];
  let client: BridgeClient;

  beforeEach(async () => {
    ({ server, messages } = await startEchoServer({ pong: true, version: '2024.1' }));
    client = await connectedClient((server.address() as AddressInfo).port);
  });

  afterEach(() => closeAll(client, server));

  test('get_bridge_status reports connected and measures latency', async () => {
    const result = await handleStatusTool('get_bridge_status', {}, client) as Record<string, unknown>;
    expect(result['connected']).toBe(true);
    expect(typeof result['latency_ms']).toBe('number');
    expect(messages[0].command).toBe('ping');
  });
});

// ── Error propagation ─────────────────────────────────────────────────────────

describe('error propagation', () => {
  let server: WebSocketServer;
  let client: BridgeClient;

  afterEach(() => closeAll(client, server));

  test('HANDLER_ERROR propagates as BridgeClientError with correct code', async () => {
    ({ server } = await startErrorServer('HANDLER_ERROR', 'something broke'));
    client = await connectedClient((server.address() as AddressInfo).port);

    const err = await handleGameObjectTool('get_scene_hierarchy', {}, client)
      .catch((e: unknown) => e);

    expect(err).toBeInstanceOf(BridgeClientError);
    expect((err as BridgeClientError).code).toBe('HANDLER_ERROR');
    expect((err as BridgeClientError).message).toBe('something broke');
  });

  test('UNKNOWN_COMMAND propagates with correct code', async () => {
    ({ server } = await startErrorServer('UNKNOWN_COMMAND', 'no handler'));
    client = await connectedClient((server.address() as AddressInfo).port);

    await expect(handleGameObjectTool('get_scene_hierarchy', {}, client))
      .rejects.toMatchObject({ code: 'UNKNOWN_COMMAND' });
  });
});

// ── Timeout ───────────────────────────────────────────────────────────────────

describe('timeout', () => {
  let server: WebSocketServer;
  let client: BridgeClient;

  afterEach(() => closeAll(client, server));

  test('request times out after configured ms and rejects with TIMEOUT', async () => {
    ({ server } = await startSilentServer());
    client = await connectedClient((server.address() as AddressInfo).port, 200);

    const start = Date.now();
    await expect(handleGameObjectTool('get_scene_hierarchy', {}, client))
      .rejects.toMatchObject({ code: 'TIMEOUT' });

    // Should have timed out in roughly the configured window (200 ms ± 100 ms)
    expect(Date.now() - start).toBeGreaterThanOrEqual(190);
    expect(Date.now() - start).toBeLessThan(600);
  }, 3_000);
});
