using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Returns detailed metadata about a specific asset by path.
/// </summary>
public class GetAssetInfoHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var path = parameters.GetProperty( "path" ).GetString()
			?? throw new Exception( "Missing required parameter: path" );

		var asset = AssetSystem.FindByPath( path );
		if ( asset == null )
			throw new Exception( $"Asset not found: {path}" );

		return Task.FromResult<object>( new
		{
			name = asset.Name,
			path = asset.Path,
			type = asset.AssetType?.Name ?? "unknown",
			package = asset.Package?.FullIdent,
			tags = asset.Tags?.ToArray(),
		} );
	}
}
