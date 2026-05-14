using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Handler interface for bridge commands.
/// </summary>
public interface IBridgeHandler
{
	Task<object> Execute( JsonElement parameters );
}

/// <summary>
/// Claude Bridge — file-based IPC server for MCP integration.
/// </summary>
public static class ClaudeBridge
{
	private static readonly Dictionary<string, IBridgeHandler> _handlers = new();
	private static bool _running;
	private static string _ipcDir;
	private static Timer _pollTimer;
	// UTF-8 without BOM — Node.js JSON.parse rejects the BOM prefix
	private static readonly Encoding _utf8NoBom = new UTF8Encoding( false );

	static ClaudeBridge()
	{
		Log.Info( "[SboxBridge] Initializing..." );
		RegisterHandlers();
		StartBridge();
	}

	[Menu( "Editor", "Claude Bridge/Status", "smart_toy" )]
	public static void ShowStatus()
	{
		var msg = _running
			? $"Running\nIPC: {_ipcDir}\nHandlers: {_handlers.Count}"
			: "Not running";
		EditorUtility.DisplayDialog( "Claude Bridge", msg );
	}

	[Menu( "Editor", "Claude Bridge/Restart", "refresh" )]
	public static void RestartBridge()
	{
		Log.Info( "[SboxBridge] Manual restart triggered via menu" );
		StartBridge();
		EditorUtility.DisplayDialog( "Claude Bridge", $"Restarted.\nIPC: {_ipcDir}\nHandlers: {_handlers.Count}" );
	}

	[EditorEvent.Hotload]
	public static void OnHotload()
	{
		Log.Info( "[SboxBridge] Hotload detected — restarting bridge..." );
		// Repopulate handlers in case the static `_handlers` dict got reset by a
		// reload of THIS class but RegisterHandlers() didn't fire (the static
		// cctor only runs on fresh type init, not on hotload re-entry).
		RegisterHandlers();
		StartBridge();
	}

	static void StartBridge()
	{
		// Always dispose any prior Timer first — handles hotload where static refs
		// may survive but the Timer's callback is JIT'd into an unloaded assembly.
		// Removing the historic `if (_running) return;` early-out fixes the
		// "bridge reports Running but Timer dead" zombie state.
		try { _pollTimer?.Dispose(); } catch { }
		_pollTimer = null;
		_running = false;

		try
		{
			_ipcDir = Path.Combine( Path.GetTempPath(), "sbox-bridge-ipc" );
			Directory.CreateDirectory( _ipcDir );

			var statusPath = Path.Combine( _ipcDir, "status.json" );
			File.WriteAllText( statusPath, JsonSerializer.Serialize( new
			{
				running = true,
				startedAt = DateTime.UtcNow.ToString( "o" ),
				handlerCount = _handlers.Count
			} ), _utf8NoBom );

		_running = true;

			// Initialize event dispatcher for editor event capture
			BridgeEventDispatcher.Initialize( _ipcDir );

			// Use a Timer only to read request files from disk (IO is thread-safe)
			// But queue the actual processing for the main thread
			_pollTimer = new Timer( ReadRequestFiles, null, 500, 20 );

			Log.Info( $"[SboxBridge] Bridge started — {_handlers.Count} handlers, IPC at {_ipcDir}" );
			Log.Info( "[SboxBridge] s&box Claude Bridge by sboxskins.gg — https://sboxskins.gg" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SboxBridge] Failed to start: {ex.Message}" );
		}
	}

	// Pending requests read from disk, to be processed on main thread
	static readonly Queue<(string responseId, string json)> _pendingRequests = new();
	static readonly object _queueLock = new();

	static void RegisterHandlers()
	{
		// ── Batch 1: File / project basics ──────────────────────────────
		Register( "get_project_info",    new GetProjectInfoHandler() );
		Register( "list_project_files",  new ListProjectFilesHandler() );
		Register( "read_file",           new ReadFileHandler() );
		Register( "write_file",          new WriteFileHandler() );
		Register( "create_script",       new CreateScriptHandler() );
		Register( "edit_script",         new EditScriptHandler() );
		Register( "delete_script",       new DeleteScriptHandler() );
		Register( "list_scenes",         new ListScenesHandler() );

		// ── Batch 2: Scene file operations ──────────────────────────────
		Register( "load_scene",          new LoadSceneHandler() );
		Register( "save_scene",          new SaveSceneHandler() );
		Register( "create_scene",        new CreateSceneHandler() );

		// ── Batch 3: GameObject CRUD ─────────────────────────────────────
		Register( "create_gameobject",   new CreateGameObjectHandler() );
		Register( "delete_gameobject",   new DeleteGameObjectHandler() );
		Register( "duplicate_gameobject",new DuplicateGameObjectHandler() );
		Register( "rename_gameobject",   new RenameGameObjectHandler() );
		Register( "set_parent",          new SetParentHandler() );
		Register( "set_enabled",         new SetEnabledHandler() );
		Register( "set_transform",       new SetTransformHandler() );
		Register( "get_scene_hierarchy", new GetSceneHierarchyHandler() );
		Register( "get_selected_objects",new GetSelectedObjectsHandler() );
		Register( "select_object",       new SelectObjectHandler() );
		Register( "focus_object",        new FocusObjectHandler() );

		// ── Batch 4: Components ──────────────────────────────────────────
		Register( "get_property",                   new GetPropertyHandler() );
		Register( "get_all_properties",             new GetAllPropertiesHandler() );
		Register( "set_property",                   new SetPropertyHandler() );
		Register( "set_prefab_ref",                 new SetPrefabRefHandler() );
		Register( "list_available_components",      new ListAvailableComponentsHandler() );
		Register( "add_component_with_properties",  new AddComponentWithPropertiesHandler() );

		// ── Batch 5: Play mode ───────────────────────────────────────────
		Register( "start_play",          new StartPlayHandler() );
		Register( "stop_play",           new StopPlayHandler() );
		// pause_play / resume_play — no API found, omitted
		Register( "is_playing",          new IsPlayingHandler() );
		Register( "get_runtime_property",new GetRuntimePropertyHandler() );
		Register( "set_runtime_property",new SetRuntimePropertyHandler() );

		// ── Batch 6: Assets ──────────────────────────────────────────────
		Register( "search_assets",       new SearchAssetsHandler() );
		Register( "get_asset_info",      new GetAssetInfoHandler() );
		Register( "assign_model",        new AssignModelHandler() );
		Register( "create_material",     new CreateMaterialHandler() );
		Register( "assign_material",     new AssignMaterialHandler() );
		Register( "set_material_property", new SetMaterialPropertyHandler() );

		// ── Batch 7: Audio ───────────────────────────────────────────────
		Register( "list_sounds",         new ListSoundsHandler() );
		Register( "create_sound_event",  new CreateSoundEventHandler() );
		Register( "assign_sound",        new AssignSoundHandler() );
		Register( "play_sound_preview",  new PlaySoundPreviewHandler() );

		// ── Batch 8: Prefabs ─────────────────────────────────────────────
		Register( "create_prefab",       new CreatePrefabHandler() );
		Register( "instantiate_prefab",  new InstantiatePrefabHandler() );
		Register( "list_prefabs",        new ListPrefabsHandler() );
		Register( "get_prefab_info",     new GetPrefabInfoHandler() );

		// ── Batch 9: Physics ─────────────────────────────────────────────
		Register( "add_physics",         new AddPhysicsHandler() );
		Register( "add_collider",        new AddColliderHandler() );
		Register( "add_joint",           new AddJointHandler() );
		Register( "raycast",             new RaycastHandler() );

		// ── Batch 10: Code templates ─────────────────────────────────────
		Register( "create_player_controller", new CreatePlayerControllerHandler() );
		Register( "create_npc_controller",    new CreateNpcControllerHandler() );
		Register( "create_game_manager",      new CreateGameManagerHandler() );
		Register( "create_trigger_zone",      new CreateTriggerZoneHandler() );

		// ── Batch 11: UI ─────────────────────────────────────────────────
		Register( "create_razor_ui",     new CreateRazorUIHandler() );
		Register( "add_screen_panel",    new AddScreenPanelHandler() );
		Register( "add_world_panel",     new AddWorldPanelHandler() );

		// ── Batch 11b: Undo/Redo ─────────────────────────────────────────
		Register( "undo",                new UndoHandler() );
		Register( "redo",                new RedoHandler() );

		// ── Batch 12: Networking ─────────────────────────────────────────
		Register( "add_network_helper",  new AddNetworkHelperHandler() );
		Register( "configure_network",   new ConfigureNetworkHandler() );
		Register( "get_network_status",  new GetNetworkStatusHandler() );
		Register( "set_ownership",       new SetOwnershipHandler() );
		Register( "network_spawn",            new NetworkSpawnHandler() );
		Register( "add_sync_property",        new AddSyncPropertyHandler() );
		Register( "add_rpc_method",           new AddRpcMethodHandler() );
		Register( "create_networked_player",  new CreateNetworkedPlayerHandler() );
		Register( "create_lobby_manager",     new CreateLobbyManagerHandler() );
		Register( "create_network_events",    new CreateNetworkEventsHandler() );

		// ── Batch 13: Publishing / config ────────────────────────────────
		Register( "get_project_config",  new GetProjectConfigHandler() );
		Register( "set_project_config",  new SetProjectConfigHandler() );
		Register( "validate_project",    new ValidateProjectHandler() );
		Register( "set_project_thumbnail",new SetProjectThumbnailHandler() );
		Register( "get_package_details", new GetPackageDetailsHandler() );
		Register( "asset_install_pinned", new AssetInstallPinnedHandler() );
		Register( "list_asset_library",  new ListAssetLibraryHandler() );

		// ── Batch 14: Console / diagnostics ─────────────────────────────
		// get_console_output / get_compile_errors / clear_console — LogCapture not available, omitted
		Register( "take_screenshot",     new TakeScreenshotHandler() );
		Register( "trigger_hotload",     new TriggerHotloadHandler() );

		// ── Batch 15: Terrain / Map building ────────────────────────────
		Register( "build_terrain_mesh",       new BuildTerrainMeshHandler() );
		Register( "invoke_button",            new InvokeButtonHandler() );
		Register( "list_component_buttons",   new ListComponentButtonsHandler() );
		Register( "raycast_terrain",          new RaycastTerrainHandler() );
		Register( "add_terrain_hill",         new AddTerrainHillHandler() );
		Register( "add_terrain_clearing",     new AddTerrainClearingHandler() );
		Register( "add_terrain_trail",        new AddTerrainTrailHandler() );
		Register( "clear_terrain_features",   new ClearTerrainFeaturesHandler() );
		Register( "add_cave_waypoint",        new AddCaveWaypointHandler() );
		Register( "clear_cave_path",          new ClearCavePathHandler() );
		Register( "add_forest_poi",           new AddForestPOIHandler() );
		Register( "add_forest_trail",         new AddForestTrailHandler() );
		Register( "set_forest_seed",          new SetForestSeedHandler() );
		Register( "clear_forest_pois",        new ClearForestPOIsHandler() );
		Register( "sculpt_terrain",           new SculptTerrainHandler() );
		Register( "paint_forest_density",     new PaintForestDensityHandler() );
		Register( "place_along_path",         new PlaceAlongPathHandler() );

		// ── Batch 16: Coding / type discovery ───────────────────────────
		Register( "describe_type",            new DescribeTypeHandler() );
		Register( "search_types",             new SearchTypesHandler() );
		Register( "get_method_signature",     new GetMethodSignatureHandler() );
		Register( "find_in_project",          new FindInProjectHandler() );

		// ── Tag management (S5, 2026-05-12) ─────────────────────────
		Register( "tag_add",                  new TagAddHandler() );
		Register( "tag_list",                 new TagListHandler() );
		Register( "tag_remove",               new TagRemoveHandler() );

		// ── Scene search (S6, 2026-05-12) ─────────────────────────
		Register( "scene_find_by_tag",        new SceneFindByTagHandler() );
		Register( "scene_find_by_component",  new SceneFindByComponentHandler() );
		Register( "scene_find_objects",       new SceneFindObjectsHandler() );

		// ── Component lifecycle (S7, 2026-05-12) ──────────────────
		Register( "component_list",           new ComponentListHandler() );
		Register( "component_remove",         new ComponentRemoveHandler() );

		// ── Scene info (S4 closing, 2026-05-12) ────────────────────────────
		Register( "get_scene_info",           new GetSceneInfoHandler() );

		// Execution (S8, 2026-05-13)
		Register( "console_run",              new ConsoleRunHandler() );
		Register( "execute_csharp",           new ExecuteCSharpHandler() );

		// Console capture (S10, 2026-05-13) — register handler FIRST so it's reachable
		// even if log-capture attachment fails (handler will surface the attach error).
		Register( "get_console_output",       new GetConsoleOutputHandler() );
		Register( "ping",                   new PingHandler() );
		try { BridgeLogTarget.EnsureAttached(); }
		catch ( Exception ex ) { Log.Warning( $"[SboxBridge] EnsureAttached threw: {ex.Message}" ); }

		Log.Info( $"[SboxBridge] Registered {_handlers.Count} handlers" );
	}

	public static int HandlerCount => _handlers.Count;

	static void Register( string name, IBridgeHandler handler )
	{
		_handlers[name] = handler;
	}

	/// <summary>
	/// Runs on a timer thread — only reads files from disk and queues them.
	/// </summary>
	static void ReadRequestFiles( object state )
	{
		if ( !_running || _ipcDir == null ) return;

		try
		{
			var files = Directory.GetFiles( _ipcDir, "req_*.json" );
			foreach ( var reqFile in files )
			{
				try
				{
					var json = File.ReadAllText( reqFile, Encoding.UTF8 );
					File.Delete( reqFile );

					var fileName = Path.GetFileNameWithoutExtension( reqFile );
					var responseId = fileName.Substring( 4 );

					lock ( _queueLock )
					{
						_pendingRequests.Enqueue( (responseId, json) );
					}
				}
				catch ( IOException ) { }
				catch ( Exception ex )
				{
					Log.Warning( $"[SboxBridge] Read error: {ex.Message}" );
				}
			}
		}
		catch { }
	}

	/// <summary>
	/// Called on the main thread by BridgePoller widget.
	/// Processes queued requests where scene APIs are safe to call.
	/// </summary>
	public static void ProcessPendingOnMainThread()
	{
		while ( true )
		{
			(string responseId, string json) item;
			lock ( _queueLock )
			{
				if ( _pendingRequests.Count == 0 ) break;
				item = _pendingRequests.Dequeue();
			}

			string response;
			try { response = ProcessRequest( item.json ).GetAwaiter().GetResult(); }
			catch ( Exception ex ) { response = MakeError( null, $"Processing error: {ex.Message}" ); }

			try
			{
				var responsePath = Path.Combine( _ipcDir, $"res_{item.responseId}.json" );
				File.WriteAllText( responsePath, response, _utf8NoBom );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[SboxBridge] Write error: {ex.Message}" );
			}
		}
	}

	static async Task<string> ProcessRequest( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		var id = root.TryGetProperty( "id", out var idProp ) ? idProp.GetString() : null;
		var command = root.TryGetProperty( "command", out var cmdProp ) ? cmdProp.GetString() : null;

		if ( string.IsNullOrEmpty( id ) )
			return MakeError( null, "Missing 'id'", "INVALID_PARAMS" );
		if ( string.IsNullOrEmpty( command ) )
			return MakeError( id, "Missing 'command'", "INVALID_PARAMS" );

		// Built-in status command
		if ( command == "get_bridge_status" )
		{
			return JsonSerializer.Serialize( new
			{
				id, success = true,
				data = new
				{
					connected = true,
					running = _running,
					handlerCount = _handlers.Count,
					registeredCommands = _handlers.Keys.ToArray()
				}
			} );
		}

		// Set prefab reference (inline — handles GameObject properties that set_property can't)
		if ( command == "set_prefab_ref" )
		{
			try
			{
				var sceneRef = SceneEditorSession.Active?.Scene;
				if ( sceneRef == null )
					return JsonSerializer.Serialize( new { id, success = false, error = "No active scene" } );

				var paramsEl = root.GetProperty( "params" );
				var targetIdStr = paramsEl.GetProperty( "id" ).GetString();
				if ( !Guid.TryParse( targetIdStr, out var targetGuid ) )
					return JsonSerializer.Serialize( new { id, success = false, error = "Invalid target GUID" } );

				var targetGo = sceneRef.Directory.FindByGuid( targetGuid );
				if ( targetGo == null )
					return JsonSerializer.Serialize( new { id, success = false, error = "Target GameObject not found" } );

				var componentType = paramsEl.GetProperty( "component" ).GetString();
				var propertyName = paramsEl.GetProperty( "property" ).GetString();
				var prefabPath = paramsEl.GetProperty( "prefabPath" ).GetString();

				var comp = targetGo.Components.GetAll()
					.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );
				if ( comp == null )
					return JsonSerializer.Serialize( new { id, success = false, error = $"Component not found: {componentType}" } );

				var prefabFile = ResourceLibrary.Get<PrefabFile>( prefabPath );
				if ( prefabFile == null )
					return JsonSerializer.Serialize( new { id, success = false, error = $"Prefab not found: {prefabPath}" } );

				GameObject prefabGo = null;
				try { prefabGo = SceneUtility.GetPrefabScene( prefabFile ); }
				catch ( Exception ex ) { return JsonSerializer.Serialize( new { id, success = false, error = $"GetPrefabScene failed: {ex.Message}" } ); }

				if ( prefabGo == null )
					return JsonSerializer.Serialize( new { id, success = false, error = "Prefab scene is null" } );

				var tDesc = Game.TypeLibrary.GetType( comp.GetType().Name );
				var prop = tDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
				if ( prop == null )
					return JsonSerializer.Serialize( new { id, success = false, error = $"Property not found: {propertyName}" } );

				prop.SetValue( comp, prefabGo );
				return JsonSerializer.Serialize( new { id, success = true, data = new { wired = propertyName, prefab = prefabPath } } );
			}
			catch ( Exception ex )
			{
				return MakeError( id, $"set_prefab_ref error: {ex.Message}" );
			}
		}

