// ParseResult.GetValue is annotated [MaybeNull] but callers handle nullability via option defaults
#pragma warning disable CS8603
using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;

namespace Console.Cli;

public abstract class CommandDef
{
    public abstract string Name { get; }
    public virtual string[] Aliases => [];
    public virtual string Description => "";

    protected ParseResult ParseResult { get; private set; } = null!;

    protected T GetValue<T>(Option<T> option) => ParseResult.GetValue(option)!;
    protected T GetValue<T>(Argument<T> argument) => ParseResult.GetValue(argument);

    protected virtual Task<int> ExecuteAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    internal virtual Command Build()
    {
        var cmd = CreateCommand();
        ConfigureCommand(cmd);
        return cmd;
    }

    protected virtual Command CreateCommand()
    {
        var cmd = new Command(Name, Description);
        foreach (var alias in Aliases)
            cmd.Aliases.Add(alias);
        return cmd;
    }

    private void ConfigureCommand(Command cmd)
    {
        foreach (var field in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = field.GetValue(this);
            if (value is null) continue;

            if (value is OptionPack pack)
                pack.AddOptionsTo(cmd);
            else if (value is CommandDef subDef)
                cmd.Add(subDef.Build());
            else if (value is Option opt)
                cmd.Add(opt);
            else if (value is Argument arg)
                cmd.Add(arg);
        }

        var executeMethod = GetType().GetMethod(
            nameof(ExecuteAsync),
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        bool hasHandler = executeMethod!.DeclaringType != typeof(CommandDef);

        var self = this;
        if (hasHandler)
        {
            cmd.SetAction(async (result, ct) =>
            {
                self.ParseResult = result;
                InjectParseResult(self, result);
                try
                {
                    return await self.ExecuteAsync(ct);
                }
                catch (InvocationException ex)
                {
                    System.Console.Error.WriteLine(ex.Message);
                    return ex.ExitCode;
                }
            });
        }
        else
        {
            cmd.Action = new HelpAction();
        }
    }

    private static void InjectParseResult(object obj, ParseResult result)
    {
        foreach (var field in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.GetValue(obj) is OptionPack pack)
            {
                pack.SetParseResult(result);
                InjectParseResult(pack, result);
            }
        }
    }
}
