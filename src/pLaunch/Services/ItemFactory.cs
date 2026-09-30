using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>Builds launcher items from paths, URLs and shell parsing names.</summary>
public static class ItemFactory
{
    // Extensions hidden from the display name, like Explorer does for shortcuts
    static readonly HashSet<string> HiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".exe", ".appref-ms", ".bat", ".cmd", ".pif", ".msc",
    };

    // Prefix of the parsing names of Start menu apps (the AppsFolder CLSID)
    const string AppsFolderParsingName = "::{4234D49B-0245-4DF3-B780-3893943456E1}";
    const string AppsFolderParsingPrefix = AppsFolderParsingName + "\\";

    /// <summary>Whether a folder parsing name is the Applications folder (shell:AppsFolder) of the Start menu.</summary>
    public static bool IsAppsFolder(string? parsingName) =>
        string.Equals(parsingName, AppsFolderParsingName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(parsingName, "shell:AppsFolder", StringComparison.OrdinalIgnoreCase);

    public static LaunchItem? FromPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
            return null;
        if (Directory.Exists(path))
            return new LaunchItem { Kind = ItemKind.Folder, Target = Path.GetFullPath(path), Name = FolderName(path) };
        if (File.Exists(path))
            return new LaunchItem { Kind = ItemKind.File, Target = Path.GetFullPath(path), Name = FileDisplayName(path) };
        return null;
    }

    public static LaunchItem? FromUrl(string text, string? title = null)
    {
        if (!TryParseUrl(text, out var uri))
            return null;
        var name = string.IsNullOrWhiteSpace(title) ? DefaultUrlName(uri) : title.Trim();
        return new LaunchItem { Kind = ItemKind.Url, Target = uri.AbsoluteUri, Name = name };
    }

    /// <summary>A path, a URL, or a shell: name, as typed on the command line or pasted.</summary>
    public static LaunchItem? FromText(string text)
    {
        text = text.Trim();
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return FromShell(text, null);
        return FromPath(text) ?? FromUrl(text);
    }

    /// <summary>A shell item: <paramref name="parsingName"/> is a SIGDN_DESKTOPABSOLUTEPARSING name or a shell: path.</summary>
    public static LaunchItem? FromShell(string parsingName, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
            return null;
        var target = parsingName.StartsWith(AppsFolderParsingPrefix, StringComparison.OrdinalIgnoreCase)
            ? "shell:AppsFolder\\" + parsingName[AppsFolderParsingPrefix.Length..]
            : parsingName;
        var name = string.IsNullOrWhiteSpace(displayName) ? target[(target.LastIndexOf('\\') + 1)..] : displayName;
        return new LaunchItem { Kind = ItemKind.Shell, Target = target, Name = name };
    }

    static readonly HashSet<string> WebSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ftp", "mailto" };

    /// <summary>
    /// Whether Windows has a handler for a URI scheme (HKCR\&lt;scheme&gt; with a "URL Protocol" value).
    /// Replaceable in tests.
    /// </summary>
    internal static Func<string, bool> IsRegisteredProtocol { get; set; } = scheme =>
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") != null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    };

    /// <summary>
    /// A link to keep: no whitespace (so "Note: buy milk" is text, not a "note:" URI) and a scheme that
    /// is either a web one or registered with Windows (ms-settings:, steam://, ...).
    /// </summary>
    public static bool TryParseUrl(string text, out Uri uri)
    {
        text = text.Trim();
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        if (!text.Any(char.IsWhiteSpace)
            && Uri.TryCreate(text, UriKind.Absolute, out uri!)
            && !uri.IsFile && !uri.IsUnc
            && uri.Scheme.Length > 1 // "c:" parses as a scheme
            && (WebSchemes.Contains(uri.Scheme) || IsRegisteredProtocol(uri.Scheme)))
        {
            return true;
        }
        uri = null!;
        return false;
    }

    public static string FileDisplayName(string path)
    {
        var ext = Path.GetExtension(path);
        return HiddenExtensions.Contains(ext) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
    }

    static string FolderName(string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? full : name; // drive roots keep "C:\"
    }

    static string DefaultUrlName(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Host))
            return uri.OriginalString;
        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
    }
}
