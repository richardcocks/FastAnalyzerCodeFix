using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;

namespace FastAnalyzerCodeFix;

public static class Program
{
	public static async Task Main()
	{
		string[] args = [@"C:\Users\Rich\Documents\code\ExampleTestProject\ExampleTestProject.sln", "NUnit2005", "NUnit2007"];
		if (args.Length < 2)
		{
			Console.WriteLine("Usage: <solution-path> <diagnostic-id1,diagnostic-id2,...>");
			return;
		}

		string solutionPath = args[0];
		string[] diagnosticIds = args[1].Split(',');

		MSBuildLocator.RegisterDefaults();
		var workspace = MSBuildWorkspace.Create();

		Console.WriteLine("Loading solution...");
		var solution = await workspace.OpenSolutionAsync(solutionPath);
		var fixedSolution = await CodeFixApplier.ApplyAllDiagnosticsFixesAsync(solution, diagnosticIds, 10, CancellationToken.None);
		workspace.TryApplyChanges(fixedSolution);

		Console.WriteLine("Done.");
	}
}