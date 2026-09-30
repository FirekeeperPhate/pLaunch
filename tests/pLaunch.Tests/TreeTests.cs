using pLaunch.Models;
using pLaunch.Services;
using pLaunch.ViewModels;

namespace pLaunch.Tests;

public sealed class TreeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pLaunchTree-" + Guid.NewGuid().ToString("N"));

    public TreeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static LaunchItem Url(string name) => new() { Kind = ItemKind.Url, Name = name, Target = "https://" + name.ToLowerInvariant() + ".example/" };
    static LaunchItem Sep() => new() { Kind = ItemKind.Separator };
    static LaunchItem Group(string name, params LaunchItem[] children) => new() { Kind = ItemKind.Group, Name = name, Children = [.. children] };

    [Fact]
    public void Alphabetical_SortsEachSectionOnItsOwn_FoldersFirst()
    {
        var items = new List<LaunchItem> { Url("delta"), Url("Alpha"), Group("zeta"), Sep(), Url("charlie"), Url("bravo") };
        var names = ItemTree.DisplayOrder(items, SortMode.Alphabetical).Select(i => i.Kind == ItemKind.Separator ? "|" : i.Name);
        Assert.Equal(["zeta", "Alpha", "delta", "|", "bravo", "charlie"], names);
        // The stored custom order is untouched
        Assert.Equal("delta", items[0].Name);
    }

    [Fact]
    public void Custom_KeepsTheStoredOrder()
    {
        var items = new List<LaunchItem> { Url("b"), Sep(), Url("a") };
        Assert.Equal(items, ItemTree.DisplayOrder(items, SortMode.Custom));
    }

    [Fact]
    public void Find_And_FindContainer_SearchSubFolders()
    {
        var deep = Url("deep");
        var inner = Group("inner", deep);
        var root = new List<LaunchItem> { Url("top"), Group("outer", inner) };
        Assert.Same(deep, ItemTree.Find(root, deep.Id));
        Assert.Same(inner.Children, ItemTree.FindContainer(root, deep.Id));
        Assert.Null(ItemTree.Find(root, "nope"));
    }

    [Fact]
    public void IsSelfOrInside_StopsFoldersGoingIntoThemselves()
    {
        var inner = Group("inner");
        var outer = Group("outer", inner);
        Assert.True(ItemTree.IsSelfOrInside(outer, outer));
        Assert.True(ItemTree.IsSelfOrInside(inner, outer));  // moving outer into inner: refused
        Assert.False(ItemTree.IsSelfOrInside(outer, inner)); // moving inner into outer: fine
    }

    [Fact]
    public void Launchables_UseTheFolderPathAsJumpListCategory()
    {
        var root = new List<LaunchItem> { Url("a"), Sep(), Group("Work", Url("b"), Group("Tools", Url("c"))) };
        var list = ItemTree.Launchables(root, "Shortcuts").Select(e => $"{e.Item.Name}@{e.Category}");
        Assert.Equal(["a@Shortcuts", "b@Work", "c@Work \x203A Tools"], list);
        Assert.Equal(2, ItemTree.CountLaunchables((LaunchItem)root[2]));
    }

    [Fact]
    public void Store_RoundTripsFoldersSeparatorsAndSettings()
    {
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        var settings = new LauncherSettings { View = ViewMode.Grid, Size = ItemSize.Large, Sort = SortMode.Alphabetical };
        store.Save([Url("a"), Sep(), Group("Work", Url("b"), Group("Empty"))], settings);

        var data = store.Load();
        Assert.Equal(ViewMode.Grid, data.Settings.View);
        Assert.Equal(ItemSize.Large, data.Settings.Size);
        Assert.Equal(SortMode.Alphabetical, data.Settings.Sort);
        Assert.Equal([ItemKind.Url, ItemKind.Separator, ItemKind.Group], data.Items.Select(i => i.Kind));
        var work = data.Items[2];
        Assert.Equal("b", work.Children![0].Name);
        Assert.Empty(work.Children[1].Children!);
    }

    [Fact]
    public void Store_ReadsVersion1Files()
    {
        var path = Path.Combine(_dir, "items.json");
        File.WriteAllText(path, """
            { "Version": 1, "Items": [ { "Id": "x", "Name": "Notepad", "Target": "C:\\Windows\\notepad.exe", "Kind": "File" } ] }
            """);
        var data = new ItemStore(path).Load();
        Assert.Single(data.Items);
        Assert.Equal(ViewMode.List, data.Settings.View); // defaults
        Assert.Equal(SortMode.Custom, data.Settings.Sort);
    }

    [Fact]
    public void Store_DropsBrokenEntriesAtEveryLevel()
    {
        var path = Path.Combine(_dir, "items.json");
        File.WriteAllText(path, """
            { "Items": [ { "Kind": "Url", "Name": "no target" },
                         { "Kind": "Group", "Name": "", "Children": [ { "Kind": "File", "Name": "empty" }, { "Kind": "Separator" } ] } ] }
            """);
        var items = new ItemStore(path).Load().Items;
        var group = Assert.Single(items);
        Assert.Equal("Folder", group.Name);
        Assert.Equal(ItemKind.Separator, Assert.Single(group.Children!).Kind);
    }

    [Fact]
    public void GroupsAndSeparators_AreNeverDuplicates()
    {
        Assert.False(Sep().IsSameTarget(Sep()));
        Assert.False(Group("a").IsSameTarget(Group("a")));
        Assert.True(Url("a").IsSameTarget(Url("a")));
    }

    [Theory]
    [InlineData(ViewMode.Grid)]
    [InlineData(ViewMode.Icons)]
    public void Tiles_FitTheirColumnsInThePopupWidth(ViewMode view)
    {
        foreach (var size in Enum.GetValues<ItemSize>())
        {
            var m = ViewMetrics.For(view, size);
            Assert.True(m.Columns * (m.TileWidth + ViewMetrics.TileMargin) <= m.Width - ViewMetrics.Chrome + 0.001);
            Assert.True(m.IconSize < m.TileWidth);
        }
    }

    [Fact]
    public void Sizes_GrowFromSmallToLarge()
    {
        foreach (var view in Enum.GetValues<ViewMode>())
        {
            var s = ViewMetrics.For(view, ItemSize.Small);
            var l = ViewMetrics.For(view, ItemSize.Large);
            Assert.True(l.IconSize > s.IconSize && l.Width > s.Width);
        }
    }
}
