using System.Text.RegularExpressions;
using Microsoft.Win32;
using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>
/// The search box as a Run box: what is typed can be opened even when it is not in the list. A path
/// ("C:\Projects", "%TEMP%"), a URL or a web address ("github.com"), a command ("cmd", "ping 1.1.1.1"),
/// and a web search for anything else. The suggestions are made on the fly and never saved.
/// </summary>
public static partial class RunSuggestions
{
    public const string IdPrefix = "run:";
    public const string WebSearchId = IdPrefix + "web";

    static readonly Dictionary<WebSearch, string> SearchUrls = new()
    {
        [WebSearch.Google] = "https://www.google.com/search?q=",
        [WebSearch.Bing] = "https://www.bing.com/search?q=",
        [WebSearch.DuckDuckGo] = "https://duckduckgo.com/?q=",
    };

    // "github.com", "it.wikipedia.org/wiki/Roma", "example.com:8080"
    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}(?::\d{1,5})?(?:[/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"^localhost(?::\d{1,5})?(?:[/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Localhost();

    // File names look like web addresses ("notes.txt"): these endings are never taken for one
    static readonly HashSet<string> FileEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "bat", "cmd", "msc", "lnk", "txt", "log", "ini", "dll", "ps1", "json", "xml", "zip", "pdf",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "png", "jpg", "jpeg", "gif", "mp3", "mp4", "csv",
    };

    /// <summary>
    /// Suggestions for <paramref name="text"/>: <c>First</c> go above the list's own results (the text
    /// is plainly a path or an address), <c>Last</c> below them (a command, a web search).
    /// </summary>
    public static (List<LaunchItem> First, List<LaunchItem> Last) For(string text, WebSearch web)
    {
        text = text.Trim();
        var first = new List<LaunchItem>();
        var last = new List<LaunchItem>();
        if (text.Length == 0)
            return (first, last);

        if (PathItem(text) is { } path)
            first.Add(path);
        else if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !text.Any(char.IsWhiteSpace))
            first.Add(Suggestion(ItemKind.Shell, text, null, "Open " + text));
        else if (ItemFactory.TryParseUrl(text, out var uri))
            first.Add(Suggestion(ItemKind.Url, uri.AbsoluteUri, null, "Go to " + text));
        else
        {
            var (command, arguments) = SplitCommand(text);
            if (CommandTarget(command) is { } program)
                last.Add(Suggestion(ItemKind.File, program, arguments, "Run " + text));
            else if (arguments == null && IsWebAddress(text)
                     && Uri.TryCreate((Localhost().IsMatch(text) ? "http://" : "https://") + text, UriKind.Absolute, out var address))
                first.Add(Suggestion(ItemKind.Url, address.AbsoluteUri, null, "Go to " + text));
        }

        if (SearchUrls.TryGetValue(web, out var search))
        {
            var item = Suggestion(ItemKind.Url, search + Uri.EscapeDataString(text), null, $"Search the web for \x201C{text}\x201D");
            item.Id = WebSearchId;
            last.Add(item);
        }
        return (first, last);
    }

    static LaunchItem Suggestion(ItemKind kind, string target, string? arguments, string name) =>
        new(IdPrefix + kind + ":" + target + (arguments is null ? "" : " " + arguments))
        {
            Kind = kind,
            Target = target,
            Arguments = arguments,
            Name = name,
            IsLive = true,
            // Like the Run box: commands start in the user's folder, not in System32
            WorkingDirectory = kind == ItemKind.File ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null,
        };

    /// <summary>A whole path: an existing file or folder, or any network path (never probed: it can hang).</summary>
    static LaunchItem? PathItem(string text)
    {
        var path = Environment.ExpandEnvironmentVariables(text.Trim('"'));
        if (path.Length == 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            path += "\\"; // "D:" = the drive, not its current folder
        bool network = path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2;
        try
        {
            if (!network && !Path.IsPathFullyQualified(path))
                return null;
            if (network || Launcher.IsNetworkPath(path))
                return Suggestion(Path.HasExtension(path) ? ItemKind.File : ItemKind.Folder, path, null, "Open " + path);
            if (Directory.Exists(path))
                return Suggestion(ItemKind.Folder, Path.GetFullPath(path), null, "Open " + path);
            if (File.Exists(path))
                return Suggestion(ItemKind.File, Path.GetFullPath(path), null, "Open " + path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
        }
        return null;
    }

    /// <summary>"ping 1.1.1.1" = ("ping", "1.1.1.1"); a quoted first part may hold spaces.</summary>
    internal static (string Command, string? Arguments) SplitCommand(string text)
    {
        int end;
        string command;
        if (text.StartsWith('"'))
        {
            end = text.IndexOf('"', 1);
            if (end < 0)
                return (text.Trim('"'), null);
            command = text[1..end];
            end++;
        }
        else
        {
            end = text.IndexOfAny([' ', '\t']);
            if (end < 0)
                return (text, null);
            command = text[..end];
        }
        var rest = text[end..].Trim();
        return (command, rest.Length > 0 ? rest : null);
    }

    /// <summary>A program file named by a full path, or found like the Run box finds it (App Paths, then PATH).</summary>
    static string? CommandTarget(string command)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(command);
            if (Path.IsPathFullyQualified(expanded))
                return !Launcher.IsNetworkPath(expanded) && File.Exists(expanded) ? expanded : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        return ResolveCommand(command);
    }

    /// <summary>
    /// Finds a program by name: the "App Paths" of Windows ("winword", "chrome"), then the PATH folders
    /// with the PATHEXT extensions. Network folders in the PATH are skipped (they can hang).
    /// </summary>
    public static string? ResolveCommand(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':', '"', '*', '?', '<', '>', '|']) >= 0)
            return null;
        bool hasExtension = Path.HasExtension(name);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + (hasExtension ? name : name + ".exe"));
                if (key?.GetValue(null) is string registered
                    && Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"')) is var path
                    && File.Exists(path))
                    return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
            }
        }
        var extensions = hasExtension ? [""]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var folder = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (folder.Length == 0 || !Path.IsPathFullyQualified(folder) || Launcher.IsNetworkPath(folder))
                    continue;
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(folder, name + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    internal static bool IsWebAddress(string text)
    {
        if (Localhost().IsMatch(text))
            return true;
        if (!WebAddress().IsMatch(text))
            return false;
        var host = text.Split(':', '/', '?', '#')[0];
        return !FileEndings.Contains(host[(host.LastIndexOf('.') + 1)..]);
    }
}
