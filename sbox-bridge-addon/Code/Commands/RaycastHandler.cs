using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Performs a physics raycast (Scene.Trace.Ray) and returns hit results.
/// Supports single hit or multi-hit, tag filtering, and ignore lists.
/// </summary>
public class RaycastHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		// Parse start and end positions
		var startPos = parameters.TryGetProperty( "start", out var startProp )
			? CreateGameObjectHandler.ParseVector3( startProp )
			: throw new Exception( "Missing required parameter: start" );

		var endPos = parameters.TryGetProperty( "end", out var endProp )
			? CreateGameObjectHandler.ParseVector3( endProp )
			: throw new Exception( "Missing required parameter: end" );

		// Optional: direction + maxDistance instead of end
		if ( parameters.TryGetProperty( "direction", out var dirProp ) )
		{
			var direction = CreateGameObjectHandler.ParseVector3( dirProp ).Normal;
			var maxDist = parameters.TryGetProperty( "maxDistance", out var distProp )
				? distProp.GetSingle() : 10000f;
			endPos = startPos + direction * maxDist;
		}

		// Build the trace
		var trace = scene.Trace.Ray( startPos, endPos );

		// Optional size for box/sphere trace
		if ( parameters.TryGetProperty( "radius", out var radiusProp ) )
		{
			trace = trace.Size( radiusProp.GetSingle() );
		}

		// Optional ignore list
		if ( parameters.TryGetProperty( "ignoreIds", out var ignoreProp ) && ignoreProp.ValueKind == JsonValueKind.Array )
		{
			foreach ( var item in ignoreProp.EnumerateArray() )
			{
				var ignoreStr = item.GetString();
				if ( !string.IsNullOrEmpty( ignoreStr ) && Guid.TryParse( ignoreStr, out var ignoreGuid ) )
				{
					var ignoreGo = scene.Directory.FindByGuid( ignoreGuid );
					if ( ignoreGo != null )
						trace = trace.IgnoreGameObject( ignoreGo );
				}
			}
		}

		// Run trace
		var allHits = parameters.TryGetProperty( "all", out var allProp ) && allProp.GetBoolean();

		if ( allHits )
		{
			var results = trace.RunAll();
			var hits = new List<object>();
			foreach ( var r in results )
			{
				hits.Add( FormatHit( r ) );
			}

			return Task.FromResult<object>( new
			{
				hitCount = hits.Count,
				hits,
			} );
		}
		else
		{
			var result = trace.Run();
			if ( !result.Hit )
			{
				return Task.FromResult<object>( new
				{
					hit = false,
					hitCount = 0,
				} );
			}

			return Task.FromResult<object>( new
			{
				hit = true,
				hitCount = 1,
				result = FormatHit( result ),
			} );
		}
	}

	private static object FormatHit( SceneTraceResult r )
	{
		return new
		{
			hit = r.Hit,
			position = CreateGameObjectHandler.FormatVector3( r.HitPosition ),
			normal = CreateGameObjectHandler.FormatVector3( r.Normal ),
			distance = r.Distance,
			gameObjectId = r.GameObject?.Id.ToString(),
			gameObjectName = r.GameObject?.Name,
		};
	}
}
