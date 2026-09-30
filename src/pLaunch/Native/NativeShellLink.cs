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
}
