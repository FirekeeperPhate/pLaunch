using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>Shell icons for launcher items (must be called on an STA thread).</summary>
public static class IconProvider
{
    static readonly Dictionary<(string, int), ImageSource?> Cache = new();
    static string? _browserPath;

    public static void ClearCache() => Cache.Clear();

    public static ImageSource? Get(LaunchItem item, int pixelSize)
    {
        var source = item.Kind == ItemKind.Url ? BrowserPath() : item.Target;
        if (string.IsNullOrEmpty(source))
            return null;
        var key = (source.ToUpperInvariant(), pixelSize);
        if (!Cache.TryGetValue(key, out var image))
        {
            image = Load(source, pixelSize);
            Cache[key] = image;
        }
        return image;
    }

    /// <summary>The default browser, whose icon stands for web links.</summary>
    static string? BrowserPath() => _browserPath ??= NativeMethods.GetAssociatedExecutable("https") ?? "";

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
