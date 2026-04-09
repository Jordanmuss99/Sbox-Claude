using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Instantiates a prefab into the active scene at an optional position/rotation.
/// Uses SceneUtility.Instantiate with ResourceLibrary to load the PrefabFile.
/// </summary>
public class InstantiatePrefabHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var path = parameters.GetProperty( "path" ).GetString()
			?? throw new Exception( "Missing required parameter: path" );

		// Load the prefab resource
		var prefab = ResourceLibrary.Get<PrefabFile>( path );
		if ( prefab == null )
			throw new Exception( $"Prefab not found: {path}" );

		// Parse optional position/rotation
		var position = parameters.TryGetProperty( "position", out var posProp )
			? CreateGameObjectHandler.ParseVector3( posProp )
			: Vector3.Zero;

		var rotation = parameters.TryGetProperty( "rotation", out var rotProp )
			? CreateGameObjectHandler.ParseRotation( rotProp )
			: Rotation.Identity;

		var scale = 1f;
		if ( parameters.TryGetProperty( "scale", out var scaleProp ) && scaleProp.ValueKind == JsonValueKind.Number )
		{
			scale = scaleProp.GetSingle();
		}

		// Instantiate the prefab
		// API-NOTE: SceneUtility.Instantiate may need adjustment per SDK version
		var go = SceneUtility.Instantiate( prefab, position, rotation );
		if ( go == null )
			throw new Exception( $"Failed to instantiate prefab: {path}" );

		if ( scale != 1f )
			go.WorldScale = new Vector3( scale, scale, scale );

		// Optional parent
		if ( parameters.TryGetProperty( "parent", out var parentProp ) )
		{
			var parentGuid = parentProp.GetString();
			if ( !string.IsNullOrEmpty( parentGuid ) && Guid.TryParse( parentGuid, out var parentId ) )
			{
				var parent = scene.Directory.FindByGuid( parentId );
				if ( parent != null )
					go.SetParent( parent );
			}
		}

		return Task.FromResult<object>( new
		{
			id = go.Id.ToString(),
			name = go.Name,
			prefab = path,
			position = CreateGameObjectHandler.FormatVector3( go.WorldPosition ),
			rotation = CreateGameObjectHandler.FormatRotation( go.WorldRotation ),
			instantiated = true,
		} );
	}
}
