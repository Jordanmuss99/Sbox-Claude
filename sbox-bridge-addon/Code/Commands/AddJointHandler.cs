using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Adds a physics joint/constraint between two GameObjects.
/// Supports Fixed, Spring, and Slider joint types.
/// </summary>
public class AddJointHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
			throw new Exception( "No active scene" );

		var id = parameters.GetProperty( "id" ).GetString()
			?? throw new Exception( "Missing required parameter: id" );
		var type = parameters.GetProperty( "type" ).GetString()
			?? throw new Exception( "Missing required parameter: type" );

		if ( !Guid.TryParse( id, out var guid ) )
			throw new Exception( $"Invalid GUID: {id}" );

		var go = scene.Directory.FindByGuid( guid );
		if ( go == null )
			throw new Exception( $"GameObject not found: {id}" );

		// Optional target body
		GameObject target = null;
		if ( parameters.TryGetProperty( "targetId", out var targetProp ) )
		{
			var targetGuidStr = targetProp.GetString();
			if ( !string.IsNullOrEmpty( targetGuidStr ) && Guid.TryParse( targetGuidStr, out var targetGuid ) )
			{
				target = scene.Directory.FindByGuid( targetGuid );
			}
		}

		string jointTypeName;
		switch ( type.ToLowerInvariant() )
		{
			case "fixed":
				var fixedJoint = go.Components.Create<FixedJoint>();
				if ( target != null )
					fixedJoint.Body = target;
				jointTypeName = "FixedJoint";
				break;

			case "spring":
				var springJoint = go.Components.Create<SpringJoint>();
				if ( target != null )
					springJoint.Body = target;
				if ( parameters.TryGetProperty( "frequency", out var freqProp ) )
					springJoint.Frequency = freqProp.GetSingle();
				if ( parameters.TryGetProperty( "damping", out var dampProp ) )
					springJoint.DampingRatio = dampProp.GetSingle();
				jointTypeName = "SpringJoint";
				break;

			case "slider":
				// API-NOTE: SliderJoint may be named differently in some SDK versions
				var sliderJoint = go.Components.Create<SliderJoint>();
				if ( target != null )
					sliderJoint.Body = target;
				jointTypeName = "SliderJoint";
				break;

			default:
				throw new Exception( $"Unknown joint type: {type}. Use: fixed, spring, slider" );
		}

		return Task.FromResult<object>( new
		{
			id,
			gameObject = go.Name,
			jointType = jointTypeName,
			targetId = target?.Id.ToString(),
			targetName = target?.Name,
			added = true,
		} );
	}
}
