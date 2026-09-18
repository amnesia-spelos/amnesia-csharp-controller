using System.Globalization;

namespace AmnesiaController.Core;

/// <summary>
/// The I/O-free Controller core. Adapters report inputs by calling its methods, always from a
/// single thread, and perform the effects each call returns.
/// </summary>
public sealed partial class Controller(TimeSpan linger)
{
    private static readonly string[] HelpLines =
    [
        "Directives (never sent to the game):",
        "  /help               show this help",
        "  /quit               exit the Controller",
        "  /reconnect          drop the connection and start a fresh Session",
        "  /clear              clear the screen",
        "  /mute <category>    hide received lines of a Line Category (still counted)",
        "  /unmute <category>  show them again and report how many were hidden",
        $"Categories: {string.Join(", ", LineCategories.AllNames)}",
        "Pose Recording and Pose Playback (negotiate, subscribe and create the Avatar yourself):",
        "  /pose-record <seconds>  record Local Pose State Updates for up to 3600 s of game time",
        "  /pose-record-stop       finish the recording early (cancels before the first sample)",
        "  /pose-record-cancel     abandon the recording and keep the previous buffer",
        "  /pose-play [avatar-id]  play the buffer as paced avatarpose Commands (default a1)",
        "  /pose-play-stop         stop playback and keep the buffer",
        "  /pose-status            show the active operation and the buffer",
        "Anything else is sent verbatim. Type // to send a line starting with /.",
    ];

    private readonly Dictionary<LineCategory, int> _hiddenCountByMutedCategory = [];
    private bool _connected;
    private bool _everConnected;
    private DateTime? _inputEndedAt;
    private DateTime _lastReceivedAt = DateTime.MinValue;

    public IReadOnlyList<ControllerEffect> LineEntered(string line, DateTime now)
    {
        if (line.StartsWith("//", StringComparison.Ordinal))
            return Send(line[1..], now);

        if (line.StartsWith('/'))
            return Directive(line, now);

        return Send(line, now);
    }

    public IReadOnlyList<ControllerEffect> WireLineReceived(string line, DateTime now)
    {
        _lastReceivedAt = now;
        var category = LineCategories.Recognise(line);
        var poseEffects = ObservePose(line, now);

        if (_hiddenCountByMutedCategory.TryGetValue(category, out var hidden))
        {
            _hiddenCountByMutedCategory[category] = hidden + 1;
            return [.. poseEffects];
        }

        return [new ShowLine(Timestamp(now), Direction.Received, category, line), .. poseEffects];
    }

    public IReadOnlyList<ControllerEffect> ConnectionEstablished(DateTime now)
    {
        _connected = true;
        _everConnected = true;
        return [Notice(now, "Connected.")];
    }

    public IReadOnlyList<ControllerEffect> ConnectionLost(DateTime now)
    {
        _connected = false;
        return [Notice(now, "Disconnected; retrying every second."), .. CancelPoseOperationOnDisconnect(now)];
    }

    /// <summary>Piped input has no more lines; the Controller lingers for late Wire Lines, then exits.</summary>
    public IReadOnlyList<ControllerEffect> InputEnded(DateTime now)
    {
        _inputEndedAt = now;
        return [];
    }

    public IReadOnlyList<ControllerEffect> TimePassed(DateTime now)
    {
        if (_inputEndedAt is not { } inputEndedAt || ActivePoseOperation() is not null)
            return [];

        var quietSince = inputEndedAt > _lastReceivedAt ? inputEndedAt : _lastReceivedAt;
        if (now - quietSince < linger)
            return [];

        return [new ExitController(_everConnected ? 0 : 1)];
    }

    private IReadOnlyList<ControllerEffect> Send(string wireLine, DateTime now)
    {
        if (!_connected)
            return [Notice(now, "Not connected; line not sent.")];

        return [new SendWireLine(wireLine), new ShowLine(Timestamp(now), Direction.Sent, null, wireLine)];
    }

    private IReadOnlyList<ControllerEffect> Directive(string line, DateTime now)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        var name = parts[0];
        var argument = parts.Length > 1 ? parts[1] : "";

        return name switch
        {
            "/help" => [.. HelpLines.Select(text => Notice(now, text))],
            "/quit" => [new ExitController(0)],
            "/clear" => [new ClearScreen()],
            "/reconnect" => [Notice(now, "Reconnecting."), new RequestReconnect()],
            "/mute" => [Mute(argument, now)],
            "/unmute" => [Unmute(argument, now)],
            "/pose-record" => PoseRecord(argument, now),
            "/pose-record-stop" => PoseRecordStop(now),
            "/pose-record-cancel" => PoseRecordCancel(now),
            "/pose-play" => PosePlay(argument, now),
            "/pose-play-stop" => PosePlayStop(now),
            "/pose-status" => PoseStatus(now),
            _ => [Notice(now, $"Unknown Directive {name}; type /help. Nothing was sent.")],
        };
    }

    private ShowLine Mute(string categoryName, DateTime now)
    {
        if (!LineCategories.TryParseName(categoryName, out var category))
            return UnknownCategory(categoryName, now);

        var name = LineCategories.NameOf(category);
        return _hiddenCountByMutedCategory.TryAdd(category, 0)
            ? Notice(now, $"Muted {name}.")
            : Notice(now, $"{name} is already muted.");
    }

    private ShowLine Unmute(string categoryName, DateTime now)
    {
        if (!LineCategories.TryParseName(categoryName, out var category))
            return UnknownCategory(categoryName, now);

        var name = LineCategories.NameOf(category);
        return _hiddenCountByMutedCategory.Remove(category, out var hidden)
            ? Notice(now, $"Unmuted {name}; {hidden} line(s) were hidden while muted.")
            : Notice(now, $"{name} is not muted.");
    }

    private static ShowLine UnknownCategory(string categoryName, DateTime now) =>
        Notice(now, $"Unknown Line Category '{categoryName}'; expected one of: {string.Join(", ", LineCategories.AllNames)}.");

    private static ShowLine Notice(DateTime now, string text) => new(Timestamp(now), Direction.Notice, null, text);

    private static string Timestamp(DateTime now) => now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
