using System.Text.Json;
using System.Threading.Tasks;

namespace SboxBridge;

/// <summary>
/// Interface for all Bridge command handlers.
/// Each command handler processes a specific MCP tool request.
/// </summary>
public interface ICommandHandler
{
	/// <summary>
	/// Execute the command with the given parameters.
	/// </summary>
	/// <param name="parameters">JSON parameters from the MCP request.</param>
	/// <returns>Result object that will be serialized to JSON.</returns>
	Task<object> Execute( JsonElement parameters );
}
