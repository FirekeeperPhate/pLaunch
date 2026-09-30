using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>Turns drag-and-drop / clipboard data into launcher items.</summary>
public static class DropReader
{
    public const string ShellIdListFormat = "Shell IDList Array";
    public const string UrlFormatW = "UniformResourceLocatorW";
    public const string UrlFormat = "UniformResourceLocator";
    public const string FileGroupDescriptorFormatW = "FileGroupDescriptorW";

    public static bool CanRead(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop)
        || data.GetDataPresent(ShellIdListFormat)
        || data.GetDataPresent(UrlFormatW)
        || data.GetDataPresent(UrlFormat)
        || (data.GetDataPresent(DataFormats.UnicodeText) && ReadText(data) is { } t && ItemFactory.FromText(t) != null);

    public static List<LaunchItem> Read(IDataObject data)
    {
        // Explorer offers both FileDrop and Shell IDList: FileDrop keeps real paths
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
            return paths.Select(ItemFactory.FromPath).OfType<LaunchItem>().ToList();

        // Start menu apps, Control Panel items and other virtual objects
        if (data.GetDataPresent(ShellIdListFormat) && data.GetData(ShellIdListFormat) is MemoryStream idList)
        {
            var items = ReadShellIdList(idList.ToArray());
            if (items.Count > 0)
                return items;
        }

        // Links dragged from a browser
        var url = ReadString(data, UrlFormatW, Encoding.Unicode) ?? ReadString(data, UrlFormat, Encoding.Default);
        if (url != null && ItemFactory.FromUrl(url, ReadDescriptorTitle(data)) is { } link)
            return [link];

        if (ReadText(data) is { } text)
        {
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ItemFactory.FromText).OfType<LaunchItem>().ToList();
        }
        return [];
    }

    static string? ReadText(IDataObject data) =>
        data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;

    static string? ReadString(IDataObject data, string format, Encoding encoding)
    {
        if (!data.GetDataPresent(format))
            return null;
        return data.GetData(format) switch
        {
            string s => s,
            MemoryStream ms => DecodeZeroTerminated(ms.ToArray(), encoding),
            _ => null,
        };
    }

    internal static string DecodeZeroTerminated(byte[] bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        int zero = text.IndexOf('\0');
        return zero >= 0 ? text[..zero] : text;
    }

    /// <summary>Browsers describe a dragged link as a virtual "Page title.url" file.</summary>
    static string? ReadDescriptorTitle(IDataObject data)
    {
        if (!data.GetDataPresent(FileGroupDescriptorFormatW) || data.GetData(FileGroupDescriptorFormatW) is not MemoryStream ms)
            return null;
        return ParseDescriptorTitle(ms.ToArray());
    }

    internal static string? ParseDescriptorTitle(byte[] descriptor)
    {
        // FILEGROUPDESCRIPTORW: UINT cItems, then FILEDESCRIPTORW[] whose cFileName (WCHAR[260]) is at offset 72
        const int nameOffset = 4 + 72, nameBytes = 260 * 2;
        if (descriptor.Length < nameOffset + 2 || BitConverter.ToUInt32(descriptor, 0) == 0)
            return null;
        int length = Math.Min(nameBytes, descriptor.Length - nameOffset);
        var name = DecodeZeroTerminated(descriptor[nameOffset..(nameOffset + length)], Encoding.Unicode).Trim();
        if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            name = name[..^4].Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>Parses a CIDA (Shell IDList Array): a parent folder PIDL followed by child PIDLs.</summary>
    static List<LaunchItem> ReadShellIdList(byte[] cida)
    {
        var result = new List<LaunchItem>();
        if (cida.Length < 8)
            return result;
        int count = BitConverter.ToInt32(cida, 0);
        if (count <= 0 || cida.Length < 4 * (count + 2))
            return result;

        var handle = GCHandle.Alloc(cida, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = handle.AddrOfPinnedObject();
            IntPtr parent = basePtr + BitConverter.ToInt32(cida, 4);
            for (int i = 0; i < count; i++)
            {
                int offset = BitConverter.ToInt32(cida, 4 * (i + 2));
                if (offset <= 0 || offset >= cida.Length)
                    continue;
                IntPtr absolute = NativeMethods.ILCombine(parent, basePtr + offset);
                if (absolute == IntPtr.Zero)
                    continue;
                try
                {
                    var path = NativeMethods.GetNameFromIDList(absolute, NativeMethods.SIGDN.FileSysPath);
                    var item = path != null
                        ? ItemFactory.FromPath(path)
                        : ItemFactory.FromShell(
                            NativeMethods.GetNameFromIDList(absolute, NativeMethods.SIGDN.DesktopAbsoluteParsing) ?? "",
                            NativeMethods.GetNameFromIDList(absolute, NativeMethods.SIGDN.NormalDisplay));
                    if (item != null)
                        result.Add(item);
                }
                finally
                {
                    NativeMethods.ILFree(absolute);
                }
            }
        }
        finally
        {
            handle.Free();
        }
        return result;
    }
}
