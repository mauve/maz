using System.CommandLine;
using System.Text;
using Console.Cli;

namespace CliGenerator.Tests;

[TestClass]
public class DuplicateCommandOptionsTests
{
    [TestMethod]
    public void NoCommand_HasDuplicateOptionNamesOrAliases()
    {
        var root = new RootCommandDef().Build();
        var violations = new List<string>();
        CheckCommand(root, "maz", violations);

        if (violations.Count == 0)
            return;
        var msg = new StringBuilder("Commands with duplicate option names or aliases:\n");
        foreach (var v in violations.OrderBy(x => x))
            msg.AppendLine(v);
        Assert.Fail(msg.ToString());
    }

    private static void CheckCommand(Command cmd, string path, List<string> violations)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in cmd.Options)
        {
            foreach (var alias in opt.Aliases.Prepend(opt.Name))
            {
                if (!seen.TryAdd(alias, opt.Name))
                    violations.Add(
                        $"{path}: '{alias}' used by both '{seen[alias]}' and '{opt.Name}'"
                    );
            }
        }
        foreach (var sub in cmd.Subcommands)
            CheckCommand(sub, $"{path} {sub.Name}", violations);
    }
}
