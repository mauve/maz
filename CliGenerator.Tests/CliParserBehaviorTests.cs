using Console.Cli;
using Console.Cli.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CliGenerator.Tests;

/// <summary>
/// Tests for CliParser behavior: optional values, required options,
/// subcommand resolution, directives, and error handling.
/// </summary>
[TestClass]
public class CliParserBehaviorTests
{
    // ── Optional-value options ─────────────────────────────────────────

    private sealed class OptionalValueCommandDef : CommandDef
    {
        public override string Name => "test";

        public readonly CliOption<string?> Filter = new()
        {
            Name = "--filter",
            ValueIsOptional = true,
        };

        public readonly CliOption<string?> RequiredName = new()
        {
            Name = "--name",
            Required = true,
        };

        public readonly CliOption<int> Port = new()
        {
            Name = "--port",
            DefaultValueFactory = () => 8080,
            ValueIsOptional = true,
        };

        public readonly CliOption<int> Count = new() { Name = "--count" };

        internal override IEnumerable<CliOption> EnumerateOptions()
        {
            yield return Filter;
            yield return RequiredName;
            yield return Port;
            yield return Count;
        }
    }

    [TestMethod]
    public void NullableOption_WithoutValue_IsProvidedWithNull()
    {
        var cmd = new OptionalValueCommandDef();
        var result = CliParser.Parse(["--filter"], cmd);

        Assert.IsTrue(
            cmd.Filter.WasProvided,
            "--filter without value should be marked as provided"
        );
        Assert.IsNull(cmd.Filter.Value, "--filter without value should have null value");
        Assert.IsFalse(
            result.Errors.Any(e => e.Contains("--filter")),
            "No error for optional-value option"
        );
    }

    [TestMethod]
    public void NullableOption_WithValue_ParsesValue()
    {
        var cmd = new OptionalValueCommandDef();
        CliParser.Parse(["--filter", "foo"], cmd);

        Assert.IsTrue(cmd.Filter.WasProvided);
        Assert.AreEqual("foo", cmd.Filter.Value);
    }

    [TestMethod]
    public void NullableOption_WithEqualsValue_ParsesValue()
    {
        var cmd = new OptionalValueCommandDef();
        CliParser.Parse(["--filter=bar"], cmd);

        Assert.IsTrue(cmd.Filter.WasProvided);
        Assert.AreEqual("bar", cmd.Filter.Value);
    }

    [TestMethod]
    public void NullableOption_NotProvided_IsNotProvided()
    {
        var cmd = new OptionalValueCommandDef();
        CliParser.Parse([], cmd);

        Assert.IsFalse(cmd.Filter.WasProvided);
        Assert.IsNull(cmd.Filter.Value);
    }

    [TestMethod]
    public void OptionWithDefault_WithoutValue_IsProvidedWithDefault()
    {
        var cmd = new OptionalValueCommandDef();
        var result = CliParser.Parse(["--port"], cmd);

        Assert.IsTrue(cmd.Port.WasProvided, "--port without value should be marked as provided");
        Assert.IsFalse(
            result.Errors.Any(e => e.Contains("--port")),
            "No error for optional-value option"
        );
    }

    [TestMethod]
    public void OptionWithDefault_WithValue_ParsesValue()
    {
        var cmd = new OptionalValueCommandDef();
        CliParser.Parse(["--port", "3000"], cmd);

        Assert.IsTrue(cmd.Port.WasProvided);
        Assert.AreEqual(3000, cmd.Port.Value);
    }

