using System.Text;
using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch.Tests;

public sealed class ItemTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pLaunchTests-" + Guid.NewGuid().ToString("N"));

    public ItemTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Store_RoundTripsItems()
    {
        var store = new ItemStore(Path.Combine(_dir, "sub", "items.json"));
        var items = new List<LaunchItem>
        {
            new() { Kind = ItemKind.File, Name = "Notepad", Target = @"C:\Windows\notepad.exe" },
            new() { Kind = ItemKind.Url, Name = "Example", Target = "https://example.com/" },
            new() { Kind = ItemKind.Shell, Name = "Calculator", Target = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", Arguments = "x" },
        };
        store.Save(items, new LauncherSettings());
        var loaded = store.Load().Items;
        Assert.Equal(3, loaded.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Assert.Equal(items[i].Id, loaded[i].Id);
            Assert.Equal(items[i].Name, loaded[i].Name);
            Assert.True(items[i].IsSameTarget(loaded[i]));
        }
        Assert.Contains("\"Url\"", File.ReadAllText(store.FilePath)); // enums stored by name
    }

    [Fact]
    public void Store_MissingFile_IsEmpty()
    {
        Assert.Empty(new ItemStore(Path.Combine(_dir, "none.json")).Load().Items);
    }

    [Fact]
    public void Store_CorruptFile_IsSetAsideAndEmpty()
    {
        var path = Path.Combine(_dir, "items.json");
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new ItemStore(path).Load().Items);
        Assert.True(File.Exists(path + ".bad"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Store_LockedFile_StartsEmptyAndNeverOverwritesIt()
    {
        var path = Path.Combine(_dir, "items.json");
        var store = new ItemStore(path) { ReadAttempts = 2 };
        store.Save([new LaunchItem { Kind = ItemKind.Url, Name = "Keep me", Target = "https://example.com/" }], new LauncherSettings());
        var original = File.ReadAllText(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(store.Load().Items);
            Assert.NotNull(store.LoadError);
            Assert.Throws<IOException>(() => store.Save([], new LauncherSettings()));
        }
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(store.Load().Items); // readable again: back to normal
        Assert.Null(store.LoadError);
    }

    [Fact]
    public void FromPath_FileAndFolder()
    {
        var file = Path.Combine(_dir, "Report.pdf");
        var shortcut = Path.Combine(_dir, "My App.lnk");
        File.WriteAllText(file, "");
        File.WriteAllText(shortcut, "");

        var doc = ItemFactory.FromPath(file)!;
        Assert.Equal(ItemKind.File, doc.Kind);
        Assert.Equal("Report.pdf", doc.Name);
        Assert.Equal("My App", ItemFactory.FromPath("\"" + shortcut + "\"")!.Name);

        var folder = ItemFactory.FromPath(_dir)!;
        Assert.Equal(ItemKind.Folder, folder.Kind);
        Assert.Equal(Path.GetFileName(_dir), folder.Name);

        Assert.Null(ItemFactory.FromPath(Path.Combine(_dir, "missing.txt")));
    }

    [Fact]
    public void FromPath_DriveRoot_KeepsThePath()
    {
        Assert.Equal(@"C:\", ItemFactory.FromPath(@"C:\")!.Name);
    }

    [Theory]
    [InlineData("https://www.example.com/page", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("mailto:someone@example.com", "example.com")]
    public void FromUrl_DefaultNames(string url, string name)
    {
        var item = ItemFactory.FromUrl(url)!;
        Assert.Equal(ItemKind.Url, item.Kind);
        Assert.Equal(name, item.Name);
    }

    [Theory]
    [InlineData(@"C:\nowhere\file.txt")]
    [InlineData(@"\\server\share")]
    [InlineData("just some words")]
    [InlineData("")]
    public void FromUrl_RejectsNonUrls(string text)
    {
        Assert.Null(ItemFactory.FromUrl(text));
    }

    [Theory]
    [InlineData("Note: buy milk")]    // whitespace: text, not a "note:" URI
    [InlineData("Re: meeting")]
    [InlineData("todo:call-bob")]     // unregistered scheme
    [InlineData("https://exa mple.com")]
    public void FromUrl_RejectsPlainText(string text)
    {
        var saved = ItemFactory.IsRegisteredProtocol;
        ItemFactory.IsRegisteredProtocol = _ => false;
        try { Assert.Null(ItemFactory.FromUrl(text)); }
        finally { ItemFactory.IsRegisteredProtocol = saved; }
    }

    [Fact]
    public void FromUrl_AcceptsRegisteredProtocols()
    {
        var saved = ItemFactory.IsRegisteredProtocol;
        ItemFactory.IsRegisteredProtocol = scheme => scheme == "ms-settings";
        try
        {
            Assert.Equal(ItemKind.Url, ItemFactory.FromUrl("ms-settings:display")!.Kind);
            Assert.Null(ItemFactory.FromUrl("unknown:thing"));
            Assert.NotNull(ItemFactory.FromUrl("https://example.com")); // web schemes need no registry
        }
        finally { ItemFactory.IsRegisteredProtocol = saved; }
    }

    [Fact]
    public void FromUrl_UsesTheTitle()
    {
        Assert.Equal("My page", ItemFactory.FromUrl("https://example.com", "  My page ")!.Name);
    }

    [Fact]
    public void FromShell_ConvertsAppsFolderParsingNames()
    {
        var item = ItemFactory.FromShell(@"::{4234D49B-0245-4DF3-B780-3893943456E1}\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculator")!;
        Assert.Equal(ItemKind.Shell, item.Kind);
        Assert.Equal(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", item.Target);
        Assert.Equal("Calculator", item.Name);
    }

    [Fact]
    public void FromText_PicksTheRightKind()
    {
        Assert.Equal(ItemKind.Folder, ItemFactory.FromText(_dir)!.Kind);
        Assert.Equal(ItemKind.Url, ItemFactory.FromText("https://example.com")!.Kind);
        Assert.Equal(ItemKind.Shell, ItemFactory.FromText("shell:Downloads")!.Kind);
        Assert.Null(ItemFactory.FromText("hello"));
    }

    [Fact]
    public void DescriptorTitle_IsReadFromTheFirstEntry()
    {
        var descriptor = new byte[4 + 592];
        BitConverter.GetBytes(1u).CopyTo(descriptor, 0);
        Encoding.Unicode.GetBytes("Example Domain.url").CopyTo(descriptor, 4 + 72);
        Assert.Equal("Example Domain", DropReader.ParseDescriptorTitle(descriptor));
        Assert.Null(DropReader.ParseDescriptorTitle(new byte[8]));
    }

    [Fact]
    public void CommandLine_ParsesOptionsAndItems()
    {
        var cl = CommandLine.Parse(["--minimized", @"C:\a.txt", "--launch", "abc", "https://x.org"]);
        Assert.True(cl.Minimized);
        Assert.Equal("abc", cl.LaunchId);
        Assert.Equal([@"C:\a.txt", "https://x.org"], cl.Items);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\pLaunch\\pLaunch.exe\" --minimized", @"C:\Program Files\pLaunch\pLaunch.exe")]
    [InlineData(@"C:\Tools\pLaunch.exe --minimized", @"C:\Tools\pLaunch.exe")]
    [InlineData(@"C:\Tools\pLaunch.exe", @"C:\Tools\pLaunch.exe")]
    public void Autostart_ReadsTheExeOfARunCommand(string command, string exe)
    {
        Assert.Equal(exe, Autostart.ExePath(command));
    }

    [Fact]
    public void ForwardedArguments_AreMadeAbsolute()
    {
        File.WriteAllText(Path.Combine(_dir, "doc.txt"), "");
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _dir;
            Assert.Equal(Path.Combine(_dir, "doc.txt"), Program.ToAbsolute("doc.txt"));
            Assert.Equal("missing.txt", Program.ToAbsolute("missing.txt"));
            Assert.Equal("https://example.com", Program.ToAbsolute("https://example.com"));
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Theory]
    [InlineData(@"\\server\share\tool.exe", true)]
    [InlineData(@"C:\Windows\notepad.exe", false)]
    [InlineData("https://example.com", false)]
    [InlineData(@"shell:AppsFolder\x", false)]
    public void NetworkPaths_AreRecognized(string path, bool expected)
    {
        Assert.Equal(expected, Launcher.IsNetworkPath(path));
    }

    [Fact]
    public void Missing_IsReportedOnlyForLocalPaths()
    {
        Assert.True(Launcher.IsMissing(new LaunchItem { Kind = ItemKind.File, Target = Path.Combine(_dir, "gone.exe") }));
        Assert.False(Launcher.IsMissing(new LaunchItem { Kind = ItemKind.Folder, Target = _dir }));
        Assert.False(Launcher.IsMissing(new LaunchItem { Kind = ItemKind.File, Target = @"\\nohost\share\x.exe" }));
        Assert.False(Launcher.IsMissing(new LaunchItem { Kind = ItemKind.Url, Target = "https://example.com" }));
    }
}
