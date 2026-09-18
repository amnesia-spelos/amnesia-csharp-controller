using System.Globalization;
using System.Text.RegularExpressions;

namespace AmnesiaController.Core;

/// <summary>One recorded Local Pose: its game time and the original text after <c>STATE localpose </c>.</summary>
internal readonly partial record struct PoseSample(ulong TimeMs, string Payload)
{
    private const string Prefix = "STATE localpose ";

    /// <summary>
    /// Accepts exactly <c>STATE localpose &lt;timeMs&gt; &lt;teleportCounter&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;yaw&gt; &lt;pitch&gt; &lt;crouch&gt; &lt;map&gt;</c>
    /// as the Game Interaction Protocol writes it; the map runs to the end of the line.
    /// </summary>
    public static bool TryParse(string wireLine, out PoseSample sample)
    {
        sample = default;
        if (!wireLine.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var payload = wireLine[Prefix.Length..];
        var fields = payload.Split(' ', 9);
        if (fields.Length != 9 || fields[8].Length == 0)
            return false;

        if (!ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var timeMs)
            || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !fields[2..7].All(IsNumber)
            || fields[7] is not ("0" or "1"))
            return false;

        sample = new PoseSample(timeMs, payload);
        return true;
    }

    private static bool IsNumber(string field) => Number().IsMatch(field) && field.Count(char.IsAsciiDigit) <= 15;

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?\z")]
    private static partial Regex Number();
}
