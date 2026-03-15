using System.CommandLine;
using Console.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.SmokeTests;

[TestClass]
public class HelpSmokeTests
{
    // Built once per test process. Each DynamicData test case is just a string,
    // so MSTest's test-discovery overhead is minimal.
    private static readonly Command RootCmd;
    private static readonly CommandLineConfiguration Config;
    private static readonly IReadOnlyList<string> AllPaths;

    static HelpSmokeTests()
    {
        RootCmd = new RootCommandDef(null).Build();
        Config = new CommandLineConfiguration(RootCmd);
        AllPaths = WalkPaths(RootCmd, "").ToList();
    }

    public static IEnumerable<object[]> AllCommandPaths =>
        AllPaths.Select(static p => new object[] { p });

    [TestMethod]
    [DynamicData(nameof(AllCommandPaths))]
    public void Command_Help_Succeeds(string commandPath)
    {
        var tokens = commandPath.Length > 0 ? commandPath.Split(' ') : [];
        var args = tokens.Append("--help").ToArray();

        ParseResult result;
        try
        {
            result = RootCmd.Parse(args, Config);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Exception during parse: {ex.Message}");
            return;
        }

        Assert.AreEqual(
            0,
            result.Errors.Count,
            $"Parse errors: {string.Join("; ", result.Errors.Select(e => e.Message))}"
        );

        var exitCode = result.Invoke();
        Assert.AreEqual(0, exitCode, $"Non-zero exit code");
    }

    private static IEnumerable<string> WalkPaths(Command cmd, string prefix)
    {
        yield return prefix;
        foreach (var sub in cmd.Subcommands)
        {
            var subPath = prefix.Length > 0 ? $"{prefix} {sub.Name}" : sub.Name;
            foreach (var p in WalkPaths(sub, subPath))
                yield return p;
        }
    }
}
