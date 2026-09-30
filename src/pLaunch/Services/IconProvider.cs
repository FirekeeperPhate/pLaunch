using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>
/// Shell icons for launcher items, extracted off the UI thread: one STA worker for local items and one
/// for network paths, which can block for the length of an SMB timeout without holding up the others.
/// </summary>
public static class IconProvider
{
    static readonly IconWorker Local = new("pLaunch icons");
    static readonly IconWorker Network = new("pLaunch network icons");

    /// <summary>Web links show the site's icon (see <see cref="FaviconService"/>); off = the browser's icon.</summary>
    public static bool WebIconsEnabled { get; set; } = true;

    static readonly HashSet<string> IconResourceFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".icl", ".cpl", ".ocx", ".scr", ".mun",
    };

    /// <summary>The icon, frozen (usable from any thread), or null when there is none (yet).</summary>
    public static async Task<ImageSource?> GetAsync(LaunchItem item, int pixelSize)
    {
        // A custom icon wins, for any kind (sub-folders included)
        if (!string.IsNullOrWhiteSpace(item.IconPath))
        {
            var path = Environment.ExpandEnvironmentVariables(item.IconPath);
            var worker = Launcher.IsNetworkPath(path) ? Network : Local;
            return await worker.Enqueue($"{path}|{item.IconIndex}", pixelSize, () => LoadCustom(path, item.IconIndex, pixelSize));
        }
        // Not for search box suggestions: each typed letter would be another site to ask
        if (item.Kind == ItemKind.Url && WebIconsEnabled && !item.IsLive && Uri.TryCreate(item.Target, UriKind.Absolute, out var uri)
            && await FaviconService.GetAsync(uri) is { } favicon)
            return favicon;
        if (item.Kind == ItemKind.Url)
            return await Local.EnqueueBrowserIcon(pixelSize);
        // A command shows the icon of what runs it
        var target = item.Kind == ItemKind.Command ? Launcher.CommandHost(item.Shell) : item.Target;
        var targetWorker = item.Kind is ItemKind.File or ItemKind.Folder && Launcher.IsNetworkPath(target) ? Network : Local;
        return await targetWorker.Enqueue(target, pixelSize, () => Load(target, pixelSize, NativeMethods.SIIGBF.IconOnly));
    }

    /// <summary>An icon picked by the user: a resource of an .exe/.dll, or an image/.ico file shown as it is.</summary>
    static BitmapSource? LoadCustom(string path, int index, int size)
    {
        if (!IconResourceFiles.Contains(Path.GetExtension(path)))
            return Load(path, size, NativeMethods.SIIGBF.ResizeToFit);
        var icon = ShellInterop.ExtractIcon(path, index, size);
        if (icon == IntPtr.Zero)
            return null;
        try
        {
            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            ShellInterop.DestroyIcon(icon);
        }
    }

    public static void ClearCache()
    {
        Local.ClearCache();
        Network.ClearCache();
    }

    sealed class IconWorker
    {
        readonly BlockingCollection<Action> _queue = new();
        // Touched only on the worker thread. Failures are not cached: a file that comes back
        // (drive plugged in again) must get its icon on the next request.
        readonly Dictionary<(string, int), ImageSource> _cache = new();
        string? _browserPath;

        public IconWorker(string name)
        {
            var thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                    work();
            })
            { IsBackground = true, Name = name };
            thread.SetApartmentState(ApartmentState.STA); // shell COM objects
            thread.Start();
        }

        /// <summary>Runs <paramref name="load"/> on the worker, cached by <paramref name="key"/> and size.</summary>
        public Task<ImageSource?> Enqueue(string key, int size, Func<ImageSource?> load)
        {
            var result = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    var cacheKey = (key.ToUpperInvariant(), size);
                    if (!_cache.TryGetValue(cacheKey, out var image) && load() is { } loaded)
                        _cache[cacheKey] = image = loaded;
                    result.SetResult(image);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException
                                               or IOException or NotSupportedException or FileFormatException)
                {
                    result.SetResult(null);
                }
            });
            return result.Task;
        }

        /// <summary>The default browser's icon, standing for web links without a site icon.</summary>
        public Task<ImageSource?> EnqueueBrowserIcon(int size)
        {
            _browserPath ??= NativeMethods.GetAssociatedExecutable("https") ?? "";
            var browser = _browserPath;
            return browser.Length == 0
                ? Task.FromResult<ImageSource?>(null)
                : Enqueue(browser, size, () => Load(browser, size, NativeMethods.SIIGBF.IconOnly));
        }

        public void ClearCache() => _queue.Add(_cache.Clear);
    }

    static BitmapSource? Load(string parsingName, int size, NativeMethods.SIIGBF flags)
    {
        var iid = NativeMethods.IID_IShellItemImageFactory;
        if (NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
            return null;
        try
        {
            var hr = factory.GetImage(new NativeMethods.SIZE { cx = size, cy = size }, flags, out var hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbmp); }
            finally { NativeMethods.DeleteObject(hbmp); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies a 32-bit DIB keeping its alpha channel (Imaging.CreateBitmapSourceFromHBitmap drops it).
    /// Old icons without alpha come back fully transparent.
    /// </summary>
    static BitmapSource? FromHBitmap(IntPtr hbmp)
    {
        if (NativeMethods.GetObject(hbmp, Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            if (NativeMethods.GetDIBits(hdc, hbmp, 0, (uint)h, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }

        var bitmap = BitmapSource.Create(w, h, 96, 96, AlphaFormat(pixels), null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// How the alpha of 32-bit BGRA pixels from the shell is to be read. Icons come back with straight
    /// alpha (the color as it is, however transparent the pixel), thumbnails premultiplied: read the wrong
    /// way, the soft edges and shadows of icons show as a light, half opaque background. Only straight
    /// alpha can have a color channel above the alpha. No alpha at all (old icons): made opaque.
    /// </summary>
    internal static PixelFormat AlphaFormat(byte[] pixels)
    {
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            anyAlpha |= alpha != 0;
            straight |= pixels[i] > alpha || pixels[i + 1] > alpha || pixels[i + 2] > alpha;
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return PixelFormats.Bgra32;
        }
        return straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
    }
}
