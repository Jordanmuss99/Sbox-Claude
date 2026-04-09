using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// WebSocket server that runs inside the s&box editor.
/// Listens on port 29015 for commands from the MCP Server.
/// </summary>
public static class BridgeServer
{
	private static HttpListener _listener;
	private static CancellationTokenSource _cts;
	private static readonly Dictionary<string, ICommandHandler> _handlers = new();
	private static bool _running;

	public const int DefaultPort = 29015;

	/// <summary>
	/// Register a command handler.
	/// </summary>
	public static void RegisterHandler( string command, ICommandHandler handler )
	{
		_handlers[command] = handler;
	}

	/// <summary>
	/// Start the WebSocket server.
	/// </summary>
	public static async Task Start( int port = DefaultPort )
	{
		if ( _running ) return;

		_cts = new CancellationTokenSource();
		_listener = new HttpListener();
		_listener.Prefixes.Add( $"http://127.0.0.1:{port}/" );
		_listener.Start();
		_running = true;

		Log.Info( $"[SboxBridge] WebSocket server started on port {port}" );

		_ = Task.Run( () => AcceptLoop( _cts.Token ), _cts.Token );
	}

	/// <summary>
	/// Stop the WebSocket server.
	/// </summary>
	public static void Stop()
	{
		if ( !_running ) return;

		_cts?.Cancel();
		_listener?.Stop();
		_running = false;

		Log.Info( "[SboxBridge] WebSocket server stopped" );
	}

	private static async Task AcceptLoop( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested )
		{
			try
			{
				var context = await _listener.GetContextAsync();

				if ( context.Request.IsWebSocketRequest )
				{
					var wsContext = await context.AcceptWebSocketAsync( null );
					_ = Task.Run( () => HandleClient( wsContext.WebSocket, ct ), ct );
				}
				else
				{
					context.Response.StatusCode = 400;
					context.Response.Close();
				}
			}
			catch ( Exception ex ) when ( !ct.IsCancellationRequested )
			{
				Log.Warning( $"[SboxBridge] Accept error: {ex.Message}" );
			}
		}
	}

	private static async Task HandleClient( WebSocket ws, CancellationToken ct )
	{
		var buffer = new byte[65536];

		Log.Info( "[SboxBridge] MCP client connected" );

		try
		{
			while ( ws.State == WebSocketState.Open && !ct.IsCancellationRequested )
			{
				var result = await ws.ReceiveAsync( new ArraySegment<byte>( buffer ), ct );

				if ( result.MessageType == WebSocketMessageType.Close )
				{
					await ws.CloseAsync( WebSocketCloseStatus.NormalClosure, "Closing", ct );
					break;
				}

				if ( result.MessageType == WebSocketMessageType.Text )
				{
					var json = Encoding.UTF8.GetString( buffer, 0, result.Count );
					var response = await ProcessRequest( json );
					var responseBytes = Encoding.UTF8.GetBytes( response );
					await ws.SendAsync( new ArraySegment<byte>( responseBytes ),
						WebSocketMessageType.Text, true, ct );
				}
			}
		}
		catch ( Exception ex ) when ( !ct.IsCancellationRequested )
		{
			Log.Warning( $"[SboxBridge] Client error: {ex.Message}" );
		}

		Log.Info( "[SboxBridge] MCP client disconnected" );
	}

	private static async Task<string> ProcessRequest( string json )
	{
		try
		{
			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;

			var id = root.GetProperty( "id" ).GetString() ?? "";
			var command = root.GetProperty( "command" ).GetString() ?? "";
			var paramsElement = root.TryGetProperty( "params", out var p ) ? p : default;

			if ( _handlers.TryGetValue( command, out var handler ) )
			{
				try
				{
					var result = await handler.Execute( paramsElement );
					return JsonSerializer.Serialize( new
					{
						id,
						success = true,
						data = result
					} );
				}
				catch ( Exception ex )
				{
					return JsonSerializer.Serialize( new
					{
						id,
						success = false,
						error = ex.Message
					} );
				}
			}

			return JsonSerializer.Serialize( new
			{
				id,
				success = false,
				error = $"Unknown command: {command}"
			} );
		}
		catch ( Exception ex )
		{
			return JsonSerializer.Serialize( new
			{
				id = (string)null,
				success = false,
				error = $"Failed to parse request: {ex.Message}"
			} );
		}
	}
}
