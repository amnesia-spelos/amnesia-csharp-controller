using System.Net.Sockets;
using System.Text;

var defaultHost = "127.0.0.1";
Console.Write($"▶️ Host (default: {defaultHost}): ");
var requestedHost = Console.ReadLine();
var host = string.IsNullOrWhiteSpace(requestedHost) ? defaultHost : requestedHost;
Console.WriteLine($"🖥️ Host: {host}");

var defaultPort = 5150;
Console.Write($"▶️ Port (default: {defaultPort}): ");
var requestedPort = Console.ReadLine();
var port = int.TryParse(requestedPort, out var parsedPort) ? parsedPort : defaultPort;
Console.WriteLine($"🔌 Port: {port}");

var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 5150);

var stream = client.GetStream();
var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
var reader = new StreamReader(stream, Encoding.ASCII);

var welcome = await reader.ReadLineAsync();
Console.WriteLine($"[Amnesia] {welcome}");

_ = Task.Run(async () =>
{
    while (true)
    {
        var r = await reader.ReadLineAsync();
        if (r == null) break; // Exit if EOF
        Console.WriteLine($"\x1b[36m[Amnesia]\x1b[0m {r}");
    }
});

/* COMMAND & LISTEN */
Console.WriteLine("✅ Connected and ready for commands!");
while (true)
{
    var command = Console.ReadLine();
    Console.WriteLine();

    if (command == "exit")
        break;

    await writer.WriteLineAsync(command);
}
