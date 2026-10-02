using System.Runtime.InteropServices;

namespace pLaunch.Native;

/// <summary>An icon handle from the bytes of an .ico file (one that is not on disk: a resource of the program).</summary>
internal static class IconFile
{
    /// <summary>
    /// The image of the file to use for an icon <paramref name="size"/> pixels wide: that size, else the
    /// next bigger one (scaled down), else the biggest there is. Null when the bytes are not an icon file.
    /// </summary>
    internal static (int Offset, int Length, int Width)? PickEntry(byte[] ico, int size)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1)
            return null;
        int count = BitConverter.ToUInt16(ico, 4);
        (int Offset, int Length, int Width)? best = null;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i;
            if (entry + 16 > ico.Length)
                break;
            int width = ico[entry] == 0 ? 256 : ico[entry];
            int length = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
            if (offset < 0 || length <= 0 || (long)offset + length > ico.Length)
                continue;
            bool better = best is not { } b
                || (b.Width < size ? width > b.Width : width >= size && width < b.Width);
            if (better)
                best = (offset, length, width);
        }
        return best;
    }

    /// <summary>The icon at <paramref name="size"/> pixels; zero when it cannot be made. Destroy it with DestroyIcon.</summary>
    public static IntPtr Load(byte[] ico, int size)
    {
        if (PickEntry(ico, size) is not { } entry)
            return IntPtr.Zero;
        const uint Version = 0x00030000;
        return CreateIconFromResourceEx(ref ico[entry.Offset], (uint)entry.Length, true, Version, size, size, 0);
    }

    [DllImport("user32.dll")]
    static extern IntPtr CreateIconFromResourceEx(ref byte bits, uint size, bool icon, uint version, int width, int height, uint flags);
}
