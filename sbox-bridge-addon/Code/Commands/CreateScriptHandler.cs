using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a new C# component script with proper s&box boilerplate.
/// Supports either generating from parameters or writing raw content.
/// </summary>
public class CreateScriptHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new System.Exception( "No project is currently open" );

		// If raw content is provided, just write it directly
		if ( parameters.TryGetProperty( "content", out var contentProp ) )
		{
			var rawContent = contentProp.GetString()
				?? throw new System.Exception( "content parameter was null" );

			var name = parameters.GetProperty( "name" ).GetString() ?? "NewComponent";
			var directory = parameters.TryGetProperty( "directory", out var dirProp )
				? dirProp.GetString() ?? "" : "";

			var relPath = string.IsNullOrEmpty( directory )
				? $"code/{name}.cs"
				: $"code/{directory}/{name}.cs";

			var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relPath ) );
			if ( !fullPath.StartsWith( projectRoot ) )
				throw new System.Exception( "Path must be within the project directory" );

			var dir = Path.GetDirectoryName( fullPath );
			if ( !string.IsNullOrEmpty( dir ) )
				Directory.CreateDirectory( dir );

			File.WriteAllText( fullPath, rawContent );

			return Task.FromResult<object>( new
			{
				path = relPath,
				name,
				generated = false,
			} );
		}

		// Generate from parameters
		return Task.FromResult<object>( GenerateScript( parameters, projectRoot ) );
	}

	private static object GenerateScript( JsonElement parameters, string projectRoot )
	{
		var name = parameters.GetProperty( "name" ).GetString()
			?? throw new System.Exception( "Missing required parameter: name" );

		var directory = parameters.TryGetProperty( "directory", out var dirProp )
			? dirProp.GetString() ?? "" : "";
		var description = parameters.TryGetProperty( "description", out var descProp )
			? descProp.GetString() ?? "" : "";

		var sb = new StringBuilder();
		sb.AppendLine( "using Sandbox;" );
		sb.AppendLine();

		if ( !string.IsNullOrEmpty( description ) )
		{
			sb.AppendLine( "/// <summary>" );
			sb.AppendLine( $"/// {description}" );
			sb.AppendLine( "/// </summary>" );
		}

		sb.AppendLine( $"public sealed class {name} : Component" );
		sb.AppendLine( "{" );

		// Add properties if specified
		if ( parameters.TryGetProperty( "properties", out var propsProp ) && propsProp.ValueKind == JsonValueKind.Array )
		{
			foreach ( var prop in propsProp.EnumerateArray() )
			{
				var propName = prop.GetProperty( "name" ).GetString() ?? "Property";
				var propType = prop.GetProperty( "type" ).GetString() ?? "float";
				var propDefault = prop.TryGetProperty( "default", out var defProp )
					? defProp.GetString() : null;
				var propDesc = prop.TryGetProperty( "description", out var pdProp )
					? pdProp.GetString() : null;

				if ( !string.IsNullOrEmpty( propDesc ) )
				{
					sb.AppendLine( $"\t/// <summary>{propDesc}</summary>" );
				}

				sb.AppendLine( "\t[Property]" );

				if ( !string.IsNullOrEmpty( propDefault ) )
					sb.AppendLine( $"\tpublic {propType} {propName} {{ get; set; }} = {propDefault};" );
				else
					sb.AppendLine( $"\tpublic {propType} {propName} {{ get; set; }}" );

				sb.AppendLine();
			}
		}

		sb.AppendLine( "\tprotected override void OnUpdate()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t}" );
		sb.AppendLine( "}" );

		var relPath = string.IsNullOrEmpty( directory )
			? $"code/{name}.cs"
			: $"code/{directory}/{name}.cs";

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relPath ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new System.Exception( "Path must be within the project directory" );

		var dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		var content = sb.ToString();
		File.WriteAllText( fullPath, content );

		return new
		{
			path = relPath,
			name,
			generated = true,
			content,
		};
	}
}