		if ( _handlers.TryGetValue( command, out var handler ) )
		{
			try
			{
				var paramsElement = root.TryGetProperty( "params", out var p ) ? p : default;
				var result = await ExecuteCommand( command, paramsElement );
				var (isFailure, errMsg, errCode) = IsHandlerFailure( result );
				if ( isFailure )
					return JsonSerializer.Serialize( new { id, success = false, error = errMsg, errorCode = errCode } );
				object dataValue = result;
				var resultType = result.GetType();
				var innerSuccessProp = resultType.GetProperty( "success" );
				if ( innerSuccessProp != null && innerSuccessProp.GetValue( result ) is bool ib && ib )
				{
					var dataProp = resultType.GetProperty( "data" );
					if ( dataProp != null ) dataValue = dataProp.GetValue( result );
				}
				return JsonSerializer.Serialize( new { id, success = true, data = dataValue } );
			}
			catch ( Exception ex )
			{
				return MakeError( id, $"Handler error: {ex.Message}", "HANDLER_ERROR" );
			}
		}

		return MakeError( id, $"Unknown command: {command}", "HANDLER_NOT_FOUND" );
	}

	static string MakeError( string id, string message, string errorCode = null )
	{
		return JsonSerializer.Serialize( new { id, success = false, error = message, errorCode = errorCode ?? "BRIDGE_DISCONNECTED" } );
	}

	/// <summary>
	/// Shared error detection — used by ProcessRequest AND BatchHandler.
	/// Handles null results, bare {error} without success, and both JsonElement/reflection paths.
	/// </summary>
	public static (bool isFailure, string error, string errorCode) IsHandlerFailure( object result )
	{
		if ( result == null )
			return (true, "Handler returned null", "HANDLER_ERROR");
		if ( result is JsonElement je && je.ValueKind == JsonValueKind.Object )
		{
			if ( je.TryGetProperty( "success", out var sp ) && sp.ValueKind == JsonValueKind.False )
			{
				var err = je.TryGetProperty( "error", out var ep ) ? ep.GetString() : "Handler reported failure";
				var ec = je.TryGetProperty( "errorCode", out var cp ) ? cp.GetString() : "HANDLER_ERROR";
				return (true, err, ec);
			}
			if ( !je.TryGetProperty( "success", out _ ) && je.TryGetProperty( "error", out var bareErr ) )
				return (true, bareErr.GetString() ?? "Handler reported failure", "HANDLER_ERROR");
		}
		var resultType = result.GetType();
		var successProp = resultType.GetProperty( "success" );
		if ( successProp != null && successProp.GetValue( result ) is bool b && !b )
		{
			var errProp = resultType.GetProperty( "error" );
			var ecProp = resultType.GetProperty( "errorCode" );
			var err = errProp?.GetValue( result ) as string ?? "Handler reported failure";
			var ec = ecProp?.GetValue( result ) as string ?? "HANDLER_ERROR";
			return (true, err, ec);
		}
		if ( successProp == null && resultType.GetProperty( "error" ) != null )
		{
			var errProp = resultType.GetProperty( "error" );
			var ecProp = resultType.GetProperty( "errorCode" );
			var err = errProp?.GetValue( result ) as string ?? "Handler reported failure";
			var ec = ecProp?.GetValue( result ) as string ?? "HANDLER_ERROR";
			return (true, err, ec);
		}
		return (false, null, null);
	}

	public static async Task<object> ExecuteCommand( string command, JsonElement? parameters = null )
	{
		if ( !_handlers.TryGetValue( command, out var handler ) )
			return new { success = false, error = $"Unknown command: {command}", errorCode = "HANDLER_NOT_FOUND" as string };
		try { return await handler.Execute( parameters ?? default ); }
		catch ( Exception ex ) { return new { success = false, error = $"Handler error: {ex.Message}", errorCode = "HANDLER_ERROR" as string }; }
	}

	public static IEnumerable<string> GetRegisteredCommands() => _handlers.Keys;

	// ── Shared helpers ────────────────────────────────────────────────────
	internal static Vector3 ParseVector3( JsonElement e )
	{
		float x = e.TryGetProperty( "x", out var ex ) ? ex.GetSingle() : 0f;
		float y = e.TryGetProperty( "y", out var ey ) ? ey.GetSingle() : 0f;
		float z = e.TryGetProperty( "z", out var ez ) ? ez.GetSingle() : 0f;
		return new Vector3( x, y, z );
	}

	internal static Rotation ParseRotation( JsonElement e )
	{
		float pitch = e.TryGetProperty( "pitch", out var ep ) ? ep.GetSingle() : 0f;
		float yaw   = e.TryGetProperty( "yaw",   out var ey ) ? ey.GetSingle() : 0f;
		float roll  = e.TryGetProperty( "roll",  out var er ) ? er.GetSingle() : 0f;
		return Rotation.From( pitch, yaw, roll );
	}

	internal static object SerializeGo( GameObject go )
	{
		return new
		{
			id       = go.Id.ToString(),
			name     = go.Name,
			enabled  = go.Enabled,
			parent   = go.Parent?.Id.ToString(),
			position = new { go.WorldPosition.x, go.WorldPosition.y, go.WorldPosition.z },
			rotation = new { pitch = go.WorldRotation.Pitch(), yaw = go.WorldRotation.Yaw(), roll = go.WorldRotation.Roll() },
			scale    = new { go.WorldScale.x, go.WorldScale.y, go.WorldScale.z },
			components = go.Components.GetAll().Select( c => c.GetType().Name ).ToArray(),
			childCount = go.Children.Count
		};
	}

	internal static object SerializeGoTree( GameObject go )
	{
		return new
		{
			id         = go.Id.ToString(),
			name       = go.Name,
			enabled    = go.Enabled,
			components = go.Components.GetAll().Select( c => c.GetType().Name ).ToArray(),
			children   = go.Children.Select( c => SerializeGoTree( c ) ).ToArray()
		};
	}
}

// ═══════════════════════════════════════════════════════════════════
// BridgeEventDispatcher — editor event capture system (Phase 2)
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Captures editor lifecycle events (scene open/play/stop/save, selection changes,
/// asset/resource modifications) and writes them as JSON-lines to events.json.
/// Ring buffer capped at 1000 entries with atomic compaction via temp-file + replace.
/// Every event carries a monotonic eventId and a sessionId GUID for session isolation.
/// </summary>
public static class BridgeEventDispatcher
{
	private static readonly object _eventLock = new();
	private static string _eventsPath;
	private static string _ipcDir;
	private static string _sessionId;
	private static long _nextEventId = 0;
	private static int _writeCountSinceCompact = 0;
	private static readonly Encoding _utf8 = new UTF8Encoding( false );

	public static void Initialize( string ipcDir )
	{
		_ipcDir = ipcDir;
		_eventsPath = Path.Combine( ipcDir, "events.json" );
		_sessionId = Guid.NewGuid().ToString();
		_nextEventId = 0;
	}

	public static void WriteEvent( string type, object data )
	{
		var entry = JsonSerializer.Serialize( new
		{
			eventId = Interlocked.Increment( ref _nextEventId ),
			sessionId = _sessionId,
			timestamp = DateTime.UtcNow.ToString( "o" ),
			type,
			data
		} );

		lock ( _eventLock )
		{
			File.AppendAllText( _eventsPath, entry + "\n", _utf8 );
			_writeCountSinceCompact++;
			var fileInfo = new FileInfo( _eventsPath );
			if ( _writeCountSinceCompact >= 10 || fileInfo.Exists && fileInfo.Length >= 500_000 )
			{
				_writeCountSinceCompact = 0;
				CompactIfNeeded();
			}
		}
	}

	private static void CompactIfNeeded()
	{
		if ( !File.Exists( _eventsPath ) ) return;
		var lines = File.ReadAllLines( _eventsPath, Encoding.UTF8 );
		if ( lines.Length <= 1000 ) return;
		var kept = lines.Skip( lines.Length - 900 ).ToArray();
		var tempPath = Path.Combine( _ipcDir, "events_compact.tmp" );
		File.WriteAllText( tempPath, string.Join( "\n", kept ) + "\n", _utf8 );
		try { File.Move( tempPath, _eventsPath, overwrite: true ); } catch ( IOException ) { /* Node may be reading — retry next compaction */ }
	}

	// ── Scene lifecycle hooks ────────────────────────────────────
	[Event( "scene.open" )]
	public static void OnSceneOpen( Scene scene ) => WriteEvent( "scene.open", new { sceneName = scene?.Name } );

	[Event( "scene.play" )]
	public static void OnScenePlay() => WriteEvent( "scene.play", new { } );

	[Event( "scene.stop" )]
	public static void OnSceneStop() => WriteEvent( "scene.stop", new { } );

	[Event( "scene.saved" )]
	public static void OnSceneSaved( Scene scene ) => WriteEvent( "scene.saved", new { sceneName = scene?.Name } );

	// ── Selection hook ───────────────────────────────────────────
	[Event( "hammer.selection.changed" )]
	public static void OnSelectionChanged()
	{
		var sel = SceneEditorSession.Active?.Selection;
		WriteEvent( "hammer.selection.changed", new
		{
			count = sel?.Count ?? 0,
			ids = sel?.Select( g => g.Id.ToString() ).ToArray()
		} );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 1 — File / project basics (unchanged)
// ═══════════════════════════════════════════════════════════════════

public class GetProjectInfoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var project = Project.Current;
		return Task.FromResult<object>( new
		{
			name       = project.Config.Title,
			org        = project.Config.Org,
			ident      = project.Config.Ident,
			type       = project.Config.Type,
			path       = project.GetRootPath(),
			assetsPath = project.GetAssetsPath()
		} );
	}
}

public class ListProjectFilesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var dir       = p.TryGetProperty( "path", out var d ) ? d.GetString() : "";
		var extension = p.TryGetProperty( "extension",  out var e ) ? e.GetString() : null;
		var recursive = !p.TryGetProperty( "recursive", out var rec ) || rec.GetBoolean();

		var searchDir = string.IsNullOrEmpty( dir )
			? rootPath
			: Path.Combine( rootPath, dir );

		if ( !Directory.Exists( searchDir ) )
			return Task.FromResult<object>( new { error = $"Directory not found: {dir}", files = Array.Empty<string>() } );

		var files = Directory.GetFiles( searchDir, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.Where( f => extension == null || f.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
			.Take( 500 )
			.ToArray();

		return Task.FromResult<object>( new { path = dir, count = files.Length, files } );
	}
}

public class ReadFileHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );
		return Task.FromResult<object>( new { path = filePath, content, length = content.Length } );
	}
}

public class WriteFileHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var content  = p.GetProperty( "content" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		File.WriteAllText( fullPath, content );
		return Task.FromResult<object>( new { path = filePath, written = true, length = content.Length } );
	}
}

public class CreateScriptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.GetProperty( "name" ).GetString();
		var template  = p.TryGetProperty( "template",  out var t ) ? t.GetString() : "component";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName  = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath  = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var className = Path.GetFileNameWithoutExtension( fileName );
		var code = template switch
		{
			"component" => $"using Sandbox;\n\npublic sealed class {className} : Component\n{{\n\tprotected override void OnUpdate()\n\t{{\n\t}}\n}}\n",
			"raw"       => p.TryGetProperty( "content", out var c ) ? c.GetString() : $"// {className}\n",
			_           => $"using Sandbox;\n\npublic sealed class {className} : Component\n{{\n}}\n",
		};

		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { path = $"{directory}/{fileName}", created = true, className } );
	}
}

public class EditScriptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );

		if ( p.TryGetProperty( "find", out var find ) && p.TryGetProperty( "replace", out var replace ) )
		{
			var findStr    = find.GetString();
			var replaceStr = replace.GetString();
			if ( !content.Contains( findStr ) )
				return Task.FromResult<object>( new { error = $"Text not found: {findStr}" } );

			content = content.Replace( findStr, replaceStr );
			File.WriteAllText( fullPath, content );
			return Task.FromResult<object>( new { path = filePath, edited = true, operation = "find_replace" } );
		}

		if ( p.TryGetProperty( "content", out var newContent ) )
		{
			File.WriteAllText( fullPath, newContent.GetString() );
			return Task.FromResult<object>( new { path = filePath, edited = true, operation = "overwrite" } );
		}

		return Task.FromResult<object>( new { error = "Provide 'find'/'replace' or 'content'" } );
	}
}

public class DeleteScriptHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		File.Delete( fullPath );
		return Task.FromResult<object>( new { path = filePath, deleted = true } );
	}
}

public class ListScenesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var scenes = Directory.GetFiles( rootPath, "*.scene", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.ToArray();

		return Task.FromResult<object>( new { count = scenes.Length, scenes } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 2 — Scene file operations
// ═══════════════════════════════════════════════════════════════════

public class LoadSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scenePath = p.GetProperty( "path" ).GetString();
		var rootPath  = Project.Current.GetRootPath();

		// Try as relative path first, then absolute
		var fullPath = Path.IsPathRooted( scenePath )
			? scenePath
			: Path.GetFullPath( Path.Combine( rootPath, scenePath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Scene file not found: {scenePath}" } );

		try
		{
			// SceneFile is the resource type for .scene files
			var sceneFile = ResourceLibrary.Get<SceneFile>( scenePath );
			if ( sceneFile != null )
			{
				EditorScene.OpenScene( sceneFile );
				return Task.FromResult<object>( new { loaded = true, path = scenePath } );
			}
			return Task.FromResult<object>( new { error = "Could not load scene resource. Try using a path relative to the assets folder." } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to load scene: {ex.Message}" } );
		}
	}
}

public class SaveSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			EditorScene.SaveSession();
			return Task.FromResult<object>( new { saved = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to save scene: {ex.Message}" } );
		}
	}
}

public class CreateSceneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var name     = p.GetProperty( "name" ).GetString();
		var rootPath = Project.Current.GetRootPath();
		var subdir   = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Scenes";

		var fileName = name.EndsWith( ".scene" ) ? name : $"{name}.scene";
		var fullPath = Path.Combine( rootPath, subdir, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Scene already exists: {subdir}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		// Minimal valid s&box scene JSON
		var sceneJson = JsonSerializer.Serialize( new
		{
			__version = 0,
			__referencedFiles = Array.Empty<string>(),
			GameObjects = Array.Empty<object>()
		}, new JsonSerializerOptions { WriteIndented = true } );

		File.WriteAllText( fullPath, sceneJson );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 3 — GameObject CRUD
// ═══════════════════════════════════════════════════════════════════

public class CreateGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var name = p.TryGetProperty( "name", out var n ) ? n.GetString() : "GameObject";

		var go = scene.CreateObject( true );
		go.Name = name;

		if ( p.TryGetProperty( "position", out var pos ) )
			go.WorldPosition = ClaudeBridge.ParseVector3( pos );

		if ( p.TryGetProperty( "rotation", out var rot ) )
			go.WorldRotation = ClaudeBridge.ParseRotation( rot );

		if ( p.TryGetProperty( "scale", out var scl ) )
			go.WorldScale = ClaudeBridge.ParseVector3( scl );

		if ( p.TryGetProperty( "parentId", out var pid ) && Guid.TryParse( pid.GetString(), out var parentGuid ) )
		{
			var parent = scene.Directory.FindByGuid( parentGuid );
			if ( parent != null )
				go.SetParent( parent, keepWorldPosition: true );
		}

		if ( p.TryGetProperty( "tags", out var tags ) && tags.ValueKind == JsonValueKind.Array )
		{
			foreach ( var tag in tags.EnumerateArray() )
				go.Tags.Add( tag.GetString() );
		}

		return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

public class DeleteGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var name = go.Name;
		go.Destroy();
		return Task.FromResult<object>( new { deleted = true, id, name } );
	}
}

public class DuplicateGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var clone = go.Clone();

		if ( p.TryGetProperty( "offset", out var off ) )
			clone.WorldPosition = go.WorldPosition + ClaudeBridge.ParseVector3( off );

		if ( p.TryGetProperty( "name", out var nm ) )
			clone.Name = nm.GetString();

		return Task.FromResult<object>( new { duplicated = true, original = id, gameObject = ClaudeBridge.SerializeGo( clone ) } );
	}
}

public class RenameGameObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var oldName = go.Name;
		go.Name = p.GetProperty( "name" ).GetString();
		return Task.FromResult<object>( new { renamed = true, id, oldName, newName = go.Name } );
	}
}

public class SetParentHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid child GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var keepWorld = !p.TryGetProperty( "keepWorldPosition", out var kw ) || kw.GetBoolean();

		// parentId == null → detach to root
		if ( p.TryGetProperty( "parentId", out var pid ) && pid.ValueKind != JsonValueKind.Null )
		{
			if ( !Guid.TryParse( pid.GetString(), out var parentGuid ) )
				return Task.FromResult<object>( new { error = "Invalid parent GUID" } );

			var parent = scene.Directory.FindByGuid( parentGuid );
			if ( parent == null )
				return Task.FromResult<object>( new { error = $"Parent not found: {pid.GetString()}" } );

			go.SetParent( parent, keepWorld );
			return Task.FromResult<object>( new { parented = true, id, parentId = pid.GetString() } );
		}

		go.SetParent( null, keepWorld );
		return Task.FromResult<object>( new { parented = true, id, parentId = (string)null } );
	}
}

public class SetEnabledHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var enabled = p.GetProperty( "enabled" ).GetBoolean();
		go.Enabled = enabled;
		return Task.FromResult<object>( new { id, enabled } );
	}
}

public class SetTransformHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var local = p.TryGetProperty( "local", out var lc ) && lc.GetBoolean();

		if ( p.TryGetProperty( "position", out var pos ) )
		{
			if ( local ) go.LocalPosition = ClaudeBridge.ParseVector3( pos );
			else         go.WorldPosition = ClaudeBridge.ParseVector3( pos );
		}

		if ( p.TryGetProperty( "rotation", out var rot ) )
		{
			if ( local ) go.LocalRotation = ClaudeBridge.ParseRotation( rot );
			else         go.WorldRotation = ClaudeBridge.ParseRotation( rot );
		}

		if ( p.TryGetProperty( "scale", out var scl ) )
		{
			if ( local ) go.LocalScale = ClaudeBridge.ParseVector3( scl );
			else         go.WorldScale  = ClaudeBridge.ParseVector3( scl );
		}

		return Task.FromResult<object>( new { transformed = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
	}
}

