using Console.Cli;
using Console.Cli.Parsing;

namespace CliGenerator.Tests;

/// <summary>
/// Simple concrete CommandDef for building test command trees
/// without depending on System.CommandLine.
/// </summary>
internal sealed class TestCommandDef : CommandDef
{
    public override string Name { get; }
    private readonly List<CommandDef> _children;
    private readonly Func<CancellationToken, Task<int>>? _handler;

    public TestCommandDef(
        string name,
        IEnumerable<CommandDef>? children = null,
        Func<CancellationToken, Task<int>>? handler = null
    )
    {
        Name = name;
        _children = children?.ToList() ?? [];
        _handler = handler;
    }

    internal override IEnumerable<CommandDef> EnumerateChildren() => _children;

    protected override Task<int> ExecuteAsync(CancellationToken cancellationToken) =>
        _handler is not null ? _handler(cancellationToken) : Task.FromResult(0);

    protected internal override bool HasExecuteHandler => _handler is not null;
}
