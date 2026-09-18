namespace AmnesiaController.Core;

/// <summary>Something the Controller core asks its adapters to do.</summary>
public abstract record ControllerEffect;

/// <summary>Send one Wire Line to the game. The line carries no terminator.</summary>
public sealed record SendWireLine(string Line) : ControllerEffect;

/// <summary>Show one line to the developer.</summary>
/// <param name="Timestamp">Local time formatted as <c>HH:mm:ss.fff</c>.</param>
/// <param name="Direction">Whether the line was sent, received, or is a local notice.</param>
/// <param name="Category">The Line Category of a received Wire Line; <c>null</c> for sent lines and notices.</param>
public sealed record ShowLine(string Timestamp, Direction Direction, LineCategory? Category, string Text) : ControllerEffect;

public sealed record ClearScreen : ControllerEffect;

public sealed record RequestReconnect : ControllerEffect;

public sealed record ExitController(int ExitCode) : ControllerEffect;

/// <summary>
/// Report <see cref="Controller.WakeupDue"/> with <paramref name="Token"/> once <paramref name="At"/> has passed.
/// A new wakeup supersedes any pending one, which may then be cancelled; a stale token has no effect.
/// </summary>
public sealed record ScheduleWakeup(DateTime At, long Token) : ControllerEffect;

public enum Direction
{
    /// <summary>Shown with <c>&gt;</c>.</summary>
    Sent,

    /// <summary>Shown with <c>&lt;</c>.</summary>
    Received,

    /// <summary>Shown with <c>*</c>.</summary>
    Notice,
}

public enum LineCategory
{
    Response,
    Event,
    State,
    Warning,
    ScriptCall,
    Greeting,
    Uncategorised,
}
