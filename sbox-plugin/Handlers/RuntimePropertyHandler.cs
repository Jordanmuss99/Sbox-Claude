using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Editor;

namespace SboxClaude;

// ── Shared helper ─────────────────────────────────────────────────────────────

internal static class RuntimePropertyHelpers
{
    internal static void AssertPlayMode()
    {
        if (!EditorScene.IsPlaying)
            throw new InvalidOperationException(
                "This tool only works in play mode. " +
                "Call start_play first, then check is_playing to confirm.");
    }
}

// ── Task 10: get_runtime_property ─────────────────────────────────────────────

/// <summary>
/// Command: get_runtime_property
///
/// Reads a single [Property]-annotated field from a component while the game
/// is running. Throws if not in play mode.
///
/// Params:
///   guid            string  GUID of the target GameObject.
///   component_type  string  Component type name.
///   property_name   string  [Property] field name.
///
/// Returns:
///   { guid, component, property, value, state }
/// </summary>
public sealed class GetRuntimePropertyHandler : IToolHandler
{
    public string Command => "get_runtime_property";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        RuntimePropertyHelpers.AssertPlayMode();

        if (!parameters.TryGetProperty("guid", out var guidEl) ||
            !Guid.TryParse(guidEl.GetString(), out var guid))
            throw new ArgumentException("Parameter 'guid' is missing or not a valid GUID.");

        if (!parameters.TryGetProperty("component_type", out var typeEl) ||
            string.IsNullOrWhiteSpace(typeEl.GetString()))
            throw new ArgumentException("Parameter 'component_type' is required.");

        if (!parameters.TryGetProperty("property_name", out var propEl) ||
            string.IsNullOrWhiteSpace(propEl.GetString()))
            throw new ArgumentException("Parameter 'property_name' is required.");

        var typeName     = typeEl.GetString()!;
        var propertyName = propEl.GetString()!;

        var scene = Scene.Active
            ?? throw new InvalidOperationException("No active scene.");

        var go = scene.GetAllObjects(false).FirstOrDefault(o => o.Id == guid)
            ?? throw new ArgumentException($"GameObject not found: {guid}");

        var component = go.Components.GetAll()
                          .FirstOrDefault(c => c.GetType().Name == typeName)
            ?? throw new ArgumentException($"Component '{typeName}' not found on '{go.Name}'.");

        var typeDesc = TypeLibrary.GetType(component.GetType())
            ?? throw new InvalidOperationException($"TypeLibrary has no entry for '{typeName}'.");

        var propDesc = typeDesc.Properties
            .Where(p => p.HasAttribute<PropertyAttribute>())
            .FirstOrDefault(p => p.Name == propertyName)
            ?? throw new ArgumentException(
                $"[Property] '{propertyName}' not found on '{typeName}'.");

        object? value = null;
        try { value = propDesc.GetValue(component); } catch { /* leave null */ }

        return Task.FromResult<object>(new
        {
            guid      = go.Id.ToString(),
            component = typeName,
            property  = propertyName,
            value,
            state     = PlayModeHelpers.GetState(),
        });
    }
}

// ── Task 10: set_runtime_property ─────────────────────────────────────────────

/// <summary>
/// Command: set_runtime_property
///
/// Writes a [Property]-annotated field on a live component during play mode.
/// Changes are lost when play mode ends — they do not affect the saved scene.
/// Throws if not in play mode.
///
/// Params:
///   guid            string  GUID of the target GameObject.
///   component_type  string  Component type name.
///   property_name   string  [Property] field name.
///   value           any     New value, JSON-compatible with the CLR type.
///
/// Returns:
///   { guid, component, property, new_value, state }
/// </summary>
public sealed class SetRuntimePropertyHandler : IToolHandler
{
    public string Command => "set_runtime_property";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        RuntimePropertyHelpers.AssertPlayMode();

        if (!parameters.TryGetProperty("guid", out var guidEl) ||
            !Guid.TryParse(guidEl.GetString(), out var guid))
            throw new ArgumentException("Parameter 'guid' is missing or not a valid GUID.");

        if (!parameters.TryGetProperty("component_type", out var typeEl) ||
            string.IsNullOrWhiteSpace(typeEl.GetString()))
            throw new ArgumentException("Parameter 'component_type' is required.");

        if (!parameters.TryGetProperty("property_name", out var propEl) ||
            string.IsNullOrWhiteSpace(propEl.GetString()))
            throw new ArgumentException("Parameter 'property_name' is required.");

        if (!parameters.TryGetProperty("value", out var valueEl))
            throw new ArgumentException("Parameter 'value' is required.");

        var typeName     = typeEl.GetString()!;
        var propertyName = propEl.GetString()!;

        var scene = Scene.Active
            ?? throw new InvalidOperationException("No active scene.");

        var go = scene.GetAllObjects(false).FirstOrDefault(o => o.Id == guid)
            ?? throw new ArgumentException($"GameObject not found: {guid}");

        var component = go.Components.GetAll()
                          .FirstOrDefault(c => c.GetType().Name == typeName)
            ?? throw new ArgumentException($"Component '{typeName}' not found on '{go.Name}'.");

        var typeDesc = TypeLibrary.GetType(component.GetType())
            ?? throw new InvalidOperationException($"TypeLibrary has no entry for '{typeName}'.");

        var propDesc = typeDesc.Properties
            .Where(p => p.HasAttribute<PropertyAttribute>())
            .FirstOrDefault(p => p.Name == propertyName)
            ?? throw new ArgumentException(
                $"[Property] '{propertyName}' not found on '{typeName}'. " +
                "Use get_all_properties to list available properties.");

        var clrType = propDesc.PropertyType
            ?? throw new InvalidOperationException(
                $"Cannot determine CLR type for '{propertyName}'.");

        object? newValue;
        try
        {
            newValue = JsonSerializer.Deserialize(
                valueEl.GetRawText(),
                clrType,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Cannot deserialise value for '{propertyName}' " +
                $"(expected {clrType.Name}): {ex.Message}");
        }

        propDesc.SetValue(component, newValue);

        object? readBack = null;
        try { readBack = propDesc.GetValue(component); } catch { /* leave null */ }

        return Task.FromResult<object>(new
        {
            guid      = go.Id.ToString(),
            component = typeName,
            property  = propertyName,
            new_value = readBack,
            state     = PlayModeHelpers.GetState(),
        });
    }
}
