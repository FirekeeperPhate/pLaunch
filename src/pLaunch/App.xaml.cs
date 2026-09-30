using System.Windows;
using pLaunch.Services;

namespace pLaunch;

public partial class App : Application
{
    readonly SingleInstance _instance;
    readonly CommandLine _options;

    public App(SingleInstance instance, CommandLine options)
    {
        _instance = instance;
        _options = options;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Autostart.Refresh();

        var window = new PopupWindow(ItemStore.CreateDefault());
        MainWindow = window;
        window.Start(minimized: _options.Minimized && _options.Items.Count == 0);
        if (_options.Items.Count > 0)
            window.AddFromArguments(_options.Items);

        _instance.StartServer(args => Dispatcher.BeginInvoke(() =>
        {
            if (args.Count > 0)
                window.AddFromArguments(args);
            else
                window.ShowPopup();
        }));
    }
}
