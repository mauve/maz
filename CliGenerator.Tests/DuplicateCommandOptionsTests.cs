using System.Text;
using Console.Cli;
using Console.Cli.Parsing;

namespace CliGenerator.Tests;

[TestClass]
public class DuplicateCommandOptionsTests
{
    [TestMethod]
    public void NoCommand_HasDuplicateOptionNamesOrAliases()
    {
        var root = new RootCommandDef(null);
        var violations = new List<string>();
        CheckCommand(root, "maz", violations);

        if (violations.Count == 0)
            return;
        var msg = new StringBuilder("Commands with duplicate option names or aliases:\n");
        foreach (var v in violations.OrderBy(x => x))
            msg.AppendLine(v);
        Assert.Fail(msg.ToString());
    }

    private static void CheckCommand(CommandDef cmd, string path, List<string> violations)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in cmd.EnumerateAllOptions())
        {
            foreach (var alias in opt.AllNames)
            {
                if (!seen.TryAdd(alias, opt.Name))
                    violations.Add(
                        $"{path}: '{alias}' used by both '{seen[alias]}' and '{opt.Name}'"
                    );
            }
        }
        foreach (var sub in cmd.EnumerateChildren())
            CheckCommand(sub, $"{path} {sub.Name}", violations);
    }
}
