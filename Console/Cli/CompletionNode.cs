namespace Console.Cli;

internal readonly struct CompletionNode
{
    private readonly CompletionNode[]? _eager;
    private readonly Func<CompletionNode[]>? _factory;

    // Existing constructor — used by tests and by any eagerly-built node.
    public CompletionNode(string name, string[] options, CompletionNode[] children)
    {
        Name = name;
        Options = options;
        _eager = children;
        _factory = null;
    }

    // Used by the generated CompletionTree for service-level nodes whose subtrees are built
    // lazily on first access (one factory call per service per process lifetime).
    internal CompletionNode(string name, string[] options, Func<CompletionNode[]> factory)
    {
        Name = name;
        Options = options;
        _eager = null;
        _factory = factory;
    }

    public string Name { get; }
    public string[] Options { get; }
    public CompletionNode[] Children => _eager ?? _factory?.Invoke() ?? Array.Empty<CompletionNode>();
}
