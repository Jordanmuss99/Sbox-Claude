using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Returns information about the current s&box project:
/// path, name, title, type, dependencies, and configuration.
/// </summary>
public class GetProjectInfoHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var project = Project.Current;

		var info = new
		{
			path = project?.GetRootPath(),
			name = project?.Config?.PackageIdent,
			title = project?.Config?.Title,
			description = project?.Config?.Description,
			type = project?.Config?.Type.ToString(),
			compiler = new
			{
				defines = project?.Config?.Defines,
			},
			packageReferences = project?.Config?.PackageReferences,
		};

		return Task.FromResult<object>( info );
	}
}