public class GetSceneHierarchyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var roots = scene.Children
			.Select( go => ClaudeBridge.SerializeGoTree( go ) )
			.ToArray();

		return Task.FromResult<object>( new
		{
			sceneName = scene.Name,
			objectCount = scene.GetAllObjects( true ).Count(),
			hierarchy = roots
		} );
	}
}

public class GetSelectedObjectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var selected = SceneEditorSession.Active.Selection
			.OfType<GameObject>()
			.Select( go => ClaudeBridge.SerializeGo( go ) )
			.ToArray();

		return Task.FromResult<object>( new { count = selected.Length, selected } );
	}
}

public class SelectObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var add = p.TryGetProperty( "addToSelection", out var at ) && at.GetBoolean();
		if ( add )
			SceneEditorSession.Active.Selection.Add( go );
		else
			SceneEditorSession.Active.Selection.Set( go );

		return Task.FromResult<object>( new { selected = true, id } );
	}
}

public class FocusObjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		// No dedicated focus API — select the object so the editor highlights it
		SceneEditorSession.Active.Selection.Set( go );
		return Task.FromResult<object>( new { focused = true, id, note = "Object selected in editor (no separate focus API)" } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 4 — Components
// ═══════════════════════════════════════════════════════════════════

public class GetPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var componentType = p.TryGetProperty( "component", out var ct ) ? ct.GetString() : null;
		var propertyName  = p.GetProperty( "property" ).GetString();

		var component = FindComponent( go, componentType );
		if ( component == null )
			return Task.FromResult<object>( new { error = $"Component not found: {componentType}" } );

		try
		{
			var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
			if ( propDesc == null )
				return Task.FromResult<object>( new { error = $"Property not found: {propertyName}" } );

			var value = propDesc.GetValue( component );
			return Task.FromResult<object>( new { id, component = component.GetType().Name, property = propertyName, value = value?.ToString() } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to get property: {ex.Message}" } );
		}
	}

	static Component FindComponent( GameObject go, string typeName )
	{
		if ( string.IsNullOrEmpty( typeName ) )
			return go.Components.GetAll().FirstOrDefault();

		return go.Components.GetAll()
			.FirstOrDefault( c => c.GetType().Name.Equals( typeName, StringComparison.OrdinalIgnoreCase ) );
	}
}

public class GetAllPropertiesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var result = new List<object>();
		foreach ( var component in go.Components.GetAll() )
		{
			var typeName = component.GetType().Name;
			var typeDesc = Game.TypeLibrary.GetType( typeName );
			var props = new List<object>();

			if ( typeDesc != null )
			{
				foreach ( var propDesc in typeDesc.Properties )
				{
					try
					{
						var value = propDesc.GetValue( component );
						props.Add( new { name = propDesc.Name, type = propDesc.PropertyType?.Name, value = value?.ToString() } );
					}
					catch { props.Add( new { name = propDesc.Name, type = propDesc.PropertyType?.Name, value = "<error>" } ); }
				}
			}

			result.Add( new { component = typeName, properties = props } );
		}

		return Task.FromResult<object>( new { id, components = result } );
	}
}

/// <summary>
/// Sets a GameObject-typed property on a component to a loaded prefab.
/// Use this when you need to assign a prefab reference that set_property can't handle.
/// </summary>
public class SetPrefabRefHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var componentType = p.GetProperty( "component" ).GetString();
		var propertyName = p.GetProperty( "property" ).GetString();
		var prefabPath = p.GetProperty( "prefabPath" ).GetString();

		var component = go.Components.GetAll()
			.FirstOrDefault( c => c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );
		if ( component == null )
			return Task.FromResult<object>( new { error = $"Component not found: {componentType}" } );

		// Load the prefab
		var prefabFile = ResourceLibrary.Get<PrefabFile>( prefabPath );
		if ( prefabFile == null )
			return Task.FromResult<object>( new { error = $"Prefab not found: {prefabPath}" } );

		// Get the GameObject from the prefab scene
		GameObject prefabGo = null;
		try
		{
			prefabGo = SceneUtility.GetPrefabScene( prefabFile );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to get prefab scene: {ex.Message}" } );
		}

		if ( prefabGo == null )
			return Task.FromResult<object>( new { error = "Prefab scene GameObject is null" } );

		try
		{
			var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
			if ( propDesc == null )
				return Task.FromResult<object>( new { error = $"Property not found: {propertyName}" } );

			propDesc.SetValue( component, prefabGo );
			return Task.FromResult<object>( new { set = true, id, component = componentType, property = propertyName, prefabPath } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set prefab ref: {ex.Message}" } );
		}
	}
}

public class SetPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var componentType = p.TryGetProperty( "component", out var ct ) ? ct.GetString() : null;
		var propertyName  = p.GetProperty( "property" ).GetString();
		var valueStr      = p.GetProperty( "value" ).GetString();

		var component = go.Components.GetAll()
			.FirstOrDefault( c => string.IsNullOrEmpty( componentType ) ||
			                      c.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) );

		if ( component == null )
			return Task.FromResult<object>( new { error = $"Component not found: {componentType}" } );

		try
		{
			var typeDesc = Game.TypeLibrary.GetType( component.GetType().Name );
			var propDesc = typeDesc?.Properties.FirstOrDefault( pp => pp.Name == propertyName );
			if ( propDesc == null )
				return Task.FromResult<object>( new { error = $"Property not found: {propertyName}" } );

			// Attempt type-safe conversion
			var propType = propDesc.PropertyType;
			object typedValue = propType?.Name switch
			{
				"Single"  or "float"  => float.Parse( valueStr ),
				"Double"  or "double" => double.Parse( valueStr ),
				"Int32"   or "int"    => int.Parse( valueStr ),
				"Boolean" or "bool"   => bool.Parse( valueStr ),
				"String"  or "string" => valueStr,
				_                     => valueStr
			};

			propDesc.SetValue( component, typedValue );
			return Task.FromResult<object>( new { set = true, id, component = component.GetType().Name, property = propertyName, value = valueStr } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set property: {ex.Message}" } );
		}
	}
}

public class ListAvailableComponentsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var filter = p.TryGetProperty( "filter", out var f ) ? f.GetString() : null;

		var types = Game.TypeLibrary.GetTypes<Component>()
			.Where( t => !t.IsAbstract )
			.Where( t => filter == null || t.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
			.Select( t => new { name = t.Name, title = t.Title, description = t.Description, fullName = t.FullName } )
			.OrderBy( t => t.name )
			.ToArray();

		return Task.FromResult<object>( new { count = types.Length, components = types } );
	}
}

public class AddComponentWithPropertiesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var typeName = p.GetProperty( "component" ).GetString();
		var typeDesc = Game.TypeLibrary.GetType( typeName );
		if ( typeDesc == null )
			return Task.FromResult<object>( new { error = $"Component type not found: {typeName}" } );

		try
		{
			var component = go.Components.Create( typeDesc );
			if ( component == null )
				return Task.FromResult<object>( new { error = "Failed to create component instance" } );

			// Apply optional property overrides
			if ( p.TryGetProperty( "properties", out var props ) && props.ValueKind == JsonValueKind.Object )
			{
				foreach ( var prop in props.EnumerateObject() )
				{
					try
					{
						var pd = typeDesc.Properties.FirstOrDefault( pp => pp.Name == prop.Name );
						if ( pd != null )
						{
							var propType = pd.PropertyType;
							object typedValue = propType?.Name switch
							{
								"Single"  or "float"  => float.Parse( prop.Value.GetString() ),
								"Double"  or "double" => double.Parse( prop.Value.GetString() ),
								"Int32"   or "int"    => int.Parse( prop.Value.GetString() ),
								"Boolean" or "bool"   => prop.Value.ValueKind == JsonValueKind.True,
								_                     => prop.Value.GetString()
							};
							pd.SetValue( component, typedValue );
						}
					}
					catch { /* best-effort property set */ }
				}
			}

			return Task.FromResult<object>( new { added = true, id, component = typeName } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add component: {ex.Message}" } );
		}
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 5 — Play mode
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Tracks editor play-mode state since Game.IsPlaying isn't reliable in editor context.
/// </summary>
public static class PlayState
{
	public static bool IsPlaying;
}

public class StartPlayHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var session = SceneEditorSession.Active;
		if ( session == null )
			return Task.FromResult<object>( new { error = "No active scene session" } );

		// Try the safe path first — matches what the editor Play button does.
		// This serializes the scene to catch any invalid state before actually playing.
		try
		{
			EditorScene.Play( session );
			PlayState.IsPlaying = true;
			return Task.FromResult<object>( new { started = true, method = "EditorScene.Play" } );
		}
		catch ( Exception editorEx )
		{
			// Fall back to direct SetPlaying. This skips scene serialization, which
			// is a workaround but can leave the editor in a half-play state if the
			// scene has invalid components. Only use if EditorScene.Play fails.
			try
			{
				session.SetPlaying( session.Scene );
				PlayState.IsPlaying = true;
				return Task.FromResult<object>( new
				{
					started = true,
					method = "SetPlaying (fallback)",
					editorErrorSkipped = editorEx.Message
				} );
			}
			catch ( Exception ex )
			{
				return Task.FromResult<object>( new
				{
					error = $"Failed both paths. Editor: {editorEx.Message} | Direct: {ex.Message}"
				} );
			}
		}
	}
}

public class StopPlayHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			SceneEditorSession.Active?.StopPlaying();
			PlayState.IsPlaying = false;
			return Task.FromResult<object>( new { stopped = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to stop play: {ex.Message}" } );
		}
	}
}

public class IsPlayingHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		// Check multiple signals: our tracked flag, Game.IsPlaying, and whether
		// the active scene is a game scene vs. editor scene.
		var tracked = PlayState.IsPlaying;
		var gameFlag = Game.IsPlaying;

		// Editor scene and game scene diverge during play mode
		bool sessionPlaying = false;
		try
		{
			var session = SceneEditorSession.Active;
			if ( session != null && Game.ActiveScene != null )
			{
				sessionPlaying = Game.ActiveScene != session.Scene;
			}
		}
		catch { }

		var isPlaying = tracked || gameFlag || sessionPlaying;

		return Task.FromResult<object>( new
		{
			isPlaying,
			isPaused = Game.IsPaused,
			gameFlag,
			tracked,
			sessionPlaying
		} );
	}
}

public class GetRuntimePropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "Game is not currently playing" } );

		// Reuse GetPropertyHandler logic
		return new GetPropertyHandler().Execute( p );
	}
}

public class SetRuntimePropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		if ( !Game.IsPlaying )
			return Task.FromResult<object>( new { error = "Game is not currently playing" } );

		return new SetPropertyHandler().Execute( p );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 6 — Assets
// ═══════════════════════════════════════════════════════════════════

public class SearchAssetsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var query     = p.TryGetProperty( "query",     out var q ) ? q.GetString() : null;
		var extension = p.TryGetProperty( "extension", out var e ) ? e.GetString() : null;

		var files = Directory.GetFiles( rootPath, "*.*", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.Where( f => extension == null || f.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
			.Where( f => query     == null || f.Contains( query, StringComparison.OrdinalIgnoreCase ) )
			.Take( 200 )
			.ToArray();

		return Task.FromResult<object>( new { count = files.Length, assets = files } );
	}
}

public class GetAssetInfoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var filePath = p.GetProperty( "path" ).GetString();
		var fullPath = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Asset not found: {filePath}" } );

		var info = new FileInfo( fullPath );
		return Task.FromResult<object>( new
		{
			path      = filePath,
			name      = info.Name,
			extension = info.Extension,
			size      = info.Length,
			modified  = info.LastWriteTimeUtc.ToString( "o" )
		} );
	}
}

public class AssignModelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var modelPath = p.GetProperty( "model" ).GetString();
		var model = Model.Load( modelPath );
		if ( model == null )
			return Task.FromResult<object>( new { error = $"Model not found: {modelPath}" } );

		var renderer = go.GetOrAddComponent<ModelRenderer>();
		renderer.Model = model;
		return Task.FromResult<object>( new { assigned = true, id, model = modelPath } );
	}
}

public class CreateMaterialHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var name     = p.GetProperty( "name" ).GetString();
		var subdir   = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Materials";

		var fileName = name.EndsWith( ".vmat" ) ? name : $"{name}.vmat";
		var fullPath = Path.Combine( rootPath, subdir, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Material already exists: {subdir}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var shader = p.TryGetProperty( "shader", out var sh ) ? sh.GetString() : "shaders/simple.shader";
		var vmat = $"// THIS FILE IS AUTO-GENERATED\n\"Layer0\"\n{{\n\tshader \"{shader}\"\n\n\tF_SELF_ILLUM 0\n\n\tTextureColor \"materials/default/default.tga\"\n}}\n";

		File.WriteAllText( fullPath, vmat );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath } );
	}
}

public class AssignMaterialHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var materialPath = p.GetProperty( "material" ).GetString();
		var material = Material.Load( materialPath );
		if ( material == null )
			return Task.FromResult<object>( new { error = $"Material not found: {materialPath}" } );

		var renderer = go.GetComponent<ModelRenderer>();
		if ( renderer == null )
			return Task.FromResult<object>( new { error = "No ModelRenderer on GameObject" } );

		renderer.MaterialOverride = material;
		return Task.FromResult<object>( new { assigned = true, id, material = materialPath } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 7 — Audio
// ═══════════════════════════════════════════════════════════════════

public class ListSoundsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var sounds = Directory.GetFiles( rootPath, "*.sound", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.ToArray();

		return Task.FromResult<object>( new { count = sounds.Length, sounds } );
	}
}

public class CreateSoundEventHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var name     = p.GetProperty( "name" ).GetString();
		var subdir   = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Sounds";

		var fileName = name.EndsWith( ".sound" ) ? name : $"{name}.sound";
		var fullPath = Path.Combine( rootPath, subdir, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Sound already exists: {subdir}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var volume = p.TryGetProperty( "volume", out var v ) ? v.GetSingle() : 1.0f;
		var soundJson = JsonSerializer.Serialize( new
		{
			__version  = 0,
			Sounds     = Array.Empty<object>(),
			Volume     = volume,
			Pitch      = 1.0f,
			Attenuation = 1.0f
		}, new JsonSerializerOptions { WriteIndented = true } );

		File.WriteAllText( fullPath, soundJson );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 8 — Prefabs
// ═══════════════════════════════════════════════════════════════════

public class CreatePrefabHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var rootPath = Project.Current.GetRootPath();

		// If "path" is given use it directly, otherwise fall back to name+directory
		string fullPath;
		if ( p.TryGetProperty( "path", out var pathProp ) )
		{
			var prefabRelPath = pathProp.GetString();
			fullPath = Path.GetFullPath( Path.Combine( rootPath, prefabRelPath ) );
		}
		else
		{
			var name   = p.TryGetProperty( "name", out var n ) ? n.GetString() : go.Name;
			var subdir = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Prefabs";
			var fileName = name.EndsWith( ".prefab" ) ? name : $"{name}.prefab";
			fullPath = Path.Combine( rootPath, subdir, fileName );
		}
		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		// Serialize a minimal prefab descriptor referencing the GameObject
		var prefabJson = JsonSerializer.Serialize( new
		{
			__version  = 0,
			RootObject = new
			{
				Id         = go.Id.ToString(),
				Name       = go.Name,
				Enabled    = go.Enabled,
				Components = go.Components.GetAll().Select( c => new { Type = c.GetType().Name } ).ToArray()
			}
		}, new JsonSerializerOptions { WriteIndented = true } );

		File.WriteAllText( fullPath, prefabJson );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath, sourceId = id } );
	}
}

public class InstantiatePrefabHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var prefabPath = p.GetProperty( "path" ).GetString();
		var rootPath   = Project.Current.GetRootPath();
		var fullPath   = Path.GetFullPath( Path.Combine( rootPath, prefabPath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Prefab not found: {prefabPath}" } );

		try
		{
			// Read the prefab to get the name
			var json      = File.ReadAllText( fullPath );
			using var doc = JsonDocument.Parse( json );
			var prefabName = doc.RootElement
				.TryGetProperty( "RootObject", out var ro ) &&
				ro.TryGetProperty( "Name", out var nm )
				? nm.GetString()
				: Path.GetFileNameWithoutExtension( prefabPath );

			// Create a new GO mirroring the prefab descriptor
			var go = scene.CreateObject( true );
			go.Name = prefabName;

			if ( p.TryGetProperty( "position", out var pos ) )
				go.WorldPosition = ClaudeBridge.ParseVector3( pos );

			if ( p.TryGetProperty( "rotation", out var rot ) )
				go.WorldRotation = ClaudeBridge.ParseRotation( rot );

			return Task.FromResult<object>( new
			{
				instantiated = true,
				prefab       = prefabPath,
				gameObject   = ClaudeBridge.SerializeGo( go ),
				note         = "Basic instantiation — full prefab resource loading requires s&box prefab asset pipeline"
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to instantiate prefab: {ex.Message}" } );
		}
	}
}

public class ListPrefabsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var prefabs = Directory.GetFiles( rootPath, "*.prefab", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( rootPath, f ).Replace( '\\', '/' ) )
			.ToArray();

		return Task.FromResult<object>( new { count = prefabs.Length, prefabs } );
	}
}

public class GetPrefabInfoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath   = Project.Current.GetRootPath();
		var prefabPath = p.GetProperty( "path" ).GetString();
		var fullPath   = Path.GetFullPath( Path.Combine( rootPath, prefabPath ) );

		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"Prefab not found: {prefabPath}" } );

		var content = File.ReadAllText( fullPath );
		var info    = new FileInfo( fullPath );
		return Task.FromResult<object>( new
		{
			path     = prefabPath,
			name     = info.Name,
			size     = info.Length,
			modified = info.LastWriteTimeUtc.ToString( "o" ),
			content
		} );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 9 — Physics
// ═══════════════════════════════════════════════════════════════════

