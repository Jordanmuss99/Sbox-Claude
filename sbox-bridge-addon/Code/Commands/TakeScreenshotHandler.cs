using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Captures the current editor viewport as a PNG screenshot.
///
/// API-NOTE: The exact screenshot API needs verification against the s&box SDK.
/// Candidate approaches (try in order):
///   1. EditorScene.Camera.TakeScreenshot( path )
///   2. EditorScene.Camera.RenderToTexture() → save to file
///   3. Graphics.RenderToTexture() → Texture.Save()
/// Currently uses approach 1 with fallback placeholder.
/// </summary>
public class TakeScreenshotHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var projectRoot = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new Exception( "No project is currently open" );

		// Ensure trailing separator for safe StartsWith check
		if ( !projectRoot.EndsWith( Path.DirectorySeparatorChar ) )
			projectRoot += Path.DirectorySeparatorChar;

		var timestamp = DateTime.UtcNow.ToString( "yyyyMMdd_HHmmss" );
		var defaultPath = $"screenshots/screenshot_{timestamp}.png";

		var relativePath = parameters.TryGetProperty( "path", out var pathProp )
			? pathProp.GetString() ?? defaultPath
			: defaultPath;

		var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );
		if ( !fullPath.StartsWith( projectRoot ) )
			throw new Exception( "Path must be within the project directory" );

		var dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );

		// Attempt to capture screenshot via s&box API
		try
		{
			// API-NOTE: Try EditorScene.Camera.TakeScreenshot first
			// If this doesn't compile, try the other approaches listed above
			var camera = EditorScene.Active?.Camera;
			if ( camera == null )
				throw new Exception( "No active camera in the editor scene" );

			// Render and save
			var texture = camera.RenderToTexture();
			texture.Save( fullPath );
		}
		catch ( Exception )
		{
			// Fallback: create a placeholder file indicating screenshot was requested
			// This path is hit when running without the real s&box SDK
			File.WriteAllText( fullPath + ".txt",
				$"Screenshot requested at {DateTime.UtcNow:o}\nPath: {relativePath}\n" +
				"Note: Actual screenshot capture requires the s&box editor to be running.\n" +
				"Wire up one of the candidate APIs in TakeScreenshotHandler.cs." );

			return Task.FromResult<object>( new
			{
				path = relativePath,
				captured = false,
				placeholder = true,
				message = "Screenshot API needs wiring — placeholder created. See TakeScreenshotHandler.cs for candidate APIs.",
			} );
		}

		return Task.FromResult<object>( new
		{
			path = relativePath,
			captured = true,
		} );
	}
}
