using System.IO.Pipes;
using System.Text;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>
/// One pLaunch per list and user session. A second start forwards its request to the running instance
/// through a named pipe: nothing (open the popup), the paths/URLs to add, or a command such as
/// <see cref="ReloadCommand"/>.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Held while any pLaunch runs: the installer (AppMutex) asks to close it first.</summary>
    public const string RunningMutexName = "pLaunch.Running";

    /// <summary>Sent by the list that moved the data folder: re-read app.json and reload the list.</summary>
    public const string ReloadCommand = "--reload-data";

    readonly Mutex _mutex;
    readonly Mutex? _running;
    readonly string _pipeName;
    CancellationTokenSource? _cts;

    public bool IsPrimary { get; }

    public SingleInstance(ListProfile profile)
    {
        _mutex = new Mutex(true, $@"Local\pLaunch-{ListProfile.UserKey}{profile.InstanceKey}", out bool created);
        IsPrimary = created;
        if (created)
            _running = new Mutex(false, RunningMutexName);
        _pipeName = PipeName(profile);
    }

    // Pipes are machine-wide, the Local\ mutex is per session: the session is part of the name
    static string PipeName(ListProfile profile)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return $"pLaunch-{ListProfile.UserKey}-{process.SessionId}{profile.InstanceKey}";
    }

    /// <summary>Sends the arguments to the running instance; an empty list just shows its popup.</summary>
    public bool SendToPrimary(IReadOnlyList<string> arguments)
    {
        // The primary is in the background: let it bring its popup to the front
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        return Send(_pipeName, arguments, 3000);
    }

    /// <summary>Sends a request to the running instance of another list; false when it is not running.</summary>
    public static bool SendTo(ListProfile profile, IReadOnlyList<string> arguments, int timeoutMs = 500) =>
        Send(PipeName(profile), arguments, timeoutMs);

    static bool Send(string pipeName, IReadOnlyList<string> arguments, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
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
        _running?.Dispose();
        if (IsPrimary)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
