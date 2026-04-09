using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Generates an NPC controller script with NavMeshAgent pathfinding.
/// Creates a patrol/chase AI that follows waypoints or targets players.
/// </summary>
public class CreateNpcControllerHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? "NpcController" : "NpcController";

		var directory = parameters.TryGetProperty( "directory", out var dirProp )
			? dirProp.GetString() ?? "" : "";

		var behavior = parameters.TryGetProperty( "behavior", out var bhProp )
			? bhProp.GetString() ?? "patrol" : "patrol";

		var moveSpeed = parameters.TryGetProperty( "moveSpeed", out var speedProp )
			? speedProp.GetSingle() : 150f;

		var chaseRange = parameters.TryGetProperty( "chaseRange", out var chaseProp )
			? chaseProp.GetSingle() : 500f;

		var sb = new StringBuilder();
		sb.AppendLine( "using Sandbox;" );
		sb.AppendLine( "using Sandbox.Navigation;" );
		sb.AppendLine( "using System.Collections.Generic;" );
		sb.AppendLine();
		sb.AppendLine( "/// <summary>" );
		sb.AppendLine( $"/// NPC controller with {behavior} behavior using NavMeshAgent." );
		sb.AppendLine( "/// Requires a NavMeshAgent component on the same GameObject." );
		sb.AppendLine( "/// </summary>" );
		sb.AppendLine( $"public sealed class {name} : Component" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\t[Property] public float MoveSpeed {{ get; set; }} = {moveSpeed}f;" );

		if ( behavior == "chase" || behavior == "patrol_chase" )
		{
			sb.AppendLine( $"\t[Property] public float ChaseRange {{ get; set; }} = {chaseRange}f;" );
			sb.AppendLine( "\t[Property] public float AttackRange { get; set; } = 50f;" );
		}

		if ( behavior == "patrol" || behavior == "patrol_chase" )
		{
			sb.AppendLine( "\t[Property] public List<Vector3> PatrolPoints { get; set; } = new();" );
			sb.AppendLine( "\t[Property] public float WaitTime { get; set; } = 2f;" );
		}

		sb.AppendLine();
		sb.AppendLine( "\tprivate NavMeshAgent _agent;" );

		if ( behavior == "patrol" || behavior == "patrol_chase" )
		{
			sb.AppendLine( "\tprivate int _currentPatrolIndex;" );
			sb.AppendLine( "\tprivate TimeSince _timeSinceArrived;" );
		}

		if ( behavior == "chase" || behavior == "patrol_chase" )
		{
			sb.AppendLine( "\tprivate bool _isChasing;" );
		}

		sb.AppendLine();
		sb.AppendLine( "\tprotected override void OnStart()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\t_agent = Components.Get<NavMeshAgent>();" );
		sb.AppendLine( "\t}" );
		sb.AppendLine();
		sb.AppendLine( "\tprotected override void OnUpdate()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\tif ( _agent == null ) return;" );
		sb.AppendLine();

		switch ( behavior )
		{
			case "chase":
				sb.AppendLine( "\t\t// Find nearest player and chase if in range" );
				sb.AppendLine( "\t\tvar target = FindNearestPlayer();" );
				sb.AppendLine( "\t\tif ( target != null && target.WorldPosition.Distance( WorldPosition ) < ChaseRange )" );
				sb.AppendLine( "\t\t{" );
				sb.AppendLine( "\t\t\t_agent.MoveTo( target.WorldPosition );" );
				sb.AppendLine( "\t\t}" );
				break;

			case "patrol":
				sb.AppendLine( "\t\t// Patrol between waypoints" );
				sb.AppendLine( "\t\tif ( PatrolPoints.Count == 0 ) return;" );
				sb.AppendLine();
				sb.AppendLine( "\t\tvar targetPos = PatrolPoints[_currentPatrolIndex];" );
				sb.AppendLine( "\t\tvar distance = WorldPosition.Distance( targetPos );" );
				sb.AppendLine();
				sb.AppendLine( "\t\tif ( distance < 30f )" );
				sb.AppendLine( "\t\t{" );
				sb.AppendLine( "\t\t\tif ( _timeSinceArrived > WaitTime )" );
				sb.AppendLine( "\t\t\t{" );
				sb.AppendLine( "\t\t\t\t_currentPatrolIndex = ( _currentPatrolIndex + 1 ) % PatrolPoints.Count;" );
				sb.AppendLine( "\t\t\t\t_timeSinceArrived = 0;" );
				sb.AppendLine( "\t\t\t}" );
				sb.AppendLine( "\t\t}" );
				sb.AppendLine( "\t\telse" );
				sb.AppendLine( "\t\t{" );
				sb.AppendLine( "\t\t\t_agent.MoveTo( targetPos );" );
				sb.AppendLine( "\t\t}" );
				break;

			case "patrol_chase":
			default:
				sb.AppendLine( "\t\tvar target = FindNearestPlayer();" );
				sb.AppendLine( "\t\t_isChasing = target != null && target.WorldPosition.Distance( WorldPosition ) < ChaseRange;" );
				sb.AppendLine();
				sb.AppendLine( "\t\tif ( _isChasing )" );
				sb.AppendLine( "\t\t{" );
				sb.AppendLine( "\t\t\t_agent.MoveTo( target.WorldPosition );" );
				sb.AppendLine( "\t\t}" );
				sb.AppendLine( "\t\telse if ( PatrolPoints.Count > 0 )" );
				sb.AppendLine( "\t\t{" );
				sb.AppendLine( "\t\t\tvar targetPos = PatrolPoints[_currentPatrolIndex];" );
				sb.AppendLine( "\t\t\tif ( WorldPosition.Distance( targetPos ) < 30f && _timeSinceArrived > WaitTime )" );
				sb.AppendLine( "\t\t\t{" );
				sb.AppendLine( "\t\t\t\t_currentPatrolIndex = ( _currentPatrolIndex + 1 ) % PatrolPoints.Count;" );
				sb.AppendLine( "\t\t\t\t_timeSinceArrived = 0;" );
				sb.AppendLine( "\t\t\t}" );
				sb.AppendLine( "\t\t\telse" );
				sb.AppendLine( "\t\t\t{" );
				sb.AppendLine( "\t\t\t\t_agent.MoveTo( targetPos );" );
				sb.AppendLine( "\t\t\t}" );
				sb.AppendLine( "\t\t}" );
				break;
		}

		sb.AppendLine( "\t}" );

		if ( behavior == "chase" || behavior == "patrol_chase" )
		{
			sb.AppendLine();
			sb.AppendLine( "\tprivate GameObject FindNearestPlayer()" );
			sb.AppendLine( "\t{" );
			sb.AppendLine( "\t\tGameObject nearest = null;" );
			sb.AppendLine( "\t\tfloat nearestDist = float.MaxValue;" );
			sb.AppendLine();
			sb.AppendLine( "\t\tforeach ( var go in Scene.GetAllObjects( true ) )" );
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( "\t\t\tif ( go.Tags.Has( \"player\" ) )" );
			sb.AppendLine( "\t\t\t{" );
			sb.AppendLine( "\t\t\t\tvar dist = go.WorldPosition.Distance( WorldPosition );" );
			sb.AppendLine( "\t\t\t\tif ( dist < nearestDist )" );
			sb.AppendLine( "\t\t\t\t{" );
			sb.AppendLine( "\t\t\t\t\tnearest = go;" );
			sb.AppendLine( "\t\t\t\t\tnearestDist = dist;" );
			sb.AppendLine( "\t\t\t\t}" );
			sb.AppendLine( "\t\t\t}" );
			sb.AppendLine( "\t\t}" );
			sb.AppendLine();
			sb.AppendLine( "\t\treturn nearest;" );
			sb.AppendLine( "\t}" );
		}

		sb.AppendLine( "}" );

		var relPath = string.IsNullOrEmpty( directory )
			? $"code/{name}.cs"
			: $"code/{directory}/{name}.cs";

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relPath ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		var dirStr = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dirStr ) )
			Directory.CreateDirectory( dirStr );

		File.WriteAllText( fullPath, sb.ToString() );

		return Task.FromResult<object>( new
		{
			path = relPath,
			name,
			behavior,
			moveSpeed,
			created = true,
		} );
	}
}
