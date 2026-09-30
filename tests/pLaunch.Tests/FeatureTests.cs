using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch.Tests;

public sealed class FeatureTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pLaunchFeat-" + Guid.NewGuid().ToString("N"));

    public FeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---------------------------------------------------------------- lists

    [Theory]
    [InlineData("Work", true)]
    [InlineData("My games 2", true)]
    [InlineData("dev-tools_1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("x:y", false)]
    [InlineData("default", false)]
    [InlineData("0123456789012345678901234567890123456789X", false)] // 41 chars
    public void ListNames_AreValidated(string name, bool valid)
    {
        Assert.Equal(valid, ListProfile.IsValidName(name));
    }

    [Fact]
    public void ListProfiles_HaveTheirOwnFileIdentityAndAutostart()
    {
        var work = ListProfile.Named("My Work");
        Assert.Equal(Path.Combine(_dir, "lists", "My Work.json"), work.FilePath(_dir));
        Assert.Equal("--list \"My Work\" ", work.Arguments);
        Assert.Equal("pLaunch (My Work)", work.AutostartValueName);
        Assert.Equal("Phate.pLaunch.List.My_Work", work.AppUserModelId);
        Assert.Contains("My Work", work.Title);

        var main = ListProfile.Default;
        Assert.Equal(Path.Combine(_dir, "items.json"), main.FilePath(_dir));
        Assert.Equal("", main.Arguments);
        Assert.Null(main.AppUserModelId); // keeps the id Windows derives from the exe (existing pins)
        Assert.NotEqual(main.InstanceKey, work.InstanceKey);
    }

    [Fact]
    public void ExistingLists_AreFoundInTheListsFolder()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lists"));
        File.WriteAllText(Path.Combine(_dir, "lists", "Work.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "lists", "Games.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "lists", "Old.json.deleted"), "{}");
        Assert.Equal(["Games", "Work"], ListProfile.ExistingNames(_dir));
        Assert.Empty(ListProfile.ExistingNames(Path.Combine(_dir, "none")));
    }

    [Fact]
    public void CommandLine_ReadsTheListAndTheUpdateSwitch()
    {
        var cl = CommandLine.Parse(["--list", "Work", "--minimized"]);
        Assert.Equal("Work", cl.Profile.Name);
        Assert.True(cl.Minimized);
        Assert.True(CommandLine.Parse(["--after-update"]).AfterUpdate);
        Assert.True(CommandLine.Parse(["--list", "bad/name"]).Profile.IsDefault);
        Assert.True(CommandLine.Parse([]).Profile.IsDefault);
        var launch = CommandLine.Parse(["--list", "Work", "--launch", "abc"]);
        Assert.Equal(("Work", "abc"), (launch.Profile.Name, launch.LaunchId));
    }

    // ---------------------------------------------------------------- properties

    [Fact]
    public void NewItemFields_AreSaved_AndTrueDefaultsSurviveFalse()
    {
        var store = new ItemStore(Path.Combine(_dir, "items.json"));
        var item = new LaunchItem
        {
            Kind = ItemKind.File, Name = "Tool", Target = @"C:\Tools\tool.exe", Arguments = "-v",
            WorkingDirectory = @"%USERPROFILE%\work", RunAsAdmin = true, IconPath = @"C:\Windows\System32\shell32.dll", IconIndex = 12,
        };
        store.Save([item], new LauncherSettings { Translucent = false, WebIcons = false, ButtonIconPath = @"C:\x.ico" });
        var data = store.Load();
        var loaded = data.Items[0];
        Assert.Equal((@"%USERPROFILE%\work", true, @"C:\Windows\System32\shell32.dll", 12),
            (loaded.WorkingDirectory, loaded.RunAsAdmin, loaded.IconPath, loaded.IconIndex));
        Assert.False(data.Settings.Translucent); // not dropped as a "default" and read back as true
        Assert.False(data.Settings.WebIcons);
        Assert.Equal(@"C:\x.ico", data.Settings.ButtonIconPath);
    }

    [Fact]
    public void StartInfo_UsesTheChosenFolderAndElevation()
    {
        var item = new LaunchItem { Kind = ItemKind.File, Target = @"C:\Tools\build.cmd" };
        Assert.Equal(@"C:\Tools", Launcher.CreateStartInfo(item).WorkingDirectory);
        Assert.Equal("", Launcher.CreateStartInfo(item).Verb);

        item.WorkingDirectory = @"%SystemRoot%\Temp";
        item.RunAsAdmin = true;
        var psi = Launcher.CreateStartInfo(item);
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "Temp"), psi.WorkingDirectory);
        Assert.Equal("runas", psi.Verb);

        // Documents cannot be elevated: the flag is ignored
        var doc = new LaunchItem { Kind = ItemKind.File, Target = @"C:\a.pdf", RunAsAdmin = true };
        Assert.Equal("", Launcher.CreateStartInfo(doc).Verb);
    }

    [Fact]
    public void GroupsWithACustomIcon_ShowIt()
    {
        Assert.False(new LaunchItem { Kind = ItemKind.Group }.HasShellIcon);
        Assert.True(new LaunchItem { Kind = ItemKind.Group, IconPath = "x.ico" }.HasShellIcon);
        Assert.False(new LaunchItem { Kind = ItemKind.Separator, IconPath = "x.ico" }.HasShellIcon);
    }

    // ---------------------------------------------------------------- favicons

    [Fact]
    public void IconLinks_AreParsedBestFirst()
    {
        var html = """
            <html><head>
            <link rel="stylesheet" href="/site.css">
            <link rel="icon" type="image/png" sizes="32x32" href="/favicon-32.png">
            <LINK REL='shortcut icon' href='favicon.ico'>
            <link rel="icon" type="image/svg+xml" href="/icon.svg">
            <link rel="apple-touch-icon" href="https://cdn.example.com/touch.png">
            <link href="/big.png" sizes="192x192 512x512" rel="icon">
            <link rel="icon" href="data:image/png;base64,AAAA">
            </head></html>
            """;
        var links = FaviconService.ParseIconLinks(html, new Uri("https://www.example.com/some/page"));
        Assert.Equal(
            ["https://www.example.com/big.png", "https://cdn.example.com/touch.png", "https://www.example.com/favicon-32.png", "https://www.example.com/some/favicon.ico"],
            links.Select(l => l.Url.AbsoluteUri));
        Assert.Equal(512, links[0].Size);
    }

    [Fact]
    public void FaviconCache_IsPerHost()
    {
        Assert.Equal("www.example.com", FaviconService.CacheKey(new Uri("https://WWW.Example.com/a/b")));
        Assert.Equal("localhost_8080", FaviconService.CacheKey(new Uri("http://localhost:8080/")));
    }

    // ---------------------------------------------------------------- updates

    static string Release(string tag, string assetName, string? digest = "sha256:ABCDEF", bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}", "html_url": "https://github.com/FirekeeperPhate/pLaunch/releases/tag/{{tag}}",
          "draft": false, "prerelease": {{(prerelease ? "true" : "false")}}, "body": "Notes",
          "assets": [
            { "name": "{{assetName}}", "browser_download_url": "https://github.com/x/{{assetName}}"{{(digest == null ? "" : $", \"digest\": \"{digest}\"")}} },
            { "name": "other.zip", "browser_download_url": "https://github.com/x/other.zip", "digest": "sha256:00" }
          ]
        }
        """;

    [Fact]
    public void Update_NewerRelease_WithTheSameEdition()
    {
        var info = UpdateService.Parse(Release("v0.2.0", "pLaunch-Setup-0.2.0-Full.exe"), new Version(0, 1, 0), "Full");
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 2, 0), info.Version);
        Assert.Equal("abcdef", info.Sha256);
        Assert.Equal("https://github.com/x/pLaunch-Setup-0.2.0-Full.exe", info.Download.AbsoluteUri);
    }

    [Theory]
    [InlineData("v0.1.0", "pLaunch-Setup-0.1.0-Full.exe", "sha256:AB", false, "Full")]   // same version
    [InlineData("v0.0.9", "pLaunch-Setup-0.0.9-Full.exe", "sha256:AB", false, "Full")]   // older
    [InlineData("v0.2.0", "pLaunch-Setup-0.2.0-Light.exe", "sha256:AB", false, "Full")]  // other edition only
    [InlineData("v0.2.0", "pLaunch-Setup-0.2.0-Full.exe", null, false, "Full")]          // no digest: not verifiable
    [InlineData("v0.2.0", "pLaunch-Setup-0.2.0-Full.exe", "md5:AB", false, "Full")]      // not a SHA-256
    [InlineData("v0.2.0", "pLaunch-Setup-0.2.0-Full.exe", "sha256:AB", true, "Full")]    // prerelease
    [InlineData("nightly", "pLaunch-Setup-0.2.0-Full.exe", "sha256:AB", false, "Full")]  // not a version
    public void Update_IsIgnoredWhenNotSafeOrNotNewer(string tag, string asset, string? digest, bool prerelease, string edition)
    {
        Assert.Null(UpdateService.Parse(Release(tag, asset, digest, prerelease), new Version(0, 1, 0), edition));
    }

    [Fact]
    public void Update_CheckIsDueOncePerDay()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateService.IsCheckDue(new AppConfig(), now));
        Assert.False(UpdateService.IsCheckDue(new AppConfig { LastUpdateCheck = now.AddHours(-3) }, now));
        Assert.True(UpdateService.IsCheckDue(new AppConfig { LastUpdateCheck = now.AddHours(-25) }, now));
        Assert.False(UpdateService.IsCheckDue(new AppConfig { CheckForUpdates = false }, now));
    }
}
