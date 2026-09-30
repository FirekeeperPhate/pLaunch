using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace pLaunch.Services;

public sealed record UpdateInfo(Version Version, string AssetName, Uri Download, string Sha256, Uri Page, string Notes);

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    /// <summary>No published release could be read (none yet, or the repository is private).</summary>
    NoRelease,
    Failed,
}

/// <summary>
/// Updates from the GitHub releases of FirekeeperPhate/pLaunch, like pViewer: the latest release's
/// installer of the same edition (Full or Light) is downloaded, checked against its SHA-256 digest and
/// run silently. Every running list closes first and is started again by the installer.
/// </summary>
public static class UpdateService
{
    const string LatestReleaseApi = "https://api.github.com/repos/FirekeeperPhate/pLaunch/releases/latest";
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // Lazy: a static field initialized here would run before CurrentVersion below (textual order)
    static readonly Lazy<HttpClient> LazyHttp = new(CreateClient);
    static HttpClient Http => LazyHttp.Value;

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"pLaunch/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    /// <summary>The Full edition carries the .NET runtime next to the exe.</summary>
    public static string Edition { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")) ? "Full" : "Light";

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Whether an automatic check is due (enabled, and 24 hours since the last one of any list).</summary>
    public static bool IsCheckDue(AppConfig config, DateTime nowUtc) =>
        config.CheckForUpdates && (config.LastUpdateCheck is not { } last || nowUtc - last >= CheckInterval);

    public static async Task<(UpdateCheckStatus Status, UpdateInfo? Info)> CheckAsync(CancellationToken token = default)
    {
        AppConfig.Update(c => c.LastUpdateCheck = DateTime.UtcNow);
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, token);
            // A private repository answers 404 like a repository without releases
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (UpdateCheckStatus.NoRelease, null);
            if (!response.IsSuccessStatusCode)
                return (UpdateCheckStatus.Failed, null);
            var info = Parse(await response.Content.ReadAsStringAsync(token), CurrentVersion, Edition);
            return info == null ? (UpdateCheckStatus.UpToDate, null) : (UpdateCheckStatus.Available, info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (UpdateCheckStatus.Failed, null);
        }
    }

    /// <summary>
    /// The update in a GitHub "latest release" answer: newer than <paramref name="current"/>, with the
    /// installer of <paramref name="edition"/> ("pLaunch-Setup-X.Y.Z-Full.exe") and its SHA-256 digest.
    /// Null when there is nothing newer or nothing safe to install.
    /// </summary>
    internal static UpdateInfo? Parse(string json, Version current, string edition)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= current)
            return null;
        version = Normalize(version);

        var expected = $"pLaunch-Setup-{version}-{edition}.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), expected, StringComparison.OrdinalIgnoreCase))
                continue;
            // Without a digest the download cannot be verified: better no update than an unchecked installer
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return null;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var download) || download.Scheme != Uri.UriSchemeHttps)
                return null;
            var page = root.TryGetProperty("html_url", out var p) && Uri.TryCreate(p.GetString(), UriKind.Absolute, out var pageUri)
                ? pageUri
                : new Uri("https://github.com/FirekeeperPhate/pLaunch/releases");
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(version, expected, download, digest[7..].ToLowerInvariant(), page, notes);
        }
        return null;
    }

    /// <summary>Downloads the installer to a temporary folder and checks its digest; throws when it does not match.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken token = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pLaunch-update");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, info.AssetName);
        using (var response = await Http.GetAsync(info.Download, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total);
            }
        }
        string actual;
        await using (var stream = File.OpenRead(file))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        if (actual != info.Sha256)
        {
            File.Delete(file);
            throw new InvalidDataException("The downloaded installer does not match its published SHA-256 digest.");
        }
        return file;
    }

    // ---------------------------------------------------------------- closing and restarting the lists

    static string ExitEventName => $@"Local\pLaunch-ExitForUpdate-{ListProfile.UserKey}";

    /// <summary>Lines of arguments of the lists to start again after the update, one per list.</summary>
    static string RelaunchFile => Path.Combine(AppConfig.ConfigDirectory, "relaunch.txt");

    /// <summary>
    /// Every running list waits on this event; when an update is about to be installed it notes its own
    /// command line in relaunch.txt and closes (<paramref name="exitForUpdate"/> runs on a worker thread).
    /// </summary>
    public static void ListenForExit(ListProfile profile, Action exitForUpdate)
    {
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
        var thread = new Thread(() =>
        {
            signal.WaitOne();
            // Several lists append at the same moment: retry while another one holds the file
            for (int attempt = 1; attempt <= 20; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(AppConfig.ConfigDirectory);
                    File.AppendAllText(RelaunchFile, (profile.Arguments + "--minimized").Trim() + Environment.NewLine);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(50);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Write("Cannot note the list for the relaunch", ex);
                }
            }
            exitForUpdate();
        })
        { IsBackground = true, Name = "pLaunch update listener" };
        thread.Start();
    }

    /// <summary>Asks every list (this one included) to close, then starts the installer.</summary>
    public static void InstallAndExit(string installer)
    {
        try { File.Delete(RelaunchFile); } catch (IOException) { }
        using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName))
            signal.Set();
        // The installer waits until no list holds pLaunch.Running any more, then relaunches them
        Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>"pLaunch --after-update", started by the installer: starts the lists noted in relaunch.txt.</summary>
    public static int RelaunchAfterUpdate()
    {
        // The event of the old processes must not close the new ones
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var signal))
        {
            signal.Reset();
            signal.Dispose();
        }
        var lines = new List<string>();
        try
        {
            if (File.Exists(RelaunchFile))
                lines = File.ReadAllLines(RelaunchFile).Where(l => l.Trim().Length > 0).Distinct().ToList();
            File.Delete(RelaunchFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (lines.Count == 0)
            lines.Add("--minimized");
        var exe = Environment.ProcessPath!;
        foreach (var arguments in lines)
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false })?.Dispose();
        return 0;
    }
}
