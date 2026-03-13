using static CliGenerator.Tests.CliGeneratorTestHelpers;

namespace CliGenerator.Tests;

[TestClass]
public class CliOptionGeneratorCoreTests
{
    [TestMethod]
    public void AttributeSource_IsGenerated()
    {
        var result = RunGenerator("").GetRunResult();

        var attributeFile = result.GeneratedTrees.SingleOrDefault(t =>
            t.FilePath.EndsWith("CliAttributes.g.cs")
        );

        Assert.IsNotNull(attributeFile, "CliAttributes.g.cs should be generated");
    }

    [TestMethod]
    public void AttributeSource_ContainsExpectedAttributeTypes()
    {
        var text = GetGeneratedText(RunGenerator("").GetRunResult(), "CliAttributes.g.cs");

        AssertContainsAll(
            text,
            "internal sealed class CliOptionAttribute",
            "internal sealed class CliParserAttribute",
            "internal sealed class ArityAttribute",
            "public bool Required { get; set; }",
            "public Type? CompletionProviderType { get; set; }"
        );
    }

    [TestMethod]
    public void SimpleOption_GeneratesPartialClass()
    {
        var text = GenerateForBody(
            "MyCommand.g.cs",
            """
            /// <summary>Test command</summary>
            public partial class MyCommand : CommandDef
            {
                /// <summary>The output path</summary>
                [CliOption("--output", "-o")]
                public partial string? Output { get; }
            }
            """
        );

        AssertContainsAll(
            text,
            "partial class MyCommand",
            "private readonly Option<string?> _opt_Output = new(\"--output\", new string[] {\"-o\"})",
            "public partial string? Output => GetValue(_opt_Output);",
            "protected override void AddGeneratedOptions(global::System.CommandLine.Command cmd)",
            "cmd.Add(_opt_Output);"
        );
    }

    [TestMethod]
    public void NoCliOptions_NoClassGenerated()
    {
        var source = """
            namespace TestApp
            {
                public class Plain { }
            }
            """;

        var result = RunGenerator(source).GetRunResult();

        var classFiles = result.GeneratedTrees.Where(t =>
            !t.FilePath.EndsWith("CliAttributes.g.cs")
        );

        Assert.AreEqual(0, classFiles.Count(), "No class-specific files should be generated");
    }

    [TestMethod]
    public void ImplicitAlias_UsesKebabCasePropertyName()
    {
        var text = GenerateForBody(
            "AliasCommand.g.cs",
            """
            public partial class AliasCommand : CommandDef
            {
                [CliOption]
                public partial string? OutputFormat { get; }
            }
            """
        );
        Assert.IsTrue(
            text.Contains("new(\"--output-format\", new string[] {})"),
            "Implicit alias should use kebab-case with '--' prefix"
        );
    }

    [TestMethod]
    public void ExplicitAliases_KeepPrimaryAndExtras()
    {
        var text = GenerateForBody(
            "VerboseCommand.g.cs",
            """
            public partial class VerboseCommand : CommandDef
            {
                [CliOption("--verbose", "-v", "-vv", "--very-verbose")]
                public partial bool Verbose { get; }
            }
            """
        );
        Assert.IsTrue(
            text.Contains("new(\"--verbose\", new string[] {\"-v\", \"-vv\", \"--very-verbose\"})"),
            "Primary alias and extras should be emitted in order"
        );
    }

    [TestMethod]
    public void RequiredInference_NonNullableReference_IsRequired()
    {
        var text = GenerateForBody(
            "RequiredCommand.g.cs",
            """
            public partial class RequiredCommand : CommandDef
            {
                [CliOption("--name")]
                public partial string Name { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "Required = true",
            "public partial string Name => GetValue(_opt_Name)!;"
        );
    }

    [TestMethod]
    public void RequiredInference_NullableReference_IsNotRequired()
    {
        var text = GenerateForBody(
            "NullableCommand.g.cs",
            """
            public partial class NullableCommand : CommandDef
            {
                [CliOption("--name")]
                public partial string? Name { get; }
            }
            """
        );
        Assert.IsFalse(
            text.Contains("Required = true"),
            "Nullable reference should not be required by inference"
        );
        Assert.IsTrue(text.Contains("public partial string? Name => GetValue(_opt_Name);"));
    }

    [TestMethod]
    public void RequiredInference_Bool_IsNotRequired()
    {
        var text = GenerateForBody(
            "BoolCommand.g.cs",
            """
            public partial class BoolCommand : CommandDef
            {
                [CliOption("--verbose")]
                public partial bool Verbose { get; }
            }
            """
        );
        Assert.IsFalse(
            text.Contains("Required = true"),
            "Bool should not be required by inference"
        );
    }

    [TestMethod]
    public void RequiredInference_Int_IsNotRequired()
    {
        var text = GenerateForBody(
            "PortCommand.g.cs",
            """
            public partial class PortCommand : CommandDef
            {
                [CliOption("--port")]
                public partial int Port { get; }
            }
            """
        );
        Assert.IsFalse(
            text.Contains("Required = true"),
            "Non-nullable int should not be required by inference"
        );
    }

    [TestMethod]
    public void DefaultInitializer_UsesDefaultValueFactory_AndFieldFallback()
    {
        var text = GenerateForBody(
            "DefaultCommand.g.cs",
            """
            public partial class DefaultCommand : CommandDef
            {
                [CliOption("--port")]
                public partial int Port { get; } = 8080;
            }
            """
        );
        AssertContainsAll(
            text,
            "DefaultValueFactory = _ => 8080",
            "if (!HasParseResult) return field;",
            "return GetValue(_opt_Port);"
        );
    }

    [TestMethod]
    public void Collection_ListString_EnablesAllowMultiple_AndOneOrMoreArity()
    {
        var text = GenerateForBody(
            "TagsCommand.g.cs",
            """
            public partial class TagsCommand : CommandDef
            {
                [CliOption("--tag")]
                public partial List<string> Tags { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "AllowMultipleArgumentsPerToken = true",
            "Arity = ArgumentArity.OneOrMore"
        );
    }

    [TestMethod]
    public void Collection_ArityAttribute_OverridesAutoArity()
    {
        var text = GenerateForBody(
            "WeightsCommand.g.cs",
            """
            public partial class WeightsCommand : CommandDef
            {
                [CliOption("--weight")]
                [Arity(2, 5)]
                public partial List<int> Weights { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "AllowMultipleArgumentsPerToken = true",
            "Arity = new ArgumentArity(2, 5)"
        );
        Assert.IsFalse(
            text.Contains("ArgumentArity.OneOrMore"),
            "Explicit arity should override auto arity"
        );
    }
}
