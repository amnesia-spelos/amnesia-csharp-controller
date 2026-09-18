using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AmnesiaController.Core;

namespace AmnesiaController.Adapters;

/// <summary>
/// Console input and output. Interactive input gets a small line editor with history whose
/// in-progress line is redrawn below every shown line; redirected input is read as plain lines.
/// </summary>
internal sealed class ConsoleTerminal
{
    private const string Prompt = "» ";
    private const string Reset = "\e[0m";

    private readonly Lock _gate = new();
    private readonly bool _interactive = !Console.IsInputRedirected;
    private readonly bool _styled = !Console.IsOutputRedirected;
    private readonly List<string> _history = [];
    private readonly StringBuilder _input = new();
    private int _cursor;
    private int _historyIndex;
    private string _draft = "";

    public ConsoleTerminal()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = utf8;
        Console.InputEncoding = utf8;
        if (_styled)
            WindowsConsole.EnableVirtualTerminalProcessing();
    }

    public void Show(ShowLine line)
    {
        lock (_gate)
        {
            EraseInputLine();
            Console.Out.Write(Format(line) + "\n");
            DrawInputLine();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_styled)
                Console.Out.Write("\e[2J\e[3J\e[H");
            DrawInputLine();
        }
    }

    /// <summary>Leaves the console on a clean line before the process exits.</summary>
    public void Close()
    {
        lock (_gate)
        {
            EraseInputLine();
            Console.Out.Flush();
        }
    }

    /// <summary>Reads entered lines on a background thread until input ends.</summary>
    public Task ReadInputAsync(Action<string> onLineEntered, Action onInputEnded) =>
        Task.Factory.StartNew(
            () =>
            {
                if (_interactive)
                    ReadInteractive(onLineEntered);
                else
                    ReadRedirected(onLineEntered, onInputEnded);
            },
            TaskCreationOptions.LongRunning);

    private static void ReadRedirected(Action<string> onLineEntered, Action onInputEnded)
    {
        while (Console.In.ReadLine() is { } line)
            onLineEntered(line);
        onInputEnded();
    }

    private void ReadInteractive(Action<string> onLineEntered)
    {
        lock (_gate)
            DrawInputLine();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            string? entered = null;

            lock (_gate)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        entered = _input.ToString();
                        if (entered.Length > 0 && (_history.Count == 0 || _history[^1] != entered))
                            _history.Add(entered);
                        _historyIndex = _history.Count;
                        _draft = "";
                        _input.Clear();
                        _cursor = 0;
                        break;
                    case ConsoleKey.Backspace when _cursor > 0:
                        var removed = CharWidthBefore(_cursor);
                        _cursor -= removed;
                        _input.Remove(_cursor, removed);
                        break;
                    case ConsoleKey.Delete when _cursor < _input.Length:
                        _input.Remove(_cursor, CharWidthAt(_cursor));
                        break;
                    case ConsoleKey.LeftArrow when _cursor > 0:
                        _cursor -= CharWidthBefore(_cursor);
                        break;
                    case ConsoleKey.RightArrow when _cursor < _input.Length:
                        _cursor += CharWidthAt(_cursor);
                        break;
                    case ConsoleKey.Home:
                        _cursor = 0;
                        break;
                    case ConsoleKey.End:
                        _cursor = _input.Length;
                        break;
                    case ConsoleKey.UpArrow when _historyIndex > 0:
                        if (_historyIndex == _history.Count)
                            _draft = _input.ToString();
                        RecallHistory(_historyIndex - 1);
                        break;
                    case ConsoleKey.DownArrow when _historyIndex < _history.Count:
                        RecallHistory(_historyIndex + 1);
                        break;
                    case ConsoleKey.Escape:
                        _input.Clear();
                        _cursor = 0;
                        break;
                    default:
                        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                        {
                            _input.Insert(_cursor, key.KeyChar);
                            _cursor++;
                        }
                        break;
                }

                DrawInputLine();
            }

            if (entered is not null)
                onLineEntered(entered);
        }
    }

    private void RecallHistory(int index)
    {
        _historyIndex = index;
        _input.Clear().Append(index == _history.Count ? _draft : _history[index]);
        _cursor = _input.Length;
    }

    private int CharWidthBefore(int index) =>
        index >= 2 && char.IsSurrogatePair(_input[index - 2], _input[index - 1]) ? 2 : 1;

    private int CharWidthAt(int index) =>
        index + 1 < _input.Length && char.IsSurrogatePair(_input[index], _input[index + 1]) ? 2 : 1;

    private void EraseInputLine()
    {
        if (_interactive && _styled)
            Console.Out.Write("\r\e[2K");
    }

    private void DrawInputLine()
    {
        if (!_interactive || !_styled)
            return;

        var text = _input.ToString();
        var columnsAfterCursor = new StringInfo(text[_cursor..]).LengthInTextElements;
        var moveBack = columnsAfterCursor > 0 ? $"\e[{columnsAfterCursor}D" : "";
        Console.Out.Write("\r\e[2K" + Prompt + text + moveBack);
    }

    private string Format(ShowLine line)
    {
        var marker = line.Direction switch
        {
            Direction.Sent => ">",
            Direction.Received => "<",
            _ => "*",
        };
        if (!_styled)
            return $"{line.Timestamp} {marker} {line.Text}";

        var colour = (line.Direction, line.Category) switch
        {
            (Direction.Notice, _) => "\e[94m",
            (Direction.Sent, _) => "\e[1m",
            (_, LineCategory.Response) => "\e[32m",
            (_, LineCategory.Event) => "\e[35m",
            (_, LineCategory.State) => "\e[34m",
            (_, LineCategory.Warning) => "\e[33m",
            (_, LineCategory.ScriptCall) => "\e[90m",
            (_, LineCategory.Greeting) => "\e[36m",
            _ => "",
        };
        return $"\e[2m{line.Timestamp}{Reset} {colour}{marker} {line.Text}{Reset}";
    }

    private static class WindowsConsole
    {
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessingFlag = 0x0004;

        public static void EnableVirtualTerminalProcessing()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var handle = GetStdHandle(StdOutputHandle);
            if (GetConsoleMode(handle, out var mode))
                SetConsoleMode(handle, mode | EnableVirtualTerminalProcessingFlag);
        }

        [DllImport("kernel32.dll")]
        private static extern nint GetStdHandle(int stdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(nint consoleHandle, out uint mode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(nint consoleHandle, uint mode);
    }
}
