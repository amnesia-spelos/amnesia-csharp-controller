using System.Net.Sockets;
using System.Text;

namespace AmnesiaController.Adapters;

/// <summary>
/// TCP connection to the game with UTF-8 line framing. Keeps (re)connecting every second while
/// disconnected and reports what happens through the callbacks, which are invoked from a background task.
/// </summary>
internal sealed class TcpTransport(string host, int port)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _firstAttemptCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _interrupt;
    private NetworkStream? _stream;

    /// <summary>Completes once the first connection attempt has either succeeded or failed.</summary>
    public Task FirstAttemptCompleted => _firstAttemptCompleted.Task;

    public async Task RunAsync(Action onEstablished, Action<string> onReceived, Action onLost, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate)
                _interrupt = interrupt;

            try
            {
                await ConnectOnceAsync(interrupt.Token, onEstablished, onReceived, onLost, cancellationToken);
            }
            finally
            {
                // Clear before disposal so Reconnect never cancels a disposed source.
                lock (_gate)
                    _interrupt = null;
            }
        }
    }

    private async Task ConnectOnceAsync(
        CancellationToken interruptToken, Action onEstablished, Action<string> onReceived, Action onLost, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        if (await TryConnectAsync(client, interruptToken))
        {
            var stream = client.GetStream();
            lock (_gate)
                _stream = stream;
            _firstAttemptCompleted.TrySetResult();
            onEstablished();

            await ReadWireLinesAsync(stream, onReceived, interruptToken);

            lock (_gate)
                _stream = null;
            if (cancellationToken.IsCancellationRequested)
                return;
            onLost();
        }
        else
        {
            _firstAttemptCompleted.TrySetResult();
        }

        // A reconnect request cancels the interrupt, which also skips this wait.
        try
        {
            await Task.Delay(RetryDelay, interruptToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task SendAsync(string wireLine)
    {
        NetworkStream? stream;
        lock (_gate)
            stream = _stream;
        if (stream is null)
            return;

        try
        {
            await stream.WriteAsync(Utf8.GetBytes(wireLine + "\n"));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            // The read loop notices the broken connection and reports it.
        }
    }

    /// <summary>Drops the current connection (if any) and connects again immediately.</summary>
    public void Reconnect()
    {
        lock (_gate)
            _interrupt?.Cancel();
    }

    private async Task<bool> TryConnectAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Reads until the connection closes; accepts <c>\n</c> or <c>\r\n</c> terminators.</summary>
    private static async Task ReadWireLinesAsync(NetworkStream stream, Action<string> onReceived, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            {
                foreach (var c in buffer.AsSpan(0, read))
                {
                    if (c != '\n')
                    {
                        line.Append(c);
                        continue;
                    }

                    if (line.Length > 0 && line[^1] == '\r')
                        line.Length--;
                    onReceived(line.ToString());
                    line.Clear();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
