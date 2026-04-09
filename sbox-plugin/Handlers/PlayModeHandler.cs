using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Editor;

namespace SboxClaude;

// ── Shared state helper ───────────────────────────────────────────────────────

/// <summary>
/// Reads the current editor play-mode state as a canonical string.
///
/// API NOTE: Verify these property names against your s&box SDK version.
/// Candidates:
///   EditorScene.IsPlaying  — true when in play mode
///   EditorScene.IsPaused   — true when paused inside play mode
/// If the API differs (e.g. uses an enum), update GetState() accordingly.
/// </summary>
internal static class PlayModeHelpers
{
    internal static string GetState()
    {
        if (!EditorScene.IsPlaying) return "stopped";
        // EditorScene.IsPaused may not exist in all SDK versions;
        // wrap in try-catch or remove if unavailable.
        try
        {
            if (EditorScene.IsPaused) return "paused";
        }
        catch { /* IsPaused not available; treat as not-paused */ }
        return "playing";
    }

    internal static object StateResult(string? message = null)
    {
        var state = GetState();
        return message is null
            ? (object)new { state }
            : new { state, message };
    }
}

// ── Task 1 handlers ───────────────────────────────────────────────────────────

/// <summary>
/// Command: start_play
/// Enters play mode in the s&box editor.
/// Returns { state } — one of "playing" | "paused" | "stopped".
/// </summary>
public sealed class StartPlayHandler : IToolHandler
{
    public string Command => "start_play";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        if (EditorScene.IsPlaying)
            return Task.FromResult(PlayModeHelpers.StateResult("Already in play mode."));

        // API NOTE: verify exact method name — may be EditorScene.StartPlay()
        // or EditorScene.PlayGame() depending on SDK version.
        EditorScene.Play();
        return Task.FromResult(PlayModeHelpers.StateResult());
    }
}

/// <summary>
/// Command: stop_play
/// Exits play mode and returns the editor to edit mode.
/// Returns { state }.
/// </summary>
public sealed class StopPlayHandler : IToolHandler
{
    public string Command => "stop_play";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        if (!EditorScene.IsPlaying)
            return Task.FromResult(PlayModeHelpers.StateResult("Already stopped."));

        EditorScene.Stop();
        return Task.FromResult(PlayModeHelpers.StateResult());
    }
}

/// <summary>
/// Command: is_playing
/// Returns the current play-mode state without changing it.
/// Returns { state }.
/// </summary>
public sealed class IsPlayingHandler : IToolHandler
{
    public string Command => "is_playing";

    public Task<object> ExecuteAsync(JsonElement parameters)
        => Task.FromResult(PlayModeHelpers.StateResult());
}

// ── Task 2 handlers ───────────────────────────────────────────────────────────

/// <summary>
/// Command: pause_play
/// Pauses a running play-mode session. No-op if already paused or stopped.
/// Returns { state }.
/// </summary>
public sealed class PausePlayHandler : IToolHandler
{
    public string Command => "pause_play";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        if (!EditorScene.IsPlaying)
            return Task.FromResult(PlayModeHelpers.StateResult("Not in play mode."));

        // API NOTE: verify — may be EditorScene.PauseGame() or similar
        EditorScene.Pause();
        return Task.FromResult(PlayModeHelpers.StateResult());
    }
}

/// <summary>
/// Command: resume_play
/// Resumes a paused play-mode session. No-op if not paused.
/// Returns { state }.
/// </summary>
public sealed class ResumePlayHandler : IToolHandler
{
    public string Command => "resume_play";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        if (!EditorScene.IsPlaying)
            return Task.FromResult(PlayModeHelpers.StateResult("Not in play mode."));

        // API NOTE: verify — may be EditorScene.ResumeGame() or similar
        EditorScene.Resume();
        return Task.FromResult(PlayModeHelpers.StateResult());
    }
}
