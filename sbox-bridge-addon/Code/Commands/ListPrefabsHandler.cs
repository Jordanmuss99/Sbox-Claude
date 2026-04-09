using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Lists all .prefab files in the current project.
/// Returns path, name, and file size for each prefab.
/// </summary>
public class ListPrefabsHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var filter = parameters.TryGetProperty( "filter", out var filterProp )
			? filterProp.GetString() ?? "" : "";

		var maxResults = parameters.TryGetProperty( "maxResults", out var maxProp )
			? Math.Clamp( maxProp.GetInt32(), 1, 500 ) : 100;

		var prefabFiles = Directory.GetFiles( projectRoot, "*.prefab", SearchOption.AllDirectories )
			.Select( f =>
			{
				var relativePath = f.Substring( projectRoot.Length ).Replace( '\\', '/' );
				var fileName = Path.GetFileNameWithoutExtension( f );
				var fileInfo = new FileInfo( f );
				return new { path = relativePath, name = fileName, sizeBytes = fileInfo.Length };
			} )
			.Where( p => string.IsNullOrEmpty( filter ) ||
				p.name.Contains( filter, StringComparison.OrdinalIgnoreCase ) ||
				p.path.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
			.Take( maxResults )
			.ToList();

		return Task.FromResult<object>( new
		{
			prefabs = prefabFiles,
			count = prefabFiles.Count,
		} );
	}
}
