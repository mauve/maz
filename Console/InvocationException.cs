namespace Console;

public class InvocationException(string message, int code = 1, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int ExitCode { get; } = code;
}
