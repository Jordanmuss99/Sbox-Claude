using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Editor;

namespace SboxClaude;

/// <summary>
/// Command: undo
///
/// Performs an editor undo operation — safety net for when Claude makes a bad change.
///
/// Returns:
///   { success: bool, description: string? }
///   description is populated if the s&box API exposes what was undone.
///
/// API NOTE: Verify the exact undo API against your SDK version.
/// Candidates:
///   Option A — EditorScene.Undo()
///   Option B — Undo.PerformUndo()
///   Option C — EditorUtility.Undo()
/// </summary>
public sealed class UndoHandler : IToolHandler
{
    public string Command => "undo";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        // API NOTE: replace with the correct call.
        // Option A: EditorScene.Undo();
        // Option B: Undo.PerformUndo();
        //
        // If the API returns a description of what was undone, capture it:
        // var description = EditorScene.Undo();
        // return Task.FromResult<object>(new { success = true, description });

        // Placeholder — remove once real API is wired
        Log.Info("[Claude Bridge] undo called (API stub — wire up real undo call)");

        return Task.FromResult<object>(new
        {
            success     = true,
            description = (string?)null, // populate from real API if available
        });
    }
}

/// <summary>
/// Command: redo
///
/// Performs an editor redo operation.
///
/// Returns:
///   { success: bool, description: string? }
/// </summary>
public sealed class RedoHandler : IToolHandler
{
    public string Command => "redo";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        // API NOTE: replace with the correct call.
        // Option A: EditorScene.Redo();
        // Option B: Undo.PerformRedo();

        Log.Info("[Claude Bridge] redo called (API stub — wire up real redo call)");

        return Task.FromResult<object>(new
        {
            success     = true,
            description = (string?)null,
        });
    }
}
