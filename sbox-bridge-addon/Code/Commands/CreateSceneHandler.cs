using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a new empty scene file. Optionally includes default objects
/// (Camera, Directional Light, ground plane).
/// </summary>
public class CreateSceneHandler : ICommandHandler
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

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? Path.GetFileNameWithoutExtension( relativePath )
			: Path.GetFileNameWithoutExtension( relativePath );

		var includeDefaults = !parameters.TryGetProperty( "includeDefaults", out var defProp )
			|| defProp.GetBoolean();

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );

		if ( !fullPath.StartsWith( projectRoot ) )
			throw new System.Exception( "Path must be within the project directory" );

		if ( File.Exists( fullPath ) )
			throw new System.Exception( $"Scene already exists: {relativePath}" );

		// Create directory
		var dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		// Generate scene JSON
		var sceneJson = GenerateSceneJson( name, includeDefaults );
		File.WriteAllText( fullPath, sceneJson );

		return Task.FromResult<object>( new
		{
			path = relativePath,
			name,
			includeDefaults,
			created = true,
		} );
	}

	private static string GenerateSceneJson( string name, bool includeDefaults )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "{" );
		sb.AppendLine( $"  \"__guid\": \"{System.Guid.NewGuid()}\"," );
		sb.AppendLine( $"  \"GameObjects\": [" );

		if ( includeDefaults )
		{
			// Camera
			sb.AppendLine( "    {" );
			sb.AppendLine( $"      \"__guid\": \"{System.Guid.NewGuid()}\"," );
			sb.AppendLine( "      \"Name\": \"Camera\"," );
			sb.AppendLine( "      \"Position\": \"0,0,200\"," );
			sb.AppendLine( "      \"Rotation\": \"0,0,0,1\"," );
			sb.AppendLine( "      \"Components\": [" );
			sb.AppendLine( "        {" );
			sb.AppendLine( "          \"__type\": \"CameraComponent\"," );
			sb.AppendLine( $"          \"__guid\": \"{System.Guid.NewGuid()}\"" );
			sb.AppendLine( "        }" );
			sb.AppendLine( "      ]" );
			sb.AppendLine( "    }," );

			// Directional Light
			sb.AppendLine( "    {" );
			sb.AppendLine( $"      \"__guid\": \"{System.Guid.NewGuid()}\"," );
			sb.AppendLine( "      \"Name\": \"Directional Light\"," );
			sb.AppendLine( "      \"Position\": \"0,0,500\"," );
			sb.AppendLine( "      \"Rotation\": \"0.4,-0.1,-0.2,0.9\"," );
			sb.AppendLine( "      \"Components\": [" );
			sb.AppendLine( "        {" );
			sb.AppendLine( "          \"__type\": \"DirectionalLight\"," );
			sb.AppendLine( $"          \"__guid\": \"{System.Guid.NewGuid()}\"," );
			sb.AppendLine( "          \"LightColor\": \"0.91,0.87,0.78,1\"," );
			sb.AppendLine( "          \"Brightness\": 2.5," );
			sb.AppendLine( "          \"Shadows\": true" );
			sb.AppendLine( "        }" );
			sb.AppendLine( "      ]" );
			sb.AppendLine( "    }," );

			// Ground plane
			sb.AppendLine( "    {" );
			sb.AppendLine( $"      \"__guid\": \"{System.Guid.NewGuid()}\"," );
			sb.AppendLine( "      \"Name\": \"Ground\"," );
			sb.AppendLine( "      \"Position\": \"0,0,0\"," );
			sb.AppendLine( "      \"Scale\": \"100,100,1\"," );
			sb.AppendLine( "      \"Components\": [" );
			sb.AppendLine( "        {" );
			sb.AppendLine( "          \"__type\": \"ModelRenderer\"," );
			sb.AppendLine( $"          \"__guid\": \"{System.Guid.NewGuid()}\"," );
			sb.AppendLine( "          \"Model\": \"models/dev/plane.vmdl\"" );
			sb.AppendLine( "        }," );
			sb.AppendLine( "        {" );
			sb.AppendLine( "          \"__type\": \"PlaneCollider\"," );
			sb.AppendLine( $"          \"__guid\": \"{System.Guid.NewGuid()}\"" );
			sb.AppendLine( "        }" );
			sb.AppendLine( "      ]" );
			sb.AppendLine( "    }" );
		}

		sb.AppendLine( "  ]," );
		sb.AppendLine( $"  \"SceneProperties\": {{" );
		sb.AppendLine( $"    \"Name\": \"{name}\"" );
		sb.AppendLine( "  }" );
		sb.AppendLine( "}" );

		return sb.ToString();
	}
}
