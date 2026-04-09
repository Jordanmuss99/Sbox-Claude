using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Generates a first-person or third-person player controller script.
/// Creates a fully functional C# component using s&box's CharacterController
/// with WASD movement, mouse look, jumping, and optional sprint.
/// </summary>
public class CreatePlayerControllerHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? "PlayerController" : "PlayerController";

		var directory = parameters.TryGetProperty( "directory", out var dirProp )
			? dirProp.GetString() ?? "" : "";

		var controllerType = parameters.TryGetProperty( "type", out var typeProp )
			? typeProp.GetString() ?? "first_person" : "first_person";

		var moveSpeed = parameters.TryGetProperty( "moveSpeed", out var speedProp )
			? speedProp.GetSingle() : 300f;

		var jumpForce = parameters.TryGetProperty( "jumpForce", out var jumpProp )
			? jumpProp.GetSingle() : 350f;

		var sprintMultiplier = parameters.TryGetProperty( "sprintMultiplier", out var sprintProp )
			? sprintProp.GetSingle() : 1.5f;

		var sb = new StringBuilder();
		sb.AppendLine( "using Sandbox;" );
		sb.AppendLine();
		sb.AppendLine( "/// <summary>" );
		sb.AppendLine( $"/// {(controllerType == "third_person" ? "Third" : "First")}-person player controller with WASD movement, mouse look, and jumping." );
		sb.AppendLine( "/// Requires a CharacterController component on the same GameObject." );
		sb.AppendLine( "/// </summary>" );
		sb.AppendLine( $"public sealed class {name} : Component" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\t[Property] public float MoveSpeed {{ get; set; }} = {moveSpeed}f;" );
		sb.AppendLine( $"\t[Property] public float JumpForce {{ get; set; }} = {jumpForce}f;" );
		sb.AppendLine( $"\t[Property] public float SprintMultiplier {{ get; set; }} = {sprintMultiplier}f;" );
		sb.AppendLine( "\t[Property] public float MouseSensitivity { get; set; } = 2.0f;" );

		if ( controllerType == "third_person" )
		{
			sb.AppendLine( "\t[Property] public float CameraDistance { get; set; } = 200f;" );
			sb.AppendLine( "\t[Property] public float CameraHeight { get; set; } = 60f;" );
		}
		else
		{
			sb.AppendLine( "\t[Property] public float EyeHeight { get; set; } = 64f;" );
		}

		sb.AppendLine();
		sb.AppendLine( "\tprivate CharacterController _cc;" );
		sb.AppendLine( "\tprivate Angles _eyeAngles;" );
		sb.AppendLine();
		sb.AppendLine( "\tprotected override void OnStart()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\t_cc = Components.Get<CharacterController>();" );
		sb.AppendLine( "\t\t_eyeAngles = WorldRotation.Angles();" );
		sb.AppendLine( "\t}" );
		sb.AppendLine();
		sb.AppendLine( "\tprotected override void OnUpdate()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\tif ( _cc == null ) return;" );
		sb.AppendLine();
		sb.AppendLine( "\t\t// Mouse look" );
		sb.AppendLine( "\t\t_eyeAngles.pitch += Input.MouseDelta.y * MouseSensitivity * -0.1f;" );
		sb.AppendLine( "\t\t_eyeAngles.yaw -= Input.MouseDelta.x * MouseSensitivity * 0.1f;" );
		sb.AppendLine( "\t\t_eyeAngles.pitch = _eyeAngles.pitch.Clamp( -89f, 89f );" );
		sb.AppendLine();
		sb.AppendLine( "\t\tWorldRotation = Rotation.From( 0, _eyeAngles.yaw, 0 );" );
		sb.AppendLine();
		sb.AppendLine( "\t\t// Movement input" );
		sb.AppendLine( "\t\tvar input = Input.AnalogMove;" );
		sb.AppendLine( "\t\tvar moveDir = WorldRotation * new Vector3( input.x, input.y, 0 ).Normal;" );
		sb.AppendLine();
		sb.AppendLine( "\t\tvar speed = MoveSpeed;" );
		sb.AppendLine( "\t\tif ( Input.Down( \"run\" ) ) speed *= SprintMultiplier;" );
		sb.AppendLine();
		sb.AppendLine( "\t\t_cc.Accelerate( moveDir * speed );" );
		sb.AppendLine();
		sb.AppendLine( "\t\tif ( _cc.IsOnGround )" );
		sb.AppendLine( "\t\t{" );
		sb.AppendLine( "\t\t\t_cc.Velocity = _cc.Velocity.WithZ( 0 );" );
		sb.AppendLine( "\t\t\t_cc.ApplyFriction( 4.0f );" );
		sb.AppendLine();
		sb.AppendLine( "\t\t\tif ( Input.Pressed( \"jump\" ) )" );
		sb.AppendLine( "\t\t\t{" );
		sb.AppendLine( "\t\t\t\t_cc.Punch( Vector3.Up * JumpForce );" );
		sb.AppendLine( "\t\t\t}" );
		sb.AppendLine( "\t\t}" );
		sb.AppendLine( "\t\telse" );
		sb.AppendLine( "\t\t{" );
		sb.AppendLine( "\t\t\t_cc.Velocity += Vector3.Down * 800f * Time.Delta;" );
		sb.AppendLine( "\t\t}" );
		sb.AppendLine();
		sb.AppendLine( "\t\t_cc.Move();" );
		sb.AppendLine();

		if ( controllerType == "third_person" )
		{
			sb.AppendLine( "\t\t// Third-person camera" );
			sb.AppendLine( "\t\tvar camPos = WorldPosition + Vector3.Up * CameraHeight" );
			sb.AppendLine( "\t\t\t- Rotation.From( _eyeAngles ) * Vector3.Forward * CameraDistance;" );
			sb.AppendLine( "\t\tvar cam = Scene.Camera;" );
			sb.AppendLine( "\t\tif ( cam != null )" );
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( "\t\t\tcam.WorldPosition = camPos;" );
			sb.AppendLine( "\t\t\tcam.WorldRotation = Rotation.From( _eyeAngles );" );
			sb.AppendLine( "\t\t}" );
		}
		else
		{
			sb.AppendLine( "\t\t// First-person camera" );
			sb.AppendLine( "\t\tvar cam = Scene.Camera;" );
			sb.AppendLine( "\t\tif ( cam != null )" );
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( "\t\t\tcam.WorldPosition = WorldPosition + Vector3.Up * EyeHeight;" );
			sb.AppendLine( "\t\t\tcam.WorldRotation = Rotation.From( _eyeAngles );" );
			sb.AppendLine( "\t\t}" );
		}

		sb.AppendLine( "\t}" );
		sb.AppendLine( "}" );

		var relPath = string.IsNullOrEmpty( directory )
			? $"code/{name}.cs"
			: $"code/{directory}/{name}.cs";

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relPath ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		var dirPath = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dirPath ) )
			Directory.CreateDirectory( dirPath );

		File.WriteAllText( fullPath, sb.ToString() );

		return Task.FromResult<object>( new
		{
			path = relPath,
			name,
			type = controllerType,
			moveSpeed,
			jumpForce,
			sprintMultiplier,
			created = true,
		} );
	}
}
