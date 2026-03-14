// ParseResult.GetValue is annotated [MaybeNull] but callers handle nullability via option defaults
#pragma warning disable CS8603
using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using Azure.Identity;

namespace Console.Cli;

public abstract partial class CommandDef
{
    /// <inheritdoc cref="AddGeneratedOptions"/>
    protected virtual void AddGeneratedOptions(Command cmd) { }

    /// <inheritdoc cref="AddGeneratedChildren"/>
    protected virtual void AddGeneratedChildren(Command cmd) { }

    /// <summary>True when the generator has wired up child OptionPacks and CommandDefs.</summary>
    protected virtual bool HasGeneratedChildren => false;

    /// <summary>
    /// True when this command operates against a data-plane endpoint
    /// (e.g. Key Vault data API) rather than ARM.
    /// Overridden to <c>true</c> by generated commands for data-plane services.
    /// </summary>
    protected virtual bool IsDataPlane => false;

    public abstract string Name { get; }
    public virtual string[] Aliases => [];
    public virtual string Description => "";
    public virtual string? DetailedDescription => Remarks;

    protected virtual string? Remarks => null;

    protected ParseResult ParseResult { get; private set; } = null!;
    protected bool HasParseResult => ParseResult is not null;

    protected T GetValue<T>(Option<T> option) => ParseResult.GetValue(option)!;

    protected T GetValue<T>(Argument<T> argument) => ParseResult.GetValue(argument);

    protected virtual Task<int> ExecuteAsync(CancellationToken cancellationToken) =>
        Task.FromResult(0);

    private Shared.AuthOptionPack? GetAuthOptionPack()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                if (field.GetValue(this) is Shared.AuthOptionPack pack)
                    return pack;
            }
        }
        return null;
    }

    internal virtual Command Build()
    {
        var cmd = CreateCommand();
        ConfigureCommand(cmd);
        if (DetailedDescription is { } r)
            RemarksRegistry.Register(cmd, r);
        if (IsDataPlane)
            DataPlaneRegistry.Register(cmd);
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
        AddGeneratedOptions(cmd);
        AddGeneratedChildren(cmd);

        foreach (var field in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = field.GetValue(this);
            if (value is null)
                continue;

            if (value is OptionPack pack)
            {
                if (!HasGeneratedChildren)
                    pack.AddOptionsTo(cmd);
            }
            else if (value is CommandDef subDef)
            {
                if (!HasGeneratedChildren)
                    cmd.Add(subDef.Build());
            }
            else if (value is Option opt)
                cmd.Add(opt);
            else if (value is Argument arg)
                cmd.Add(arg);
        }

        var executeMethod = GetType()
            .GetMethod(nameof(ExecuteAsync), BindingFlags.NonPublic | BindingFlags.Instance);
        bool hasHandler = executeMethod!.DeclaringType != typeof(CommandDef);

        var self = this;
        if (hasHandler)
        {
            cmd.SetAction(
                async (result, ct) =>
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
                    catch (AuthenticationFailedException ex)
                    {
                        var authPack = self.GetAuthOptionPack();
                        var configured = authPack?.AllowedCredentialTypes;
                        System.Console.Error.WriteLine(
                            AuthenticationErrorFormatter.Format(ex, configured)
                        );
                        if (result.GetValue(Shared.DiagnosticOptionPack.DetailedErrorsOption))
                            System.Console.Error.WriteLine(ex.ToString());
                        return 1;
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        System.Console.Error.WriteLine(HttpRequestErrorFormatter.Format(ex));
                        if (result.GetValue(Shared.DiagnosticOptionPack.DetailedErrorsOption))
                            System.Console.Error.WriteLine(ex.ToString());
                        return 1;
                    }
                }
            );
        }
        else
        {
            cmd.Action = new HelpAction();
        }
    }

    private static void InjectParseResult(object obj, ParseResult result) =>
        InjectParseResult(obj, result, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static void InjectParseResult(object obj, ParseResult result, HashSet<object> visited)
    {
        if (!visited.Add(obj))
            return;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                if (field.GetValue(obj) is OptionPack pack)
                {
                    pack.SetParseResult(result);
                    InjectParseResult(pack, result, visited);
                }
            }
        }
    }
}
