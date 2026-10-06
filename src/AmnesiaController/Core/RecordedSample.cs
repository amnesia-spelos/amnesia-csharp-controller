using System.Globalization;
using System.Text.RegularExpressions;

namespace AmnesiaController.Core;

/// <summary>
/// One recorded line at a game time: a State Update, whose <paramref name="Payload"/> is the original text after its
/// <c>STATE &lt;name&gt; </c> prefix, or an Event, whose <paramref name="Payload"/> is the whole Command that reproduces it.
/// </summary>
internal readonly partial record struct RecordedSample(ulong TimeMs, string Payload, bool IsEvent = false)
{
    private const string LocalPosePrefix = "STATE localpose ";

    private const string ReportedBodiesPrefix = "STATE reportedbodies ";

    private const string InteractionStartPrefix = "EVENT interactionstart ";

    private const string InteractionEndPrefix = "EVENT interactionend ";

    private const string ReportBrokePrefix = "EVENT reportbroke ";

    private const int MaxBodyEntries = 32;

    private const int FieldsPerBodyEntry = 15;

    private const int FieldsPerBodyState = 13;

    private static readonly string[] InteractionEndings = ["released", "thrown", "too-far", "destroyed"];

    /// <summary>
    /// Turns an <c>interactionstart</c>, <c>interactionend</c> or <c>reportbroke</c> Event into the Command that
    /// reproduces it on a Peer-Driven Entity: <c>entityinteracting &lt;entityId&gt; 1|0 &lt;map&gt;</c> or
    /// <c>entitybreak &lt;entityId&gt; &lt;state&gt; &lt;map&gt;</c>. Other Events have no such Command.
    /// </summary>
    public static bool TryParseInteractionEvent(string wireLine, out string command)
    {
        command = "";
        if (wireLine.StartsWith(InteractionStartPrefix, StringComparison.Ordinal))
        {
            var fields = wireLine[InteractionStartPrefix.Length..].Split(' ', 3);
            if (fields.Length != 3 || !IsIdentifier(fields[0]) || !IsIdentifier(fields[1]) || fields[2].Length == 0)
                return false;

            command = $"entityinteracting {fields[0]} 1 {fields[2]}";
            return true;
        }

        if (wireLine.StartsWith(InteractionEndPrefix, StringComparison.Ordinal))
        {
            var fields = wireLine[InteractionEndPrefix.Length..].Split(' ', 4);
            if (fields.Length != 4 || !IsIdentifier(fields[0]) || !IsIdentifier(fields[1])
                || !InteractionEndings.Contains(fields[2]) || fields[3].Length == 0)
                return false;

            command = $"entityinteracting {fields[0]} 0 {fields[3]}";
            return true;
        }

        if (wireLine.StartsWith(ReportBrokePrefix, StringComparison.Ordinal))
        {
            var payload = wireLine[ReportBrokePrefix.Length..];
            var fields = payload.Split(' ', FieldsPerBodyState + 2);
            if (fields.Length != FieldsPerBodyState + 2 || !IsIdentifier(fields[0])
                || !fields[1..^1].All(IsNumber) || fields[^1].Length == 0)
                return false;

            command = "entitybreak " + payload;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Accepts exactly <c>STATE localpose &lt;timeMs&gt; &lt;teleportCounter&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;yaw&gt; &lt;pitch&gt; &lt;crouch&gt; &lt;map&gt;</c>
    /// as the Game Interaction Protocol writes it; the map runs to the end of the line.
    /// </summary>
    public static bool TryParseLocalPose(string wireLine, out RecordedSample sample)
    {
        sample = default;
        if (!wireLine.StartsWith(LocalPosePrefix, StringComparison.Ordinal))
            return false;

        var payload = wireLine[LocalPosePrefix.Length..];
        var fields = payload.Split(' ', 9);
        if (fields.Length != 9 || fields[8].Length == 0)
            return false;

        if (!TryParseTime(fields[0], out var timeMs)
            || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !fields[2..7].All(IsNumber)
            || fields[7] is not ("0" or "1"))
            return false;

        sample = new RecordedSample(timeMs, payload);
        return true;
    }

    /// <summary>
    /// Accepts exactly <c>STATE reportedbodies &lt;timeMs&gt; &lt;count&gt; &lt;entry&gt;... &lt;map&gt;</c> with a count of 0–32,
    /// where each entry is <c>&lt;entityId&gt; &lt;bodyId&gt;</c> and 13 numbers; the map runs to the end of the line.
    /// </summary>
    public static bool TryParseReportedBodies(string wireLine, out RecordedSample sample)
    {
        sample = default;
        if (!wireLine.StartsWith(ReportedBodiesPrefix, StringComparison.Ordinal))
            return false;

        var payload = wireLine[ReportedBodiesPrefix.Length..];
        var head = payload.Split(' ', 3);
        if (head.Length != 3
            || !TryParseTime(head[0], out var timeMs)
            || !int.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count > MaxBodyEntries)
            return false;

        var entryFieldCount = count * FieldsPerBodyEntry;
        var fields = head[2].Split(' ', entryFieldCount + 1);
        if (fields.Length != entryFieldCount + 1 || fields[^1].Length == 0)
            return false;

        for (var entry = 0; entry < entryFieldCount; entry += FieldsPerBodyEntry)
        {
            if (!IsIdentifier(fields[entry]) || !IsIdentifier(fields[entry + 1])
                || !fields[(entry + 2)..(entry + FieldsPerBodyEntry)].All(IsNumber))
                return false;
        }

        sample = new RecordedSample(timeMs, payload);
        return true;
    }

    private static bool TryParseTime(string field, out ulong timeMs) =>
        ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out timeMs);

    private static bool IsNumber(string field) => Number().IsMatch(field) && field.Count(char.IsAsciiDigit) <= 15;

    /// <summary>An Entity or Body Identifier: a signed decimal 32-bit integer without <c>+</c>.</summary>
    private static bool IsIdentifier(string field) =>
        Identifier().IsMatch(field) && int.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?\z")]
    private static partial Regex Number();

    [GeneratedRegex(@"^-?[0-9]+\z")]
    private static partial Regex Identifier();
}
