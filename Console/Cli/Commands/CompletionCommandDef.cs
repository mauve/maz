using Console.Cli.Parsing;

namespace Console.Cli.Commands;

/// <summary>Generate shell completion scripts.</summary>
/// <remarks>
/// Use this command to emit completion scripts for supported shells.
/// Add the generated command snippet to your shell profile to enable tab completion.
/// </remarks>
public partial class CompletionCommandDef : CommandDef
{
    public override string Name => "completion";
    protected internal override bool IsManualCommand => true;

    /// <summary>Hide the "Authentication" group for this command.</summary>
    internal CompletionCommandDef()
    {
        HiddenHelpGroups = ["Authentication"];
    }

    public override string? DetailedDescription =>
        """
            To enable completions, add one of the following to your shell profile:

            Bash (~/.bashrc or ~/.bash_profile):
              eval "$(maz completion bash)"

            Zsh (~/.zshrc):
              eval "$(maz completion zsh)"

            Fish (~/.config/fish/config.fish):
              maz completion fish | source

            PowerShell ($PROFILE):
              maz completion pwsh | Invoke-Expression
            """;

    public readonly CliArgument<string> Shell = new()
    {
        Name = "shell",
        Description = "Target shell: bash, zsh, fish, pwsh",
        CompletionValues = ["bash", "zsh", "fish", "pwsh"],
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return Shell;
    }

    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var script = GetValue(Shell) switch
        {
            "bash" => BashScript,
            "zsh" => ZshScript,
            "fish" => FishScript,
            "pwsh" => PwshScript,
            var s => throw new InvocationException(
                $"Unknown shell '{s}'. Use: bash, zsh, fish, pwsh"
            ),
        };
        System.Console.WriteLine(script);
        return Task.FromResult(0);
    }

    private const string BashScript = """
        _maz_completions() {
            local completions
            completions="$(maz "[suggest:$COMP_POINT]" "$COMP_LINE" 2>/dev/null)"
            if [ -n "$completions" ]; then
                IFS=$'\n' read -rd '' -a COMPREPLY <<< "$completions"
            fi
        }
        complete -F _maz_completions maz
        """;

    private const string ZshScript = """
        _maz_completions() {
            local completions
            completions=(${(f)"$(maz "[suggest:${#BUFFER}]" "$BUFFER" 2>/dev/null)"})
            _describe 'completions' completions
        }
        compdef _maz_completions maz
        """;

    private const string FishScript = """
        function __maz_completions
            set -l cmd (commandline -b)
            set -l pos (commandline -C)
            maz "[suggest:$pos]" "$cmd" 2>/dev/null
        end
        complete -c maz -f -a '(__maz_completions)'
        """;

    private const string PwshScript = """
        Register-ArgumentCompleter -Native -CommandName maz -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            maz "[suggest:$cursorPosition]" "$commandAst" 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;
}
