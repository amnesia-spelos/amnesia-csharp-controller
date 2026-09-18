namespace AmnesiaController.Core;

internal static class LineCategories
{
    private static readonly (string Marker, LineCategory Category)[] Markers =
    [
        ("RESPONSE:", LineCategory.Response),
        ("EVENT:", LineCategory.Event),
        ("STATE ", LineCategory.State),
        ("WARNING:", LineCategory.Warning),
        ("SCRIPT_CALL:", LineCategory.ScriptCall),
        ("Hello, from Amnesia", LineCategory.Greeting),
    ];

    private static readonly Dictionary<string, LineCategory> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["response"] = LineCategory.Response,
        ["event"] = LineCategory.Event,
        ["state"] = LineCategory.State,
        ["warning"] = LineCategory.Warning,
        ["script_call"] = LineCategory.ScriptCall,
        ["greeting"] = LineCategory.Greeting,
        ["uncategorised"] = LineCategory.Uncategorised,
    };

    public static IEnumerable<string> AllNames => Names.Keys;

    public static LineCategory Recognise(string wireLine)
    {
        foreach (var (marker, category) in Markers)
        {
            if (wireLine.StartsWith(marker, StringComparison.Ordinal))
                return category;
        }

        return LineCategory.Uncategorised;
    }

    public static bool TryParseName(string name, out LineCategory category) => Names.TryGetValue(name, out category);

    public static string NameOf(LineCategory category) => Names.First(pair => pair.Value == category).Key;
}
