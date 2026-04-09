using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Attaches a sound event to a SoundPointComponent on a GameObject.
/// Creates the component if it doesn't exist.
/// </summary>
public class AssignSoundHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var soundEvent = parameters.GetProperty( "sound" ).GetString()
			?? throw new Exception( "Missing required parameter: sound" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		// Find or create SoundPointComponent
		var soundComponent = go.Components.Get<SoundPointComponent>();
		if ( soundComponent == null )
		{
			soundComponent = go.Components.Create<SoundPointComponent>();
		}

		// Assign the sound event
		soundComponent.SoundEvent = SoundEvent.Load( soundEvent );

		// Optionally set playback properties
		if ( parameters.TryGetProperty( "playOnStart", out var playProp ) )
		{
			soundComponent.StartOnPlay = playProp.GetBoolean();
		}

		return Task.FromResult<object>( new
		{
			id,
			name = go.Name,
			sound = soundEvent,
			componentCreated = true,
			assigned = true,
		} );
	}
}
