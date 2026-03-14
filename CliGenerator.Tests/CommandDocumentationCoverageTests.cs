using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CliGenerator.Tests;

[TestClass]
public class CommandDocumentationCoverageTests
{
    [TestMethod]
    public void AllCommandDefClasses_HaveSummaryAndRemarksXmlDocs()
    {
        var repoRoot = FindRepoRoot();
        var cliRoot = Path.Combine(repoRoot, "Console", "Cli");

        var files = Directory
            .EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
            )
            .Where(path =>
                !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
            )
            .ToArray();

        var missing = new List<string>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetRoot();

            var classes = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(IsCommandDefClass)
                .ToArray();

            foreach (var cls in classes)
            {
                var docs = cls.GetLeadingTrivia().ToFullString();
                bool hasSummary = docs.Contains("<summary>", StringComparison.Ordinal);
                bool hasRemarks = docs.Contains("<remarks>", StringComparison.Ordinal);

                if (hasSummary && hasRemarks)
                    continue;

                var line = cls.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                missing.Add(
                    $"{Path.GetRelativePath(repoRoot, file)}:{line} {cls.Identifier.ValueText} summary={hasSummary} remarks={hasRemarks}"
                );
            }
        }

        if (missing.Count == 0)
            return;

        var message = new StringBuilder();
        message.AppendLine(
            "Command classes must have both class-level <summary> and <remarks> XML docs."
        );
        foreach (var item in missing.OrderBy(x => x, StringComparer.Ordinal))
            message.AppendLine(item);

        Assert.Fail(message.ToString());
    }

    private static bool IsCommandDefClass(ClassDeclarationSyntax cls)
    {
        if (cls.BaseList is null)
            return false;

        foreach (var type in cls.BaseList.Types)
        {
            var typeText = type.Type.ToString();
            if (
                typeText is "CommandDef" or "Console.Cli.CommandDef"
                || typeText.EndsWith(".CommandDef", StringComparison.Ordinal)
            )
                return true;
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var marker = Path.Combine(dir.FullName, "maz.slnx");
            if (File.Exists(marker))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root containing maz.slnx."
        );
    }
}
