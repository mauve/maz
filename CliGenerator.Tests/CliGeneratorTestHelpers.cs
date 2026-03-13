using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CliGenerator.Tests;

internal static class CliGeneratorTestHelpers
{
    internal const string Prelude = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.CommandLine;
        using System.CommandLine.Completions;

        namespace Console.Cli
        {
            public static class AdvancedOptionRegistry
            {
                public static void Register<T>(Option<T> _) { }
            }

            public static class CliCompletionProviderBridge
            {
                public static IEnumerable<CompletionItem> GetCompletions<T>(CompletionContext _) => Array.Empty<CompletionItem>();
            }

            public abstract partial class CommandDef
            {
                protected bool HasParseResult => false;
                protected T GetValue<T>(Option<T> _) => default!;
                protected abstract void AddGeneratedOptions(Command cmd);
                protected virtual bool HasGeneratedChildren => false;
                protected virtual void AddGeneratedChildren(Command cmd) { }
                public virtual string Description => string.Empty;
                protected virtual string? Remarks => null;
                public Command Build() => new("test");
            }

            public abstract partial class OptionPack
            {
                protected bool HasParseResult => false;
                protected T GetValue<T>(Option<T> _) => default!;
                protected abstract void AddGeneratedOptions(Command cmd);
                protected virtual void AddChildPacksTo(Command cmd) { }

                public void AddOptionsTo(Command cmd)
                {
                    AddGeneratedOptions(cmd);
                    AddChildPacksTo(cmd);
                }
            }

            public interface ICompletionProvider
            {
                IEnumerable<CompletionItem> GetCompletions(CompletionContext context);
            }
        }
        """;

    internal static string ComposeSource(string body) => Prelude + "\n" + body;

    internal static string InTestApp(string body) =>
        ComposeSource(
            $$"""
            namespace TestApp
            {
                using Console.Cli;
            {{body}}
            }
            """
        );

    internal static string InTestApp(string body, string additionalUsings) =>
        ComposeSource(
            $$"""
            namespace TestApp
            {
                using Console.Cli;
            {{additionalUsings}}
            {{body}}
            }
            """
        );

    internal static GeneratorDriver RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = AppDomain
            .CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(
                MetadataReference.CreateFromFile(
                    typeof(System.CommandLine.Option<>).Assembly.Location
                )
            )
            .ToList();

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new CliOptionGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
    }

    internal static GeneratorDriverRunResult RunAndGetResult(string source)
    {
        var result = RunGenerator(source).GetRunResult();
        Assert.AreEqual(0, result.Diagnostics.Length, "Generator should produce no diagnostics");
        return result;
    }

    internal static string GetGeneratedText(GeneratorDriverRunResult result, string fileSuffix)
    {
        var tree = result.GeneratedTrees.SingleOrDefault(t => t.FilePath.EndsWith(fileSuffix));
        Assert.IsNotNull(tree, $"Expected generated file ending with '{fileSuffix}'");
        return tree!.GetText().ToString();
    }

    internal static string GenerateFor(string source, string fileSuffix) =>
        GetGeneratedText(RunAndGetResult(source), fileSuffix);

    internal static string GenerateForBody(string fileSuffix, string body) =>
        GenerateFor(InTestApp(body), fileSuffix);

    internal static void AssertContainsAll(string text, params string[] fragments)
    {
        foreach (var fragment in fragments)
            Assert.IsTrue(
                text.Contains(fragment),
                $"Expected generated text to contain: {fragment}"
            );
    }

    internal static void AssertContainsInOrder(string text, params string[] fragments)
    {
        var currentIndex = 0;
        foreach (var fragment in fragments)
        {
            var nextIndex = text.IndexOf(fragment, currentIndex, StringComparison.Ordinal);
            Assert.IsTrue(
                nextIndex >= 0,
                $"Expected generated text to contain in order: {fragment}"
            );
            currentIndex = nextIndex + fragment.Length;
        }
    }
}
