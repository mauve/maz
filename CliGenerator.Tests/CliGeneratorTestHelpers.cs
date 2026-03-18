using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CliGenerator.Tests;

internal static class CliGeneratorTestHelpers
{
    internal const string Prelude = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;

        namespace Console.Cli
        {
            public static class AdvancedOptionRegistry
            {
                public static void Register(Parsing.CliOption _) { }
            }

            public static class CliCompletionProviderRegistry
            {
                public static void Register(string[] aliases, System.Type providerType) { }
                public static void Register(string[] aliases, string[] values) { }
            }

            public abstract partial class CommandDef
            {
                public abstract string Name { get; }
                protected bool HasParseResult => false;
                protected T GetValue<T>(Parsing.CliOption<T> _) => default!;
                internal virtual IEnumerable<Parsing.CliOption> EnumerateOptions() { yield break; }
                internal virtual IEnumerable<CommandDef> EnumerateChildren() { yield break; }
                internal virtual IEnumerable<OptionPack> EnumerateOptionPacks() { yield break; }
                protected virtual bool HasGeneratedChildren => false;
                public virtual string Description => string.Empty;
                public virtual string? DetailedDescription => Remarks;
                protected virtual string? Remarks => null;
            }

            public abstract partial class OptionPack
            {
                protected bool HasParseResult => false;
                protected T GetValue<T>(Parsing.CliOption<T> _) => default!;
                internal virtual IEnumerable<Parsing.CliOption> EnumerateOptions() { yield break; }
                internal virtual IEnumerable<OptionPack> EnumerateChildPacks() { yield break; }

                internal IEnumerable<Parsing.CliOption> EnumerateAllOptions()
                {
                    foreach (var child in EnumerateChildPacks())
                        foreach (var opt in child.EnumerateAllOptions())
                            yield return opt;
                    foreach (var opt in EnumerateOptions())
                        yield return opt;
                }
            }

            public interface ICompletionProvider { }
        }

        namespace Console.Cli.Parsing
        {
            public abstract class CliOption
            {
                public string Name { get; init; }
                public string[] Aliases { get; init; }
                public string Description { get; init; }
                public bool Required { get; init; }
                public bool Hidden { get; init; }
                public bool Recursive { get; init; }
                public bool IsAdvanced { get; init; }
                public bool AllowMultipleArgumentsPerToken { get; init; }
                public bool ValueIsOptional { get; init; }
                public bool Stackable { get; init; }
                public bool WasProvided { get; set; }
                public object HelpGroup { get; set; }
                public object Metadata { get; init; }
            }

            public sealed class CliOption<T> : CliOption
            {
                public T Value { get; set; }
                public T DefaultValue { get; init; }
                public Func<string, T> Parser { get; init; }
                public Func<string, object> ElementParser { get; init; }
                public Func<T> DefaultValueFactory { get; init; }
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
