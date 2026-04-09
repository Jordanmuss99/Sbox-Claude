using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Diagnostics;

namespace SboxBridge;

/// <summary>
/// Reads recent console log entries from s&box.
/// Captures log messages by hooking into the s&box logging system.
/// </summary>
public class GetConsoleOutputHandler : ICommandHandler
{
	/// <summary>
	/// Internal log buffer — entries are captured by the LogCapture class.
	/// </summary>
	internal static readonly List<LogEntry> LogBuffer = new();
	private static readonly object BufferLock = new();
	private const int MaxBufferSize = 500;

	public static void AddEntry( string message, string severity, string source = "" )
	{
		lock ( BufferLock )
		{
			LogBuffer.Add( new LogEntry
			{
				Message = message,
				Severity = severity,
				Source = source,
				Timestamp = DateTime.UtcNow.ToString( "o" ),
			} );

			// Keep buffer from growing unbounded
			while ( LogBuffer.Count > MaxBufferSize )
				LogBuffer.RemoveAt( 0 );
		}
	}

	public static void Clear()
	{
		lock ( BufferLock )
		{
			LogBuffer.Clear();
		}
	}

	public Task<object> Execute( JsonElement parameters )
	{
		var count = parameters.TryGetProperty( "count", out var countProp )
			? countProp.GetInt32() : 50;
		var severity = parameters.TryGetProperty( "severity", out var sevProp )
			? sevProp.GetString() ?? "all" : "all";

		List<LogEntry> entries;
		lock ( BufferLock )
		{
			IEnumerable<LogEntry> filtered = LogBuffer;

			if ( severity != "all" )
				filtered = filtered.Where( e => e.Severity.Equals( severity, StringComparison.OrdinalIgnoreCase ) );

			entries = filtered.TakeLast( count ).ToList();
		}

		return Task.FromResult<object>( new
		{
			count = entries.Count,
			entries,
		} );
	}

	internal class LogEntry
	{
		public string Message { get; set; }
		public string Severity { get; set; }
		public string Source { get; set; }
		public string Timestamp { get; set; }
	}
}
