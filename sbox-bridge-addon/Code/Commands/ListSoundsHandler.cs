using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Lists available sound assets in the project and installed packages.
/// </summary>
public class ListSoundsHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var filter = parameters.TryGetProperty( "filter", out var filterProp )
			? filterProp.GetString() ?? ""
			: "";

		var maxResults = parameters.TryGetProperty( "maxResults", out var maxProp )
			? maxProp.GetInt32() : 50;

		var sounds = new List<object>();

		// Search for sound assets (.sound, .vsnd)
		IEnumerable<Asset> assets = AssetSystem.All
			.Where( a =>
				a.AssetType?.Name?.Contains( "Sound", StringComparison.OrdinalIgnoreCase ) == true ||
				(a.Path?.EndsWith( ".sound", StringComparison.OrdinalIgnoreCase ) ?? false) ||
				(a.Path?.EndsWith( ".vsnd", StringComparison.OrdinalIgnoreCase ) ?? false) ||
				(a.Path?.EndsWith( ".sound_event", StringComparison.OrdinalIgnoreCase ) ?? false)
			);

		if ( !string.IsNullOrEmpty( filter ) )
		{
			assets = assets.Where( a =>
				(a.Name?.Contains( filter, StringComparison.OrdinalIgnoreCase ) ?? false) ||
				(a.Path?.Contains( filter, StringComparison.OrdinalIgnoreCase ) ?? false)
			);
		}

		foreach ( var asset in assets.Take( maxResults ) )
		{
			sounds.Add( new
			{
				name = asset.Name,
				path = asset.Path,
				type = asset.AssetType?.Name ?? "sound",
				package = asset.Package?.FullIdent,
			} );
		}

		return Task.FromResult<object>( new
		{
			filter = string.IsNullOrEmpty( filter ) ? null : filter,
			count = sounds.Count,
			sounds,
		} );
	}
}
