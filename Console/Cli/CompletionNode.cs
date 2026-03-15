namespace Console.Cli;

internal readonly struct CompletionNode(
    string name,
    string[] options,
    CompletionNode[] children)
{
    public string Name => name;
    public string[] Options => options;
    public CompletionNode[] Children => children;
}