public class AddPhysicsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var rb = go.GetOrAddComponent<Rigidbody>();

		if ( p.TryGetProperty( "gravity", out var g ) ) rb.Gravity      = g.GetBoolean();
		if ( p.TryGetProperty( "mass",    out var m ) ) rb.MassOverride = m.GetSingle();

		var colliderType = p.TryGetProperty( "collider", out var ct ) ? ct.GetString() : "box";
		var added = new List<string> { "Rigidbody" };

		switch ( colliderType.ToLower() )
		{
			case "sphere":
				var sphere = go.GetOrAddComponent<SphereCollider>();
				if ( p.TryGetProperty( "radius", out var r ) ) sphere.Radius = r.GetSingle();
				added.Add( "SphereCollider" );
				break;
			case "capsule":
				go.GetOrAddComponent<CapsuleCollider>();
				added.Add( "CapsuleCollider" );
				break;
			default: // "box"
				var box = go.GetOrAddComponent<BoxCollider>();
				if ( p.TryGetProperty( "scale", out var s ) ) box.Scale = ClaudeBridge.ParseVector3( s );
				added.Add( "BoxCollider" );
				break;
		}

		return Task.FromResult<object>( new { physicsAdded = true, id, components = added } );
	}
}

public class AddColliderHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var colliderType = p.TryGetProperty( "type", out var ct ) ? ct.GetString() : "box";
		var isTrigger    = p.TryGetProperty( "isTrigger", out var it ) && it.GetBoolean();

		string addedType;
		switch ( colliderType.ToLower() )
		{
			case "sphere":
				var sphere = go.GetOrAddComponent<SphereCollider>();
				if ( p.TryGetProperty( "radius", out var r ) ) sphere.Radius = r.GetSingle();
				sphere.IsTrigger = isTrigger;
				addedType = "SphereCollider";
				break;
			case "capsule":
				var cap = go.GetOrAddComponent<CapsuleCollider>();
				cap.IsTrigger = isTrigger;
				addedType = "CapsuleCollider";
				break;
			case "mesh":
				var mesh = go.GetOrAddComponent<HullCollider>();
				mesh.IsTrigger = isTrigger;
				addedType = "HullCollider";
				break;
			default: // "box"
				var box = go.GetOrAddComponent<BoxCollider>();
				if ( p.TryGetProperty( "scale", out var s ) ) box.Scale = ClaudeBridge.ParseVector3( s );
				box.IsTrigger = isTrigger;
				addedType = "BoxCollider";
				break;
		}

		return Task.FromResult<object>( new { added = true, id, collider = addedType, isTrigger } );
	}
}

public class RaycastHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var start = ClaudeBridge.ParseVector3( p.GetProperty( "start" ) );
		var end   = ClaudeBridge.ParseVector3( p.GetProperty( "end" ) );

		try
		{
			var tr = scene.Trace.Ray( start, end ).Run();

			return Task.FromResult<object>( new
			{
				hit          = tr.Hit,
				hitPosition  = tr.Hit ? new { tr.HitPosition.x, tr.HitPosition.y, tr.HitPosition.z } : null,
				normal       = tr.Hit ? new { tr.Normal.x, tr.Normal.y, tr.Normal.z } : null,
				distance     = tr.Distance,
				gameObjectId = tr.GameObject?.Id.ToString(),
				gameObjectName = tr.GameObject?.Name
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Raycast failed: {ex.Message}" } );
		}
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 10 — Code templates
// ═══════════════════════════════════════════════════════════════════

public class CreatePlayerControllerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "PlayerController";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	[Property] public float MoveSpeed {{ get; set; }} = 200f;
	[Property] public float JumpForce {{ get; set; }} = 400f;

	private CharacterController _controller;

	protected override void OnStart()
	{{
		_controller = GetOrAddComponent<CharacterController>();
	}}

	protected override void OnUpdate()
	{{
		if ( _controller == null ) return;

		var move = new Vector3(
			Input.AnalogMove.x,
			0,
			Input.AnalogMove.y
		) * MoveSpeed;

		if ( _controller.IsOnGround && Input.Pressed( ""jump"" ) )
			_controller.Punch( Vector3.Up * JumpForce );

		_controller.Accelerate( move );
		_controller.ApplyFriction( 10f );
		_controller.Move();
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateNpcControllerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NpcController";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	[Property] public float MoveSpeed    {{ get; set; }} = 100f;
	[Property] public float DetectRadius {{ get; set; }} = 500f;
	[Property] public GameObject Target  {{ get; set; }}

	private NavMeshAgent _agent;

	protected override void OnStart()
	{{
		_agent = GetOrAddComponent<NavMeshAgent>();
	}}

	protected override void OnUpdate()
	{{
		if ( Target == null || _agent == null ) return;

		float dist = Vector3.DistanceBetween( WorldPosition, Target.WorldPosition );
		if ( dist < DetectRadius )
			_agent.MoveTo( Target.WorldPosition );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateGameManagerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "GameManager";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component, Component.INetworkListener
{{
	public static {className} Instance {{ get; private set; }}

	[Property] public int MaxPlayers {{ get; set; }} = 16;
	[Property] public string GameState {{ get; set; }} = ""waiting"";

	protected override void OnStart()
	{{
		Instance = this;
		Log.Info( $""[{className}] Started. State: {{GameState}}"" );
	}}

	protected override void OnDestroy()
	{{
		if ( Instance == this ) Instance = null;
	}}

	public void OnActive( Connection channel )
	{{
		Log.Info( $""[{className}] Player connected: {{channel.DisplayName}}"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateTriggerZoneHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "TriggerZone";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component, Component.ITriggerListener
{{
	[Property] public string TriggerTag {{ get; set; }} = ""player"";

	protected override void OnStart()
	{{
		var collider = GetOrAddComponent<BoxCollider>();
		collider.IsTrigger = true;
	}}

	public void OnTriggerEnter( Collider other )
	{{
		if ( other.GameObject.Tags.Has( TriggerTag ) )
			OnPlayerEnter( other.GameObject );
	}}

	public void OnTriggerExit( Collider other )
	{{
		if ( other.GameObject.Tags.Has( TriggerTag ) )
			OnPlayerExit( other.GameObject );
	}}

	private void OnPlayerEnter( GameObject player )
	{{
		Log.Info( $""[{className}] {{player.Name}} entered trigger"" );
	}}

	private void OnPlayerExit( GameObject player )
	{{
		Log.Info( $""[{className}] {{player.Name}} exited trigger"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 11 — UI
// ═══════════════════════════════════════════════════════════════════

public class CreateRazorUIHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.GetProperty( "name" ).GetString();
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "UI";

		var fileName = name.EndsWith( ".razor" ) ? name : $"{name}.razor";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		var componentName = Path.GetFileNameWithoutExtension( fileName );
		var razor = $@"@using Sandbox;
@using Sandbox.UI;

@namespace {componentName}

<root class=""{componentName.ToLower()}"">
	<div class=""container"">
		<label>@Title</label>
	</div>
</root>

@code {{
	[Property] public string Title {{ get; set; }} = ""{componentName}"";
}}
";
		File.WriteAllText( fullPath, razor );
		var relativePath = Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );
		return Task.FromResult<object>( new { created = true, path = relativePath, componentName } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 12 — Networking
// ═══════════════════════════════════════════════════════════════════

public class NetworkSpawnHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		try
		{
			go.NetworkSpawn();
			return Task.FromResult<object>( new { spawned = true, id } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"NetworkSpawn failed: {ex.Message}" } );
		}
	}
}

public class AddSyncPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath     = Project.Current.GetRootPath();
		var filePath     = p.GetProperty( "path" ).GetString();
		var propertyName = p.GetProperty( "propertyName" ).GetString();
		var propertyType = p.TryGetProperty( "propertyType", out var ptProp ) ? ptProp.GetString() ?? "float" : "float";
		var defaultValue = p.TryGetProperty( "defaultValue", out var dvProp ) ? dvProp.GetString() : null;
		var fullPath     = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );

		// Find the property declaration and add [Sync] above it if not already present
		var propPattern = $"public ";
		var propIndex   = content.IndexOf( $"public ", StringComparison.Ordinal );

		// More targeted: find the specific property
		var searchStr = $"public.*{propertyName}";
		var lines     = content.Split( '\n' ).ToList();
		bool modified = false;

		for ( int i = 0; i < lines.Count; i++ )
		{
			if ( lines[i].Contains( propertyName ) && lines[i].Contains( "public" ) && lines[i].Contains( "{" ) )
			{
				if ( i > 0 && lines[i - 1].TrimStart().StartsWith( "[Sync]" ) )
				{
					return Task.FromResult<object>( new { error = $"Property '{propertyName}' already has [Sync]" } );
				}

				var indent = new string( '\t', lines[i].TakeWhile( c => c == '\t' ).Count() );
				lines.Insert( i, $"{indent}[Sync]" );
				modified = true;
				break;
			}
		}

		if ( !modified )
			return Task.FromResult<object>( new { error = $"Property '{propertyName}' not found in file" } );

		File.WriteAllText( fullPath, string.Join( '\n', lines ) );
		return Task.FromResult<object>( new { added = true, path = filePath, property = propertyName, attribute = "[Sync]" } );
	}
}

public class AddRpcMethodHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath   = Project.Current.GetRootPath();
		var filePath   = p.GetProperty( "path" ).GetString();
		var methodName = p.TryGetProperty( "methodName", out var m ) ? m.GetString() : "MyRpc";
		var rpcType    = p.TryGetProperty( "rpcType", out var rt ) ? rt.GetString() : "Broadcast";
		var fullPath   = Path.GetFullPath( Path.Combine( rootPath, filePath ) );

		if ( !fullPath.StartsWith( rootPath, StringComparison.OrdinalIgnoreCase ) )
			return Task.FromResult<object>( new { error = "Path traversal denied" } );
		if ( !File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File not found: {filePath}" } );

		var content = File.ReadAllText( fullPath );

		// Insert new RPC method before the last closing brace of the class
		var lastBrace = content.LastIndexOf( '}' );
		if ( lastBrace < 0 )
			return Task.FromResult<object>( new { error = "Could not find closing brace in file" } );

		var rpcAttr = rpcType.ToLower() switch
		{
			"owner"  => "[Rpc.Owner]",
			"host"   => "[Rpc.Host]",
			_        => "[Rpc.Broadcast]"
		};

		var methodCode = $"\n\t{rpcAttr}\n\tpublic void {methodName}()\n\t{{\n\t\t// TODO: implement RPC\n\t}}\n";
		content = content.Insert( lastBrace, methodCode );
		File.WriteAllText( fullPath, content );

		return Task.FromResult<object>( new { added = true, path = filePath, method = methodName, attribute = rpcAttr } );
	}
}

public class CreateNetworkedPlayerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NetworkedPlayer";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	[Sync] public string PlayerName {{ get; set; }}
	[Sync] public int    Health     {{ get; set; }} = 100;

	[Property] public float MoveSpeed {{ get; set; }} = 200f;

	private CharacterController _controller;

	protected override void OnStart()
	{{
		_controller = GetOrAddComponent<CharacterController>();

		if ( IsProxy ) return;

		PlayerName = Connection.Local.DisplayName;
		Health     = 100;
	}}

	protected override void OnUpdate()
	{{
		if ( IsProxy || _controller == null ) return;

		var move = new Vector3(
			Input.AnalogMove.x,
			0,
			Input.AnalogMove.y
		) * MoveSpeed;

		_controller.Accelerate( move );
		_controller.ApplyFriction( 10f );
		_controller.Move();
	}}

	[Rpc.Broadcast]
	public void TakeDamage( int amount )
	{{
		Health -= amount;
		if ( Health <= 0 )
			Log.Info( $""{{PlayerName}} died!"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateLobbyManagerHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "LobbyManager";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;
using System.Collections.Generic;

public sealed class {className} : Component, Component.INetworkListener
{{
	public static {className} Instance {{ get; private set; }}

	[Sync] public int PlayerCount {{ get; private set; }}

	[Property] public int     MaxPlayers  {{ get; set; }} = 16;
	[Property] public string  LobbyState  {{ get; set; }} = ""waiting"";

	private readonly List<Connection> _players = new();

	protected override void OnStart()
	{{
		Instance = this;
	}}

	protected override void OnDestroy()
	{{
		if ( Instance == this ) Instance = null;
	}}

	public void OnActive( Connection channel )
	{{
		_players.Add( channel );
		PlayerCount = _players.Count;
		Log.Info( $""[{className}] {{channel.DisplayName}} joined. Players: {{PlayerCount}}/{{MaxPlayers}}"" );

		if ( PlayerCount >= MaxPlayers )
			StartGame();
	}}

	public void OnDisconnected( Connection channel )
	{{
		_players.Remove( channel );
		PlayerCount = _players.Count;
	}}

	private void StartGame()
	{{
		LobbyState = ""playing"";
		Log.Info( $""[{className}] Game starting!"" );
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

public class CreateNetworkEventsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath  = Project.Current.GetRootPath();
		var name      = p.TryGetProperty( "name",      out var n ) ? n.GetString() : "NetworkEvents";
		var directory = p.TryGetProperty( "directory", out var d ) ? d.GetString() : "Code";

		var fileName = name.EndsWith( ".cs" ) ? name : $"{name}.cs";
		var fullPath = Path.Combine( rootPath, directory, fileName );

		if ( File.Exists( fullPath ) )
			return Task.FromResult<object>( new { error = $"File already exists: {directory}/{fileName}" } );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
		var className = Path.GetFileNameWithoutExtension( fileName );

		var code = $@"using Sandbox;

public sealed class {className} : Component
{{
	/// <summary>Broadcasts a named event to all connected clients.</summary>
	[Rpc.Broadcast]
	public void SendEvent( string eventName, string payload )
	{{
		Log.Info( $""[{className}] Event '{{eventName}}' received with payload: {{payload}}"" );
		OnNetworkEvent( eventName, payload );
	}}

	/// <summary>Sends an event only to the host.</summary>
	[Rpc.Host]
	public void SendEventToHost( string eventName, string payload )
	{{
		Log.Info( $""[{className}] Host received event '{{eventName}}'"" );
		OnNetworkEvent( eventName, payload );
	}}

	private void OnNetworkEvent( string eventName, string payload )
	{{
		// Dispatch locally — extend this switch to handle specific events
		switch ( eventName )
		{{
			case ""player_scored"":
				Log.Info( $""Player scored: {{payload}}"" );
				break;
			default:
				Log.Info( $""Unhandled event: {{eventName}}"" );
				break;
		}}
	}}
}}
";
		File.WriteAllText( fullPath, code );
		return Task.FromResult<object>( new { created = true, path = $"{directory}/{fileName}", className } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 13 — Publishing / config
// ═══════════════════════════════════════════════════════════════════

public class GetProjectConfigHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var sbproj   = Directory.GetFiles( rootPath, "*.sbproj", SearchOption.TopDirectoryOnly ).FirstOrDefault();

		if ( sbproj == null )
			return Task.FromResult<object>( new { error = ".sbproj file not found in project root" } );

		var content = File.ReadAllText( sbproj );
		return Task.FromResult<object>( new
		{
			path    = Path.GetRelativePath( rootPath, sbproj ).Replace( '\\', '/' ),
			content,
			project = new
			{
				title = Project.Current.Config.Title,
				org   = Project.Current.Config.Org,
				ident = Project.Current.Config.Ident,
				type  = Project.Current.Config.Type
			}
		} );
	}
}

public class SetProjectConfigHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var sbproj   = Directory.GetFiles( rootPath, "*.sbproj", SearchOption.TopDirectoryOnly ).FirstOrDefault();

		if ( sbproj == null )
			return Task.FromResult<object>( new { error = ".sbproj file not found in project root" } );

		var content = File.ReadAllText( sbproj );

		// Apply find/replace pairs from the "changes" object
		if ( p.TryGetProperty( "changes", out var changes ) && changes.ValueKind == JsonValueKind.Object )
		{
			foreach ( var change in changes.EnumerateObject() )
			{
				// Replace JSON string values by key name pattern
				var searchPattern = $"\"{change.Name}\":";
				var idx = content.IndexOf( searchPattern, StringComparison.OrdinalIgnoreCase );
				if ( idx >= 0 )
				{
					// find the value start
					var valueStart = content.IndexOf( '"', idx + searchPattern.Length );
					var valueEnd   = content.IndexOf( '"', valueStart + 1 );
					if ( valueStart >= 0 && valueEnd > valueStart )
					{
						content = content.Substring( 0, valueStart + 1 )
						        + change.Value.GetString()
						        + content.Substring( valueEnd );
					}
				}
			}
			File.WriteAllText( sbproj, content );
		}
		else if ( p.TryGetProperty( "content", out var newContent ) )
		{
			File.WriteAllText( sbproj, newContent.GetString() );
		}
		else
		{
			return Task.FromResult<object>( new { error = "Provide 'changes' object or 'content' string" } );
		}

		return Task.FromResult<object>( new { updated = true, path = Path.GetRelativePath( rootPath, sbproj ).Replace( '\\', '/' ) } );
	}
}

public class ValidateProjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath = Project.Current.GetRootPath();
		var issues   = new List<string>();
		var checks   = new List<object>();

		// Check for .sbproj
		var sbproj = Directory.GetFiles( rootPath, "*.sbproj", SearchOption.TopDirectoryOnly ).FirstOrDefault();
		var hasSbproj = sbproj != null;
		checks.Add( new { check = "sbproj_exists", pass = hasSbproj, detail = hasSbproj ? sbproj : "No .sbproj found" } );
		if ( !hasSbproj ) issues.Add( "Missing .sbproj file" );

		// Check for at least one scene
		var sceneCount = Directory.GetFiles( rootPath, "*.scene", SearchOption.AllDirectories ).Length;
		checks.Add( new { check = "has_scenes", pass = sceneCount > 0, detail = $"{sceneCount} scene(s) found" } );
		if ( sceneCount == 0 ) issues.Add( "No .scene files found" );

		// Check project ident
		var hasIdent = !string.IsNullOrEmpty( Project.Current.Config.Ident );
		checks.Add( new { check = "has_ident", pass = hasIdent, detail = hasIdent ? Project.Current.Config.Ident : "No ident set" } );
		if ( !hasIdent ) issues.Add( "Project Ident not set" );

		// Check project title
		var hasTitle = !string.IsNullOrEmpty( Project.Current.Config.Title );
		checks.Add( new { check = "has_title", pass = hasTitle, detail = hasTitle ? Project.Current.Config.Title : "No title set" } );
		if ( !hasTitle ) issues.Add( "Project Title not set" );

		var valid = issues.Count == 0;
		return Task.FromResult<object>( new { valid, issueCount = issues.Count, issues, checks } );
	}
}

public class SetProjectThumbnailHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var rootPath   = Project.Current.GetRootPath();
		var sourcePath = p.GetProperty( "sourcePath" ).GetString();
		var fullSource = Path.GetFullPath( Path.Combine( rootPath, sourcePath ) );

		if ( !File.Exists( fullSource ) )
			return Task.FromResult<object>( new { error = $"Source image not found: {sourcePath}" } );

		var ext  = Path.GetExtension( fullSource ).ToLower();
		if ( ext != ".png" && ext != ".jpg" && ext != ".jpeg" )
			return Task.FromResult<object>( new { error = "Thumbnail must be a .png or .jpg file" } );

		var thumbDest = Path.Combine( rootPath, "thumb.png" );
		File.Copy( fullSource, thumbDest, overwrite: true );

		return Task.FromResult<object>( new { set = true, thumbnail = "thumb.png" } );
	}
}

// ═══════════════════════════════════════════════════════════════════
// BATCH 15 — New handlers (joints, sound, UI panels, undo/redo,
//             networking helpers, packages, assets, screenshot, hotload)
// ═══════════════════════════════════════════════════════════════════

public class AddJointHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var jointType = p.TryGetProperty( "type", out var jt ) ? jt.GetString() : "fixed";

		// Resolve optional target body
		GameObject targetGo = null;
		if ( p.TryGetProperty( "targetId", out var tid ) && Guid.TryParse( tid.GetString(), out var targetGuid ) )
			targetGo = scene.Directory.FindByGuid( targetGuid );

		try
		{
			string addedType;
			switch ( jointType?.ToLower() )
			{
				case "spring":
				{
					var joint = go.AddComponent<SpringJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					if ( p.TryGetProperty( "frequency", out var freq ) ) joint.Frequency = freq.GetSingle();
					if ( p.TryGetProperty( "damping",   out var damp ) ) joint.Damping   = damp.GetSingle();
					addedType = "SpringJoint";
					break;
				}
				case "hinge":
				{
					var joint = go.AddComponent<HingeJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					addedType = "HingeJoint";
					break;
				}
				case "slider":
				{
					var joint = go.AddComponent<SliderJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					addedType = "SliderJoint";
					break;
				}
				default: // "fixed"
				{
					var joint = go.AddComponent<FixedJoint>();
					if ( targetGo != null ) joint.Body = targetGo;
					addedType = "FixedJoint";
					break;
				}
			}
			return Task.FromResult<object>( new { added = true, id, joint = addedType, targetId = targetGo?.Id.ToString() } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add joint: {ex.Message}" } );
		}
	}
}

public class AssignSoundHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var soundPath  = p.GetProperty( "sound" ).GetString();
		var playOnStart = p.TryGetProperty( "playOnStart", out var pos ) && pos.GetBoolean();

		try
		{
			var spc = go.GetOrAddComponent<SoundPointComponent>();

			// Load the SoundEvent from the path and assign it
			var soundEvent = ResourceLibrary.Get<SoundEvent>( soundPath );
			if ( soundEvent != null )
				spc.SoundEvent = soundEvent;

			if ( playOnStart )
				spc.StartSound();

			return Task.FromResult<object>( new
			{
				assigned    = true,
				id,
				sound       = soundPath,
				soundLoaded = soundEvent != null,
				playOnStart
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to assign sound: {ex.Message}" } );
		}
	}
}

public class PlaySoundPreviewHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var eventName = p.GetProperty( "sound" ).GetString();
		var volume    = p.TryGetProperty( "volume", out var v ) ? v.GetSingle() : 1.0f;

		try
		{
			var handle = Sound.Play( eventName );
			return Task.FromResult<object>( new { playing = true, sound = eventName, volume } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to play sound: {ex.Message}" } );
		}
	}
}

public class SetMaterialPropertyHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var renderer = go.GetComponent<ModelRenderer>();
		if ( renderer == null )
			return Task.FromResult<object>( new { error = "No ModelRenderer on GameObject" } );

		var propertyName = p.GetProperty( "property" ).GetString();
		var value        = p.GetProperty( "value" );

		try
		{
			// Ensure we have a mutable material override
			var mat = renderer.MaterialOverride;
			if ( mat == null )
				return Task.FromResult<object>( new { error = "No MaterialOverride set — assign a material first via assign_material" } );

			// Apply the property based on the JSON value kind
			switch ( value.ValueKind )
			{
				case JsonValueKind.Number:
					mat.Set( propertyName, value.GetSingle() );
					break;
				case JsonValueKind.True:
				case JsonValueKind.False:
					mat.Set( propertyName, value.GetBoolean() ? 1f : 0f );
					break;
				case JsonValueKind.Object:
					// Try to interpret as Color (r,g,b,a) or Vector3 (x,y,z)
					if ( value.TryGetProperty( "r", out var cr ) )
					{
						float r = cr.GetSingle();
						float g = value.TryGetProperty( "g", out var cg ) ? cg.GetSingle() : 0f;
						float b = value.TryGetProperty( "b", out var cb ) ? cb.GetSingle() : 0f;
						float a = value.TryGetProperty( "a", out var ca ) ? ca.GetSingle() : 1f;
						mat.Set( propertyName, new Color( r, g, b, a ) );
					}
					else
					{
						float x = value.TryGetProperty( "x", out var vx ) ? vx.GetSingle() : 0f;
						float y = value.TryGetProperty( "y", out var vy ) ? vy.GetSingle() : 0f;
						float z = value.TryGetProperty( "z", out var vz ) ? vz.GetSingle() : 0f;
						mat.Set( propertyName, new Vector3( x, y, z ) );
					}
					break;
				default:
					mat.Set( propertyName, value.GetString() );
					break;
			}

			return Task.FromResult<object>( new { set = true, id, property = propertyName } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set material property: {ex.Message}" } );
		}
	}
}

public class AddScreenPanelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var name   = p.TryGetProperty( "name",   out var n  ) ? n.GetString()  : "Screen Panel";
		var zIndex = p.TryGetProperty( "zIndex", out var zi ) ? zi.GetInt32()  : 0;

		// Resolve optional parent
		GameObject parentGo = null;
		if ( p.TryGetProperty( "parent", out var par ) && Guid.TryParse( par.GetString(), out var parGuid ) )
			parentGo = scene.Directory.FindByGuid( parGuid );

		try
		{
			var go = scene.CreateObject( true );
			go.Name = name;

			if ( parentGo != null )
				go.SetParent( parentGo, false );

			var panel = go.AddComponent<ScreenPanel>();
			panel.ZIndex = zIndex;

			// Optionally add a named panel component type
			if ( p.TryGetProperty( "panelComponent", out var pc ) )
			{
				var typeName = pc.GetString();
				if ( !string.IsNullOrEmpty( typeName ) )
				{
					var typeDesc = Game.TypeLibrary.GetType( typeName );
					if ( typeDesc != null )
						go.Components.Create( typeDesc );
				}
			}

			return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add ScreenPanel: {ex.Message}" } );
		}
	}
}

public class AddWorldPanelHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var name          = p.TryGetProperty( "name",          out var n   ) ? n.GetString()    : "World Panel";
		var lookAtCamera  = p.TryGetProperty( "lookAtCamera",  out var lac ) && lac.GetBoolean();

		// Resolve optional parent
		GameObject parentGo = null;
		if ( p.TryGetProperty( "parent", out var par ) && Guid.TryParse( par.GetString(), out var parGuid ) )
			parentGo = scene.Directory.FindByGuid( parGuid );

		try
		{
			var go = scene.CreateObject( true );
			go.Name = name;

			if ( parentGo != null )
				go.SetParent( parentGo, false );

			if ( p.TryGetProperty( "position", out var pos ) )
				go.WorldPosition = ClaudeBridge.ParseVector3( pos );

			if ( p.TryGetProperty( "rotation", out var rot ) )
				go.WorldRotation = ClaudeBridge.ParseRotation( rot );

			if ( p.TryGetProperty( "worldScale", out var ws ) )
				go.WorldScale = ClaudeBridge.ParseVector3( ws );

			var panel = go.AddComponent<WorldPanel>();
			panel.LookAtCamera = lookAtCamera;

			// Optionally add a named panel component type
			if ( p.TryGetProperty( "panelComponent", out var pc ) )
			{
				var typeName = pc.GetString();
				if ( !string.IsNullOrEmpty( typeName ) )
				{
					var typeDesc = Game.TypeLibrary.GetType( typeName );
					if ( typeDesc != null )
						go.Components.Create( typeDesc );
				}
			}

			return Task.FromResult<object>( new { created = true, gameObject = ClaudeBridge.SerializeGo( go ) } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add WorldPanel: {ex.Message}" } );
		}
	}
}

public class UndoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			SceneEditorSession.Active?.UndoSystem?.Undo();
			return Task.FromResult<object>( new { undone = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Undo failed: {ex.Message}" } );
		}
	}
}

public class RedoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			SceneEditorSession.Active?.UndoSystem?.Redo();
			return Task.FromResult<object>( new { redone = true } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Redo failed: {ex.Message}" } );
		}
	}
}

