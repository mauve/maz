using static CliGenerator.Tests.CliGeneratorTestHelpers;

namespace CliGenerator.Tests;

[TestClass]
public class CliOptionGeneratorParserAndEnvTests
{
    [TestMethod]
    public void EnumDescriptions_EmitAllowedValuesAndCustomParser()
    {
        var text = GenerateFor(
            InTestApp(
                """
                public enum OutputFormat
                {
                    [Description("json")]
                    Json,
                    [Description("table")]
                    Table,
                }

                public partial class EnumCommand : CommandDef
                {
                    /// <summary>Format value</summary>
                    [CliOption("--format")]
                    public partial OutputFormat Format { get; }
                }
                """,
                "using System.ComponentModel;"
            ),
            "EnumCommand.g.cs"
        );
        AssertContainsAll(
            text,
            "CustomParser = r => r.Tokens[0].Value switch",
            "\"json\" => TestApp.OutputFormat.Json",
            "\"table\" => TestApp.OutputFormat.Table",
            "OptionMetadataRegistry.Register(_opt_Format",
            "\"json, table\""
        );
    }

    [TestMethod]
    public void EnvVar_String_EmitsFallbackSuffixAndDescriptionTag()
    {
        var text = GenerateForBody(
            "EnvCommand.g.cs",
            """
            public partial class EnvCommand : CommandDef
            {
                /// <summary>API key</summary>
                [CliOption("--api-key", EnvVar = "API_KEY")]
                public partial string? ApiKey { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "OptionMetadataRegistry.Register(_opt_ApiKey",
            "\"API_KEY\"",
            "GetValue(_opt_ApiKey) ?? System.Environment.GetEnvironmentVariable(\"API_KEY\")"
        );
    }

    [TestMethod]
    public void EnvVar_WithDefault_DoesNotEmitFallbackSuffix()
    {
        var text = GenerateForBody(
            "EnvDefaultCommand.g.cs",
            """
            public partial class EnvDefaultCommand : CommandDef
            {
                [CliOption("--port", EnvVar = "PORT")]
                public partial int Port { get; } = 8080;
            }
            """
        );
        Assert.IsTrue(
            text.Contains("OptionMetadataRegistry.Register(_opt_Port") && text.Contains("\"PORT\""),
            "Should emit registry registration with env var"
        );
        Assert.IsFalse(
            text.Contains("GetEnvironmentVariable(\"PORT\")"),
            "Default should suppress env fallback suffix emission"
        );
    }

    [TestMethod]
    public void GlobalAndAdvancedFlags_AreEmitted()
    {
        var text = GenerateForBody(
            "FlagsCommand.g.cs",
            """
            public partial class FlagsCommand : CommandDef
            {
                [CliOption("--verbose", Global = true, Advanced = true)]
                public partial bool Verbose { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "Recursive = true",
            "global::Console.Cli.AdvancedOptionRegistry.Register(_opt_Verbose);"
        );
    }

    [TestMethod]
    public void CompletionProviderType_EmitsBridgeCall()
    {
        var text = GenerateFor(
            InTestApp(
                """
                public sealed class NameCompletions : ICompletionProvider
                {
                    public IEnumerable<CompletionItem> GetCompletions(CompletionContext context) => [];
                }

                public partial class CompletionCommand : CommandDef
                {
                    [CliOption("--name", CompletionProviderType = typeof(NameCompletions))]
                    public partial string? Name { get; }
                }
                """,
                "using System.Collections.Generic;\nusing System.CommandLine.Completions;"
            ),
            "CompletionCommand.g.cs"
        );
        Assert.IsTrue(
            text.Contains(
                "CliCompletionProviderBridge.GetCompletions<global::TestApp.NameCompletions>(c)"
            ),
            "Completion provider bridge call should be emitted"
        );
    }

    [TestMethod]
    public void ParserPrecedence_CliParser_WinsOverParseAndCtor()
    {
        var text = GenerateForBody(
            "ParserCommand.g.cs",
            """
            public sealed class ThingConverter
            {
                public object ConvertFromString(string value) => new Thing(value);
            }

            [CliParser(typeof(ThingConverter))]
            public sealed class Thing
            {
                public Thing(string value) { }
                public static Thing Parse(string value) => new(value);
            }

            public partial class ParserCommand : CommandDef
            {
                [CliOption("--thing")]
                public partial Thing Thing { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "CustomParser = r => (TestApp.Thing)(new TestApp.ThingConverter().ConvertFromString(r.Tokens[0].Value))"
        );
        Assert.IsFalse(text.Contains("TestApp.Thing.Parse(r.Tokens[0].Value)"));
    }

    [TestMethod]
    public void ParserFallback_UsesStaticParse_WhenCliParserMissing()
    {
        var text = GenerateForBody(
            "ParseCommand.g.cs",
            """
            public sealed class Endpoint
            {
                public static Endpoint Parse(string value) => new();
            }

            public partial class ParseCommand : CommandDef
            {
                [CliOption("--endpoint")]
                public partial Endpoint Endpoint { get; }
            }
            """
        );
        Assert.IsTrue(
            text.Contains("CustomParser = r => TestApp.Endpoint.Parse(r.Tokens[0].Value)")
        );
    }

    [TestMethod]
    public void ParserFallback_UsesStringConstructor_WhenNoCliParserOrParse()
    {
        var text = GenerateForBody(
            "SlugCommand.g.cs",
            """
            public sealed class Slug
            {
                public Slug(string value) { }
            }

            public partial class SlugCommand : CommandDef
            {
                [CliOption("--slug")]
                public partial Slug Slug { get; }
            }
            """
        );
        Assert.IsTrue(text.Contains("CustomParser = r => new TestApp.Slug(r.Tokens[0].Value)"));
    }

    [TestMethod]
    public void ParserFallback_NoValidParser_DoesNotEmitCustomParser()
    {
        var text = GenerateForBody(
            "OpaqueCommand.g.cs",
            """
            public sealed class Opaque
            {
                public Opaque() { }
            }

            public partial class OpaqueCommand : CommandDef
            {
                [CliOption("--opaque")]
                public partial Opaque Opaque { get; }
            }
            """
        );
        Assert.IsFalse(
            text.Contains("CustomParser ="),
            "No parser path should avoid emitting CustomParser"
        );
    }

    [TestMethod]
    public void NullableEnum_UsesTokenCountGuardAndNullableCast()
    {
        var text = GenerateFor(
            InTestApp(
                """
                public enum Mode
                {
                    [Description("fast")]
                    Fast,
                    [Description("safe")]
                    Safe,
                }

                public partial class ModeCommand : CommandDef
                {
                    [CliOption("--mode")]
                    public partial Mode? RunMode { get; }
                }
                """,
                "using System.ComponentModel;"
            ),
            "ModeCommand.g.cs"
        );
        AssertContainsAll(
            text,
            "CustomParser = r => r.Tokens.Count > 0 ? (TestApp.Mode?)",
            ": (TestApp.Mode?)null"
        );
    }

    [TestMethod]
    public void EnumCollection_UsesTokenSelectToListParser()
    {
        var text = GenerateFor(
            InTestApp(
                """
                public enum OutputKind
                {
                    [Description("json")]
                    Json,
                    [Description("table")]
                    Table,
                }

                public partial class MultiEnumCommand : CommandDef
                {
                    [CliOption("--kind")]
                    public partial List<OutputKind> Kinds { get; }
                }
                """,
                "using System.ComponentModel;"
            ),
            "MultiEnumCommand.g.cs"
        );
        AssertContainsAll(
            text,
            "CustomParser = r => r.Tokens.Select(t => t.Value switch",
            "\"json\" => TestApp.OutputKind.Json",
            "\"table\" => TestApp.OutputKind.Table",
            "}).ToList()"
        );
    }

    [TestMethod]
    public void RequiredInference_Guid_IsRequired_ButNullableGuidIsNot()
    {
        var text = GenerateForBody(
            "GuidCommand.g.cs",
            """
            public partial class GuidCommand : CommandDef
            {
                [CliOption("--id")]
                public partial Guid Id { get; }

                [CliOption("--maybe-id")]
                public partial Guid? MaybeId { get; }
            }
            """
        );
        AssertContainsAll(text, "_opt_Id = new(\"--id\"", "Required = true");
        AssertContainsAll(text, "_opt_MaybeId = new(\"--maybe-id\"");
    }

    [TestMethod]
    public void EnvVar_CustomType_EmitsGuardedConversionSuffix()
    {
        var text = GenerateForBody(
            "UriEnvCommand.g.cs",
            """
            public partial class UriEnvCommand : CommandDef
            {
                [CliOption("--service-url", EnvVar = "SERVICE_URL")]
                public partial Uri? ServiceUrl { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "GetEnvironmentVariable(\"SERVICE_URL\") is string __s",
            "new System.Uri(__s)",
            ")null"
        );
    }
}
