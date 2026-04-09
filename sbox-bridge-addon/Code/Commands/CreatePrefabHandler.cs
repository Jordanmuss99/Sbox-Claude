using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Saves an existing GameObject as a .prefab file.
/// The prefab file is written to the project's assets directory.
/// </summary>
public class CreatePrefabHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var path = parameters.GetProperty( "path" ).GetString()
			?? throw new Exception( "Missing required parameter: path" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		// Ensure .prefab extension
		if ( !path.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) )
			path += ".prefab";

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, path ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		var dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		// Serialize the GameObject to prefab JSON
		// API-NOTE: PrefabFile.Save / go.Serialize may need adjustment for actual SDK
		var prefabJson = go.Serialize().ToString();
		File.WriteAllText( fullPath, prefabJson );

		return Task.FromResult<object>( new
		{
			path,
			sourceObject = go.Name,
			sourceId = id,
			created = true,
		} );
	}
}
