using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Generates a game manager script with configurable game loop features.
/// Supports score tracking, round timers, player spawning, and game state management.
/// </summary>
public class CreateGameManagerHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var name = parameters.TryGetProperty( "name", out var nameProp )
			? nameProp.GetString() ?? "GameManager" : "GameManager";

		var directory = parameters.TryGetProperty( "directory", out var dirProp )
			? dirProp.GetString() ?? "" : "";

		var includeScore = !parameters.TryGetProperty( "includeScore", out var scoreProp )
			|| scoreProp.GetBoolean();
		var includeTimer = parameters.TryGetProperty( "includeTimer", out var timerProp )
			&& timerProp.GetBoolean();
		var includeSpawning = parameters.TryGetProperty( "includeSpawning", out var spawnProp )
			&& spawnProp.GetBoolean();

		var sb = new StringBuilder();
		sb.AppendLine( "using Sandbox;" );
		sb.AppendLine( "using System;" );
		sb.AppendLine();
		sb.AppendLine( "/// <summary>" );
		sb.AppendLine( "/// Central game manager that handles game state, score, and round logic." );
		sb.AppendLine( "/// Add this component to a persistent GameObject in your scene." );
		sb.AppendLine( "/// </summary>" );
		sb.AppendLine( $"public sealed class {name} : Component, Component.INetworkListener" );
		sb.AppendLine( "{" );
		sb.AppendLine( "\tpublic enum GameState { Waiting, Playing, GameOver }" );
		sb.AppendLine();
		sb.AppendLine( "\t[Property] public GameState CurrentState { get; set; } = GameState.Waiting;" );

		if ( includeScore )
		{
			sb.AppendLine( "\t[Property] public int Score { get; set; }" );
			sb.AppendLine( "\t[Property] public int HighScore { get; set; }" );
		}

		if ( includeTimer )
		{
			sb.AppendLine( "\t[Property] public float RoundDuration { get; set; } = 120f;" );
			sb.AppendLine( "\tpublic float TimeRemaining { get; private set; }" );
		}

		if ( includeSpawning )
		{
			sb.AppendLine( "\t[Property] public GameObject PlayerPrefab { get; set; }" );
			sb.AppendLine( "\t[Property] public GameObject SpawnPoint { get; set; }" );
		}

		sb.AppendLine();
		sb.AppendLine( "\tprotected override void OnStart()" );
		sb.AppendLine( "\t{" );

		if ( includeTimer )
			sb.AppendLine( "\t\tTimeRemaining = RoundDuration;" );

		sb.AppendLine( "\t}" );
		sb.AppendLine();
		sb.AppendLine( "\tprotected override void OnUpdate()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\tif ( CurrentState != GameState.Playing ) return;" );

		if ( includeTimer )
		{
			sb.AppendLine();
			sb.AppendLine( "\t\tTimeRemaining -= Time.Delta;" );
			sb.AppendLine( "\t\tif ( TimeRemaining <= 0 )" );
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( "\t\t\tEndGame();" );
			sb.AppendLine( "\t\t}" );
		}

		sb.AppendLine( "\t}" );

		// StartGame method
		sb.AppendLine();
		sb.AppendLine( "\tpublic void StartGame()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\tCurrentState = GameState.Playing;" );

		if ( includeScore )
			sb.AppendLine( "\t\tScore = 0;" );
		if ( includeTimer )
			sb.AppendLine( "\t\tTimeRemaining = RoundDuration;" );
		if ( includeSpawning )
		{
			sb.AppendLine();
			sb.AppendLine( "\t\tif ( PlayerPrefab != null && SpawnPoint != null )" );
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( "\t\t\tvar player = PlayerPrefab.Clone( SpawnPoint.WorldPosition );" );
			sb.AppendLine( "\t\t\tplayer.WorldRotation = SpawnPoint.WorldRotation;" );
			sb.AppendLine( "\t\t}" );
		}

		sb.AppendLine( "\t}" );

		// EndGame method
		sb.AppendLine();
		sb.AppendLine( "\tpublic void EndGame()" );
		sb.AppendLine( "\t{" );
		sb.AppendLine( "\t\tCurrentState = GameState.GameOver;" );

		if ( includeScore )
		{
			sb.AppendLine( "\t\tif ( Score > HighScore ) HighScore = Score;" );
		}

		sb.AppendLine( "\t}" );

		if ( includeScore )
		{
			sb.AppendLine();
			sb.AppendLine( "\tpublic void AddScore( int points )" );
			sb.AppendLine( "\t{" );
			sb.AppendLine( "\t\tScore += points;" );
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
			includeScore,
			includeTimer,
			includeSpawning,
			created = true,
		} );
	}
}
