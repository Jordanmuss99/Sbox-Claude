using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Creates a new GameObject with a ScreenPanel component for screen-space UI.
/// The ScreenPanel serves as a container for Razor UI components.
/// </summary>
public class AddScreenPanelHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? "Screen UI" : "Screen UI";

		// Create the UI container GameObject
		var go = scene.CreateObject();
		go.Name = name;

		// Add ScreenPanel component
		var screenPanel = go.Components.Create<ScreenPanel>();
		if ( screenPanel == null )
			throw new Exception( "Failed to create ScreenPanel component" );

		// Optional: set Z-index for layering
		if ( parameters.TryGetProperty( "zIndex", out var zProp ) )
			screenPanel.ZIndex = zProp.GetInt32();

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
			screenPanel = true,
			created = true,
		} );
	}
}
