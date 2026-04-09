using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Generates a trigger zone script with configurable enter/exit callbacks.
/// Creates a component that uses a trigger collider to detect GameObjects
/// entering and leaving a zone, with tag filtering and event handling.
/// </summary>
public class CreateTriggerZoneHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? "TriggerZone" : "TriggerZone";

		var directory = parameters.TryGetProperty( "directory", out var dirProp )
			? dirProp.GetString() ?? "" : "";

		var triggerAction = parameters.TryGetProperty( "action", out var actProp )
			? actProp.GetString() ?? "log" : "log";

		var filterTag = parameters.TryGetProperty( "filterTag", out var tagProp )
			? tagProp.GetString() ?? "player" : "player";

		var sb = new StringBuilder();
		sb.AppendLine( "using Sandbox;" );
		sb.AppendLine();
		sb.AppendLine( "/// <summary>" );
		sb.AppendLine( $"/// Trigger zone that detects GameObjects with tag \"{filterTag}\"." );
		sb.AppendLine( "/// Requires a Collider component set to IsTrigger on the same GameObject." );
		sb.AppendLine( "/// </summary>" );
		sb.AppendLine( $"public sealed class {name} : Component, Component.ITriggerListener" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\t[Property] public string FilterTag {{ get; set; }} = \"{filterTag}\";" );
		sb.AppendLine( "\t[Property] public bool OnlyTriggerOnce { get; set; }" );
		sb.AppendLine();

		switch ( triggerAction )
		{
			case "teleport":
				sb.AppendLine( "\t[Property] public Vector3 TeleportDestination { get; set; }" );
				break;
			case "damage":
				sb.AppendLine( "\t[Property] public float DamageAmount { get; set; } = 25f;" );
				break;
			case "spawn":
				sb.AppendLine( "\t[Property] public GameObject SpawnPrefab { get; set; }" );
				sb.AppendLine( "\t[Property] public Vector3 SpawnOffset { get; set; } = Vector3.Up * 100f;" );
				break;
		}

		sb.AppendLine( "\tprivate bool _hasTriggered;" );
		sb.AppendLine();
		sb.AppendLine( "\tpublic void OnTriggerEnter( Collider other )" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\tif ( _hasTriggered && OnlyTriggerOnce ) return;" );
		sb.AppendLine( "\t\tif ( !string.IsNullOrEmpty( FilterTag ) && !other.GameObject.Tags.Has( FilterTag ) ) return;" );
		sb.AppendLine();
		sb.AppendLine( "\t\t_hasTriggered = true;" );

		switch ( triggerAction )
		{
			case "teleport":
				sb.AppendLine( "\t\tother.GameObject.WorldPosition = TeleportDestination;" );
				sb.AppendLine( "\t\tLog.Info( $\"{other.GameObject.Name} teleported to {TeleportDestination}\" );" );
				break;
			case "damage":
				sb.AppendLine( "\t\t// Apply damage - customize this for your health system" );
				sb.AppendLine( "\t\tLog.Info( $\"Dealing {DamageAmount} damage to {other.GameObject.Name}\" );" );
				break;
			case "spawn":
				sb.AppendLine( "\t\tif ( SpawnPrefab != null )" );
				sb.AppendLine( "\t\t{" );
				sb.AppendLine( "\t\t\tvar spawned = SpawnPrefab.Clone( WorldPosition + SpawnOffset );" );
				sb.AppendLine( "\t\t\tLog.Info( $\"Spawned {spawned.Name} at trigger zone\" );" );
				sb.AppendLine( "\t\t}" );
				break;
			case "log":
			default:
				sb.AppendLine( "\t\tLog.Info( $\"{other.GameObject.Name} entered trigger zone: {GameObject.Name}\" );" );
				break;
		}

		sb.AppendLine( "\t}" );
		sb.AppendLine();
		sb.AppendLine( "\tpublic void OnTriggerExit( Collider other )" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\tif ( !string.IsNullOrEmpty( FilterTag ) && !other.GameObject.Tags.Has( FilterTag ) ) return;" );
		sb.AppendLine( "\t\tLog.Info( $\"{other.GameObject.Name} exited trigger zone: {GameObject.Name}\" );" );
		sb.AppendLine( "\t}" );
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
			action = triggerAction,
			filterTag,
			created = true,
		} );
	}
}
