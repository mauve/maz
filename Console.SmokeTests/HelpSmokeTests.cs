using System.CommandLine;
using Console.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.SmokeTests;

[TestClass]
public class HelpSmokeTests
{
    [TestMethod]
    public void All_Commands_Help_Succeeds()
    {
        var rootDef = new RootCommandDef(null);
        var rootCmd = rootDef.Build();
        var config = new CommandLineConfiguration(rootCmd);

        var failures = new List<string>();
        foreach (var path in WalkPaths(rootCmd, ""))
        {
            var tokens = path.Length > 0 ? path.Split(' ') : [];
            var args = tokens.Append("--help").ToArray();

            ParseResult result;
            try
            {
                result = rootCmd.Parse(args, config);
            }
            catch (Exception ex)
            {
                failures.Add($"'{path} --help': exception during parse: {ex.Message}");
                continue;
            }

            if (result.Errors.Count > 0)
            {
                failures.Add(
                    $"'{path} --help': parse errors: {string.Join("; ", result.Errors.Select(e => e.Message))}"
                );
                continue;
            }

            var exitCode = result.Invoke();
            if (exitCode != 0)
                failures.Add($"'{path} --help': exit code {exitCode}");
        }

        Assert.AreEqual(
            0,
            failures.Count,
            $"{failures.Count} command(s) failed:\n{string.Join("\n", failures)}"
        );
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
