using System.Windows;
using pLaunch.Native;
using pLaunch.Services;

namespace pLaunch;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = CommandLine.Parse(args);

        // Started by the installer after an automatic update: restart the lists that were running
        if (options.AfterUpdate)
            return UpdateService.RelaunchAfterUpdate();

        // Jump list entries start a short-lived pLaunch that only launches the item
        if (options.LaunchId != null)
            return LaunchById(options.Profile, options.LaunchId);

        // A named list has its own taskbar button: its own AppUserModelID, set before any window exists
        if (options.Profile.AppUserModelId is { } appId)
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(appId);

        using var instance = new SingleInstance(options.Profile);
        if (!instance.IsPrimary)
        {
            // A duplicate autostart (e.g. two Run entries) has nothing to ask: opening the popup at logon would be wrong
            if (options.Minimized && options.Items.Count == 0)
                return 0;
            // Relative paths belong to this process's current folder, not the running instance's
            instance.SendToPrimary(options.Items.Select(ToAbsolute).ToList());
            return 0;
        }

        var app = new App(instance, options);
        app.InitializeComponent();
        return app.Run();
    }

    internal static string ToAbsolute(string argument)
    {
        var path = argument.Trim().Trim('"');
        try
        {
            return File.Exists(path) || Directory.Exists(path) ? Path.GetFullPath(path) : argument;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return argument;
        }
    }

    static int LaunchById(ListProfile profile, string id)
    {
        var item = ItemTree.Find(ItemStore.For(profile).Load().Items, id);
        if (item is not { IsLaunchable: true })
            return 1;
        try
        {
            Launcher.Launch(item);
            return 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Cannot open \"{item.Name}\".\n\n{ex.Message}", "pLaunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }
}

public sealed record CommandLine(bool Minimized, string? LaunchId, IReadOnlyList<string> Items)
{
    /// <summary>The list to show: "--list Name", else the default one.</summary>
    public ListProfile Profile { get; init; } = ListProfile.Default;

    /// <summary>"--after-update": started by the installer to bring the lists back.</summary>
    public bool AfterUpdate { get; init; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        bool minimized = false, afterUpdate = false;
        string? launchId = null, list = null;
        var items = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                minimized = true;
            else if (a.Equals("--launch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                launchId = args[++i];
            else if (a.Equals("--list", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                list = args[++i];
            else if (a.Equals("--after-update", StringComparison.OrdinalIgnoreCase))
                afterUpdate = true;
            else if (!a.StartsWith("--", StringComparison.Ordinal))
                items.Add(a);
        }
        return new CommandLine(minimized, launchId, items)
        {
            Profile = ListProfile.FromArgument(list),
            AfterUpdate = afterUpdate,
        };
    }
}
