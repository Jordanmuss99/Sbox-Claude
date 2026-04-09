using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Plays a sound in the editor for preview purposes.
/// Useful for testing sounds without entering play mode.
/// </summary>
public class PlaySoundPreviewHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var sound = parameters.GetProperty( "sound" ).GetString()
			?? throw new Exception( "Missing required parameter: sound" );

		var volume = parameters.TryGetProperty( "volume", out var volProp )
			? volProp.GetSingle() : 1.0f;

		// Play the sound as a UI sound (no 3D positioning needed for preview)
		var handle = Sound.Play( sound );
		if ( handle.IsValid() )
		{
			handle.Volume = volume;
		}

		return Task.FromResult<object>( new
		{
			sound,
			volume,
			playing = handle.IsValid(),
		} );
	}
}
