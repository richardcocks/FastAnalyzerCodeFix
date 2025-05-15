using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;

namespace FastAnalyzerCodeFix;

public static class Program
{
	public static async Task Main(string[] args)
	{
		if (args.Length < 2)
		{
			Console.WriteLine("Usage: <solution-path> <diagnostic-id1,diagnostic-id2,...>");
			return;
		}
		
		using var cts = new CancellationTokenSource();
		var cancellationToken = cts.Token;

		Console.CancelKeyPress += (sender, e) =>
		{
			Console.WriteLine("Cancellation requested by user...");
			e.Cancel = true; // Prevent immediate termination
			cts.Cancel();    // Actively signal cancellation
		};
		

		string solutionPath = args[0];
		string[] diagnosticIds = args[1].Split(',');

		MSBuildLocator.RegisterDefaults();
		var workspace = MSBuildWorkspace.Create();

		Console.WriteLine("Loading solution...");
		var solution = await workspace.OpenSolutionAsync(solutionPath);
		await CodeFixApplier.ApplyFixesPerProjectAsync(solution, diagnosticIds, cts.Token);
		Console.WriteLine("Done.");
	}
}