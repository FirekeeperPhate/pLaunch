using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pLaunch.Services;

/// <summary>
/// Site icons for web links. The site's home page is read once for its declared icons (the largest
/// non-SVG one wins), with /favicon.ico as fallback; the result is cached as PNG per host in
/// %AppData%\pLaunch\favicons for 30 days (a failure is remembered for 3 days).
/// </summary>
public static partial class FaviconService
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    static readonly TimeSpan FailureAge = TimeSpan.FromDays(3);
    const int MaxDownload = 1024 * 1024;

    static readonly HttpClient Http = CreateClient();
    static readonly Dictionary<string, Task<ImageSource?>> Pending = new();
    static readonly object Gate = new();

    public static string CacheDirectory => Path.Combine(AppConfig.ConfigDirectory, "favicons");

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) pLaunch");
        return client;
    }

    /// <summary>The site icon of <paramref name="page"/>'s host (frozen), or null when there is none.</summary>
    public static Task<ImageSource?> GetAsync(Uri page)
    {
        if (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(page.Host))
            return Task.FromResult<ImageSource?>(null);
        var key = CacheKey(page);
        lock (Gate)
        {
            // One download per host even when several links of the same site ask at once
            if (!Pending.TryGetValue(key, out var task))
                Pending[key] = task = Task.Run(() => LoadAsync(page, key));
            return task;
        }
    }

    /// <summary>Forgets every cached icon (menu: refresh), so they are fetched again.</summary>
    public static void ClearCache()
    {
        lock (Gate)
            Pending.Clear();
        try
        {
            if (Directory.Exists(CacheDirectory))
                Directory.Delete(CacheDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string CacheKey(Uri page) =>
        (page.IsDefaultPort ? page.Host : $"{page.Host}_{page.Port}").ToLowerInvariant();

    static async Task<ImageSource?> LoadAsync(Uri page, string key)
    {
        var file = Path.Combine(CacheDirectory, key + ".png");
        try
        {
            if (File.Exists(file))
            {
                var info = new FileInfo(file);
                var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                if (info.Length == 0 && age < FailureAge)
                    return null;
                if (info.Length > 0 && age < MaxAge)
                    return Decode(await File.ReadAllBytesAsync(file));
            }

            var png = await FetchAsync(new Uri(page, "/"));
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(file, png ?? []); // empty = no icon, try again in a few days
            return png == null ? null : Decode(png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>The best icon of a site as PNG bytes, or null.</summary>
    static async Task<byte[]?> FetchAsync(Uri home)
    {
        var candidates = new List<Uri>();
        try
        {
            using var response = await Http.GetAsync(home, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.Contains("html") != false)
            {
                var html = await ReadLimitedAsync(response, 512 * 1024);
                // Redirects (http -> https, www.) change the base of relative links
                var baseUri = response.RequestMessage?.RequestUri ?? home;
                candidates.AddRange(ParseIconLinks(System.Text.Encoding.UTF8.GetString(html), baseUri).Select(c => c.Url));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Home page unreachable: /favicon.ico may still answer
        }
        candidates.Add(new Uri(home, "/favicon.ico"));

        foreach (var url in candidates.Distinct())
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    continue;
                var bytes = await ReadLimitedAsync(response, MaxDownload);
                if (ToPng(bytes) is { } png)
                    return png;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
            }
        }
        return null;
    }

    static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
                break;
        }
        return buffer.ToArray();
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"\b(rel|href|sizes|type)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    /// <summary>
    /// Icon links declared in a page, best first: largest declared size (apple-touch-icon counts as 180),
    /// SVG left out (WPF cannot draw it).
    /// </summary>
    internal static List<(Uri Url, int Size)> ParseIconLinks(string html, Uri baseUri)
    {
        var found = new List<(Uri, int)>();
        foreach (Match tag in LinkTag().Matches(html))
        {
            string? rel = null, href = null, sizes = null, type = null;
            foreach (Match a in Attribute().Matches(tag.Value))
            {
                var value = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
                switch (a.Groups[1].Value.ToLowerInvariant())
                {
                    case "rel": rel = value.ToLowerInvariant(); break;
                    case "href": href = value.Trim(); break;
                    case "sizes": sizes = value.ToLowerInvariant(); break;
                    case "type": type = value.ToLowerInvariant(); break;
                }
            }
            if (rel == null || href == null || !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(r => r is "icon" or "apple-touch-icon" or "apple-touch-icon-precomposed"))
                continue;
            if (type == "image/svg+xml" || href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(baseUri, href, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                continue;
            int size = rel.Contains("apple-touch-icon") ? 180 : 0;
            if (sizes != null)
            {
                foreach (var s in sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = s.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w))
                        size = Math.Max(size, w);
                }
            }
            found.Add((url, size == 0 ? 16 : size));
        }
        return found.OrderByDescending(f => f.Item2).ToList();
    }

    /// <summary>Any image WPF reads (ICO, PNG, JPEG, GIF, BMP) as PNG, from its largest frame.</summary>
    static byte[]? ToPng(byte[] bytes)
    {
        if (bytes.Length < 8)
            return null;
        try
        {
            using var input = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.MaxBy(f => f.PixelWidth);
            if (frame == null || frame.PixelWidth < 8)
                return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null; // an HTML error page, an SVG, a broken file
        }
    }

    static ImageSource? Decode(byte[] png)
    {
        if (png.Length == 0)
            return null;
        using var input = new MemoryStream(png);
        var image = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }
}