public class AddNetworkHelperHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var name = p.TryGetProperty( "name", out var n ) ? n.GetString() : null;
		if ( name != null ) go.Name = name;

		try
		{
			var helper = go.GetOrAddComponent<NetworkHelper>();
			helper.StartServer = true;

			return Task.FromResult<object>( new { added = true, id, component = "NetworkHelper" } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to add NetworkHelper: {ex.Message}" } );
		}
	}
}

public class ConfigureNetworkHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			// Networking.MaxPlayers is read-only — set via lobby config
			if ( p.TryGetProperty( "lobbyName",   out var ln ) ) Networking.ServerName  = ln.GetString();

			return Task.FromResult<object>( new
			{
				configured   = true,
				maxPlayers   = Networking.MaxPlayers,
				serverName   = Networking.ServerName
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to configure network: {ex.Message}" } );
		}
	}
}

public class GetNetworkStatusHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		try
		{
			return Task.FromResult<object>( new
			{
				isActive      = Networking.IsActive,
				isHost        = Networking.IsHost,
				isClient      = Networking.IsClient,
				isConnecting  = Networking.IsConnecting,
				maxPlayers    = Networking.MaxPlayers,
				serverName    = Networking.ServerName
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to get network status: {ex.Message}" } );
		}
	}
}

public class SetOwnershipHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var connectionId = p.TryGetProperty( "connectionId", out var cid ) ? cid.GetString() : null;

		try
		{
			if ( string.IsNullOrEmpty( connectionId ) )
			{
				go.Network.DropOwnership();
				return Task.FromResult<object>( new { ownershipDropped = true, id } );
			}
			else
			{
				// Find connection by steam ID or display name
				var conn = Connection.All.FirstOrDefault( c =>
					c.SteamId.ToString() == connectionId ||
					c.Id.ToString()      == connectionId );

				if ( conn == null )
					return Task.FromResult<object>( new { error = $"Connection not found: {connectionId}" } );

				go.Network.AssignOwnership( conn );
				return Task.FromResult<object>( new { ownershipAssigned = true, id, connectionId } );
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to set ownership: {ex.Message}" } );
		}
	}
}

public class GetPackageDetailsHandler : IBridgeHandler
{
	public async Task<object> Execute( JsonElement p )
	{
		var ident = p.GetProperty( "ident" ).GetString();

		try
		{
			var pkg = await Package.FetchAsync( ident, false );
			if ( pkg == null )
				return new { error = $"Package not found: {ident}" };

			return new
			{
				fullIdent   = pkg.FullIdent,
				title       = pkg.Title,
				summary     = pkg.Summary,
				description = pkg.Description,
				org         = pkg.Org
			};
		}
		catch ( Exception ex )
		{
			return new { error = $"Failed to fetch package: {ex.Message}" };
		}
	}
}

public class AssetInstallPinnedHandler : IBridgeHandler
{
	// Implements B.1.3 (pin to PackageReferences), B.1.9 (idempotency on both
	// AssetSystem and PackageReferences), B.1.10 (persist via direct .sbproj
	// JSON mutation — EditorUtility.SaveProjectSettings<T> requires T:ConfigData
	// which Sandbox.DataModel.ProjectConfig does NOT satisfy, so we follow Lou's
	// SetProjectConfigHandler pattern of direct file mutation), and B.1.14
	// (downloaded: bool | "unknown" honesty on InstallAsync exception paths).
	// See `.omc/specs/gate1-decisions.md` for the binding contract.
	public async Task<object> Execute( JsonElement p )
	{
		var ident = p.GetProperty( "ident" ).GetString();
		if ( string.IsNullOrWhiteSpace( ident ) )
			return new { error = "ident is required" };

		object downloaded;
		string assetName = null;
		string assetPath = null;
		string assetRelative = null;

		try
		{
			// B.1.9: skip InstallAsync if asset is already cloud-installed
			if ( AssetSystem.IsCloudInstalled( ident ) )
			{
				downloaded = false;
			}
			else
			{
				var asset = await AssetSystem.InstallAsync( ident, true );
				downloaded = true;
				if ( asset == null )
					return new { error = $"InstallAsync returned null for: {ident}", downloaded };
				assetName = asset.Name;
				assetPath = asset.Path;
				assetRelative = asset.RelativePath;
			}
		}
		catch ( Exception ex )
		{
			// B.1.14: honest "unknown" when InstallAsync throws
			return new
			{
				error = $"Failed to install asset: {ex.Message}",
				downloaded = "unknown"
			};
		}

		// B.1.3 + B.1.10: pin into ProjectConfig.PackageReferences per D4.
		// Persistence: direct .sbproj JSON mutation via System.Text.Json.Nodes
		// (mirrors SetProjectConfigHandler at L2504). The runtime mount already
		// happened via AssetSystem.InstallAsync; this step makes it survive reload.
		bool pinned = false;
		bool alreadyPinned = false;
		string pinError = null;
		try
		{
			var rootPath = Project.Current.GetRootPath();
			var sbproj = Directory.GetFiles( rootPath, "*.sbproj", SearchOption.TopDirectoryOnly ).FirstOrDefault();
			if ( sbproj == null )
			{
				pinError = ".sbproj file not found in project root";
			}
			else
			{
				var rootNode = JsonNode.Parse( File.ReadAllText( sbproj ) ) as JsonObject;
				if ( rootNode == null )
				{
					pinError = ".sbproj root is not a JSON object";
				}
				else
				{
					if ( !rootNode.ContainsKey( "Config" ) )
						rootNode["Config"] = new JsonObject();
					var configNode = rootNode["Config"] as JsonObject;
					if ( configNode == null )
					{
						pinError = ".sbproj 'Config' is not an object";
					}
					else
					{
						if ( !configNode.ContainsKey( "PackageReferences" ) )
							configNode["PackageReferences"] = new JsonArray();
						var refsArr = configNode["PackageReferences"] as JsonArray;
						if ( refsArr == null )
						{
							pinError = ".sbproj 'Config.PackageReferences' is not an array";
						}
						else
						{
							// B.1.9 idempotency: check if ident already present
							foreach ( var node in refsArr )
							{
								if ( node != null && node.GetValueKind() == JsonValueKind.String
									&& node.GetValue<string>() == ident )
								{
									alreadyPinned = true;
									break;
								}
							}
							if ( !alreadyPinned )
							{
								refsArr.Add( ident );
								File.WriteAllText( sbproj, rootNode.ToJsonString(
									new JsonSerializerOptions { WriteIndented = true } ) );
								pinned = true;
							}
						}
					}
				}
			}
		}
		catch ( Exception ex )
		{
			pinError = ex.Message;
		}

		return new
		{
			installed     = true,
			downloaded,
			pinned,
			alreadyPinned,
			pinError,
			ident,
			name          = assetName,
			path          = assetPath,
			relativePath  = assetRelative
		};
	}
}

public class ListAssetLibraryHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var query      = p.TryGetProperty( "query",      out var q  ) ? q.GetString()  : null;
		var typeFilter = p.TryGetProperty( "type",       out var tf ) ? tf.GetString() : null;
		var maxResults = p.TryGetProperty( "maxResults", out var mr ) ? mr.GetInt32()  : 200;

		try
		{
			var assets = AssetSystem.All
				.Where( a => query == null || a.Name.Contains( query, StringComparison.OrdinalIgnoreCase ) )
				.Where( a => typeFilter == null || a.AssetType?.ToString().Contains( typeFilter, StringComparison.OrdinalIgnoreCase ) == true )
				.Take( maxResults )
				.Select( a => new
				{
					name         = a.Name,
					path         = a.Path,
					relativePath = a.RelativePath,
					assetType    = a.AssetType?.ToString()
				} )
				.ToArray();

			return Task.FromResult<object>( new { count = assets.Length, assets } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to list asset library: {ex.Message}" } );
		}
	}
}

public class TakeScreenshotHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var path = p.TryGetProperty( "path", out var pt ) ? pt.GetString() : null;

		try
		{
			EditorScene.TakeHighResScreenshot( 1920, 1080 );
			return Task.FromResult<object>( new
			{
				taken = true,
				note  = "Screenshot taken via EditorScene.TakeHighResScreenshot(1920, 1080)",
				path  = path ?? "<default editor location>"
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Failed to take screenshot: {ex.Message}" } );
		}
	}
}

public class TriggerHotloadHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		return Task.FromResult<object>( new
		{
			message = "Hotload is automatic in s&box when files change. Save a .cs file to trigger recompilation.",
			note    = "No manual hotload API is available. Modify a script file to trigger a hotload."
		} );
	}
}

// ═══════════════════════════════════════════════════════════════════
// Main-thread poller — ensures scene APIs run on the editor thread
// ═══════════════════════════════════════════════════════════════════

