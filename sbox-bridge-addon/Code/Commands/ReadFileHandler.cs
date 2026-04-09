using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Reads the contents of a file in the project.
/// </summary>
public class ReadFileHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new System.Exception( "No project is currently open" );

		var relativePath = parameters.GetProperty( "path" ).GetString()
			?? throw new System.Exception( "Missing required parameter: path" );

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );

		// Security: ensure the path stays within the project
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new System.Exception( "Path must be within the project directory" );

		if ( !File.Exists( fullPath ) )
			throw new System.Exception( $"File not found: {relativePath}" );

		var content = File.ReadAllText( fullPath );

		return Task.FromResult<object>( new
		{
			path = relativePath,
			content,
			lineCount = content.Split( '\n' ).Length,
		} );
	}
}
