using System.Text;

namespace SpecGenerator.Emitting;

/// <summary>Indent-aware StringBuilder wrapper for emitting formatted C# code.</summary>
public sealed class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;
    private const string IndentUnit = "    ";

    public void Indent() => _indent++;

    public void Outdent() => _indent = Math.Max(0, _indent - 1);

    public void Line() => _sb.AppendLine();

    public void Line(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _sb.AppendLine();
            return;
        }

        for (var i = 0; i < _indent; i++)
            _sb.Append(IndentUnit);

        _sb.AppendLine(text);
    }

    public void Block(string header, Action body)
    {
        Line(header);
        Line("{");
        Indent();
        body();
        Outdent();
        Line("}");
    }

    public override string ToString() => _sb.ToString();
}
