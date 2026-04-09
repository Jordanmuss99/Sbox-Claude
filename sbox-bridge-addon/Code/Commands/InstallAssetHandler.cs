using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Installs/adds a community asset package to the current project.
/// Takes a package ident (e.g. "facepunch.flatgrass") and adds it as a dependency.
/// </summary>
public class InstallAssetHandler : ICommandHandler
{
	public async Task<object> Execute( JsonElement parameters )
	{
		var ident = parameters.GetProperty( "ident" ).GetString()
			?? throw new Exception( "Missing required parameter: ident" );

		var project = Project.Current;
		if ( project == null )
			throw new Exception( "No project is currently open" );

		// Find the package
		var package = await Package.FetchAsync( ident, false );
		if ( package == null )
			throw new Exception( $"Package not found: {ident}" );

		// Add as a reference to the project
		project.Config.PackageReferences.Add( new PackageReference
		{
			FullIdent = package.FullIdent,
		} );

		// Save the project config
		project.SaveConfig();

		return new
		{
			ident = package.FullIdent,
			title = package.Title,
			type = package.PackageType.ToString(),
			installed = true,
		};
	}
}
