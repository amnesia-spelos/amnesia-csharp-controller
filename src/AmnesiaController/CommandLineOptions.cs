using System.Globalization;

namespace AmnesiaController;

internal sealed record CommandLineOptions(string Host, int Port, TimeSpan Linger)
{
    public const string Usage =
        """
        Usage: dotnet run --project src/AmnesiaController -- [options]

          --host <host>   game host (default: 127.0.0.1)
          --port <port>   game port (default: 5150)
          --linger <ms>   after piped input ends, exit once the game is quiet this long (default: 2000)
          --help          show this help
        """;

    /// <summary>Parses arguments; returns <c>null</c> with an error message (or empty for --help) when not runnable.</summary>
    public static CommandLineOptions? Parse(string[] args, out string error)
    {
        var options = new CommandLineOptions("127.0.0.1", 5150, TimeSpan.FromMilliseconds(2000));
        error = "";

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
                return null;

            if (i + 1 >= args.Length)
            {
                error = $"Missing value for {args[i]}.";
                return null;
            }

            var value = args[++i];
            switch (args[i - 1])
            {
                case "--host" when !string.IsNullOrWhiteSpace(value):
                    options = options with { Host = value };
                    break;
                case "--port" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535:
                    options = options with { Port = port };
                    break;
                case "--linger" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms):
                    options = options with { Linger = TimeSpan.FromMilliseconds(ms) };
                    break;
                case "--host" or "--port" or "--linger":
                    error = $"Invalid value '{value}' for {args[i - 1]}.";
                    return null;
                default:
                    error = $"Unknown option {args[i - 1]}.";
                    return null;
            }
        }

        return options;
    }
}
