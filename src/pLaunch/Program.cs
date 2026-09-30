using System.Windows;
using pLaunch.Services;

namespace pLaunch;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = CommandLine.Parse(args);

        // Jump list entries start a short-lived pLaunch that only launches the item
        if (options.LaunchId != null)
            return LaunchById(options.LaunchId);

        using var instance = new SingleInstance();
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

    static int LaunchById(string id)
    {
        var item = ItemTree.Find(ItemStore.CreateDefault().Load().Items, id);
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
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        bool minimized = false;
        string? launchId = null;
        var items = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                minimized = true;
            else if (a.Equals("--launch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                launchId = args[++i];
            else if (!a.StartsWith("--", StringComparison.Ordinal))
                items.Add(a);
        }
        return new CommandLine(minimized, launchId, items);
    }
}
