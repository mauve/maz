using Console.Cli.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CliGenerator.Tests;

/// <summary>
/// Verifies the runtime behavior of the bool option toggle pattern:
///   - --no-{option}           → false
///   - --{option}              → true  (no token)
///   - --{option} true         → true  (explicit token)
///   - --{option} false        → false (explicit token)
/// The CliParser handles --no-X negation natively.
/// </summary>
[TestClass]
public class BoolOptionBehaviorTests
{
    private sealed class VerboseCommandDef : Console.Cli.CommandDef
    {
        public override string Name => "test";

        public readonly CliOption<bool> Verbose = new()
        {
            Name = "--verbose",
            Aliases = ["--no-verbose"],
        };

        internal override IEnumerable<CliOption> EnumerateOptions()
        {
            yield return Verbose;
        }
    }

    private static bool Parse(params string[] args)
    {
        var cmd = new VerboseCommandDef();
        CliParser.Parse(args, cmd);
        return cmd.Verbose.Value;
    }

    [TestMethod]
    public void NegationAlias_ReturnsFalse()
    {
        Assert.IsFalse(Parse("--no-verbose"), "--no-verbose should set value to false");
    }

    [TestMethod]
    public void MainAlias_WithoutToken_ReturnsTrue()
    {
        Assert.IsTrue(Parse("--verbose"), "--verbose without token should set value to true");
    }

    [TestMethod]
    public void MainAlias_WithExplicitTrue_ReturnsTrue()
    {
        Assert.IsTrue(Parse("--verbose", "true"), "--verbose true should set value to true");
    }

    [TestMethod]
    public void MainAlias_WithExplicitFalse_ReturnsFalse()
    {
        Assert.IsFalse(
            Parse("--verbose", "false"),
            "--verbose false should set value to false"
        );
    }

    [TestMethod]
    public void NotProvided_ReturnsFalse()
    {
        Assert.IsFalse(Parse(), "unprovided bool option should default to false");
    }
}