[Dock( "Editor", "Claude Bridge", "smart_toy" )]
public class BridgePoller : Widget
{
	public BridgePoller( Widget parent ) : base( parent )
	{
		MinimumSize = new Vector2( 200, 80 );
		WindowTitle = "Claude Bridge";

		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 4;

		var title = Layout.Add( new Label( "Claude Bridge", this ) );
		title.SetStyles( "font-size: 14px; font-weight: bold; color: white;" );

		var status = Layout.Add( new Label( $"Handlers: {ClaudeBridge.HandlerCount} | IPC Active", this ) );
		status.SetStyles( "font-size: 11px; color: #aaa;" );

		Layout.AddSpacingCell( 8 );

		var credit = Layout.Add( new Label( "A project by sboxskins.gg", this ) );
		credit.SetStyles( "font-size: 11px; color: #4fc3f7;" );

		var url = Layout.Add( new Label( "https://sboxskins.gg", this ) );
		url.SetStyles( "font-size: 10px; color: #888;" );
	}

	[EditorEvent.Frame]
	public void OnFrame()
	{
		ClaudeBridge.ProcessPendingOnMainThread();
	}
}

/// <summary>
/// Generates a smooth heightmap terrain mesh via MCP.
/// Params: size (float), resolution (int), hills (array of {x,y,radius,height}),
///         clearings (array of {x,y,radius}), name (string)
/// </summary>
public class BuildTerrainMeshHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var size = p.TryGetProperty( "size", out var sz ) ? sz.GetSingle() : 9600f;
		var resolution = p.TryGetProperty( "resolution", out var res ) ? res.GetInt32() : 64;
		var name = p.TryGetProperty( "name", out var nm ) ? nm.GetString() : "Generated Terrain";

		// Parse hills: [{x, y, radius, height}]
		var hills = new System.Collections.Generic.List<(Vector2 pos, float radius, float height)>();
		if ( p.TryGetProperty( "hills", out var hillsArr ) && hillsArr.ValueKind == JsonValueKind.Array )
		{
			foreach ( var h in hillsArr.EnumerateArray() )
			{
				var hx = h.TryGetProperty( "x", out var hxp ) ? hxp.GetSingle() : 0;
				var hy = h.TryGetProperty( "y", out var hyp ) ? hyp.GetSingle() : 0;
				var hr = h.TryGetProperty( "radius", out var hrp ) ? hrp.GetSingle() : 500;
				var hh = h.TryGetProperty( "height", out var hhp ) ? hhp.GetSingle() : 100;
				hills.Add( (new Vector2( hx, hy ), hr, hh) );
			}
		}

		// Parse clearings: [{x, y, radius}]
		var clearings = new System.Collections.Generic.List<(Vector2 pos, float radius)>();
		if ( p.TryGetProperty( "clearings", out var clArr ) && clArr.ValueKind == JsonValueKind.Array )
		{
			foreach ( var c in clArr.EnumerateArray() )
			{
				var cx = c.TryGetProperty( "x", out var cxp ) ? cxp.GetSingle() : 0;
				var cy = c.TryGetProperty( "y", out var cyp ) ? cyp.GetSingle() : 0;
				var cr = c.TryGetProperty( "radius", out var crp ) ? crp.GetSingle() : 300;
				clearings.Add( (new Vector2( cx, cy ), cr) );
			}
		}

		var go = scene.CreateObject( true );
		go.Name = name;
		go.WorldPosition = Vector3.Zero;

		var mesh = go.AddComponent<MeshComponent>();
		// MeshComponent.Mesh is null on a freshly-added component — must assign a fresh PolygonMesh
		if ( mesh.Mesh == null ) mesh.Mesh = new PolygonMesh();
		var polyMesh = mesh.Mesh;

		var halfSize = size * 0.5f;
		var step = size / resolution;
		var stride = resolution + 1;

		// Generate heightmap vertices
		var handles = new HalfEdgeMesh.VertexHandle[stride * stride];
		for ( int z = 0; z <= resolution; z++ )
		{
			for ( int x = 0; x <= resolution; x++ )
			{
				var worldX = -halfSize + x * step;
				var worldY = -halfSize + z * step;
				var height = CalcHeight( worldX, worldY, hills, clearings );
				handles[z * stride + x] = polyMesh.AddVertex( new Vector3( worldX, worldY, height ) );
			}
		}

		// Generate quad faces
		int faceCount = 0;
		for ( int z = 0; z < resolution; z++ )
		{
			for ( int x = 0; x < resolution; x++ )
			{
				var tl = z * stride + x;
				var tr = tl + 1;
				var bl = (z + 1) * stride + x;
				var br = bl + 1;
				polyMesh.AddFace( new[] { handles[tl], handles[bl], handles[br], handles[tr] } );
				faceCount++;
			}
		}

		return Task.FromResult<object>( new
		{
			built = true,
			id = go.Id.ToString(),
			name = go.Name,
			vertices = handles.Length,
			faces = faceCount
		} );
	}

	private static float CalcHeight( float x, float y,
		System.Collections.Generic.List<(Vector2 pos, float radius, float height)> hills,
		System.Collections.Generic.List<(Vector2 pos, float radius)> clearings )
	{
		float height = 0f;
		var pos = new Vector2( x, y );

		// Hills with smooth cosine falloff
		foreach ( var (hillPos, radius, hillHeight) in hills )
		{
			var dist = Vector2.DistanceBetween( pos, hillPos );
			if ( dist < radius )
			{
				var t = dist / radius;
				var blend = (MathF.Cos( t * MathF.PI ) + 1f) * 0.5f;
				height += hillHeight * blend;
			}
		}

		// Flatten clearings
		foreach ( var (clearPos, clearRadius) in clearings )
		{
			var dist = Vector2.DistanceBetween( pos, clearPos );
			if ( dist < clearRadius )
			{
				var t = dist / clearRadius;
				var blend = (MathF.Cos( t * MathF.PI ) + 1f) * 0.5f;
				height = MathX.Lerp( height, 0f, blend );
			}
		}

		// Subtle noise
		var ix = (int)MathF.Floor( x * 0.001f );
		var iy = (int)MathF.Floor( y * 0.001f );
		var fx = x * 0.001f - ix;
		var fy = y * 0.001f - iy;
		fx = fx * fx * (3f - 2f * fx);
		fy = fy * fy * (3f - 2f * fy);
		float Hash( int px, int py ) { var n = px * 127 + py * 311; n = (n << 13) ^ n; return 1f - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824f; }
		var a = Hash( ix, iy ); var b = Hash( ix + 1, iy ); var c = Hash( ix, iy + 1 ); var d = Hash( ix + 1, iy + 1 );
		height += MathX.Lerp( MathX.Lerp( a, b, fx ), MathX.Lerp( c, d, fx ), fy ) * 25f;

		return height;
	}
}



// ════════════════════════════════════════════════════════════════════════
// New handlers — Map editing, sculpt, type discovery (Batch 15 + 16)
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared helpers for world-gen and reflection-driven handlers.
/// </summary>
internal static class WorldGenHelpers
{
	public static Component FindFirstComponent( string typeName, out string error )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) { error = "No active scene"; return null; }
		foreach ( var go in scene.GetAllObjects( false ) )
		{
			foreach ( var comp in go.Components.GetAll() )
			{
				if ( comp.GetType().Name == typeName ) { error = null; return comp; }
			}
		}
		error = $"No component of type '{typeName}' found in scene"; return null;
	}

	public static Component ResolveComponent( JsonElement p, string defaultType, out string error )
	{
		string typeName = p.TryGetProperty( "component", out var c ) ? c.GetString() : defaultType;
		if ( p.TryGetProperty( "id", out var idEl ) && idEl.ValueKind == JsonValueKind.String )
		{
			var idStr = idEl.GetString();
			if ( !Guid.TryParse( idStr, out var guid ) ) { error = "Invalid GameObject GUID"; return null; }
			var scene = SceneEditorSession.Active?.Scene;
			if ( scene == null ) { error = "No active scene"; return null; }
			var go = scene.Directory.FindByGuid( guid );
			if ( go == null ) { error = "GameObject not found"; return null; }
			foreach ( var comp in go.Components.GetAll() )
			{
				if ( comp.GetType().Name == typeName ) { error = null; return comp; }
			}
			error = $"No component '{typeName}' on the given GameObject"; return null;
		}
		return FindFirstComponent( typeName, out error );
	}

	public static System.Collections.IList GetListProperty( Component comp, string propertyName, out string error, out Type elementType )
	{
		elementType = null;
		var prop = comp.GetType().GetProperty( propertyName );
		if ( prop == null ) { error = $"Property '{propertyName}' not found on {comp.GetType().Name}"; return null; }
		var val = prop.GetValue( comp );
		if ( val is System.Collections.IList list )
		{
			if ( prop.PropertyType.IsGenericType )
				elementType = prop.PropertyType.GetGenericArguments()[0];
			error = null;
			return list;
		}
		error = $"Property '{propertyName}' is not a list"; return null;
	}

	public static bool InvokeButton( Component comp, string buttonLabel )
	{
		var type = comp.GetType();
		var methods = type.GetMethods( BindingFlags.Public | BindingFlags.Instance );

		// Strategy 1: ButtonAttribute label match
		foreach ( var method in methods )
		{
			if ( method.GetParameters().Length > 0 ) continue;
			foreach ( var attr in method.GetCustomAttributes( true ) )
			{
				if ( !attr.GetType().Name.Contains( "Button" ) ) continue;
				if ( AttributeStringMatches( attr, buttonLabel ) )
				{
					InvokeUnwrap( method, comp );
					return true;
				}
			}
		}

		// Strategy 2: exact method name
		foreach ( var method in methods )
		{
			if ( method.GetParameters().Length > 0 ) continue;
			if ( method.Name == buttonLabel )
			{
				InvokeUnwrap( method, comp );
				return true;
			}
		}

		// Strategy 3: case-insensitive, ignore spaces
		var normalized = buttonLabel.Replace( " ", "" );
		foreach ( var method in methods )
		{
			if ( method.GetParameters().Length > 0 ) continue;
			if ( string.Equals( method.Name, normalized, StringComparison.OrdinalIgnoreCase ) )
			{
				InvokeUnwrap( method, comp );
				return true;
			}
		}

		return false;
	}

	// Invoke a method and re-throw the inner exception (not the TargetInvocationException wrapper)
	// so callers see the real error message instead of "Exception has been thrown by the target of an invocation."
	private static void InvokeUnwrap( MethodInfo method, object target )
	{
		try { method.Invoke( target, null ); }
		catch ( TargetInvocationException tie )
		{
			var inner = tie.InnerException ?? tie;
			throw new Exception( $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}" );
		}
	}

	private static bool AttributeStringMatches( object attr, string target )
	{
		var t = attr.GetType();
		foreach ( var pi in t.GetProperties() )
		{
			if ( pi.PropertyType != typeof( string ) ) continue;
			try { if ( (pi.GetValue( attr ) as string) == target ) return true; } catch { }
		}
		foreach ( var fi in t.GetFields() )
		{
			if ( fi.FieldType != typeof( string ) ) continue;
			try { if ( (fi.GetValue( attr ) as string) == target ) return true; } catch { }
		}
		return false;
	}

	public static List<string> ListButtons( Component comp )
	{
		var labels = new List<string>();
		var type = comp.GetType();
		foreach ( var method in type.GetMethods( BindingFlags.Public | BindingFlags.Instance ) )
		{
			if ( method.GetParameters().Length > 0 ) continue;
			foreach ( var attr in method.GetCustomAttributes( true ) )
			{
				if ( !attr.GetType().Name.Contains( "Button" ) ) continue;
				var label = ExtractAttributeString( attr ) ?? method.Name;
				labels.Add( label );
			}
		}
		return labels;
	}

	private static string ExtractAttributeString( object attr )
	{
		var t = attr.GetType();
		foreach ( var pi in t.GetProperties() )
		{
			if ( pi.PropertyType != typeof( string ) ) continue;
			try { var v = pi.GetValue( attr ) as string; if ( !string.IsNullOrEmpty( v ) ) return v; } catch { }
		}
		foreach ( var fi in t.GetFields() )
		{
			if ( fi.FieldType != typeof( string ) ) continue;
			try { var v = fi.GetValue( attr ) as string; if ( !string.IsNullOrEmpty( v ) ) return v; } catch { }
		}
		return null;
	}

	public static void SetMember( object obj, string memberName, object value )
	{
		var t = obj.GetType();
		var prop = t.GetProperty( memberName );
		if ( prop != null && prop.CanWrite ) { prop.SetValue( obj, ConvertValue( value, prop.PropertyType ) ); return; }
		var field = t.GetField( memberName );
		if ( field != null ) { field.SetValue( obj, ConvertValue( value, field.FieldType ) ); }
	}

	private static object ConvertValue( object value, Type target )
	{
		if ( value == null ) return null;
		if ( target.IsAssignableFrom( value.GetType() ) ) return value;
		try { return Convert.ChangeType( value, target ); } catch { return value; }
	}
}

// ───────── invoke_button ─────────────────────────────────────────────────
public class InvokeButtonHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var typeName = p.TryGetProperty( "component", out var c ) ? c.GetString() : null;
		var label = p.TryGetProperty( "button", out var b ) ? b.GetString() : null;
		if ( string.IsNullOrEmpty( typeName ) || string.IsNullOrEmpty( label ) )
			return Task.FromResult<object>( new { error = "component and button are required" } );

		var comp = WorldGenHelpers.ResolveComponent( p, typeName, out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		try
		{
			var ok = WorldGenHelpers.InvokeButton( comp, label );
			if ( !ok ) return Task.FromResult<object>( new { error = $"Button '{label}' not found on {typeName}" } );
			return Task.FromResult<object>( new { invoked = true, component = typeName, button = label } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Invoke failed: {ex.Message}" } );
		}
	}
}

// ───────── list_component_buttons ────────────────────────────────────────
public class ListComponentButtonsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var typeName = p.TryGetProperty( "component", out var c ) ? c.GetString() : null;
		if ( string.IsNullOrEmpty( typeName ) )
			return Task.FromResult<object>( new { error = "component is required" } );

		var comp = WorldGenHelpers.ResolveComponent( p, typeName, out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var buttons = WorldGenHelpers.ListButtons( comp );
		return Task.FromResult<object>( new { component = typeName, buttons } );
	}
}

// ───────── raycast_terrain ───────────────────────────────────────────────
public class RaycastTerrainHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;

		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		try
		{
			var sample = comp.GetType().GetMethod( "SampleHeight" );
			if ( sample == null ) return Task.FromResult<object>( new { error = "SampleHeight not available on MapBuilder" } );
			var height = (float)sample.Invoke( comp, new object[] { x, y } );
			return Task.FromResult<object>( new { x, y, z = height } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Raycast failed: {ex.Message}" } );
		}
	}
}

// ───────── add_terrain_hill ──────────────────────────────────────────────
public class AddTerrainHillHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 500f;
		var height = p.TryGetProperty( "height", out var hp ) ? hp.GetSingle() : 100f;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Hills", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var hill = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( hill, "Position", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( hill, "Radius", radius );
		WorldGenHelpers.SetMember( hill, "Height", height );
		list.Add( hill );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );

		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── add_terrain_clearing ──────────────────────────────────────────
public class AddTerrainClearingHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 300f;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Clearings", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Position", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( item, "Radius", radius );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── add_terrain_trail ─────────────────────────────────────────────
public class AddTerrainTrailHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var fromEl = p.TryGetProperty( "from", out var fp ) ? fp : default;
		var toEl = p.TryGetProperty( "to", out var tp ) ? tp : default;
		if ( fromEl.ValueKind != JsonValueKind.Object || toEl.ValueKind != JsonValueKind.Object )
			return Task.FromResult<object>( new { error = "from and to are required objects with x/y" } );

		var from = new Vector2(
			fromEl.TryGetProperty( "x", out var fx ) ? fx.GetSingle() : 0f,
			fromEl.TryGetProperty( "y", out var fy ) ? fy.GetSingle() : 0f );
		var to = new Vector2(
			toEl.TryGetProperty( "x", out var tx ) ? tx.GetSingle() : 0f,
			toEl.TryGetProperty( "y", out var ty ) ? ty.GetSingle() : 0f );
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Trails", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "From", from );
		WorldGenHelpers.SetMember( item, "To", to );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── clear_terrain_features ────────────────────────────────────────
public class ClearTerrainFeaturesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var which = p.TryGetProperty( "what", out var w ) ? w.GetString() : "all";
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var report = new Dictionary<string, int>();
		string[] targets = which == "all"
			? new[] { "Hills", "Clearings", "Trails", "CavePath" }
			: new[] { which };

		foreach ( var prop in targets )
		{
			var list = WorldGenHelpers.GetListProperty( comp, prop, out var lerr, out _ );
			if ( list == null ) continue;
			report[prop] = list.Count;
			list.Clear();
		}

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Terrain" );
		return Task.FromResult<object>( new { cleared = report, rebuilt = rebuild } );
	}
}

// ───────── add_cave_waypoint ─────────────────────────────────────────────
public class AddCaveWaypointHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "CaveBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var z = p.TryGetProperty( "z", out var zp ) ? zp.GetSingle() : 0f;
		var index = p.TryGetProperty( "index", out var ip ) ? ip.GetInt32() : -1;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Path", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Position", new Vector3( x, y, z ) );
		if ( index >= 0 && index <= list.Count ) list.Insert( index, item );
		else list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Build Cave" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── clear_cave_path ───────────────────────────────────────────────
public class ClearCavePathHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "CaveBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var list = WorldGenHelpers.GetListProperty( comp, "Path", out var lerr, out _ );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );
		var was = list.Count;
		list.Clear();

		WorldGenHelpers.InvokeButton( comp, "Clear Cave" );
		return Task.FromResult<object>( new { cleared = was } );
	}
}

