using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Deletes a script file from the project.
/// </summary>
public class DeleteScriptHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new System.Exception( "No project is currently open" );

		// Ensure trailing separator for safe StartsWith check
		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var relativePath = parameters.GetProperty( "path" ).GetString()
			?? throw new System.Exception( "Missing required parameter: path" );

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );

		// Security: ensure the path stays within the project
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new System.Exception( "Path must be within the project directory" );

		if ( !File.Exists( fullPath ) )
			throw new System.Exception( $"File not found: {relativePath}" );

		File.Delete( fullPath );

		return Task.FromResult<object>( new
		{
			path = relativePath,
			deleted = true,
		} );
	}
}
