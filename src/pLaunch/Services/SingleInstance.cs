using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>
/// One pLaunch per user session. A second start forwards its request to the running instance
/// through a named pipe: "show" (open the popup) or the paths/URLs to add.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly string _pipeName;
    CancellationTokenSource? _cts;

    public bool IsPrimary { get; }

    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _mutex = new Mutex(true, $@"Local\pLaunch-{sid}", out bool created);
        IsPrimary = created;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        _pipeName = $"pLaunch-{sid}-{process.SessionId}"; // pipes are machine-wide, the Local\ mutex is per session
    }

    /// <summary>Sends the arguments to the running instance; an empty list just shows its popup.</summary>
    public bool SendToPrimary(IReadOnlyList<string> arguments)
    {
        // The primary is in the background: let it bring its popup to the front
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join("\n", arguments));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for other instances; <paramref name="onRequest"/> runs on a worker thread.</summary>
    public void StartServer(Action<IReadOnlyList<string>> onRequest)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(token);
                    onRequest(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // A client that disconnected early: wait for the next one
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsPrimary)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