// ───────── add_forest_poi ────────────────────────────────────────────────
public class AddForestPOIHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var name = p.TryGetProperty( "name", out var nm ) ? nm.GetString() : "POI";
		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 300f;
		var density = p.TryGetProperty( "density_multiplier", out var dp ) ? dp.GetSingle() : 1f;
		var rebuild = p.TryGetProperty( "rebuild", out var rb ) && rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "POIs", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Name", name );
		WorldGenHelpers.SetMember( item, "Position", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( item, "Radius", radius );
		WorldGenHelpers.SetMember( item, "DensityMultiplier", density );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { added = true, index = list.Count - 1, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── add_forest_trail ──────────────────────────────────────────────
public class AddForestTrailHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var fromIdx = p.TryGetProperty( "from_index", out var f ) ? f.GetInt32() : 0;
		var toIdx = p.TryGetProperty( "to_index", out var t ) ? t.GetInt32() : 0;
		var rebuild = p.TryGetProperty( "rebuild", out var rb ) && rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "Trails", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "FromIndex", fromIdx );
		WorldGenHelpers.SetMember( item, "ToIndex", toIdx );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { added = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── set_forest_seed ───────────────────────────────────────────────
public class SetForestSeedHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var seed = p.TryGetProperty( "seed", out var sp ) ? sp.GetInt32() : 77;
		var rebuild = !p.TryGetProperty( "rebuild", out var rb ) || rb.GetBoolean();

		var prop = comp.GetType().GetProperty( "Seed" );
		if ( prop == null ) return Task.FromResult<object>( new { error = "Seed property missing" } );
		prop.SetValue( comp, seed );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { set = true, seed, rebuilt = rebuild } );
	}
}

// ───────── clear_forest_pois ─────────────────────────────────────────────
public class ClearForestPOIsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var list = WorldGenHelpers.GetListProperty( comp, "POIs", out var lerr, out _ );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );
		var was = list.Count;
		list.Clear();

		var trailList = WorldGenHelpers.GetListProperty( comp, "Trails", out _, out _ );
		trailList?.Clear();

		WorldGenHelpers.InvokeButton( comp, "Clear Forest" );
		return Task.FromResult<object>( new { cleared = was } );
	}
}

// ───────── sculpt_terrain ────────────────────────────────────────────────
public class SculptTerrainHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "MapBuilder", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 400f;
		var strength = p.TryGetProperty( "strength", out var sp ) ? sp.GetSingle() : 50f;
		var mode = p.TryGetProperty( "mode", out var mp ) ? mp.GetString() : "raise";

		var sculpt = comp.GetType().GetMethod( "Sculpt" );
		if ( sculpt == null ) return Task.FromResult<object>( new { error = "Sculpt method missing on MapBuilder" } );

		try
		{
			var affected = (int)sculpt.Invoke( comp, new object[] { x, y, radius, strength, mode } );
			return Task.FromResult<object>( new { sculpted = true, mode, affected_vertices = affected } );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Sculpt failed: {ex.Message}" } );
		}
	}
}

// ───────── paint_forest_density ──────────────────────────────────────────
public class PaintForestDensityHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var comp = WorldGenHelpers.ResolveComponent( p, "ForestGenerator", out var err );
		if ( comp == null ) return Task.FromResult<object>( new { error = err } );

		var x = p.TryGetProperty( "x", out var xp ) ? xp.GetSingle() : 0f;
		var y = p.TryGetProperty( "y", out var yp ) ? yp.GetSingle() : 0f;
		var radius = p.TryGetProperty( "radius", out var rp ) ? rp.GetSingle() : 800f;
		var density = p.TryGetProperty( "density", out var dp ) ? dp.GetSingle() : 1f;
		var rebuild = p.TryGetProperty( "rebuild", out var rb ) && rb.GetBoolean();

		var list = WorldGenHelpers.GetListProperty( comp, "DensityRegions", out var lerr, out var et );
		if ( list == null ) return Task.FromResult<object>( new { error = lerr } );

		var item = Activator.CreateInstance( et );
		WorldGenHelpers.SetMember( item, "Center", new Vector2( x, y ) );
		WorldGenHelpers.SetMember( item, "Radius", radius );
		WorldGenHelpers.SetMember( item, "Density", density );
		list.Add( item );

		if ( rebuild ) WorldGenHelpers.InvokeButton( comp, "Generate Forest" );
		return Task.FromResult<object>( new { painted = true, total = list.Count, rebuilt = rebuild } );
	}
}

// ───────── place_along_path ──────────────────────────────────────────────
public class PlaceAlongPathHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null ) return Task.FromResult<object>( new { error = "No active scene" } );

		var modelPath = p.TryGetProperty( "model", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrEmpty( modelPath ) )
			return Task.FromResult<object>( new { error = "model path is required (e.g. 'models/dev/box.vmdl')" } );

		var spacing = p.TryGetProperty( "spacing", out var sp ) ? sp.GetSingle() : 200f;
		var jitter = p.TryGetProperty( "jitter", out var jp ) ? jp.GetSingle() : 0f;
		var minScale = p.TryGetProperty( "min_scale", out var mnp ) ? mnp.GetSingle() : 1f;
		var maxScale = p.TryGetProperty( "max_scale", out var mxp ) ? mxp.GetSingle() : 1f;
		var seed = p.TryGetProperty( "seed", out var sdp ) ? sdp.GetInt32() : 42;
		var name = p.TryGetProperty( "name", out var np ) ? np.GetString() : "PathItem";

		if ( !p.TryGetProperty( "points", out var pointsEl ) || pointsEl.ValueKind != JsonValueKind.Array )
			return Task.FromResult<object>( new { error = "points must be an array of {x,y,z}" } );

		var points = new List<Vector3>();
		foreach ( var pt in pointsEl.EnumerateArray() )
		{
			points.Add( new Vector3(
				pt.TryGetProperty( "x", out var px ) ? px.GetSingle() : 0f,
				pt.TryGetProperty( "y", out var py ) ? py.GetSingle() : 0f,
				pt.TryGetProperty( "z", out var pz ) ? pz.GetSingle() : 0f ) );
		}
		if ( points.Count < 2 ) return Task.FromResult<object>( new { error = "need at least 2 points" } );

		Model model;
		try { model = Model.Load( modelPath ); }
		catch ( Exception ex ) { return Task.FromResult<object>( new { error = $"Could not load model '{modelPath}': {ex.Message}" } ); }

		var rng = new Random( seed );
		var folder = scene.CreateObject( true );
		folder.Name = $"== {name}s ==";

		int placed = 0;
		for ( int i = 0; i < points.Count - 1; i++ )
		{
			var from = points[i];
			var to = points[i + 1];
			var seg = to - from;
			var len = seg.Length;
			if ( len < 0.01f ) continue;
			var dir = seg / len;
			var steps = Math.Max( 1, (int)(len / spacing) );
			for ( int s = 0; s <= steps; s++ )
			{
				var t = (float)s / steps;
				var basePos = from + seg * t;
				var jx = (float)(rng.NextDouble() * 2 - 1) * jitter;
				var jy = (float)(rng.NextDouble() * 2 - 1) * jitter;
				var pos = basePos + new Vector3( jx, jy, 0 );

				var go = scene.CreateObject( true );
				go.Name = $"{name} {++placed}";
				go.SetParent( folder );
				go.WorldPosition = pos;
				go.WorldRotation = Rotation.FromYaw( (float)(rng.NextDouble() * 360.0) );
				var scale = MathX.Lerp( minScale, maxScale, (float)rng.NextDouble() );
				go.WorldScale = new Vector3( scale );

				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = model;
			}
		}

		return Task.FromResult<object>( new { placed, folder = folder.Id.ToString() } );
	}
}

// ════════════════════════════════════════════════════════════════════════
// Coding / type discovery handlers (Batch 16)
// ════════════════════════════════════════════════════════════════════════

// ───────── describe_type ─────────────────────────────────────────────────
public class DescribeTypeHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var name = p.TryGetProperty( "name", out var n ) ? n.GetString() : null;
		if ( string.IsNullOrEmpty( name ) ) return Task.FromResult<object>( new { error = "name is required" } );

		var typeDesc = Game.TypeLibrary.GetType( name );
		Type targetType = typeDesc?.TargetType;

		if ( targetType == null )
		{
			foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
			{
				try
				{
					foreach ( var t in asm.GetTypes() )
					{
						if ( t.Name == name || t.FullName == name ) { targetType = t; break; }
					}
				}
				catch { }
				if ( targetType != null ) break;
			}
		}

		if ( targetType == null ) return Task.FromResult<object>( new { error = $"Type '{name}' not found" } );

		var properties = new List<object>();
		foreach ( var pi in targetType.GetProperties( BindingFlags.Public | BindingFlags.Instance ) )
		{
			properties.Add( new
			{
				name = pi.Name,
				type = pi.PropertyType.Name,
				canRead = pi.CanRead,
				canWrite = pi.CanWrite
			} );
		}

		var methods = new List<object>();
		foreach ( var m in targetType.GetMethods( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static ).Take( 80 ) )
		{
			if ( m.IsSpecialName ) continue;
			var pars = string.Join( ", ", m.GetParameters().Select( pp => $"{pp.ParameterType.Name} {pp.Name}" ) );
			methods.Add( new
			{
				name = m.Name,
				returns = m.ReturnType.Name,
				signature = $"{m.ReturnType.Name} {m.Name}({pars})",
				isStatic = m.IsStatic
			} );
		}

		var events = targetType.GetEvents( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static )
			.Select( e => new { name = e.Name, type = e.EventHandlerType?.Name } ).ToList();

		var attrs = targetType.GetCustomAttributes( false ).Select( a => a.GetType().Name ).ToList();

		return Task.FromResult<object>( new
		{
			name = targetType.Name,
			fullName = targetType.FullName,
			baseType = targetType.BaseType?.Name,
			isAbstract = targetType.IsAbstract,
			isComponent = typeof( Component ).IsAssignableFrom( targetType ),
			properties,
			methods,
			events,
			attributes = attrs
		} );
	}
}

// ───────── search_types ──────────────────────────────────────────────────
public class SearchTypesHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var pattern = p.TryGetProperty( "pattern", out var pat ) ? pat.GetString() : "";
		var ns = p.TryGetProperty( "namespace", out var nsp ) ? nsp.GetString() : null;
		var componentsOnly = p.TryGetProperty( "components_only", out var co ) && co.GetBoolean();
		var limit = p.TryGetProperty( "limit", out var lp ) ? lp.GetInt32() : 50;

		var matches = new List<object>();
		var compType = typeof( Component );

		foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
		{
			try
			{
				foreach ( var t in asm.GetTypes() )
				{
					if ( !t.IsPublic ) continue;
					if ( componentsOnly && !compType.IsAssignableFrom( t ) ) continue;
					if ( !string.IsNullOrEmpty( ns ) && (t.Namespace == null || !t.Namespace.Contains( ns, StringComparison.OrdinalIgnoreCase )) ) continue;
					if ( !string.IsNullOrEmpty( pattern ) && !t.Name.Contains( pattern, StringComparison.OrdinalIgnoreCase ) ) continue;

					matches.Add( new
					{
						name = t.Name,
						fullName = t.FullName,
						isComponent = compType.IsAssignableFrom( t ),
						isAbstract = t.IsAbstract
					} );
					if ( matches.Count >= limit ) break;
				}
			}
			catch { }
			if ( matches.Count >= limit ) break;
		}

		return Task.FromResult<object>( new { count = matches.Count, matches } );
	}
}

// ───────── get_method_signature ──────────────────────────────────────────
public class GetMethodSignatureHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var typeName = p.TryGetProperty( "type", out var tp ) ? tp.GetString() : null;
		var methodName = p.TryGetProperty( "method", out var mp ) ? mp.GetString() : null;
		if ( string.IsNullOrEmpty( typeName ) || string.IsNullOrEmpty( methodName ) )
			return Task.FromResult<object>( new { error = "type and method are required" } );

		Type targetType = Game.TypeLibrary.GetType( typeName )?.TargetType;
		if ( targetType == null )
		{
			foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
			{
				try { foreach ( var t in asm.GetTypes() ) if ( t.Name == typeName ) { targetType = t; break; } } catch { }
				if ( targetType != null ) break;
			}
		}
		if ( targetType == null ) return Task.FromResult<object>( new { error = $"Type '{typeName}' not found" } );

		var overloads = new List<object>();
		foreach ( var m in targetType.GetMethods( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static ) )
		{
			if ( m.Name != methodName ) continue;
			var pars = m.GetParameters().Select( par => new { name = par.Name, type = par.ParameterType.Name, hasDefault = par.HasDefaultValue, defaultValue = par.HasDefaultValue ? par.DefaultValue?.ToString() : null } ).ToArray();
			overloads.Add( new
			{
				returns = m.ReturnType.Name,
				signature = $"{m.ReturnType.Name} {m.Name}({string.Join( ", ", pars.Select( x => $"{x.type} {x.name}" ) )})",
				parameters = pars,
				isStatic = m.IsStatic
			} );
		}

		if ( overloads.Count == 0 ) return Task.FromResult<object>( new { error = $"Method '{methodName}' not found on '{typeName}'" } );
		return Task.FromResult<object>( new { type = typeName, method = methodName, overloads } );
	}
}

// ───────── find_in_project ───────────────────────────────────────────────
public class FindInProjectHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var symbol = p.TryGetProperty( "symbol", out var sp ) ? sp.GetString() : null;
		if ( string.IsNullOrEmpty( symbol ) ) return Task.FromResult<object>( new { error = "symbol is required" } );

		var ext = p.TryGetProperty( "extension", out var ep ) ? ep.GetString() : ".cs";
		var maxResults = p.TryGetProperty( "max_results", out var mp ) ? mp.GetInt32() : 25;

		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( root ) || !Directory.Exists( root ) )
			return Task.FromResult<object>( new { error = "Project root not found" } );

		var hits = new List<object>();
		try
		{
			foreach ( var file in Directory.EnumerateFiles( root, "*" + ext, SearchOption.AllDirectories ) )
			{
				if ( hits.Count >= maxResults ) break;
				if ( file.Contains( $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}" ) ) continue;
				if ( file.Contains( $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}" ) ) continue;
				if ( file.Contains( $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" ) ) continue;

				try
				{
					var lines = File.ReadAllLines( file );
					for ( int i = 0; i < lines.Length; i++ )
					{
						if ( lines[i].Contains( symbol, StringComparison.Ordinal ) )
						{
							hits.Add( new
							{
								file = file.Substring( root.Length ).TrimStart( Path.DirectorySeparatorChar ),
								line = i + 1,
								text = lines[i].Trim()
							} );
							if ( hits.Count >= maxResults ) break;
						}
					}
				}
				catch { }
			}
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = $"Search failed: {ex.Message}" } );
		}

		return Task.FromResult<object>( new { symbol, count = hits.Count, results = hits } );
	}
}

// ──────────────────────────────────────────────────────────────────────────────
// Tag management handlers (S5 — 2026-05-12).
// Operate on GameObject.Tags (Sandbox.GameTags). Canonical names match JTC's
// tag_add / tag_list / tag_remove since no Lou tool by these names previously
// existed. Idempotency is enforced via Has() pre-check on add/remove.
// ──────────────────────────────────────────────────────────────────────────────

public class TagAddHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var tag = p.GetProperty( "tag" ).GetString();
		if ( string.IsNullOrWhiteSpace( tag ) )
			return Task.FromResult<object>( new { error = "tag is required" } );

		bool alreadyHad = go.Tags.Has( tag );
		if ( !alreadyHad )
			go.Tags.Add( tag );

		return Task.FromResult<object>( new { added = !alreadyHad, alreadyHad, id, tag } );
	}
}

public class TagListHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var tags = go.Tags.TryGetAll().ToArray();
		return Task.FromResult<object>( new { id, tags, count = tags.Length } );
	}
}

public class TagRemoveHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var tag = p.GetProperty( "tag" ).GetString();
		if ( string.IsNullOrWhiteSpace( tag ) )
			return Task.FromResult<object>( new { error = "tag is required" } );

		bool had = go.Tags.Has( tag );
		if ( had )
			go.Tags.Remove( tag );

		return Task.FromResult<object>( new { removed = had, id, tag } );
	}
}

// ──────────────────────────────────────────────────────────────────────────────
// Scene search handlers (S6 — 2026-05-12).
// Walk the active scene and return GameObjects matching a tag, component type,
// or name pattern. Canonical names match JTC's scene_find_* trio since no Lou
// tool by these names previously existed.
// ──────────────────────────────────────────────────────────────────────────────

public class SceneFindByTagHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var tag = p.GetProperty( "tag" ).GetString();
		if ( string.IsNullOrWhiteSpace( tag ) )
			return Task.FromResult<object>( new { error = "tag is required" } );

		var found = scene.FindAllWithTag( tag )
			.Select( go => new {
				id = go.Id.ToString(),
				name = go.Name,
				tags = go.Tags.TryGetAll().ToArray()
			})
			.ToArray();

		return Task.FromResult<object>( new { tag, count = found.Length, results = found } );
	}
}

public class SceneFindByComponentHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var typeName = p.GetProperty( "componentType" ).GetString();
		if ( string.IsNullOrWhiteSpace( typeName ) )
			return Task.FromResult<object>( new { error = "componentType is required" } );

		var typeDesc = Game.TypeLibrary.GetType( typeName );
		if ( typeDesc == null )
			return Task.FromResult<object>( new { error = $"Component type not found: {typeName}" } );

		var found = scene.GetAllComponents( typeDesc.TargetType )
			.Select( c => new {
				id = c.GameObject.Id.ToString(),
				name = c.GameObject.Name,
				componentType = c.GetType().Name
			})
			.ToArray();

		return Task.FromResult<object>( new { componentType = typeName, count = found.Length, results = found } );
	}
}

public class SceneFindObjectsHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var query = p.GetProperty( "query" ).GetString();
		if ( string.IsNullOrWhiteSpace( query ) )
			return Task.FromResult<object>( new { error = "query is required" } );

		// Glob → regex: escape regex specials, then * → .*
		var regexPattern = "^" + Regex.Escape( query ).Replace( "\\*", ".*" ) + "$";
		var regex = new Regex( regexPattern, RegexOptions.IgnoreCase );

		var found = scene.GetAllObjects( true )
			.Where( go => go.Name != null && regex.IsMatch( go.Name ) )
			.Select( go => new { id = go.Id.ToString(), name = go.Name } )
			.ToArray();

		return Task.FromResult<object>( new { query, count = found.Length, results = found } );
	}
}

