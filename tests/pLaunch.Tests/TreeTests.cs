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
    public void DescribeContent_CountsShortcutsAndSubFolders()
    {
        Assert.Equal("2 shortcuts, 1 sub-folder", ItemTree.DescribeContent(Group("g", Url("a"), Group("h", Url("b")))));
        Assert.Equal("2 sub-folders", ItemTree.DescribeContent(Group("g", Group("x"), Group("y"))));
        Assert.Equal("1 shortcut", ItemTree.DescribeContent(Group("g", Url("a"), Sep())));
        Assert.Null(ItemTree.DescribeContent(Group("g", Sep()))); // nothing worth a confirmation
        Assert.Null(ItemTree.DescribeContent(Group("g")));
    }

    [Fact]
    public void Store_GivesRepeatedOrMissingIdsNewOnes()
    {
        var path = Path.Combine(_dir, "items.json");
        File.WriteAllText(path, """
            { "Items": [ { "Id": "same", "Kind": "Url", "Name": "a", "Target": "https://a.example/" },
                         { "Id": "same", "Kind": "Url", "Name": "b", "Target": "https://b.example/" },
                         { "Id": "", "Kind": "Separator" },
                         { "Id": "g", "Kind": "Group", "Name": "G", "Children": [ { "Id": "same", "Kind": "Url", "Name": "c", "Target": "https://c.example/" } ] },
                         { "Kind": "Url", "Name": "no id", "Target": "https://d.example/" } ] }
            """);
        var store = new ItemStore(path);
        var items = store.Load().Items;
        var all = items.Concat(items[3].Children!).Select(i => i.Id).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal("same", items[0].Id); // the first keeps its id (jump list entries still work)
        Assert.Equal("same-2", items[1].Id);
        Assert.Equal("same-3", items[3].Children![0].Id);
        Assert.Equal("item", items[2].Id);  // empty id
        Assert.Equal("item-2", items[4].Id); // no "Id" at all
        Assert.True(store.IdsRepaired);

        // Another process reading the same file (a jump list entry) gets the same ids
        var again = new ItemStore(path).Load().Items;
        Assert.Equal(items.Select(i => i.Id), again.Select(i => i.Id));
    }

    [Fact]
    public void Store_CleanFile_NeedsNoRepair()
    {
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        store.Save([Url("a"), Group("g", Url("b"))], new LauncherSettings());
        store.Load();
        Assert.False(store.IdsRepaired);
    }

    [Fact]
    public void NewItems_GetRandomIds()
    {
        Assert.NotEqual(new LaunchItem().Id, new LaunchItem().Id);
        Assert.Equal(32, new LaunchItem().Id.Length);
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