    [TestMethod]
    public void NonNullableOptionWithoutDefault_WithoutValue_Errors()
    {
        var cmd = new OptionalValueCommandDef();
        var result = CliParser.Parse(["--count"], cmd);

        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("--count") && e.Contains("requires a value")),
            "Non-nullable non-default option without value should error"
        );
    }

    // ── Required options ──────────────────────────────────────────────

    [TestMethod]
    public void RequiredOption_Missing_ProducesError()
    {
        var cmd = new OptionalValueCommandDef();
        var result = CliParser.Parse([], cmd);

        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("--name") && e.Contains("is required")),
            "Missing required option should produce an error"
        );
    }

    [TestMethod]
    public void RequiredOption_Provided_NoError()
    {
        var cmd = new OptionalValueCommandDef();
        var result = CliParser.Parse(["--name", "test"], cmd);

        Assert.IsFalse(
            result.Errors.Any(e => e.Contains("--name")),
            "Provided required option should not error"
        );
    }

    // ── Subcommand resolution ─────────────────────────────────────────

    [TestMethod]
    public void SubcommandResolution_MatchesLeafCommand()
    {
        var leaf = new TestCommandDef("sub");
        var root = new TestCommandDef("root", [leaf]);
        var result = CliParser.Parse(["sub"], root);

        Assert.AreSame(leaf, result.Command);
        Assert.AreEqual(2, result.CommandPath.Count);
    }

    [TestMethod]
    public void SubcommandResolution_UnknownToken_StaysAtParent()
    {
        var root = new TestCommandDef("root", [new TestCommandDef("sub")]);
        var result = CliParser.Parse(["unknown"], root);

        Assert.AreEqual("root", result.Command!.Name);
        Assert.IsTrue(result.UnmatchedTokens.Contains("unknown"));
    }

    // ── Directives ────────────────────────────────────────────────────

    [TestMethod]
    public void Directives_Parsed_FromBracketedTokens()
    {
        var root = new TestCommandDef("root");
        var result = CliParser.Parse(["[suggest:42]"], root);

        Assert.AreEqual(1, result.Directives.Count);
        Assert.AreEqual("suggest", result.Directives[0].Name);
        Assert.AreEqual("42", result.Directives[0].Value);
    }

    // ── --no-X negation ──────────────────────────────────────────────

    private sealed class NegationCommandDef : CommandDef
    {
        public override string Name => "test";

        public readonly CliOption<bool> Verbose = new() { Name = "--verbose" };

        internal override IEnumerable<CliOption> EnumerateOptions()
        {
            yield return Verbose;
        }
    }

    [TestMethod]
    public void NoPrefix_NegatesBoolOption()
    {
        var cmd = new NegationCommandDef();
        CliParser.Parse(["--no-verbose"], cmd);

        Assert.IsTrue(cmd.Verbose.WasProvided);
        Assert.IsFalse(cmd.Verbose.Value, "--no-verbose should set value to false");
    }

    [TestMethod]
    public void NoPrefix_DoesNotApply_ToNonBoolOptions()
    {
        var cmd = new OptionalValueCommandDef();
        var result = CliParser.Parse(["--no-count"], cmd);

        // --no-count should be unmatched (count is int, not bool)
        Assert.IsTrue(result.UnmatchedTokens.Contains("--no-count"));
    }

    // ── --foo=bar syntax ─────────────────────────────────────────────

    [TestMethod]
    public void EqualsSign_ParsesValue()
    {
        var cmd = new OptionalValueCommandDef();
        CliParser.Parse(["--name=hello"], cmd);

        Assert.IsTrue(cmd.RequiredName.WasProvided);
        Assert.AreEqual("hello", cmd.RequiredName.Value);
    }

    // ── Multiple options together ────────────────────────────────────

    [TestMethod]
    public void NullableOption_BareFlag_FollowedByAnotherOption_DoesNotConsumeIt()
    {
        var cmd = new OptionalValueCommandDef();
        var result = CliParser.Parse(["--filter", "--name", "test"], cmd);

        Assert.IsTrue(cmd.Filter.WasProvided);
        Assert.IsNull(cmd.Filter.Value, "--filter should get null, not '--name'");
        Assert.AreEqual("test", cmd.RequiredName.Value);
    }

    // ── ValueIsOptional default application ──────────────────────────

    [TestMethod]
    public void OptionalValue_BareFlag_AppliesDefault()
    {
        var cmd = new OptionalValueCommandDef();
        CliParser.Parse(["--port"], cmd);

        Assert.IsTrue(cmd.Port.WasProvided);
        Assert.AreEqual(8080, cmd.Port.Value, "Bare --port should apply default value 8080");
    }

    // ── Stackable options ─────────────────────────────────────────────

    private sealed class StackableCommandDef : CommandDef
    {
        public override string Name => "test";

        public readonly CliOption<int> Verbose = new()
        {
            Name = "--verbose",
            Aliases = ["-v"],
            Stackable = true,
            ValueIsOptional = true,
            DefaultValueFactory = () => 1,
        };

        internal override IEnumerable<CliOption> EnumerateOptions()
        {
            yield return Verbose;
        }
    }

    [TestMethod]
    public void Stackable_SingleShortAlias_SetsOne()
    {
        var cmd = new StackableCommandDef();
        CliParser.Parse(["-v"], cmd);

        Assert.IsTrue(cmd.Verbose.WasProvided);
        Assert.AreEqual(1, cmd.Verbose.Value);
    }

    [TestMethod]
    public void Stackable_DoubleShortAlias_SetsTwo()
    {
        var cmd = new StackableCommandDef();
        CliParser.Parse(["-vv"], cmd);

        Assert.IsTrue(cmd.Verbose.WasProvided);
        Assert.AreEqual(2, cmd.Verbose.Value);
    }

    [TestMethod]
    public void Stackable_TripleShortAlias_SetsThree()
    {
        var cmd = new StackableCommandDef();
        CliParser.Parse(["-vvv"], cmd);

        Assert.IsTrue(cmd.Verbose.WasProvided);
        Assert.AreEqual(3, cmd.Verbose.Value);
    }

    [TestMethod]
    public void Stackable_LongForm_BareFlag_SetsDefault()
    {
        var cmd = new StackableCommandDef();
        CliParser.Parse(["--verbose"], cmd);

        Assert.IsTrue(cmd.Verbose.WasProvided);
        Assert.AreEqual(1, cmd.Verbose.Value, "Bare --verbose should default to 1");
    }

    [TestMethod]
    public void Stackable_LongForm_WithExplicitValue()
    {
        var cmd = new StackableCommandDef();
        CliParser.Parse(["--verbose", "5"], cmd);

        Assert.IsTrue(cmd.Verbose.WasProvided);
        Assert.AreEqual(5, cmd.Verbose.Value);
    }

    [TestMethod]
    public void Stackable_LongForm_WithEqualsValue()
    {
        var cmd = new StackableCommandDef();
        CliParser.Parse(["--verbose=3"], cmd);

        Assert.IsTrue(cmd.Verbose.WasProvided);
        Assert.AreEqual(3, cmd.Verbose.Value);
    }

    [TestMethod]
    public void Stackable_NotProvided_DefaultsToZero()
    {
        var cmd = new StackableCommandDef();
        CliParser.Parse([], cmd);

        Assert.IsFalse(cmd.Verbose.WasProvided);
    }

    [TestMethod]
    public void Stackable_MixedChars_NotStacked()
    {
        var cmd = new StackableCommandDef();
        var result = CliParser.Parse(["-vx"], cmd);

        // -vx has mixed chars, should not be recognized as stacking
        Assert.IsFalse(cmd.Verbose.WasProvided);
        Assert.IsTrue(result.UnmatchedTokens.Contains("-vx"));
    }

    [TestMethod]
    public void Stackable_OnNonIntOption_Throws()
    {
        var cmd = new StackableOnStringCommandDef();
        var threw = false;
        try
        {
            CliParser.Parse([], cmd);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Assert.IsTrue(threw, "Expected InvalidOperationException for Stackable on non-int option");
    }

    private sealed class StackableOnStringCommandDef : CommandDef
    {
        public override string Name => "test";

        public readonly CliOption<string?> Bad = new()
        {
            Name = "--bad",
            Aliases = ["-b"],
            Stackable = true,
        };

        internal override IEnumerable<CliOption> EnumerateOptions()
        {
            yield return Bad;
        }
    }
}