// ───────── component lifecycle (S7, 2026-05-12) ─────────────────────────────
// component_list: enumerate go.Components.GetAll(); returns type + enabled state.
// component_remove: resolve type via Game.TypeLibrary, Destroy() matching
// components on the GO. Idempotent — reports removed=false if absent.
// ──────────────────────────────────────────────────────────────────────────────

public class ComponentListHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var components = go.Components.GetAll()
			.Select( c => new {
				type = c.GetType().Name,
				fullName = c.GetType().FullName,
				enabled = c.Enabled,
			} )
			.ToArray();

		return Task.FromResult<object>( new {
			id,
			name = go.Name,
			count = components.Length,
			components,
		} );
	}
}

public class ComponentRemoveHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "No active scene" } );

		var id = p.GetProperty( "id" ).GetString();
		if ( !Guid.TryParse( id, out var guid ) )
			return Task.FromResult<object>( new { error = "Invalid GUID" } );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			return Task.FromResult<object>( new { error = $"GameObject not found: {id}" } );

		var typeName = p.GetProperty( "component" ).GetString();
		if ( string.IsNullOrWhiteSpace( typeName ) )
			return Task.FromResult<object>( new { error = "component is required" } );

		var typeDesc = Game.TypeLibrary.GetType( typeName );
		if ( typeDesc == null )
			return Task.FromResult<object>( new { error = $"Component type not found: {typeName}" } );

		var matching = go.Components.GetAll()
			.Where( c => c.GetType() == typeDesc.TargetType )
			.ToArray();

		if ( matching.Length == 0 )
			return Task.FromResult<object>( new {
				removed = false,
				reason = $"No '{typeName}' component on GameObject",
				id,
				component = typeName,
			} );

		foreach ( var c in matching )
			c.Destroy();

		return Task.FromResult<object>( new {
			removed = true,
			count = matching.Length,
			id,
			component = typeName,
		} );
	}
}

// ───────── scene info (S4 closing, 2026-05-12) ─────────────────────────────────────────────────
// get_scene_info: returns metadata about the currently-active editor scene
// session — name, source resource path, dirty flag. Used by the JTC-compat
// alias `editor_scene_info` (passthrough). Path is null for unsaved scenes;
// dirty=true when the session has unsaved changes.
// ──────────────────────────────────────────────────────────────────────────────

public class GetSceneInfoHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var session = SceneEditorSession.Active;
		if ( session == null )
			return Task.FromResult<object>( new { error = "No active scene editor session" } );

		var scene = session.Scene;
		if ( scene == null )
			return Task.FromResult<object>( new { error = "Session has no active scene" } );

		var source = scene.Source;
		return Task.FromResult<object>( new {
			name = scene.Name,
			title = scene.Title,
			path = source?.ResourcePath,
			resourceName = source?.ResourceName,
			dirty = scene.HasUnsavedChanges,
			isPrefabSession = session.IsPrefabSession,
			isPlaying = session.IsPlaying,
		} );
	}
}

// ──────────────────────────────────────────────────────────────────────────────
// S8 (2026-05-13) — Execution tools
//
// console_run:    Sandbox.ConsoleSystem.Run() wrapper. Fire-and-forget on the
//                 editor side; returns success/error synchronously.
// execute_csharp: Roslyn-via-reflection scripting. Microsoft.CodeAnalysis.
//                 CSharp.Scripting isn't a hard reference (s&box doesn't expose
//                 it as a project dep), but the assembly is loaded into the
//                 editor's AppDomain. ScriptRunner.TryCreate returns null when
//                 that assembly isn't found, and ExecuteCSharpHandler surfaces
//                 a clear structured error instead of crashing.
//
// Both handlers complete inside the bridge's 30 s timeout. Roslyn first-compile
// takes 2–5 s; subsequent calls are sub-second.
// ──────────────────────────────────────────────────────────────────────────────

public class ConsoleRunHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var command = p.TryGetProperty( "command", out var c ) ? c.GetString() : null;
		if ( string.IsNullOrWhiteSpace( command ) )
			return Task.FromResult<object>( new { error = "command is required" } );

		try
		{
			Sandbox.ConsoleSystem.Run( command );
			return Task.FromResult<object>( new {
				executed = true,
				command,
				note = "ConsoleSystem.Run dispatched; output (if any) goes to the editor console, not this response.",
			} );
		}
		catch ( Exception ex )
		{
			return Task.FromResult<object>( new { error = ex.Message } );
		}
	}
}

public class ExecuteCSharpHandler : IBridgeHandler
{
	static readonly Lazy<ScriptRunner> _runner = new( ScriptRunner.TryCreate );

	public async Task<object> Execute( JsonElement p )
	{
		var code = p.TryGetProperty( "code", out var c ) ? c.GetString() : null;
		if ( string.IsNullOrWhiteSpace( code ) )
			return new { error = "code is required" };

		var imports = p.TryGetProperty( "imports", out var im ) ? im.GetString() : null;

		var runner = _runner.Value;
		if ( runner == null )
		{
			return new {
				executed = false,
				result = "",
				error = "Roslyn scripting (Microsoft.CodeAnalysis.CSharp.Scripting) is not loaded in this s&box build.",
				note = "Use console_run or write_file + trigger_hotload to apply code permanently.",
			};
		}

		try
		{
			var result = await runner.EvaluateAsync( code, imports );
			return new {
				executed = true,
				result = result?.ToString() ?? "(null)",
				type = result?.GetType().FullName ?? "void",
			};
		}
		catch ( Exception ex )
		{
			// Roslyn wraps compile errors in CompilationErrorException; its Message is the diagnostic.
			return new {
				executed = false,
				result = "",
				error = ex.Message,
				stack = ex.StackTrace ?? "",
			};
		}
	}

	// ── Reflective Roslyn adapter ────────────────────────────────────────────
	sealed class ScriptRunner
	{
		readonly MethodInfo _evaluateAsync;
		readonly Type _scriptOptionsType;
		readonly object _scriptOptions;

		ScriptRunner( MethodInfo evaluateAsync, Type scriptOptionsType, object scriptOptions )
		{
			_evaluateAsync = evaluateAsync;
			_scriptOptionsType = scriptOptionsType;
			_scriptOptions = scriptOptions;
		}

		public static ScriptRunner TryCreate()
		{
			try
			{
				var asm = AppDomain.CurrentDomain.GetAssemblies()
					.FirstOrDefault( a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp.Scripting" );
				if ( asm == null )
				{
					try { asm = Assembly.Load( "Microsoft.CodeAnalysis.CSharp.Scripting" ); }
					catch { return null; }
				}

				var scriptType = asm?.GetType( "Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript" );
				if ( scriptType == null ) return null;

				var eval = scriptType.GetMethods( BindingFlags.Public | BindingFlags.Static )
					.Where( m => m.Name == "EvaluateAsync" && !m.IsGenericMethodDefinition )
					.FirstOrDefault( m =>
					{
						var pp = m.GetParameters();
						return pp.Length >= 1 && pp[0].ParameterType == typeof( string );
					} );
				if ( eval == null ) return null;

				var optionsType = eval.GetParameters().Length > 1 ? eval.GetParameters()[1].ParameterType : null;
				if ( optionsType == null ) return null;

				var defaultProp = optionsType.GetProperty( "Default", BindingFlags.Public | BindingFlags.Static );
				var defaultOpts = defaultProp?.GetValue( null );
				if ( defaultOpts == null ) return null;

				// Inject all loaded assemblies as script references — lets the snippet
				// reach Sandbox / Editor / project types without manual `using` work.
				var refs = AppDomain.CurrentDomain.GetAssemblies()
					.Where( a => !a.IsDynamic && !string.IsNullOrEmpty( a.Location ) )
					.ToArray();
				var withRefs = optionsType.GetMethod( "WithReferences", new[] { typeof( IEnumerable<Assembly> ) } );
				if ( withRefs != null )
				{
					var maybe = withRefs.Invoke( defaultOpts, new object[] { refs } );
					if ( maybe != null ) defaultOpts = maybe;
				}

				var withImports = optionsType.GetMethod( "WithImports", new[] { typeof( IEnumerable<string> ) } );
				if ( withImports != null )
				{
					var defaultImports = new[] {
						"System", "System.Linq", "System.Collections.Generic",
						"Sandbox", "Editor",
					};
					var maybe = withImports.Invoke( defaultOpts, new object[] { defaultImports } );
					if ( maybe != null ) defaultOpts = maybe;
				}

				return new ScriptRunner( eval, optionsType, defaultOpts );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[SboxBridge] Could not initialise Roslyn scripting: {ex.Message}" );
				return null;
			}
		}

		public async Task<object> EvaluateAsync( string code, string extraImports )
		{
			var opts = _scriptOptions;
			if ( !string.IsNullOrWhiteSpace( extraImports ) )
			{
				var addImports = _scriptOptionsType.GetMethod( "AddImports", new[] { typeof( IEnumerable<string> ) } );
				if ( addImports != null )
				{
					var ns = extraImports.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
					var maybe = addImports.Invoke( opts, new object[] { ns } );
					if ( maybe != null ) opts = maybe;
				}
			}

			// Signature: EvaluateAsync(string code, ScriptOptions options, object globals, Type globalsType, CancellationToken token)
			var paramInfo = _evaluateAsync.GetParameters();
			var args = new object[paramInfo.Length];
			args[0] = code;
			if ( paramInfo.Length > 1 ) args[1] = opts;
			for ( var i = 2; i < paramInfo.Length; i++ )
				args[i] = paramInfo[i].HasDefaultValue ? paramInfo[i].DefaultValue : null;

			var task = (Task)_evaluateAsync.Invoke( null, args );
			await task.ConfigureAwait( false );
			var resultProp = task.GetType().GetProperty( "Result" );
			return resultProp?.GetValue( task );
		}
	}
}

// ──────────────────────────────────────────────────────────────────────────────
// S10 (2026-05-13) — Console capture via reflection on NLog.Targets.MemoryTarget
//
// s&box uses NLog but does NOT expose the NLog namespace at compile time —
// only at runtime (the assembly is loaded into the editor's AppDomain). Same
// problem JTC hit, prompting their fallback to manual AddEntry() in their
// `ConsoleCapture.cs`. We take the harder path: reflection-only access to
// NLog's built-in `MemoryTarget`, which captures every Log.Info/Warning/Error
// from any code in the editor (game, addons, Facepunch internals).
//
// MemoryTarget formats each entry using its inherited `Layout` (default:
// `${longdate}|${level:uppercase=true}|${logger}|${message}`) and stores them
// in a public IList<string> property called `Logs`. We read that list at
// query time, parse the `|`-separated fields back into a structured shape, and
// surface them via get_console_output.
// ──────────────────────────────────────────────────────────────────────────────

public static class BridgeLogTarget
{
	static object _target;        // NLog.Targets.MemoryTarget instance
	static object _logsList;       // IList<string> from MemoryTarget.Logs
	static bool _attached;
	static string _attachError;

	public static bool Attached => _attached;
	public static string AttachError => _attachError;

	public static void EnsureAttached()
	{
		if ( _attached ) return;
		try
		{
			var nlogAsm = AppDomain.CurrentDomain.GetAssemblies()
				.FirstOrDefault( a => a.GetName().Name == "NLog" );
			if ( nlogAsm == null )
			{
				_attachError = "NLog assembly not loaded in AppDomain";
				Log.Warning( $"[SboxBridge] {_attachError}" );
				return;
			}

			var memoryTargetType = nlogAsm.GetType( "NLog.Targets.MemoryTarget" );
			var logManagerType = nlogAsm.GetType( "NLog.LogManager" );
			var logLevelType = nlogAsm.GetType( "NLog.LogLevel" );
			var logFactoryType = nlogAsm.GetType( "NLog.LogFactory" );
			var loggingConfigType = nlogAsm.GetType( "NLog.Config.LoggingConfiguration" );
			var targetBaseType = nlogAsm.GetType( "NLog.Targets.Target" );

			if ( memoryTargetType == null || logManagerType == null || logLevelType == null
				|| logFactoryType == null || loggingConfigType == null || targetBaseType == null )
			{
				_attachError = "required NLog types not found in assembly";
				Log.Warning( $"[SboxBridge] {_attachError}" );
				return;
			}

			// Create MemoryTarget + set Name
			var target = Activator.CreateInstance( memoryTargetType );
			memoryTargetType.GetProperty( "Name" )?.SetValue( target, "sbox-bridge-capture" );

			// Locate LogFactory via LogManager.Setup().LogFactory
			var setupMi = logManagerType.GetMethod( "Setup", Type.EmptyTypes );
			var builder = setupMi?.Invoke( null, null );
			var factoryProp = builder?.GetType().GetProperty( "LogFactory" );
			var factory = factoryProp?.GetValue( builder );
			if ( factory == null )
			{
				_attachError = "could not reach LogFactory via reflection";
				Log.Warning( $"[SboxBridge] {_attachError}" );
				return;
			}

			var configProp = logFactoryType.GetProperty( "Configuration" );
			var config = configProp?.GetValue( factory );
			if ( config == null )
			{
				_attachError = "LogFactory.Configuration is null";
				Log.Warning( $"[SboxBridge] {_attachError}" );
				return;
			}

			// AddTarget(name, target)
			var addTarget = loggingConfigType.GetMethod( "AddTarget", new[] { typeof( string ), targetBaseType } );
			addTarget?.Invoke( config, new object[] { "sbox-bridge-capture", target } );

			// AddRule(LogLevel.Trace, LogLevel.Fatal, target, "*")
			var traceLevel = logLevelType.GetField( "Trace", BindingFlags.Public | BindingFlags.Static )?.GetValue( null );
			var fatalLevel = logLevelType.GetField( "Fatal", BindingFlags.Public | BindingFlags.Static )?.GetValue( null );
			var addRule = loggingConfigType.GetMethod( "AddRule", new[] { logLevelType, logLevelType, targetBaseType, typeof( string ) } );
			addRule?.Invoke( config, new object[] { traceLevel, fatalLevel, target, "*" } );

			// Re-assign config + reconfig
			configProp.SetValue( factory, config );
			logFactoryType.GetMethod( "ReconfigExistingLoggers", Type.EmptyTypes )?.Invoke( factory, null );

			_target = target;
			_logsList = memoryTargetType.GetProperty( "Logs" )?.GetValue( target );
			_attached = _logsList != null;

			if ( _attached )
				Log.Info( "[SboxBridge] BridgeLogTarget attached via reflection — NLog log capture is live" );
			else
			{
				_attachError = "MemoryTarget.Logs property missing";
				Log.Warning( $"[SboxBridge] {_attachError}" );
			}
		}
		catch ( Exception ex )
		{
			_attachError = ex.Message;
			Log.Warning( $"[SboxBridge] Reflection-attach failed: {ex.Message}" );
		}
	}

	/// <summary>
	/// Snapshot of the MemoryTarget Logs list (oldest-first per NLog convention).
	/// Caller is responsible for trimming + reversing for newest-first display.
	/// </summary>
	public static List<string> Snapshot()
	{
		if ( _logsList == null ) return new List<string>();
		try
		{
			if ( _logsList is System.Collections.IEnumerable enumerable )
			{
				var list = new List<string>();
				foreach ( var item in enumerable )
					list.Add( item?.ToString() ?? "" );
				return list;
			}
		}
		catch { }
		return new List<string>();
	}

	public static int Count
	{
		get
		{
			if ( _logsList is System.Collections.ICollection c ) return c.Count;
			return 0;
		}
	}
}

public class GetConsoleOutputHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement p )
	{
		var count = p.TryGetProperty( "count", out var cn ) ? cn.GetInt32() : 50;
		if ( count < 1 ) count = 1;
		if ( count > 500 ) count = 500;

		var severity = p.TryGetProperty( "severity", out var sv ) ? sv.GetString() : "all";
		severity = (severity ?? "all").ToLowerInvariant();

		if ( !BridgeLogTarget.Attached )
			return Task.FromResult<object>( new {
				error = $"BridgeLogTarget not attached: {BridgeLogTarget.AttachError ?? "unknown reason"}",
				total = 0,
				returned = 0,
				severity,
				entries = Array.Empty<object>(),
			} );

		var snapshot = BridgeLogTarget.Snapshot();
		var total = snapshot.Count;

		// Newest first
		snapshot.Reverse();

		var parsed = snapshot.Select( ParseEntry ).ToList();
		var filtered = severity == "all"
			? parsed
			: parsed.Where( e => MatchesSeverity( e.level, severity ) ).ToList();
		var trimmed = filtered.Take( count ).ToList();

		return Task.FromResult<object>( new {
			total,
			returned = trimmed.Count,
			severity,
			entries = trimmed.Select( e => new {
				timestamp = e.ts,
				level = e.level,
				loggerName = e.logger,
				message = e.message,
			} ).ToList(),
		} );
	}

	// Default NLog Layout: ${longdate}|${level:uppercase=true}|${logger}|${message}
	// Parse defensively — if the layout has been overridden, return the raw line as message.
	static (string ts, string level, string logger, string message) ParseEntry( string raw )
	{
		if ( string.IsNullOrEmpty( raw ) )
			return ("", "", "", "");
		var parts = raw.Split( new[] { '|' }, 4 );
		if ( parts.Length == 4 )
			return (parts[0], parts[1], parts[2], parts[3]);
		return ("", "", "", raw);
	}

	static bool MatchesSeverity( string entryLevel, string filter )
	{
		var l = (entryLevel ?? "").ToLowerInvariant();
		return filter switch
		{
			"info" => l == "info" || l == "debug" || l == "trace",
			"warning" or "warn" => l == "warn" || l == "warning",
			"error" => l == "error" || l == "fatal",
			_ => true,
		};
	}

/// <summary>
/// Phase 0 — Real IPC round-trip latency. Called by bridge.measureLatency().
/// </summary>
public class PingHandler : IBridgeHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		return Task.FromResult<object>( new {
			success = true,
			data = new { pong = true, timestamp = DateTime.UtcNow.ToString( "o" ) }
		} );
	}
}

}
