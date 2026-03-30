using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CliGenerator.Tests;

[TestClass]
public class CompletionTreeGeneratorTests
{
    // Mini program with a RootCommandDef, two services, and manual + generated options.
    private const string Source = """
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

            public interface ICliCompletionProvider
            {
                System.Threading.Tasks.ValueTask<System.Collections.Generic.IEnumerable<string>>
                    GetCompletionsAsync(CliCompletionContext ctx);
            }

            public sealed class CliCompletionContext
            {
                public string WordToComplete => "";
                public CliCompletionContext(string w, object? root) { }
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
                public virtual string? DetailedDescription => null;
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

            // A non-partial option pack annotated with [CliManualOptions]
            [CliManualOptions("--verbose", "--detailed-errors")]
            public class DiagOptionPack : OptionPack
            {
            }

            public enum OutputFormat
            {
                [Description("json")] Json,
                [Description("jsonl")] JsonL,
                [Description("json-pretty")] JsonPretty,
                [Description("column")] Column,
                [Description("text")] Text,
            }

            // A partial option pack with generated options
            public partial class SubOptionPack : OptionPack
            {
                /// <summary>Output format</summary>
                [CliOption("--output", "-o")]
                public partial string? Output { get; }

                /// <summary>The format to use.</summary>
                [CliOption("--format", "-f")]
                public partial OutputFormat? Format { get; }

                /// <summary>Hidden advanced option</summary>
                [CliOption("--secret", Advanced = true)]
                public partial string? Secret { get; }
            }

            public partial class FooListCommandDef : CommandDef
            {
                public override string Name => "list";

                public readonly SubOptionPack Sub = new();

                protected override async System.Threading.Tasks.Task<int> ExecuteAsync(
                    System.Threading.CancellationToken ct) => 0;
            }

            public partial class FooCommandDef : CommandDef
            {
                public override string Name => "foo";
                public readonly FooListCommandDef List = new();
            }

            public partial class BarCommandDef : CommandDef
            {
                public override string Name => "bar";
            }

            public partial class RootCommandDef : CommandDef
            {
                public override string Name => "maz";
                public readonly DiagOptionPack Diag = new();
                public readonly SubOptionPack Sub = new();
                public readonly FooCommandDef Foo = new();
                public readonly BarCommandDef Bar = new();
            }
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

    [TestMethod]
    public void GeneratesCompletionTreeFile()
    {
        var result = CliGeneratorTestHelpers.RunAndGetResult(Source);
        var tree = result.GeneratedTrees.SingleOrDefault(t =>
            t.FilePath.EndsWith("CompletionTree.g.cs")
        );
        Assert.IsNotNull(tree, "CompletionTree.g.cs should be generated");
    }

    [TestMethod]
    public void RootNodeContainsAllTopLevelCommands()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        // The root node's children should include "foo" and "bar"
        Assert.IsTrue(text.Contains("\"foo\""), "Root children should contain 'foo'");
        Assert.IsTrue(text.Contains("\"bar\""), "Root children should contain 'bar'");
    }

    [TestMethod]
    public void ChildNodeContainsSubcommands()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        // FooCommandDef has a "list" child
        Assert.IsTrue(text.Contains("\"list\""), "Foo node should contain 'list' subcommand");
    }

    [TestMethod]
    public void AdvancedOptionsAreExcluded()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        Assert.IsFalse(
            text.Contains("\"--secret\""),
            "Advanced options should not appear in the completion tree"
        );
    }

    [TestMethod]
    public void ManualOptionsFromAttributeAreIncluded()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        Assert.IsTrue(
            text.Contains("\"--verbose\""),
            "[CliManualOptions] aliases should be in the tree"
        );
        Assert.IsTrue(
            text.Contains("\"--detailed-errors\""),
            "[CliManualOptions] aliases should be in the tree"
        );
    }

    [TestMethod]
    public void GeneratedOptionsFromPartialPackAreIncluded()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        // SubOptionPack has "--output" / "-o"
        Assert.IsTrue(
            text.Contains("\"--output\""),
            "Generated options from partial OptionPack should be included"
        );
        Assert.IsTrue(
            text.Contains("\"-o\""),
            "Extra aliases from partial OptionPack should be included"
        );
    }

    [TestMethod]
    public void DynamicProvidersMapIsGenerated()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        // DynamicProviders dictionary should be emitted
        Assert.IsTrue(
            text.Contains("DynamicProviders"),
            "DynamicProviders field should be emitted"
        );
        Assert.IsTrue(
            text.Contains("IReadOnlyDictionary"),
            "DynamicProviders should use IReadOnlyDictionary"
        );
    }

    [TestMethod]
    public void StaticValueProvidersMapIsGenerated()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        Assert.IsTrue(
            text.Contains("StaticValueProviders"),
            "StaticValueProviders field should be emitted"
        );
    }

    [TestMethod]
    public void StaticValueProvidersContainsEnumOptionAliases()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        // --format and -f are aliases for the enum-typed option
        Assert.IsTrue(
            text.Contains("[\"--format\"]"),
            "StaticValueProviders should contain --format alias"
        );
        Assert.IsTrue(text.Contains("[\"-f\"]"), "StaticValueProviders should contain -f alias");
    }

    [TestMethod]
    public void StaticValueProvidersContainsAllEnumValues()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        // All enum Description values should appear as completion values
        Assert.IsTrue(text.Contains("\"json\""), "Should contain 'json' enum value");
        Assert.IsTrue(text.Contains("\"jsonl\""), "Should contain 'jsonl' enum value");
        Assert.IsTrue(text.Contains("\"json-pretty\""), "Should contain 'json-pretty' enum value");
        Assert.IsTrue(text.Contains("\"column\""), "Should contain 'column' enum value");
        Assert.IsTrue(text.Contains("\"text\""), "Should contain 'text' enum value");
    }

    [TestMethod]
    public void StaticValueProvidersExcludesNonEnumOptions()
    {
        var text = CliGeneratorTestHelpers.GetGeneratedText(
            CliGeneratorTestHelpers.RunAndGetResult(Source),
            "CompletionTree.g.cs"
        );

        // --output is a string option, not an enum — should not be in StaticValueProviders
        Assert.IsFalse(
            text.Contains("[\"--output\"]"),
            "Non-enum options should not appear in StaticValueProviders"
        );
    }
}
