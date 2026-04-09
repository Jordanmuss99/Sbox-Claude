using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Triggers s&box to recompile and hotload all C# scripts.
/// </summary>
public class TriggerHotloadHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		// EditorUtility.RestartCompiler triggers a full recompile + hotload
		EditorUtility.RestartCompiler();

		return Task.FromResult<object>( new
		{
			triggered = true,
			message = "Hotload triggered — scripts are recompiling",
		} );
	}
}
