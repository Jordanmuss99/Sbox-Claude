using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Assigns a model to a ModelRenderer component on a GameObject.
/// If the object doesn't have a ModelRenderer, one is added automatically.
/// </summary>
public class AssignModelHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var modelPath = parameters.GetProperty( "model" ).GetString()
			?? throw new Exception( "Missing required parameter: model" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		// Find or add ModelRenderer
		var renderer = go.Components.Get<ModelRenderer>();
		if ( renderer == null )
		{
			renderer = go.Components.Create<ModelRenderer>();
		}

		// Load and assign the model
		var model = Model.Load( modelPath );
		if ( model == null )
			throw new Exception( $"Failed to load model: {modelPath}" );

		renderer.Model = model;

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			model = modelPath,
			rendererCreated = renderer == go.Components.Get<ModelRenderer>(),
			assigned = true,
		} );
	}
}
