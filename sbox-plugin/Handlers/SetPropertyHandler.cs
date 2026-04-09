using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxClaude;

/// <summary>
/// Command: set_property
///
/// Write a single [Property]-annotated field on a component.
/// This is the write counterpart to get_all_properties.
///
/// Params:
///   guid            string  GUID of the target GameObject. Required.
///   component_type  string  Type name of the component. Required.
///   property_name   string  Name of the [Property] field to set. Required.
///   value           any     New value. Must be JSON-compatible with the property's CLR type.
///
/// Returns:
///   { guid, component, property, new_value }
/// </summary>
public sealed class SetPropertyHandler : IToolHandler
{
    public string Command => "set_property";

    public Task<object> ExecuteAsync(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("guid", out var guidEl) ||
            !Guid.TryParse(guidEl.GetString(), out var guid))
            throw new ArgumentException("Parameter 'guid' is missing or not a valid GUID.");

        if (!parameters.TryGetProperty("component_type", out var typeEl) ||
            string.IsNullOrWhiteSpace(typeEl.GetString()))
            throw new ArgumentException("Parameter 'component_type' is required.");

        if (!parameters.TryGetProperty("property_name", out var propNameEl) ||
            string.IsNullOrWhiteSpace(propNameEl.GetString()))
            throw new ArgumentException("Parameter 'property_name' is required.");

        if (!parameters.TryGetProperty("value", out var valueEl))
            throw new ArgumentException("Parameter 'value' is required.");

        var typeName = typeEl.GetString()!;
        var propertyName = propNameEl.GetString()!;

        var scene = Scene.Active
            ?? throw new InvalidOperationException("No active scene.");

        var go = scene.GetAllObjects(false).FirstOrDefault(o => o.Id == guid)
            ?? throw new ArgumentException($"GameObject not found: {guid}");

        var component = go.Components.GetAll()
                          .FirstOrDefault(c => c.GetType().Name == typeName)
            ?? throw new ArgumentException(
                $"Component '{typeName}' not found on '{go.Name}'.");

        var typeDesc = TypeLibrary.GetType(component.GetType())
            ?? throw new InvalidOperationException(
                $"TypeLibrary has no entry for '{typeName}'.");

        var propDesc = typeDesc.Properties
            .Where(p => p.HasAttribute<PropertyAttribute>())
            .FirstOrDefault(p => p.Name == propertyName)
            ?? throw new ArgumentException(
                $"[Property] '{propertyName}' not found on '{typeName}'. " +
                "Call get_all_properties to list available properties.");

        // Deserialise the JSON value to the declared CLR type
        var clrType = propDesc.PropertyType
            ?? throw new InvalidOperationException(
                $"Cannot determine CLR type for property '{propertyName}'.");

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

        // Read back to confirm
        object? readBack = null;
        try { readBack = propDesc.GetValue(component); } catch { /* ignore */ }

        return Task.FromResult<object>(new
        {
            guid      = go.Id.ToString(),
            component = typeName,
            property  = propertyName,
            new_value = readBack,
        });
    }
}
