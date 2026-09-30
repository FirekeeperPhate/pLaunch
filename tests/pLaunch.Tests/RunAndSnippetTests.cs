using System.Diagnostics;
using System.Text;
using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch.Tests;

public sealed class RunAndSnippetTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pLaunchRun-" + Guid.NewGuid().ToString("N"));

    public RunAndSnippetTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    // ---------------------------------------------------------------- search box as a Run box

    [Fact]
    public void Run_APathOpensFirst_WithEnvironmentVariables()
    {
        var (first, last) = RunSuggestions.For(_dir, WebSearch.Off);
        var open = Assert.Single(first);
        Assert.Equal((ItemKind.Folder, _dir), (open.Kind, open.Target));
        Assert.StartsWith("Open ", open.Name);
        Assert.True(open.IsLive);
        Assert.Empty(last);

        var temp = RunSuggestions.For("%WINDIR%", WebSearch.Off).First;
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Assert.Single(temp).Target, ignoreCase: true);
        Assert.Empty(RunSuggestions.For(Path.Combine(_dir, "missing"), WebSearch.Off).First);
    }

    [Fact]
    public void Run_CommandsAreFoundLikeTheRunBox_BelowTheList()
    {
        var (first, last) = RunSuggestions.For("cmd", WebSearch.Off);
        Assert.Empty(first);
        var run = Assert.Single(last);
        Assert.Equal(ItemKind.File, run.Kind);
        Assert.Equal(Cmd, run.Target, ignoreCase: true);
        Assert.Null(run.Arguments);
        Assert.Equal("Run cmd", run.Name);
        // Like Win+R: in the user's folder, not in System32
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), run.WorkingDirectory);

        var ping = Assert.Single(RunSuggestions.For("ping -n 1 127.0.0.1", WebSearch.Off).Last);
        Assert.EndsWith("PING.EXE", ping.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("-n 1 127.0.0.1", ping.Arguments);
    }

    [Fact]
    public void Run_AQuotedFullPathWithArguments()
    {
        var run = Assert.Single(RunSuggestions.For($"\"{Cmd}\" /c echo hi", WebSearch.Off).Last);
        Assert.Equal((Cmd, "/c echo hi"), (run.Target, run.Arguments));
    }

    [Theory]
    [InlineData("github.com", "https://github.com/")]
    [InlineData("it.wikipedia.org/wiki/Roma", "https://it.wikipedia.org/wiki/Roma")]
    [InlineData("localhost:5173", "http://localhost:5173/")]
    [InlineData("https://example.org/a?b=1", "https://example.org/a?b=1")]
    [InlineData("www.example.org", "https://www.example.org/")]
    public void Run_WebAddressesGoFirst(string text, string url)
    {
        var go = Assert.Single(RunSuggestions.For(text, WebSearch.Off).First);
        Assert.Equal(ItemKind.Url, go.Kind);
        Assert.Equal(url, new Uri(go.Target).AbsoluteUri);
        Assert.Equal("Go to " + text, go.Name);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("report.pdf")]
    [InlineData("two words.com")]
    public void Run_FileNamesAndSentencesAreNotWebAddresses(string text)
    {
        var (first, last) = RunSuggestions.For(text, WebSearch.Off);
        Assert.Empty(first);
        Assert.Empty(last);
    }

    [Fact]
    public void Run_ShellFoldersOpen()
    {
        var shell = Assert.Single(RunSuggestions.For("shell:Downloads", WebSearch.Off).First);
        Assert.Equal((ItemKind.Shell, "shell:Downloads"), (shell.Kind, shell.Target));
    }

    [Fact]
    public void Run_WebSearchComesLast_EncodedAndOptional()
    {
        var (first, last) = RunSuggestions.For("città & co", WebSearch.DuckDuckGo);
        Assert.Empty(first);
        var search = Assert.Single(last);
        Assert.Equal(RunSuggestions.WebSearchId, search.Id);
        Assert.Equal("https://duckduckgo.com/?q=citt%C3%A0%20%26%20co", search.Target);
        Assert.Contains("città & co", search.Name);
        Assert.Equal("https://www.google.com/search?q=cmd", RunSuggestions.For("cmd", WebSearch.Google).Last[^1].Target);
        Assert.Empty(RunSuggestions.For("anything at all", WebSearch.Off).Last);
    }

    [Fact]
    public void Run_TheWebSearchIsSeparate_SoItShowsAtOnce()
    {
        Assert.Empty(RunSuggestions.Typed("anything at all").Last); // no disk look-up needed for it
        Assert.Equal(RunSuggestions.WebSearchId, RunSuggestions.WebSearchFor("x", WebSearch.Bing)!.Id);
        Assert.Null(RunSuggestions.WebSearchFor("x", WebSearch.Off));
        Assert.Null(RunSuggestions.WebSearchFor("   ", WebSearch.Google));
    }

    [Fact]
    public void Run_NetworkPathsAreOfferedWithoutAskingTheServer()
    {
        var open = Assert.Single(RunSuggestions.Typed(@"\\no-such-server-" + Guid.NewGuid().ToString("N")[..8] + @"\share").First);
        Assert.True(RunSuggestions.IsNetworkSuggestion(open)); // no icon look-up while typing
        Assert.False(RunSuggestions.IsNetworkSuggestion(Assert.Single(RunSuggestions.Typed(_dir).First)));
        Assert.False(RunSuggestions.IsNetworkSuggestion(new LaunchItem { Kind = ItemKind.Folder, Target = @"\\server\share" })); // a saved item
    }

    [Theory]
    [InlineData("ping 1.1.1.1", "ping", "1.1.1.1")]
    [InlineData("calc", "calc", null)]
    [InlineData("\"C:\\My Tools\\x.exe\" -a  -b", "C:\\My Tools\\x.exe", "-a  -b")]
    [InlineData("\"unclosed quote", "unclosed quote", null)]
    public void Run_SplitsTheCommandFromItsArguments(string text, string command, string? arguments)
    {
        Assert.Equal((command, arguments), RunSuggestions.SplitCommand(text));
    }

    [Fact]
    public void ResolveCommand_FindsProgramsOnThePath()
    {
        Assert.Equal(Cmd, RunSuggestions.ResolveCommand("cmd"), ignoreCase: true);
        Assert.Equal(Cmd, RunSuggestions.ResolveCommand("CMD.EXE"), ignoreCase: true);
        Assert.Null(RunSuggestions.ResolveCommand("no-such-program-" + Guid.NewGuid().ToString("N")));
        Assert.Null(RunSuggestions.ResolveCommand(@"..\cmd"));
    }

    [Fact]
    public void Suggestions_OpenFromTheSameCode_AndAreNeverSaved()
    {
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        var saved = new LaunchItem { Kind = ItemKind.Url, Name = "x", Target = "https://x.org/" };
        store.Save([saved], new LauncherSettings { WebSearch = WebSearch.Bing });
        var data = store.Load();
        Assert.Equal(WebSearch.Bing, data.Settings.WebSearch);
        Assert.DoesNotContain("IsLive", File.ReadAllText(store.FilePath));
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public void Command_Cmd_RunsEveryLine_AndCanStayOpen()
    {
        var item = new LaunchItem { Kind = ItemKind.Command, Target = "echo \"a b\"\n\n  cd \\  \necho c" };
        var psi = Launcher.CreateStartInfo(item);
        Assert.Equal(Launcher.CommandHost(CommandShell.Cmd), psi.FileName);
        Assert.Equal("/s /c \"echo \"a b\" & cd \\ & echo c\"", psi.Arguments);
        Assert.True(psi.UseShellExecute);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), psi.WorkingDirectory);
        item.KeepOpen = true;
        Assert.StartsWith("/s /k ", Launcher.CreateStartInfo(item).Arguments);
    }

    [Fact]
    public void Command_PowerShell_GetsTheScriptEncoded()
    {
        var item = new LaunchItem { Kind = ItemKind.Command, Shell = CommandShell.PowerShell, Target = "Write-Host \"it's\" $env:USERNAME\nGet-Date", KeepOpen = true };
        var psi = Launcher.CreateStartInfo(item);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-NoExit", psi.Arguments);
        var encoded = psi.Arguments[(psi.Arguments.LastIndexOf(' ') + 1)..];
        Assert.Equal("Write-Host \"it's\" $env:USERNAME\nGet-Date", Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void Command_Hidden_HasNoConsole_UnlessElevatedOrKeptOpen()
    {
        var item = new LaunchItem { Kind = ItemKind.Command, Target = "echo x", StartWindow = StartWindow.Hidden, WorkingDirectory = "%TEMP%" };
        var psi = Launcher.CreateStartInfo(item);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(Environment.ExpandEnvironmentVariables("%TEMP%"), psi.WorkingDirectory);

        var admin = Launcher.CreateStartInfo(item, asAdmin: true);
        Assert.True(admin.UseShellExecute);
        Assert.Equal("runas", admin.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, admin.WindowStyle);

        item.KeepOpen = true; // a window kept open must be seen
        Assert.Equal(ProcessWindowStyle.Normal, Launcher.CreateStartInfo(item).WindowStyle);
        Assert.True(Launcher.CanRunAsAdmin(item));
    }

    [Fact]
    public void Command_ActuallyRuns()
    {
        var marker = Path.Combine(_dir, "ran.txt");
        var item = new LaunchItem { Kind = ItemKind.Command, Target = $"echo done> \"{marker}\"", StartWindow = StartWindow.Hidden };
        using (var process = Process.Start(Launcher.CreateStartInfo(item))!)
            Assert.True(process.WaitForExit(10_000));
        Assert.Equal("done", File.ReadAllText(marker).Trim());
    }

    // ---------------------------------------------------------------- text snippets

    [Fact]
    public void PlainText_BecomesOneSnippet_UnlessEveryLineIsALink()
    {
        var links = DropReader.FromPlainText("https://a.org\r\nhttps://b.org\r\n");
        Assert.Equal([ItemKind.Url, ItemKind.Url], links.Select(i => i.Kind));

        var snippet = Assert.Single(DropReader.FromPlainText("Dear Sir,\r\nplease find attached https://a.org\r\n"));
        Assert.Equal(ItemKind.Text, snippet.Kind);
        Assert.Equal("Dear Sir,\nplease find attached https://a.org", snippet.Target);
        Assert.Equal("Dear Sir,", snippet.Name);
        Assert.True(snippet.PasteText);
        Assert.Empty(DropReader.FromPlainText(" \r\n "));
    }

    [Fact]
    public void Summary_TakesTheFirstLine_Shortened()
    {
        Assert.Equal("first", LaunchItem.Summary("\n  first  \nsecond"));
        Assert.Equal("abcdefghi\x2026", LaunchItem.Summary("abcdefghijklmnop", 10));
    }

    [Fact]
    public void NewKinds_AreSavedWithTheirOptions()
    {
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        store.Save([
            new LaunchItem { Kind = ItemKind.Command, Name = "c", Target = "dir", Shell = CommandShell.Pwsh, KeepOpen = true, StartWindow = StartWindow.Hidden },
            new LaunchItem { Kind = ItemKind.Text, Name = "t", Target = "line 1\nline 2\n", PasteText = false },
            new LaunchItem { Kind = ItemKind.File, Name = "n", Target = @"C:\n.exe", SwitchToRunning = true },
        ], new LauncherSettings());
        var items = store.Load().Items;
        Assert.Equal((ItemKind.Command, CommandShell.Pwsh, true, StartWindow.Hidden), (items[0].Kind, items[0].Shell, items[0].KeepOpen, items[0].StartWindow));
        Assert.Equal(("line 1\nline 2\n", false), (items[1].Target, items[1].PasteText));
        Assert.True(items[2].SwitchToRunning);
    }

    [Fact]
    public void Snippets_PasteByDefault_EvenInOlderFiles()
    {
        var path = Path.Combine(_dir, "items.json");
        File.WriteAllText(path, """{ "Version": 2, "Items": [ { "Id": "t", "Kind": "Text", "Name": "t", "Target": "hi" } ] }""");
        Assert.True(new ItemStore(path).Load().Items[0].PasteText);
    }

    [Fact]
    public void Icons_SnippetsShowAGlyph_CommandsTheirHost()
    {
        Assert.False(new LaunchItem { Kind = ItemKind.Text, Target = "x" }.HasShellIcon);
        Assert.True(new LaunchItem { Kind = ItemKind.Text, Target = "x", IconPath = "a.ico" }.HasShellIcon);
        Assert.True(new LaunchItem { Kind = ItemKind.Command, Target = "x" }.HasShellIcon);
        Assert.EndsWith("cmd.exe", Launcher.CommandHost(CommandShell.Cmd), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- running programs

    [Fact]
    public void Running_OnlyProgramsAndApps_CanBeSwitchedTo()
    {
        Assert.True(RunningApps.CanSwitch(new LaunchItem { Kind = ItemKind.File, Target = @"C:\x\app.EXE" }));
        Assert.True(RunningApps.CanSwitch(new LaunchItem { Kind = ItemKind.File, Target = @"C:\x\app.lnk" }));
        Assert.True(RunningApps.CanSwitch(new LaunchItem { Kind = ItemKind.Shell, Target = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" }));
        Assert.False(RunningApps.CanSwitch(new LaunchItem { Kind = ItemKind.File, Target = @"C:\x\doc.pdf" }));
        Assert.False(RunningApps.CanSwitch(new LaunchItem { Kind = ItemKind.Shell, Target = "shell:Downloads" }));
        Assert.False(RunningApps.CanSwitch(new LaunchItem { Kind = ItemKind.Url, Target = "https://x.org/" }));
    }

    [Fact]
    public void Running_WindowsAreMatchedByProgramOrAppId()
    {
        var exe = new LaunchItem { Kind = ItemKind.File, Target = @"C:\Tools\Editor.exe" };
        var app = new LaunchItem { Kind = ItemKind.Shell, Target = @"shell:AppsFolder\Contoso.App_123!App" };
        List<AppWindow> windows =
        [
            new(1, @"c:\tools\editor.EXE", null),
            new(2, @"C:\Windows\ApplicationFrameHost.exe", "contoso.app_123!app"),
            new(3, @"C:\Other\x.exe", null),
            new(4, @"C:\Tools\Editor.exe", "Something"),
        ];
        Assert.Equal([(IntPtr)1, (IntPtr)4], RunningApps.WindowsOf(exe, windows));
        Assert.Equal([(IntPtr)2], RunningApps.WindowsOf(app, windows));
        Assert.Empty(RunningApps.WindowsOf(new LaunchItem { Kind = ItemKind.Url, Target = "https://x.org/" }, windows));
    }

    [Fact]
    public void Running_StartMenuIdsOfDesktopPrograms_NameTheirFile()
    {
        var programFiles = new Guid("6D809377-6AF0-444B-8957-A3773F02200E");
        Func<Guid, string?> folders = id => id == programFiles ? @"C:\Program Files" : null;
        Assert.Equal(@"C:\Program Files\Notepad++\notepad++.exe",
            RunningApps.ExeFromAppId(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Notepad++\notepad++.exe", folders));
        Assert.Null(RunningApps.ExeFromAppId("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", folders));
        Assert.Null(RunningApps.ExeFromAppId(@"{00000000-0000-0000-0000-000000000000}\x.exe", folders));
        // The real known folder
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            RunningApps.ExeFromAppId(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\cmd.exe"), ignoreCase: true);
    }

    [Fact]
    public void Running_WindowsOfOtherProgramsAreListed_NotOurOwn()
    {
        var windows = RunningApps.Windows();
        var own = Process.GetCurrentProcess().MainModule!.FileName;
        Assert.DoesNotContain(windows, w => string.Equals(w.ExePath, own, StringComparison.OrdinalIgnoreCase)
                                            && w.Handle == Process.GetCurrentProcess().MainWindowHandle);
        Assert.All(windows, w => Assert.NotEqual(IntPtr.Zero, w.Handle));
    }

    [Fact]
    public void Running_AFreshLookIsNeverTheCachedOne_ARecentOneMayBe()
    {
        var fresh = RunningApps.Windows();
        Assert.NotSame(fresh, RunningApps.Windows()); // zero age: always looked up again
        Assert.Same(RunningApps.Windows(), RunningApps.Windows(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Running_NothingOpen_StartsNormally()
    {
        var item = new LaunchItem { Kind = ItemKind.File, Target = Path.Combine(_dir, "never-running.exe"), SwitchToRunning = true };
        Assert.False(RunningApps.TryActivate(item));
    }
}
