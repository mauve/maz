namespace Console.Cli.Commands.Bootstrap;

/// <summary>Shared border-rendering helpers used by the bootstrap wizard and configure command.</summary>
internal static class WizardUi
{
    public static int GetTermWidth()
    {
        try { return Math.Clamp(System.Console.WindowWidth, 40, 300); }
        catch { return 80; }
    }

    /// <summary>
    /// Renders the top border of a wizard slice.
    ///   ╔═  {title}  (n/total)  ══════════════╗
    /// </summary>
    public static void RenderTopBorder(string title, int stepIndex, int total, int boxWidth, bool dim = false)
    {
        var border  = dim ? "\x1b[2;37m" : "\x1b[35m";
        var bold    = dim ? "\x1b[2;37m" : "\x1b[1;35m";
        var regular = dim ? "\x1b[2;37m" : "\x1b[35m";

        var titleText = title.Length > 0
            ? $"  {title}  ({stepIndex + 1}/{total})  "
            : $"  ({stepIndex + 1}/{total})  ";
        var titleAnsi = title.Length > 0
            ? $"  {bold}{title}\x1b[0m{regular}  ({stepIndex + 1}/{total})  "
            : $"  {regular}({stepIndex + 1}/{total})  ";

        // ╔═{titleAnsi}{fill}╗  → 1 + 1 + titleText.Length + fill + 1 = boxWidth
        var topFill = Math.Max(0, boxWidth - 3 - titleText.Length);
        System.Console.Write($"{border}╔═{titleAnsi}{border}{new string('═', topFill)}╗\x1b[0m\n");
    }

    /// <summary>
    /// Renders the bottom border of a wizard slice with an embedded navigation hint.
    ///   ╚════  {hint}  ════╝
    /// navHint may contain ANSI codes; visible length is computed by stripping them.
    /// </summary>
    public static void RenderBottomBorder(string navHint, int boxWidth, bool dim = false)
    {
        var border = dim ? "\x1b[2;37m" : "\x1b[35m";
        var text   = dim ? "\x1b[2;37m" : "\x1b[0m";

        var visibleLen = StripAnsi(navHint).Length;
        var botFill = Math.Max(0, boxWidth - 2 - visibleLen);
        var leftFill = botFill / 2;
        var rightFill = botFill - leftFill;
        System.Console.Write(
            $"{border}╚{new string('═', leftFill)}\x1b[0m{text}{navHint}\x1b[0m{border}{new string('═', rightFill)}╝\x1b[0m\n"
        );
    }

    /// <summary>Moves the cursor to the specified 1-indexed terminal row and column.</summary>
    public static void MoveTo(int row, int col = 1) =>
        System.Console.Write($"\x1b[{row};{col}H");

    public static int GetTermHeight()
    {
        try { return Math.Clamp(System.Console.WindowHeight, 10, 500); }
        catch { return 24; }
    }

    private static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*m", "");
}
