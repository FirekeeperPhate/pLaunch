using System.Diagnostics;
using System.Windows.Input;
using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch.Tests;

public sealed class SearchAndHotkeyTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pLaunchSearch-" + Guid.NewGuid().ToString("N"));

    public SearchAndHotkeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    static LaunchItem Url(string name, int launches = 0) =>
        new() { Kind = ItemKind.Url, Name = name, Target = "https://" + name.Replace(" ", "").ToLowerInvariant() + ".example/", LaunchCount = launches };

    // ---------------------------------------------------------------- shortcuts

    [Theory]
    [InlineData("Win+Alt+Space", ModifierKeys.Windows | ModifierKeys.Alt, Key.Space)]
    [InlineData("ctrl + alt + e", ModifierKeys.Control | ModifierKeys.Alt, Key.E)]
    [InlineData("Ctrl+Shift+1", ModifierKeys.Control | ModifierKeys.Shift, Key.D1)]
    [InlineData("Alt+F5", ModifierKeys.Alt, Key.F5)]
    public void Hotkeys_Parse(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal((modifiers, key), (g.Modifiers, g.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("E")]              // no modifier
    [InlineData("Shift+E")]        // Shift alone would steal typing
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+E+F")]       // two keys
    [InlineData("Ctrl+Banana")]
    public void Hotkeys_RejectUnusable(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void Hotkeys_RoundTrip_AndNativeValues()
    {
        Assert.True(HotkeyGesture.TryParse("alt+win+space", out var g));
        Assert.Equal("Win+Alt+Space", g.ToString());
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+1", out var digit));
        Assert.Equal("Ctrl+Shift+1", digit.ToString());
        var (mods, vk) = g.ToNative();
        Assert.Equal(0x1u | 0x8u | 0x4000u, mods); // Alt | Win | NoRepeat
        Assert.Equal(0x20u, vk);                    // VK_SPACE
    }

    [Fact]
    public void Settings_ShortcutDefaultsAndPersists()
    {
        Assert.Equal("Win+Alt+Space", new LauncherSettings().Hotkey);
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        store.Save([], new LauncherSettings { Hotkey = "" }); // "none" must not come back as the default
        Assert.Equal("", store.Load().Settings.Hotkey);
    }

    // ---------------------------------------------------------------- search

    [Fact]
    public void Search_FindsInSubFolders_BestMatchesFirst()
    {
        var tools = new LaunchItem { Kind = ItemKind.Group, Name = "Dev tools", Children = [Url("Notepad plus"), Url("Git bash")] };
        var root = new List<LaunchItem> { Url("My notes", launches: 1), Url("Notepad"), tools, new() { Kind = ItemKind.Separator } };
        var results = ItemSearch.Find(root, "note");
        Assert.Equal(["Notepad", "Notepad plus", "My notes"], results.Select(r => r.Item.Name));
        Assert.Equal("Dev tools", results[1].Path);
        Assert.Equal("", results[0].Path);
    }

    [Fact]
    public void Search_IgnoresCaseAndAccents_AndNeedsEveryWord()
    {
        var root = new List<LaunchItem> { Url("Città Studi"), Url("Città nuova"), Url("Studio") };
        Assert.Equal(["Città Studi"], ItemSearch.Find(root, "CITTA stu").Select(r => r.Item.Name));
        Assert.Empty(ItemSearch.Find(root, "   "));
    }

    [Fact]
    public void Search_MatchesTheTargetFileName()
    {
        var item = new LaunchItem { Kind = ItemKind.File, Name = "Editor", Target = @"C:\Tools\notepad++.exe" };
        Assert.Single(ItemSearch.Find([item], "notepad"));
    }

    [Fact]
    public void Search_MoreLaunchedFirstWithinARank()
    {
        var root = new List<LaunchItem> { Url("Mail work"), Url("Mail home", launches: 7) };
        Assert.Equal("Mail home", ItemSearch.Find(root, "mail")[0].Item.Name);
    }

    [Fact]
    public void PathTo_GivesTheFoldersAboveAnItem()
    {
        var deep = Url("deep");
        var inner = new LaunchItem { Kind = ItemKind.Group, Name = "inner", Children = [deep] };
        var outer = new LaunchItem { Kind = ItemKind.Group, Name = "outer", Children = [inner] };
        Assert.Equal([outer, inner], ItemTree.PathTo([outer], deep.Id));
        Assert.Empty(ItemTree.PathTo([outer], outer.Id)!);
        Assert.Null(ItemTree.PathTo([outer], "missing"));
    }

    // ---------------------------------------------------------------- most used

    [Fact]
    public void MostUsed_OrdersEachSectionByLaunches_FoldersFirst()
    {
        var a = Url("a", 1);
        var b = Url("b", 5);
        var c = Url("c", 5);
        c.LastLaunched = DateTime.UtcNow; // same count, more recent
        var g = new LaunchItem { Kind = ItemKind.Group, Name = "zz" };
        var items = new List<LaunchItem> { a, b, c, g, new() { Kind = ItemKind.Separator }, Url("x"), Url("y", 2) };
        var names = ItemTree.DisplayOrder(items, SortMode.MostUsed).Select(i => i.Kind == ItemKind.Separator ? "|" : i.Name);
        Assert.Equal(["zz", "c", "b", "a", "|", "y", "x"], names);
    }

    [Fact]
    public void Launches_AreCountedAndSaved()
    {
        var item = Url("site");
        Launcher.RecordLaunch(item);
        Launcher.RecordLaunch(item);
        Assert.Equal(2, item.LaunchCount);
        Assert.NotNull(item.LastLaunched);
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        store.Save([item], new LauncherSettings());
        Assert.Equal(2, store.Load().Items[0].LaunchCount);
    }

    // ---------------------------------------------------------------- per item

    [Fact]
    public void StartWindow_IsPassedToTheProgram()
    {
        var item = new LaunchItem { Kind = ItemKind.File, Target = @"C:\Windows\notepad.exe", StartWindow = StartWindow.Maximized };
        Assert.Equal(ProcessWindowStyle.Maximized, Launcher.CreateStartInfo(item).WindowStyle);
        item.StartWindow = StartWindow.Minimized;
        Assert.Equal(ProcessWindowStyle.Minimized, Launcher.CreateStartInfo(item).WindowStyle);
    }

    [Fact]
    public void ItemShortcutAndLiveFlag_AreSaved()
    {
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        store.Save([
            new LaunchItem { Kind = ItemKind.File, Name = "n", Target = @"C:\n.exe", Hotkey = "Ctrl+Alt+N", StartWindow = StartWindow.Minimized },
            new LaunchItem { Kind = ItemKind.Folder, Name = "d", Target = @"C:\Docs", ShowContents = true },
        ], new LauncherSettings());
        var items = store.Load().Items;
        Assert.Equal(("Ctrl+Alt+N", StartWindow.Minimized), (items[0].Hotkey, items[0].StartWindow));
        Assert.True(items[1].ShowContents && items[1].IsNavigable);
        Assert.DoesNotContain("IsLive", File.ReadAllText(store.FilePath)); // never saved
    }

    // ---------------------------------------------------------------- live folders

    [Fact]
    public void LiveFolder_ListsFoldersThenFiles_WithoutHiddenOnes()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "b folder"));
        Directory.CreateDirectory(Path.Combine(_dir, "A folder"));
        File.WriteAllText(Path.Combine(_dir, "zeta.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "Alpha.lnk"), "");
        var hidden = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(hidden, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var (items, truncated) = LiveFolder.List(_dir);
        Assert.False(truncated);
        Assert.Equal(["A folder", "b folder", "Alpha", "zeta.txt"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.True(i.IsLive));
        Assert.True(items[0].IsNavigable); // sub-folders open inside too
        Assert.Equal("live:" + Path.Combine(_dir, "zeta.txt"), items[3].Id);
    }

    [Fact]
    public void LiveFolder_StopsAtTheLimit()
    {
        for (int i = 0; i < LiveFolder.MaxEntries + 5; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i:000}.txt"), "");
        var (items, truncated) = LiveFolder.List(_dir);
        Assert.True(truncated);
        Assert.Equal(LiveFolder.MaxEntries, items.Count);
    }
}
