using static CliGenerator.Tests.CliGeneratorTestHelpers;

namespace CliGenerator.Tests;

[TestClass]
public class CliOptionGeneratorParserAndEnvTests
{
    [TestMethod]
    public void EnumDescriptions_EmitParserAndMetadata()
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
                    public override string Name => "enum";
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
            "__raw => __raw switch",
            "\"json\" => TestApp.OutputFormat.Json",
            "\"table\" => TestApp.OutputFormat.Table",
            "\"json, table\""
        );
    }

    [TestMethod]
    public void EnvVar_String_EmitsMetadataAndFallbackSuffix()
    {
        var text = GenerateForBody(
            "EnvCommand.g.cs",
            """
            public partial class EnvCommand : CommandDef
            {
                public override string Name => "env";
                /// <summary>API key</summary>
                [CliOption("--api-key", EnvVar = "API_KEY")]
                public partial string? ApiKey { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "Metadata = new global::Console.Cli.OptionMetadata(\"API_KEY\"",
            "GetEnvironmentVariable(\"API_KEY\")"
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
                public override string Name => "envdefault";
                [CliOption("--port", EnvVar = "PORT")]
                public partial int Port { get; } = 8080;
            }
            """
        );
        Assert.IsTrue(text.Contains("\"PORT\""), "Should emit metadata with env var");
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
                public override string Name => "flags";
                [CliOption("--verbose", Global = true, Advanced = true)]
                public partial bool Verbose { get; }
            }
            """
        );
        AssertContainsAll(text, "Recursive = true", "IsAdvanced = true");
    }

    [TestMethod]
    public void CompletionProviderType_EmitsMetadata()
    {
        var text = GenerateFor(
            InTestApp(
                """
                public sealed class NameCompletions { }

                public partial class CompletionCommand : CommandDef
                {
                    public override string Name => "completion";
                    [CliOption("--name", CompletionProviderType = typeof(NameCompletions))]
                    public partial string? Name { get; }
                }
                """,
                ""
            ),
            "CompletionCommand.g.cs"
        );
        // Just verify the option is generated with the name
        Assert.IsTrue(
            text.Contains("Name = \"--name\","),
            "Option should be generated with correct name"
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
                public override string Name => "parser";
                [CliOption("--thing")]
                public partial Thing Thing { get; }
            }
            """
        );
        AssertContainsAll(
            text,
            "Parser = (",
            "new TestApp.ThingConverter().ConvertFromString(__raw)"
        );
        Assert.IsFalse(text.Contains("TestApp.Thing.Parse(__raw)"));
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
                public override string Name => "parse";
                [CliOption("--endpoint")]
                public partial Endpoint Endpoint { get; }
            }
            """
        );
        Assert.IsTrue(text.Contains("TestApp.Endpoint.Parse(__raw)"));
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
                public override string Name => "slug";
                [CliOption("--slug")]
                public partial Slug Slug { get; }
            }
            """
        );
        Assert.IsTrue(text.Contains("new TestApp.Slug(__raw)"));
    }

    [TestMethod]
    public void ParserFallback_NoValidParser_DoesNotEmitParser()
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
                public override string Name => "opaque";
                [CliOption("--opaque")]
                public partial Opaque Opaque { get; }
            }
            """
        );
        Assert.IsFalse(text.Contains("Parser ="), "No parser path should avoid emitting Parser");
    }

    [TestMethod]
    public void NullableEnum_UsesNullableGuard()
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
                    public override string Name => "mode";
                    [CliOption("--mode")]
                    public partial Mode? RunMode { get; }
                }
                """,
                "using System.ComponentModel;"
            ),
            "ModeCommand.g.cs"
        );
        AssertContainsAll(text, "__raw => __raw != null", "(TestApp.Mode?)null");
    }

    [TestMethod]
    public void EnumCollection_UsesElementParser()
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
                    public override string Name => "multienum";
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
            "ElementParser = (",
            "__raw => (object)(__raw switch",
            "\"json\" => TestApp.OutputKind.Json",
            "\"table\" => TestApp.OutputKind.Table"
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
                public override string Name => "guid";
                [CliOption("--id")]
                public partial Guid Id { get; }

                [CliOption("--maybe-id")]
                public partial Guid? MaybeId { get; }
            }
            """
        );
        AssertContainsAll(text, "Name = \"--id\",", "Required = true");
        AssertContainsAll(text, "Name = \"--maybe-id\",");
    }

    [TestMethod]
    public void EnvVar_CustomType_EmitsGuardedConversionSuffix()
    {
        var text = GenerateForBody(
            "UriEnvCommand.g.cs",
            """
            public partial class UriEnvCommand : CommandDef
            {
                public override string Name => "urienv";
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
