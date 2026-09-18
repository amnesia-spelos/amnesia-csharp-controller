using System.Globalization;
using System.Threading.Channels;
using AmnesiaController;
using AmnesiaController.Adapters;
using AmnesiaController.Core;

var options = CommandLineOptions.Parse(args, out var error);
if (options is null)
{
    if (error.Length == 0)
    {
        Console.WriteLine(CommandLineOptions.Usage);
        return 0;
    }

    Console.Error.WriteLine($"{error}\n\n{CommandLineOptions.Usage}");
    return 2;
}

var controller = new Controller(options.Linger);
var terminal = new ConsoleTerminal();
var transport = new TcpTransport(options.Host, options.Port);
using var shutdown = new CancellationTokenSource();

// Every input reaches the core through this channel, so the core only ever runs on one thread.
var inputs = Channel.CreateUnbounded<Func<DateTime, IReadOnlyList<ControllerEffect>>>();
void Report(Func<DateTime, IReadOnlyList<ControllerEffect>> input) => inputs.Writer.TryWrite(input);

terminal.Show(new ShowLine(
    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
    Direction.Notice,
    null,
    $"Connecting to {options.Host}:{options.Port}. Type /help for Directives."));

_ = transport.RunAsync(
    onEstablished: () => Report(controller.ConnectionEstablished),
    onReceived: line => Report(now => controller.WireLineReceived(line, now)),
    onLost: () => Report(controller.ConnectionLost),
    shutdown.Token);

_ = Task.Run(async () =>
{
    // Piped lines would otherwise race the first connection attempt and be rejected.
    if (Console.IsInputRedirected)
        await transport.FirstAttemptCompleted;

    await terminal.ReadInputAsync(
        onLineEntered: line => Report(now => controller.LineEntered(line, now)),
        onInputEnded: () => Report(controller.InputEnded));
});

_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
    while (await timer.WaitForNextTickAsync(shutdown.Token))
        Report(controller.TimePassed);
});

// At most one wakeup is pending; a newer one supersedes it. The core also ignores stale tokens.
CancellationTokenSource? pendingWakeup = null;

async Task WakeAsync(ScheduleWakeup wakeup, CancellationToken cancellation)
{
    // Task.Delay cannot wait longer than about 49 days; such a recorded gap is simply cut short.
    var maxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    var delay = wakeup.At - DateTime.Now;
    delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > maxDelay ? maxDelay : delay;
    try
    {
        await Task.Delay(delay, cancellation);
    }
    catch (OperationCanceledException)
    {
        return;
    }

    Report(now => controller.WakeupDue(wakeup.Token, now));
}

await foreach (var input in inputs.Reader.ReadAllAsync())
{
    foreach (var effect in input(DateTime.Now))
    {
        switch (effect)
        {
            case SendWireLine send:
                await transport.SendAsync(send.Line);
                break;
            case ShowLine show:
                terminal.Show(show);
                break;
            case ClearScreen:
                terminal.Clear();
                break;
            case RequestReconnect:
                transport.Reconnect();
                break;
            case ScheduleWakeup wakeup:
                pendingWakeup?.Cancel();
                pendingWakeup?.Dispose();
                pendingWakeup = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                _ = WakeAsync(wakeup, pendingWakeup.Token);
                break;
            case ExitController exit:
                shutdown.Cancel();
                terminal.Close();
                return exit.ExitCode;
        }
    }
}

return 0;
