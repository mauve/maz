namespace Console.Rendering;

public sealed class Throbber : IDisposable
{

    private readonly Timer? _timer;
    private readonly string _message;
    private int _frame;
    private bool _disposed;
    private readonly bool _active;

    public Throbber(string message)
    {
        _message = message;
        _active =
            !System.Console.IsErrorRedirected
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
            && Environment.GetEnvironmentVariable("TERM") != "dumb";

        if (_active)
            _timer = new Timer(Tick, null, 0, 80);
    }

    private void Tick(object? state)
    {
        if (_disposed)
            return;
        var frame = Ansi.ThrobberFrames[_frame % Ansi.ThrobberFrames.Length];
        _frame++;
        System.Console.Error.Write($"\r{frame} {_message}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer?.Dispose();

        if (_active)
        {
            var clearLine = new string(' ', _message.Length + 4);
            System.Console.Error.Write($"\r{clearLine}\r");
        }
    }
}
