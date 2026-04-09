using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Searches for assets in the current project by name, type, or tag.
/// Covers models, materials, sounds, textures, prefabs, etc.
/// </summary>
public class SearchAssetsHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var query = parameters.TryGetProperty( "query", out var queryProp )
			? queryProp.GetString() ?? ""
			: "";

		var assetType = parameters.TryGetProperty( "type", out var typeProp )
			? typeProp.GetString() ?? ""
			: "";

		var maxResults = parameters.TryGetProperty( "maxResults", out var maxProp )
			? maxProp.GetInt32() : 50;

		var results = new List<object>();

		// Search through all assets known to the asset system
		IEnumerable<Asset> assets = AssetSystem.All;

		// Filter by type
		if ( !string.IsNullOrEmpty( assetType ) )
		{
			assets = assets.Where( a =>
				a.AssetType?.Name?.Contains( assetType, StringComparison.OrdinalIgnoreCase ) == true ||
				(a.Path?.EndsWith( $".{assetType}", StringComparison.OrdinalIgnoreCase ) ?? false)
			);
		}

		// Filter by query (name/path)
		if ( !string.IsNullOrEmpty( query ) )
		{
			assets = assets.Where( a =>
				(a.Name?.Contains( query, StringComparison.OrdinalIgnoreCase ) ?? false) ||
				(a.Path?.Contains( query, StringComparison.OrdinalIgnoreCase ) ?? false)
			);
		}

		foreach ( var asset in assets.Take( maxResults ) )
		{
			results.Add( new
			{
				name = asset.Name,
				path = asset.Path,
				type = asset.AssetType?.Name ?? "unknown",
				package = asset.Package?.FullIdent,
			} );
		}

		return Task.FromResult<object>( new
		{
			query,
			type = string.IsNullOrEmpty( assetType ) ? null : assetType,
			count = results.Count,
			assets = results,
		} );
	}
}
