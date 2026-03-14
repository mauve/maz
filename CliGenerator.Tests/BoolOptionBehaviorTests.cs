using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CliGenerator.Tests;

/// <summary>
/// Verifies the runtime behavior of the bool option toggle pattern emitted by CliOptionGenerator:
///   - --no-{option}           → false
///   - --{option}              → true  (no token)
///   - --{option} true         → true  (explicit token)
///   - --{option} false        → false (explicit token)
/// </summary>
[TestClass]
public class BoolOptionBehaviorTests
{
    /// <summary>
    /// Builds an Option&lt;bool&gt; with the same aliases and custom parser the generator emits.
    /// </summary>
    private static readonly string[] VerboseAliases = ["--no-verbose"];

    private static Option<bool> BuildVerboseOption()
    {
        var opt = new Option<bool>("--verbose", VerboseAliases)
        {
            CustomParser = r =>
                r.Tokens.Count > 0
                    ? bool.Parse(r.Tokens[0].Value)
                    : !(
                        r.Parent is global::System.CommandLine.Parsing.OptionResult __or
                        && __or.IdentifierToken?.Value?.StartsWith(
                            "--no-",
                            global::System.StringComparison.OrdinalIgnoreCase
                        ) == true
                    ),
        };
        return opt;
    }

    private static bool Parse(Option<bool> opt, params string[] args)
    {
        var cmd = new RootCommand();
        cmd.Add(opt);
        var result = cmd.Parse(args);
        return result.GetValue(opt);
    }

    [TestMethod]
    public void NegationAlias_ReturnsFalse()
    {
        var opt = BuildVerboseOption();
        Assert.IsFalse(Parse(opt, "--no-verbose"), "--no-verbose should set value to false");
    }

    [TestMethod]
    public void MainAlias_WithoutToken_ReturnsTrue()
    {
        var opt = BuildVerboseOption();
        Assert.IsTrue(Parse(opt, "--verbose"), "--verbose without token should set value to true");
    }

    [TestMethod]
    public void MainAlias_WithExplicitTrue_ReturnsTrue()
    {
        var opt = BuildVerboseOption();
        Assert.IsTrue(Parse(opt, "--verbose", "true"), "--verbose true should set value to true");
    }

    [TestMethod]
    public void MainAlias_WithExplicitFalse_ReturnsFalse()
    {
        var opt = BuildVerboseOption();
        Assert.IsFalse(
            Parse(opt, "--verbose", "false"),
            "--verbose false should set value to false"
        );
    }

    [TestMethod]
    public void NotProvided_ReturnsFalse()
    {
        var opt = BuildVerboseOption();
        Assert.IsFalse(Parse(opt), "unprovided bool option should default to false");
    }
}
