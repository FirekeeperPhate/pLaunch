using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using pLaunch.Services;

namespace pLaunch;

// Automatic updates from the GitHub releases
public partial class PopupWindow
{
    DispatcherTimer? _updateTimer;
    UpdateInfo? _update;
    bool _downloading;

    void InitUpdates()
    {
        // First check a little after startup (not during logon), then look every hour whether one is due
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            if (_update == null && UpdateService.IsCheckDue(AppConfig.Load(), DateTime.UtcNow))
                await CheckForUpdate(manual: false);
        };
        _updateTimer.Start();
    }

    internal async Task CheckForUpdate(bool manual)
    {
        var (status, info) = await UpdateService.CheckAsync();
        if (status == UpdateCheckStatus.Available && info != null)
        {
            if (!manual && AppConfig.Load().SkippedVersion == info.Version.ToString())
                return;
            ShowUpdate(info);
            if (manual)
                ShowPopup();
            return;
        }
        if (!manual)
            return;
        var message = status switch
        {
            UpdateCheckStatus.UpToDate => $"pLaunch {UpdateService.CurrentVersion} is the latest version.",
            UpdateCheckStatus.NoRelease => "No published release could be found (the repository may not be public yet).",
            _ => "Could not check for updates. Is the computer online?",
        };
        ShowModal(() => MessageBox.Show(this, message, "pLaunch", MessageBoxButton.OK, MessageBoxImage.Information));
    }

    void ShowUpdate(UpdateInfo info)
    {
        _update = info;
        UpdateText.Text = $"pLaunch {info.Version} is available (you have {UpdateService.CurrentVersion}).";
        UpdateButtons.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
        // A small badge on the taskbar button tells about it while the popup is closed
        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Overlay = UpdateBadge();
        TaskbarItemInfo.Description = UpdateText.Text;
        if (IsOpen)
            Place();
    }

    void HideUpdate()
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        if (TaskbarItemInfo != null)
        {
            TaskbarItemInfo.Overlay = null;
            TaskbarItemInfo.Description = "";
        }
        if (IsOpen)
            Place();
    }

    async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } info || _downloading)
            return;
        _downloading = true;
        UpdateButtons.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateText.Text = $"Downloading pLaunch {info.Version}\x2026";
        try
        {
            var installer = await UpdateService.DownloadAsync(info, new Progress<double>(p => UpdateProgress.Value = p));
            UpdateText.Text = "Installing\x2026 pLaunch restarts by itself.";
            // Every list closes (this one too); the installer starts them again when it is done
            UpdateService.InstallAndExit(installer);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
                                       or InvalidDataException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            _downloading = false;
            ShowUpdate(info);
            ShowError("The update could not be installed.", ex.Message);
        }
    }

    void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _update = null; // asked again at the next due check
        HideUpdate();
    }

    void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update is { } info)
            AppConfig.Update(c => c.SkippedVersion = info.Version.ToString());
        _update = null;
        HideUpdate();
    }

    /// <summary>The taskbar overlay: an accent-colored dot with a down arrow.</summary>
    static DrawingImage UpdateBadge()
    {
        var accent = SystemParameters.WindowGlassColor;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(accent.A == 0 ? Color.FromRgb(0x00, 0x67, 0xC0) : Color.FromRgb(accent.R, accent.G, accent.B)),
            new Pen(Brushes.White, 1.2), new EllipseGeometry(new Point(8, 8), 7.2, 7.2)));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
            Geometry.Parse("M 8,4 L 8,11.5 M 5,8.8 L 8,11.8 L 11,8.8")));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
