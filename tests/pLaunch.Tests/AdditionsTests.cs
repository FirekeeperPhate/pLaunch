using pLaunch.Models;
using pLaunch.Services;
using pLaunch.ViewModels;

namespace pLaunch.Tests;

/// <summary>Files dropped on a program ("open with"), the notification area menu's shortcuts, the place of a search result.</summary>
public sealed class AdditionsTests
{
    static LaunchItem File(string name, string target, int launches = 0, int daysAgo = 0) => new()
    {
        Kind = ItemKind.File, Name = name, Target = target, LaunchCount = launches,
        LastLaunched = launches > 0 ? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo) : null,
    };

    [Theory]
    [InlineData(@"C:\Tools\editor.exe", true)]
    [InlineData(@"C:\Tools\build.CMD", true)]
    [InlineData(@"C:\Tools\run.bat", true)]
    [InlineData(@"C:\Docs\notes.txt", false)]   // a document opens by itself: nothing to open with it
    [InlineData(@"C:\Docs\missing.lnk", false)] // a shortcut that leads nowhere
    public void FilesCanBeDroppedOnPrograms_NotOnDocuments(string target, bool opens)
    {
        Assert.Equal(opens, Launcher.OpensFiles(File("x", target)));
    }

    [Fact]
    public void OnlyFileItemsOpenFiles()
    {
        Assert.False(Launcher.OpensFiles(new LaunchItem { Kind = ItemKind.Folder, Target = @"C:\Tools" }));
        Assert.False(Launcher.OpensFiles(new LaunchItem { Kind = ItemKind.Url, Target = "https://example.com/a.exe" }));
        Assert.False(Launcher.OpensFiles(new LaunchItem { Kind = ItemKind.Group, Name = "Tools.exe" }));
    }

    [Theory]
    [InlineData(true, @"C:\Docs\report.pdf", @"C:\Docs\notes.txt")]
    [InlineData(true, @"C:\Projects\site")]                          // a folder: an editor opens it
    [InlineData(false, @"C:\Users\me\Desktop\Notepad++.lnk")]        // a shortcut: it is being added
    [InlineData(false, @"C:\Tools\editor.EXE")]
    [InlineData(false, @"C:\Docs\notes.txt", @"C:\Tools\run.bat")]   // one program among them: all are added
    [InlineData(false, @"C:\Links\site.url")]
    public void OnlyDocumentsAreOpenedWithAProgram_ProgramsAndShortcutsAreAdded(bool documents, params string[] files)
    {
        Assert.Equal(documents, Launcher.AreDocuments(files));
    }

    [Fact]
    public void DroppedFiles_GoAfterTheItemsOwnArguments_EachQuoted()
    {
        var item = File("Editor", @"C:\Tools\editor.exe");
        item.Arguments = "-n";
        var psi = Launcher.CreateOpenWithStartInfo(item, [@"C:\My Docs\a.txt", @"C:\b.txt"]);
        Assert.Equal(@"C:\Tools\editor.exe", psi.FileName);
        Assert.Equal(@"-n ""C:\My Docs\a.txt"" ""C:\b.txt""", psi.Arguments);

        item.Arguments = null;
        Assert.Equal(@"""C:\b.txt""", Launcher.CreateOpenWithStartInfo(item, [@"C:\b.txt"]).Arguments);
    }

    [Fact]
    public void MostUsed_AreTheMostLaunchedOfAnyLevel_ThenTheMostRecent()
    {
        var tools = new LaunchItem { Kind = ItemKind.Group, Name = "Tools", Children = [File("Deep", @"C:\d.exe", launches: 9), File("Never", @"C:\n.exe")] };
        List<LaunchItem> root =
        [
            File("Old", @"C:\o.exe", launches: 3, daysAgo: 30),
            new() { Kind = ItemKind.Separator },
            tools,
            File("Recent", @"C:\r.exe", launches: 3, daysAgo: 1),
            File("Once", @"C:\1.exe", launches: 1),
        ];
        Assert.Equal(["Deep", "Recent", "Old"], ItemTree.MostUsed(root, 3).Select(i => i.Name));
        Assert.Equal(4, ItemTree.MostUsed(root, 8).Count); // never launched ones are left out
    }

    [Fact]
    public void MostUsed_WithNothingLaunchedYet_AreTheFirstOfTheList()
    {
        List<LaunchItem> root = [File("A", @"C:\a.exe"), new() { Kind = ItemKind.Group, Name = "G", Children = [File("B", @"C:\b.exe")] }, File("C", @"C:\c.exe")];
        Assert.Equal(["A", "B"], ItemTree.MostUsed(root, 2).Select(i => i.Name));
        Assert.Empty(ItemTree.MostUsed([], 8));
    }

    [Fact]
    public void SearchResult_TellsWhereItIs_AndTheRowKnowsWhenToShowIt()
    {
        var vm = new ItemViewModel(File("Editor", @"C:\e.exe"));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(vm.HasLocation);
        vm.Location = "Work \x203A Tools";
        Assert.True(vm.HasLocation);
        Assert.Contains(nameof(ItemViewModel.Location), changed);
        Assert.Contains(nameof(ItemViewModel.HasLocation), changed);
        vm.Location = null; // back in its own level
        Assert.False(vm.HasLocation);
    }
}
