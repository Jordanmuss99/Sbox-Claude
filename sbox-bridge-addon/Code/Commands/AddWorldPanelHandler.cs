using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a new GameObject with a WorldPanel component for in-world UI.
/// WorldPanel renders UI in 3D space (e.g., health bars above NPCs, signs).
/// </summary>
public class AddWorldPanelHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? "World UI" : "World UI";

		// Create the UI container GameObject
		var go = scene.CreateObject();
		go.Name = name;

		// Set position in world space
		if ( parameters.TryGetProperty( "position", out var posProp ) )
			go.WorldPosition = CreateGameObjectHandler.ParseVector3( posProp );

		if ( parameters.TryGetProperty( "rotation", out var rotProp ) )
			go.WorldRotation = CreateGameObjectHandler.ParseRotation( rotProp );

		// Add WorldPanel component
		var worldPanel = go.Components.Create<WorldPanel>();
		if ( worldPanel == null )
			throw new Exception( "Failed to create WorldPanel component" );

		// Configure world panel properties
		if ( parameters.TryGetProperty( "worldScale", out var scaleProp ) )
			worldPanel.WorldScale = scaleProp.GetSingle();

		if ( parameters.TryGetProperty( "lookAtCamera", out var lookProp ) )
			worldPanel.LookAtCamera = lookProp.GetBoolean();

		// Optional: add a Razor panel component by type name
		if ( parameters.TryGetProperty( "panelComponent", out var panelProp ) )
		{
			var panelTypeName = panelProp.GetString();
			if ( !string.IsNullOrEmpty( panelTypeName ) )
			{
				var typeDesc = TypeLibrary.GetType( panelTypeName )
					?? TypeLibrary.GetTypes<Sandbox.UI.PanelComponent>()
						.FirstOrDefault( t => t.Name.Equals( panelTypeName, StringComparison.OrdinalIgnoreCase ) );

				if ( typeDesc != null )
				{
					go.Components.Create( typeDesc );
				}
			}
		}

		// Optional parent
		if ( parameters.TryGetProperty( "parent", out var parentProp ) )
		{
			var parentGuid = parentProp.GetString();
			if ( !string.IsNullOrEmpty( parentGuid ) && Guid.TryParse( parentGuid, out var parentId ) )
			{
				var parent = scene.Directory.FindByGuid( parentId );
				if ( parent != null )
					go.SetParent( parent );
			}
		}

		return Task.FromResult<object>( new
		{
			id = go.Id.ToString(),
			name = go.Name,
			position = CreateGameObjectHandler.FormatVector3( go.WorldPosition ),
			worldPanel = true,
			created = true,
		} );
	}
}
