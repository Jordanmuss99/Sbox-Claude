using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Browses the s&box community asset library (packages).
/// Returns available asset packages that can be installed.
/// </summary>
public class ListAssetLibraryHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var query = parameters.TryGetProperty( "query", out var queryProp )
			? queryProp.GetString() ?? ""
			: "";

		var packageType = parameters.TryGetProperty( "type", out var typeProp )
			? typeProp.GetString() ?? ""
			: "";

		var maxResults = parameters.TryGetProperty( "maxResults", out var maxProp )
			? maxProp.GetInt32() : 25;

		// Use Package.FindAsync or similar s&box API to search community packages
		// For now, list installed/referenced packages
		var packages = new List<object>();

		var allPackages = Package.All;
		IEnumerable<Package> filtered = allPackages;

		if ( !string.IsNullOrEmpty( query ) )
		{
			filtered = filtered.Where( p =>
				(p.Title?.Contains( query, StringComparison.OrdinalIgnoreCase ) ?? false) ||
				(p.FullIdent?.Contains( query, StringComparison.OrdinalIgnoreCase ) ?? false) ||
				(p.Description?.Contains( query, StringComparison.OrdinalIgnoreCase ) ?? false)
			);
		}

		if ( !string.IsNullOrEmpty( packageType ) )
		{
			filtered = filtered.Where( p =>
				p.PackageType.ToString().Contains( packageType, StringComparison.OrdinalIgnoreCase )
			);
		}

		foreach ( var pkg in filtered.Take( maxResults ) )
		{
			packages.Add( new
			{
				ident = pkg.FullIdent,
				title = pkg.Title,
				description = pkg.Description,
				type = pkg.PackageType.ToString(),
				author = pkg.Author,
			} );
		}

		return Task.FromResult<object>( new
		{
			query = string.IsNullOrEmpty( query ) ? null : query,
			count = packages.Count,
			packages,
		} );
	}
}
