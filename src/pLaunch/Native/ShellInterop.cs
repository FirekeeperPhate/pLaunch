using System.Runtime.InteropServices;
using System.Text;

namespace pLaunch.Native;

/// <summary>Shell interop for taskbar identity, icon picking/extraction and shell item ids.</summary>
internal static class ShellInterop
{
    // ---- Taskbar identity of a window (named lists) ----

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    /// <summary>
    /// A PROPVARIANT holding a string (VT_LPWSTR); the pointer is freed with PropVariantClear. It is 24
    /// bytes on x64 (the union holds 16-byte counted arrays): PropVariantClear zeroes all of it, so a
    /// shorter struct gets the stack around it overwritten.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;

        public static PROPVARIANT FromString(string value) =>
            new() { vt = 31 /* VT_LPWSTR */, pointer = Marshal.StringToCoTaskMemUni(value) };
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT value);

    static readonly Guid AppUserModelKeys = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static readonly PROPERTYKEY RelaunchCommandKey = new(AppUserModelKeys, 2);
    static readonly PROPERTYKEY RelaunchIconKey = new(AppUserModelKeys, 3);
    static readonly PROPERTYKEY RelaunchDisplayNameKey = new(AppUserModelKeys, 4);
    static readonly PROPERTYKEY IdKey = new(AppUserModelKeys, 5);

    /// <summary>
    /// Gives a window its own taskbar button (AppUserModelID) and what a pinned copy of that button runs:
    /// without the relaunch properties Windows would pin plain "pLaunch.exe" (the default list).
    /// </summary>
    public static void SetWindowIdentity(IntPtr hwnd, string appId, string relaunchCommand, string displayName, string icon)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null)
            return;
        try
        {
            // The relaunch properties must be set before the id
            Set(store, RelaunchCommandKey, relaunchCommand);
            Set(store, RelaunchDisplayNameKey, displayName);
            Set(store, RelaunchIconKey, icon);
            Set(store, IdKey, appId);
            store.Commit();
        }
        catch (COMException)
        {
            // Explorer not ready or refusing: the window just groups with the exe
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    static void Set(IPropertyStore store, PROPERTYKEY key, string value)
    {
        var variant = PROPVARIANT.FromString(value);
        try { store.SetValue(ref key, ref variant); }
        finally { PropVariantClear(ref variant); }
    }

    // ---- Icons ----

    [DllImport("shell32.dll", EntryPoint = "#62", CharSet = CharSet.Unicode)]
    static extern int PickIconDlg(IntPtr hwnd, StringBuilder path, int size, ref int index);

    /// <summary>The Windows "Change Icon" dialog; null when cancelled.</summary>
    public static (string Path, int Index)? PickIcon(IntPtr owner, string? path, int index)
    {
        var buffer = new StringBuilder(string.IsNullOrWhiteSpace(path) ? @"%SystemRoot%\System32\shell32.dll" : path, 1024);
        if (PickIconDlg(owner, buffer, buffer.Capacity, ref index) == 0)
            return null;
        return (buffer.ToString(), index);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIconW(string iconFile, int index, uint flags, out IntPtr large, IntPtr small, uint iconSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    /// <summary>An icon resource of an .exe/.dll at <paramref name="size"/> pixels (HICON, caller destroys it).</summary>
    public static IntPtr ExtractIcon(string file, int index, int size) =>
        SHDefExtractIconW(file, index, 0, out var icon, IntPtr.Zero, (uint)size) == 0 ? icon : IntPtr.Zero;

    // ---- Shell item ids (dragging Store apps out) ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    static extern uint ILGetSize(IntPtr pidl);

    /// <summary>
    /// A "Shell IDList Array" (CIDA) with one item, as Explorer and the Start menu put on the clipboard:
    /// the desktop as parent (an empty id list) and the item's absolute id list as child. Null if the
    /// name cannot be parsed.
    /// </summary>
    public static MemoryStream? CreateIdListArray(string parsingName)
    {
        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return null;
        try
        {
            int size = (int)ILGetSize(pidl);
            var child = new byte[size];
            Marshal.Copy(pidl, child, 0, size);
            // UINT count; UINT offsets[count + 1]; parent id list; child id list
            const int header = 4 * 3;
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(1u);
                writer.Write((uint)header);
                writer.Write((uint)(header + 2));
                writer.Write((ushort)0); // empty id list = the desktop
                writer.Write(child);
            }
            stream.Position = 0;
            return stream;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }
}
