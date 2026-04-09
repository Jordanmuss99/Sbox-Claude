using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Assigns a material to a ModelRenderer component on a GameObject.
/// Optionally targets a specific material slot by index.
/// </summary>
public class AssignMaterialHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var materialPath = parameters.GetProperty( "material" ).GetString()
			?? throw new Exception( "Missing required parameter: material" );

		var slot = parameters.TryGetProperty( "slot", out var slotProp )
			? slotProp.GetInt32() : 0;

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		var renderer = go.Components.Get<ModelRenderer>();
		if ( renderer == null )
			throw new Exception( $"No ModelRenderer on '{go.Name}'. Add one first or use assign_model" );

		// Load and assign the material
		var material = Material.Load( materialPath );
		if ( material == null )
			throw new Exception( $"Failed to load material: {materialPath}" );

		renderer.SetMaterialOverride( material, slot );

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			material = materialPath,
			slot,
			assigned = true,
		} );
	}
}
