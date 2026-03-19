using System.Diagnostics;
using System.Net.Http.Headers;

namespace Console.Cli.Shared;

/// <summary>
/// Runtime diagnostic logger with tree-scoped output, timestamps, and category coloring.
/// Never null — callers always hold an instance. Use <see cref="Null"/> for no-op.
/// </summary>
public sealed class DiagnosticLog
{
    private readonly TextWriter _writer;
    private readonly bool _color;
    private readonly int _level; // 1 = headers, 2 = headers + bodies
    private readonly Stopwatch _sw;
    private readonly bool _absoluteTimestamps;
    private readonly int _bodyLimit;
    private int _depth;

    /// <summary>Singleton no-op instance (level 0). All methods return immediately.</summary>
    public static DiagnosticLog Null { get; } = new();

    /// <summary>Whether this instance actually produces output.</summary>
    public bool IsEnabled => _level > 0;

    // ANSI codes
    private const string Dim = "\x1b[2m";
    private const string Reset = "\x1b[0m";
    private const string Magenta = "\x1b[35m";
    private const string Cyan = "\x1b[36m";
    private const string Gray = "\x1b[90m";

    private DiagnosticLog()
    {
        _writer = TextWriter.Null;
        _color = false;
        _level = 0;
        _sw = new Stopwatch();
        _absoluteTimestamps = false;
        _bodyLimit = 0;
    }

    private DiagnosticLog(
        TextWriter writer,
        bool color,
        int level,
        bool absoluteTimestamps,
        int bodyLimit
    )
    {
        _writer = writer;
        _color = color;
        _level = level;
        _absoluteTimestamps = absoluteTimestamps;
        _bodyLimit = bodyLimit;
        _sw = Stopwatch.StartNew();
    }

    /// <summary>Creates a log that writes to stderr with optional color detection.</summary>
    public static DiagnosticLog Stderr(
        int level,
        bool absoluteTimestamps = false,
        int bodyLimit = 8192
    )
    {
        if (level <= 0)
            return Null;
        var color =
            !System.Console.IsErrorRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null
            && Environment.GetEnvironmentVariable("TERM") != "dumb";
        return new DiagnosticLog(System.Console.Error, color, level, absoluteTimestamps, bodyLimit);
    }

    /// <summary>Creates a log that writes to a file (no color).</summary>
    public static DiagnosticLog ToFile(
        string path,
        int level,
        bool absoluteTimestamps = false,
        int bodyLimit = 8192
    )
    {
        if (level <= 0)
            return Null;
        var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        return new DiagnosticLog(writer, false, level, absoluteTimestamps, bodyLimit);
    }

    // ── Scoping ──────────────────────────────────────────────────────

    /// <summary>Prints a tagged label and increments depth.</summary>
    public void BeginScope(string label)
    {
        if (_level == 0)
            return;
        WriteLine(label);
        _depth++;
    }

    /// <summary>Prints the closing └ and decrements depth.</summary>
    public void EndScope()
    {
        if (_level == 0)
            return;
        if (_depth > 0)
            _depth--;
        WriteLineRaw(FormatTimestamp() + " " + DimText("└"));
    }

    // ── Domain methods ───────────────────────────────────────────────

    /// <summary>Logs a credential/auth diagnostic message.</summary>
    public void Credential(string msg)
    {
        if (_level == 0)
            return;
        WriteLine(Colorize("[auth]", Magenta) + " " + msg);
    }

    /// <summary>Logs an HTTP request line and headers.</summary>
    public void HttpRequest(HttpMethod method, string url, HttpRequestMessage? request)
    {
        if (_level == 0)
            return;
        WriteLine(Colorize("[http]", Cyan) + $" {method} {url}");
        if (request is not null)
        {
            _depth++;
            LogHeaders(request.Headers);
            if (request.Content is not null)
            {
                LogHeaders(request.Content.Headers);
                if (_level >= 2)
                    LogBody(
                        request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                        request.Content.Headers.ContentType
                    );
            }
            _depth--;
        }
    }

    /// <summary>Logs an HTTP response status, timing, and headers.</summary>
    public void HttpResponse(HttpResponseMessage response, long elapsedMs)
    {
        if (_level == 0)
            return;
        var status = $"{(int)response.StatusCode} {response.ReasonPhrase} ({elapsedMs}ms)";
        WriteLine(Colorize("[http]", Cyan) + " ← " + status);
        _depth++;
        LogHeaders(response.Headers);
        LogHeaders(response.Content.Headers);

        var isError = !response.IsSuccessStatusCode;
        if (isError || _level >= 2)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            LogBody(body, response.Content.Headers.ContentType);
        }
        _depth--;
    }

    /// <summary>Logs a general trace diagnostic message.</summary>
    public void Trace(string msg)
    {
        if (_level == 0)
            return;
        WriteLine(Colorize("[trace]", Gray) + " " + msg);
    }

    // ── Internals ────────────────────────────────────────────────────

    private void LogHeaders(HttpHeaders headers)
    {
        foreach (var h in headers)
        {
            var value = string.Join(", ", h.Value);
            if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                value = Redact(value);
            WriteLine(DimText($"{h.Key}: {value}"));
        }
    }

    private void LogBody(string? body, MediaTypeHeaderValue? contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
            return;
        if (!IsPrintable(contentType))
            return;

        var display =
            body.Length > _bodyLimit
                ? body[.._bodyLimit] + $"... ({body.Length} bytes total)"
                : body;
        foreach (var line in display.Split('\n'))
            WriteLine(DimText(line.TrimEnd('\r')));
    }

    private static bool IsPrintable(MediaTypeHeaderValue? ct)
    {
        if (ct?.MediaType is not { } mt)
            return false;
        return mt.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mt.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || mt.StartsWith("application/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string Redact(string value)
    {
        if (value.Length <= 10)
            return "***";
        return value[..10] + "...";
    }

    private void WriteLine(string msg)
    {
        var prefix = FormatTimestamp() + " " + TreePrefix();
        WriteLineRaw(prefix + msg);
    }

    private void WriteLineRaw(string line)
    {
        lock (_writer)
        {
            _writer.WriteLine(line);
        }
    }

    private string FormatTimestamp()
    {
        if (_absoluteTimestamps)
            return DimText(DateTime.Now.ToString("HH:mm:ss.fff"));
        var elapsed = _sw.Elapsed;
        return DimText($"+{elapsed.TotalSeconds:F3}s");
    }

    private string TreePrefix()
    {
        if (_depth <= 0)
            return "";
        return DimText("│") + " ";
    }

    private string Colorize(string text, string ansiCode)
    {
        if (!_color)
            return text;
        return ansiCode + text + Reset;
    }

    private string DimText(string text)
    {
        if (!_color)
            return text;
        return Dim + text + Reset;
    }
}
