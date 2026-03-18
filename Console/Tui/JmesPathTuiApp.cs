using System.Text.Json;
using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Interactive JMESPath explorer TUI. Reusable — any command can invoke it with arbitrary JSON.
/// Enters the alternate screen buffer on start and restores it on exit.
/// </summary>
internal sealed class JmesPathTuiApp
{
    private readonly string _inputJson;
    private readonly JsonElement _inputElement;
    private readonly DevLab.JmesPath.JmesPath _jmes = new();
    private readonly EditorPane _editor;
    private readonly JsonViewPane _inputPane = new();
    private readonly JsonViewPane _outputPane = new();

    private bool _running;
    private int _width;
    private int _height;
    private int _splitRow;
    private int _splitCol;
    private string? _result;

    public JmesPathTuiApp(string inputJson, string? initialQuery = null)
    {
        _inputJson = inputJson;
        _inputElement = JsonDocument.Parse(inputJson).RootElement;
        _editor = new EditorPane(
            initialQuery ?? "",
            JmesPathHighlighter.Highlight,
            "JMESPath Query"
        );
        _inputPane.SetTitle("Input (sample resources)");
        _inputPane.SetJson(inputJson);
        _outputPane.SetTitle("Output (JMESPath result)");
        if (!string.IsNullOrWhiteSpace(initialQuery))
            EvaluateExpression(initialQuery);
    }

    /// <summary>
    /// Returns the final JMESPath expression on accept (Ctrl+Enter/F5), or null on cancel (Esc).
    /// </summary>
    public async Task<string?> RunAsync(CancellationToken ct)
    {
        _width = System.Console.WindowWidth;
        _height = System.Console.WindowHeight;

        bool prevTreatCtrlC = System.Console.TreatControlCAsInput;
        System.Console.TreatControlCAsInput = true;

        System.Console.Write("\x1b[?1049h"); // enter alternate screen buffer
        System.Console.Write("\x1b[?25l"); // hide cursor during setup

        try
        {
            Redraw();
            System.Console.Write("\x1b[?25h"); // show cursor
            await MainLoop(ct);
            return _result;
        }
        finally
        {
            System.Console.Write("\x1b[?25h"); // ensure cursor is visible
            System.Console.Write("\x1b[?1049l"); // exit alternate screen buffer
            System.Console.TreatControlCAsInput = prevTreatCtrlC;
        }
    }

    private async Task MainLoop(CancellationToken ct)
    {
        _running = true;
        while (_running && !ct.IsCancellationRequested)
        {
            // Detect window resize
            if (System.Console.WindowWidth != _width || System.Console.WindowHeight != _height)
            {
                _width = System.Console.WindowWidth;
                _height = System.Console.WindowHeight;
                Redraw();
            }

            if (System.Console.KeyAvailable)
            {
                var key = System.Console.ReadKey(intercept: true);
                HandleKey(key);
                Redraw();
            }
            else
            {
                await Task.Delay(30, ct);
            }
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        // Ctrl+C: exit
        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _running = false;
            return;
        }

        // Escape: dismiss autocomplete first, then exit
        if (key.Key == ConsoleKey.Escape)
        {
            if (_editor.DismissAutocomplete())
                return;
            _running = false;
            return;
        }

        // F5 or Ctrl+Enter: accept expression
        if (key.Key == ConsoleKey.F5
            || (key.Key == ConsoleKey.Enter && (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            var expr = _editor.GetText().Trim();
            if (!string.IsNullOrEmpty(expr))
            {
                _result = expr;
                _running = false;
            }
            return;
        }

        // Scroll input pane: Ctrl+E up, Ctrl+D down
        if (key.Key == ConsoleKey.E && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _inputPane.ScrollUp(3);
            return;
        }
        if (key.Key == ConsoleKey.D && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _inputPane.ScrollDown(3);
            return;
        }

        // Scroll output pane: Ctrl+R up, Ctrl+F down
        if (key.Key == ConsoleKey.R && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _outputPane.ScrollUp(3);
            return;
        }
        if (key.Key == ConsoleKey.F && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _outputPane.ScrollDown(3);
            return;
        }

        // Autocomplete navigation
        if (_editor.IsAutocompleteVisible)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    _editor.AutocompleteUp();
                    return;
                case ConsoleKey.DownArrow:
                    _editor.AutocompleteDown();
                    return;
                case ConsoleKey.Tab:
                case ConsoleKey.Enter:
                    _editor.AutocompleteAccept();
                    EvaluateExpression(_editor.GetText());
                    return;
            }
        }

        // Tab: trigger autocomplete
        if (key.Key == ConsoleKey.Tab)
        {
            StartAutocomplete();
            return;
        }

        // Enter in editor: run query (single-line mode)
        if (key.Key == ConsoleKey.Enter)
        {
            EvaluateExpression(_editor.GetText());
            return;
        }

        // All other keys: delegate to editor, then re-evaluate
        _editor.HandleKey(key);
        EvaluateExpression(_editor.GetText());
    }

    private void EvaluateExpression(string expression)
    {
        expression = expression.Trim();
        if (string.IsNullOrEmpty(expression))
        {
            _outputPane.SetPlainText("");
            return;
        }

        try
        {
            var result = _jmes.Transform(_inputJson, expression);
            _outputPane.SetJson(result);
        }
        catch (Exception ex)
        {
            _outputPane.SetError(ex.Message);
        }
    }

    private void StartAutocomplete()
    {
        var prefix = _editor.GetWordAtCursor();
        var completions = JmesPathAutocomplete.GetCompletions(prefix, _inputElement);
        if (completions.Count == 1)
        {
            _editor.ShowAutocomplete(completions);
            _editor.AutocompleteAccept();
            EvaluateExpression(_editor.GetText());
        }
        else if (completions.Count > 1)
        {
            _editor.ShowAutocomplete(completions);
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Redraw()
    {
        if (_width < 10 || _height < 5)
            return;

        System.Console.Write("\x1b[?25l"); // hide cursor during redraw

        _splitRow = _height * 65 / 100;
        _splitCol = _width / 2;
        int editorHeight = _height - _splitRow - 1; // remaining for editor + status bar

        _inputPane.Render(0, 0, _splitCol, _splitRow, false);
        _outputPane.Render(0, _splitCol, _width - _splitCol, _splitRow, false);
        _editor.Render(_splitRow, 0, _width, editorHeight);
        DrawStatusBar(_height - 1);

        // Position cursor in editor
        var (curRow, curCol) = _editor.GetCursorScreenPosition(_splitRow);
        curRow = Math.Clamp(curRow, 0, _height - 2);
        curCol = Math.Clamp(curCol, 0, _width - 1);
        System.Console.Write($"\x1b[{curRow + 1};{curCol + 1}H");
        System.Console.Write("\x1b[?25h");
    }

    private void DrawStatusBar(int row)
    {
        System.Console.Write($"\x1b[{row + 1};1H");
        var bar =
            "  Tab: complete  │  Enter: run  │  Ctrl+Enter/F5: accept  │  Esc: exit  │  Ctrl+E/D: scroll input  │  Ctrl+R/F: scroll output  ";
        System.Console.Write(Ansi.Color(bar.PadRight(_width), "\x1b[7m"));
    }
}
