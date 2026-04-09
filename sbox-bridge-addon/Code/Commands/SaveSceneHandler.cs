using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Saves the currently open scene in the s&box editor.
/// </summary>
public class SaveSceneHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = EditorScene.Active;
		if ( scene == null )
			throw new System.Exception( "No scene is currently open" );

		// Save to a specific path or the current path
		if ( parameters.TryGetProperty( "path", out var pathProp ) )
		{
			var path = pathProp.GetString();
			if ( !string.IsNullOrEmpty( path ) )
			{
				EditorScene.SaveAs( path );
				return Task.FromResult<object>( new
				{
					path,
					saved = true,
				} );
			}
		}

		EditorScene.Save();

		return Task.FromResult<object>( new
		{
			saved = true,
		} );
	}
}
