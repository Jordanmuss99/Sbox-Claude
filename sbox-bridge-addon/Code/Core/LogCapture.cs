using Sandbox;
using Sandbox.Diagnostics;

namespace SboxBridge;

/// <summary>
/// Hooks into s&box's logging system to capture console output
/// for the get_console_output command.
/// </summary>
public static class LogCapture
{
	private static bool _initialized;

	[Event( "editor.loaded" )]
	public static void Initialize()
	{
		if ( _initialized ) return;
		_initialized = true;

		// Hook into the s&box logger
		Logger.OnMessage += OnLogMessage;

		Log.Info( "[SboxBridge] Log capture initialized" );
	}

	private static void OnLogMessage( LogMessage msg )
	{
		var severity = msg.Level switch
		{
			LogLevel.Trace => "info",
			LogLevel.Info => "info",
			LogLevel.Warning => "warning",
			LogLevel.Error => "error",
			_ => "info",
		};

		GetConsoleOutputHandler.AddEntry( msg.Text, severity, msg.Logger ?? "" );
	}
}
