using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Returns detailed information about a prefab file, including its JSON contents.
/// </summary>
public class GetPrefabInfoHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var path = parameters.GetProperty( "path" ).GetString()
			?? throw new Exception( "Missing required parameter: path" );

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, path ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		if ( !File.Exists( fullPath ) )
			throw new Exception( $"Prefab file not found: {path}" );

		var content = File.ReadAllText( fullPath );
		var fileInfo = new FileInfo( fullPath );

		return Task.FromResult<object>( new
		{
			path,
			name = Path.GetFileNameWithoutExtension( fullPath ),
			sizeBytes = fileInfo.Length,
			lastModified = fileInfo.LastWriteTimeUtc.ToString( "o" ),
			content,
		} );
	}
}
