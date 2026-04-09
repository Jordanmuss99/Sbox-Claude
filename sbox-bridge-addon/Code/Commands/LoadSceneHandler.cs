using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Opens a scene in the s&box editor.
/// </summary>
public class LoadSceneHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new System.Exception( "No project is currently open" );

		var relativePath = parameters.GetProperty( "path" ).GetString()
			?? throw new System.Exception( "Missing required parameter: path" );

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );

		if ( !fullPath.StartsWith( projectRoot ) )
			throw new System.Exception( "Path must be within the project directory" );

		if ( !File.Exists( fullPath ) )
			throw new System.Exception( $"Scene file not found: {relativePath}" );

		// Use s&box's scene loading API
		var sceneAsset = AssetSystem.FindByPath( relativePath );
		if ( sceneAsset == null )
			throw new System.Exception( $"Could not find scene asset: {relativePath}" );

		EditorScene.Load( sceneAsset );

		return Task.FromResult<object>( new
		{
			path = relativePath,
			loaded = true,
		} );
	}
}
