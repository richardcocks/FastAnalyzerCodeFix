using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

public static class CodeFixApplier
{
    public static async Task<Solution> ApplyCodeFixesAsync(
        Solution solution,
        IEnumerable<string> targetDiagnosticIds,
        CancellationToken cancellationToken)
    {
        var diagnosticIdSet = targetDiagnosticIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSolution = solution;

        foreach (var project in solution.Projects)
        {
            var analyzers = project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(project.Language))
                .ToImmutableArray();

            if (!analyzers.Any())
            {
                Console.WriteLine("No analyzers found.");
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken) ?? throw new NullReferenceException();
            var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);
            var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
            Console.WriteLine(string.Join(Environment.NewLine, diagnostics.Select(d => d.Id)));

            var matchingDiagnostics = diagnostics
                .Where(d => diagnosticIdSet.Contains(d.Id))
                .ToList();

            Console.WriteLine(string.Join(Environment.NewLine, matchingDiagnostics.Select(d => d.Id)));
            
            if (!matchingDiagnostics.Any()) continue;

            // Load CodeFixProviders from AnalyzerReferences
            var codeFixProviders = LoadCodeFixProviders(project.AnalyzerReferences);
            
            
            foreach (var document in project.Documents)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                var docDiagnostics = matchingDiagnostics
                    .Where(d => d.Location.IsInSource && d.Location.SourceTree == syntaxTree)
                    .ToList();

                if (!docDiagnostics.Any()) continue;

                var currentDocument = document;

                foreach (var diagnostic in docDiagnostics)
                {
                    foreach (var provider in codeFixProviders)
                    {
                        if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
                            continue;

                        var actions = new List<CodeAction>();
                        var context = new CodeFixContext(
                            currentDocument,
                            diagnostic,
                            (a, _) => actions.Add(a),
                            cancellationToken
                        );

                        await provider.RegisterCodeFixesAsync(context);

                        var action = actions.FirstOrDefault();
                        if (action == null) continue;

                        var operations = await action.GetOperationsAsync(cancellationToken);
                        foreach (var op in operations)
                        {
                            if (op is ApplyChangesOperation applyChange)
                            {
                                newSolution = applyChange.ChangedSolution;
                                currentDocument = newSolution.GetDocument(currentDocument.Id);
                                break;
                            }
                        }

                        break; // Apply only one fix per diagnostic
                    }
                }
            }
        }

        return newSolution;
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
                    .Where(t => typeof(CodeFixProvider).IsAssignableFrom(t) && !t.IsAbstract)
                    .Select(t => (CodeFixProvider)Activator.CreateInstance(t))
                    .ToList();

                providers.AddRange(fixProviders);
            }
            catch
            {
                // Ignore invalid/unloadable assemblies
            }
        }

        return providers;
    }
}
