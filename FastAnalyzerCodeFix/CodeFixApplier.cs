using System.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis.MSBuild;


public static class CodeFixApplier
{
    public static async Task ApplyFixesPerProjectAsync(
        Solution solution,
        IEnumerable<string> diagnosticIds,
        CancellationToken cancellationToken = default)

    {
        var idSet = diagnosticIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Cancellation requested...");
            e.Cancel = true; // Prevent process termination
            cancellationToken.ThrowIfCancellationRequested();
        };

        foreach (var project in solution.Projects)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            Console.WriteLine($"Processing project: {project.Name}");

            var updatedProject = await ApplyFixesToProjectAsync(project, idSet, cancellationToken);
            if (updatedProject != null && !updatedProject.Solution.Workspace.TryApplyChanges(updatedProject.Solution))
            {
                Console.WriteLine($"Failed to apply changes for {project.Name}");
            }
        }
    }
private static async Task<Project?> ApplyFixesToProjectAsync(
    Project project,
    HashSet<string> diagnosticIds,
    CancellationToken cancellationToken)
{
    var analyzers = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language)).ToImmutableArray();
    if (!analyzers.Any()) return project;

    var compilation = await project.GetCompilationAsync(cancellationToken);
    var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);

    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
    var relevantDiagnostics = diagnostics
        .Where(d => diagnosticIds.Contains(d.Id) && d.Location.IsInSource)
        .OrderBy(d => d.Location.SourceSpan.Start)
        .ToList();

    if (!relevantDiagnostics.Any()) return project;

    var providers = LoadCodeFixProviders(project.AnalyzerReferences);

    var currentSolution = project.Solution;
    bool fixApplied = false;

    foreach (var diagnostic in relevantDiagnostics)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var doc = currentSolution.GetDocument(diagnostic.Location.SourceTree);
        if (doc == null) continue;

        foreach (var provider in providers)
        {
            if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id)) continue;

            var actions = new List<CodeAction>();
            var context = new CodeFixContext(
                doc,
                diagnostic,
                (a, _) => actions.Add(a),
                cancellationToken);

            await provider.RegisterCodeFixesAsync(context);

            var action = actions.FirstOrDefault();
            if (action == null) continue;

            var operations = await action.GetOperationsAsync(cancellationToken);
            foreach (var op in operations)
            {
                if (op is ApplyChangesOperation applyChange)
                {
                    currentSolution = applyChange.ChangedSolution;
                    fixApplied = true;
                    break;
                }
            }

            break; // Only one provider per diagnostic
        }
    }

    return fixApplied ? currentSolution.GetProject(project.Id) : project;
}

private static List<CodeFixProvider> LoadCodeFixProviders(IEnumerable<AnalyzerReference> analyzerReferences)
{
    var providers = new List<CodeFixProvider>();

    foreach (var reference in analyzerReferences.OfType<AnalyzerFileReference>())
    {
        try
        {
            var assembly = Assembly.LoadFrom(reference.FullPath);

            var fixProviders = assembly.GetTypes()
                .Where(t => typeof(CodeFixProvider).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (CodeFixProvider)Activator.CreateInstance(t)!)
                .ToList();

            providers.AddRange(fixProviders);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load CodeFixProvider from {reference.FullPath}: {ex.Message}");
        }
    }

    return providers;
}

}
