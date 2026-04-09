using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace SboxBridge;

/// <summary>
/// Returns current C# compilation errors and warnings from the s&box compiler.
/// </summary>
public class GetCompileErrorsHandler : ICommandHandler
{
	public Task<object> Execute( JsonElement parameters )
	{
		var diagnostics = CompileErrors.Current;

		var errors = new List<object>();
		var warnings = new List<object>();

		if ( diagnostics != null )
		{
			foreach ( var diagnostic in diagnostics )
			{
				var entry = new
				{
					message = diagnostic.Message,
					file = diagnostic.FilePath,
					line = diagnostic.Line,
					column = diagnostic.Column,
					code = diagnostic.Id,
				};

				if ( diagnostic.IsWarning )
					warnings.Add( entry );
				else
					errors.Add( entry );
			}
		}

		return Task.FromResult<object>( new
		{
			hasErrors = errors.Count > 0,
			errorCount = errors.Count,
			warningCount = warnings.Count,
			errors,
			warnings,
		} );
	}
}
