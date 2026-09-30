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

    /// <summary>The icon, frozen (usable from any thread), or null when the shell has none (yet).</summary>
    public static Task<ImageSource?> GetAsync(LaunchItem item, int pixelSize)
    {
        var worker = item.Kind is ItemKind.File or ItemKind.Folder && Launcher.IsNetworkPath(item.Target) ? Network : Local;
        return worker.Enqueue(item.Kind == ItemKind.Url ? null : item.Target, pixelSize);
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

        /// <summary><paramref name="target"/> null = a web link, shown with the default browser's icon.</summary>
        public Task<ImageSource?> Enqueue(string? target, int size)
        {
            var result = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    var source = target ?? (_browserPath ??= NativeMethods.GetAssociatedExecutable("https") ?? "");
                    if (source.Length == 0)
                    {
                        result.SetResult(null);
                        return;
                    }
                    var key = (source.ToUpperInvariant(), size);
                    if (!_cache.TryGetValue(key, out var image) && Load(source, size) is { } loaded)
                        _cache[key] = image = loaded;
                    result.SetResult(image);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
                {
                    result.SetResult(null);
                }
            });
            return result.Task;
        }

        public void ClearCache() => _queue.Add(_cache.Clear);
    }

    static BitmapSource? Load(string parsingName, int size)
    {
        var iid = NativeMethods.IID_IShellItemImageFactory;
        if (NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
            return null;
        try
        {
            var hr = factory.GetImage(new NativeMethods.SIZE { cx = size, cy = size }, NativeMethods.SIIGBF.IconOnly, out var hbmp);
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
    /// The shell returns premultiplied pixels; old icons without alpha come back fully transparent.
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

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { anyAlpha = true; break; }
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
