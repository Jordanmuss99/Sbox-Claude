using System;
using System.IO;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Editor;

namespace SboxClaude;

/// <summary>
/// Command: take_screenshot
///
/// Captures the current editor viewport to a PNG file.
///
/// Params:
///   path  string  Destination file path relative to the project root.
///                 Default: "screenshots/screenshot_{timestamp}.png"
///
/// Returns:
///   { path, width, height, timestamp }
///
/// API NOTE: Verify the exact render-to-texture API against your s&box SDK.
/// Candidates:
///   Option A — EditorScene.Camera.RenderToTexture(width, height)
///   Option B — Sandbox.Texture.Capture(camera, width, height)
///   Option C — Gizmo.Camera.Render(Rect) from the Editor namespace
///
/// The stub below shows the intended flow; replace the render call with
/// whichever API compiles in your SDK version.
/// </summary>
public sealed class TakeScreenshotHandler : IToolHandler
{
    public string Command => "take_screenshot";

    public async Task<object> ExecuteAsync(JsonElement parameters)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var path = parameters.TryGetProperty("path", out var pathEl) && !string.IsNullOrWhiteSpace(pathEl.GetString())
            ? pathEl.GetString()!
            : Path.Combine("screenshots", $"screenshot_{timestamp}.png");

        // Ensure the directory exists
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        int width  = 1920;
        int height = 1080;

        // ── Render ────────────────────────────────────────────────────────
        // API NOTE: replace with the correct s&box call.
        // Option A (most likely):
        //   var tex = EditorScene.Camera.RenderToTexture(width, height);
        //   await File.WriteAllBytesAsync(path, tex.GetPixels().ToPng(width, height));
        //
        // Option B:
        //   var tex = Sandbox.Texture.Capture(EditorScene.Camera, width, height);
        //   await File.WriteAllBytesAsync(path, tex.ToPng());
        //
        // Placeholder: write a 1×1 white PNG header so the path is a valid file
        // even before the real render call is wired up.
        await File.WriteAllBytesAsync(path, PlaceholderPng());
        // ─────────────────────────────────────────────────────────────────

        Log.Info($"[Claude Bridge] Screenshot saved: {path}");

        return new
        {
            path      = Path.GetFullPath(path),
            width,
            height,
            timestamp,
        };
    }

    // Returns a minimal valid 1×1 white PNG as a placeholder
    private static byte[] PlaceholderPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAD" +
        "hQGAWjR9awAAAABJRU5ErkJggg==");
}
