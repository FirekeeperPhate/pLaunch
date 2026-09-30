using System.Runtime.InteropServices;
using System.Text;

namespace pLaunch.Native;

/// <summary>Minimal IShellLinkW interop, used to read the target of .lnk files.</summary>
internal static class NativeShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    public class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        // Remaining methods are not called; declaring GetPath alone is enough because it is first in the vtable
    }

    /// <summary>The file a .lnk points to; null when it cannot be read or points to no file (e.g. a Store app).</summary>
    public static string? GetTarget(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }
}
