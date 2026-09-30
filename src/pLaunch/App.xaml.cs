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

    DateTime _lastErrorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // pLaunch lives on the taskbar all day: an unexpected error must not make it vanish
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ErrorLog.Write("Fatal error", ex);
        };

        Autostart.Refresh();

        var store = ItemStore.CreateDefault();
        var window = new PopupWindow(store);
        MainWindow = window;
        window.Start(minimized: _options.Minimized && _options.Items.Count == 0);
        if (store.LoadError != null)
        {
            MessageBox.Show(
                $"The list of shortcuts could not be read:\n{store.LoadError}\n\n" +
                $"{store.FilePath}\n\npLaunch starts with an empty list and will not overwrite the file. Restart pLaunch to try again.",
                "pLaunch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("Unhandled error", e.Exception);
        e.Handled = true;
        // One message at a time: an error repeating on every tick must not bury the desktop in dialogs
        if (DateTime.UtcNow - _lastErrorShown < TimeSpan.FromSeconds(30))
            return;
        _lastErrorShown = DateTime.UtcNow;
        MessageBox.Show(
            $"Something went wrong:\n{e.Exception.Message}\n\npLaunch keeps running. Details are in\n{ErrorLog.FilePath}",
            "pLaunch", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
