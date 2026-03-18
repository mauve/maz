using Console.Cli;
using Console.Cli.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.SmokeTests;

[TestClass]
public class HelpSmokeTests
{
    private static readonly CommandDef RootDef;
    private static readonly IReadOnlyList<string> AllPaths;

    static HelpSmokeTests()
    {
        RootDef = new RootCommandDef(null);
        AllPaths = WalkPaths(RootDef, "").ToList();
    }

    public static IEnumerable<object[]> AllCommandPaths =>
        AllPaths.Select(static p => new object[] { p });

    [TestMethod]
    [DynamicData(nameof(AllCommandPaths))]
    public void Command_Help_Succeeds(string commandPath)
    {
        var tokens = commandPath.Length > 0 ? commandPath.Split(' ') : [];
        var args = tokens.Append("--help").ToArray();

        // Create a fresh root for each test since CliParser mutates option state
        var root = new RootCommandDef(null);
        var result = CliParser.Parse(args, root);

        // Verify the command was matched
        Assert.IsNotNull(result.Command, "Expected a matched command");

        // Verify --help was recognized (CliParser reports required-option errors even
        // when --help is provided, which is fine — the real entry point checks
        // _helpOption.WasProvided before looking at errors)
        Assert.IsTrue(result.Command._helpOption.WasProvided, "Expected --help to be recognized");

        // Only non-required-option errors indicate real problems
        var nonRequiredErrors = result.Errors
            .Where(e => !e.Contains("is required"))
            .ToList();
        Assert.AreEqual(
            0,
            nonRequiredErrors.Count,
            $"Parse errors: {string.Join("; ", nonRequiredErrors)}"
        );
    }

    private static IEnumerable<string> WalkPaths(CommandDef cmd, string prefix)
    {
        yield return prefix;
        foreach (var sub in cmd.EnumerateChildren())
        {
            var subPath = prefix.Length > 0 ? $"{prefix} {sub.Name}" : sub.Name;
            foreach (var p in WalkPaths(sub, subPath))
                yield return p;
        }
    }
}
