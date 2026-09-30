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
            instance.SendToPrimary(options.Items);
            return 0;
        }

        var app = new App(instance, options);
        app.InitializeComponent();
        return app.Run();
    }

    static int LaunchById(string id)
    {
        var item = ItemStore.CreateDefault().Load().FirstOrDefault(i => i.Id == id);
        if (item == null)
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
