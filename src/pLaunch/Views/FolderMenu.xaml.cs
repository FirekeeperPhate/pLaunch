using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using pLaunch.Models;
using pLaunch.ViewModels;

namespace pLaunch.Views;

/// <summary>
/// A sub-folder opened beside the list (or beside another menu), like the menus of the old Quick Launch.
/// Only the view: the popup fills it, places it and handles what happens in it. It never takes the
/// focus (WS_EX_NOACTIVATE), so the popup stays the active window and keeps the keyboard.
/// </summary>
public partial class FolderMenu : Window
{
    public FolderMenu(LaunchItem folder, ItemViewModel opener)
    {
        InitializeComponent();
        Folder = folder;
        Opener = opener;
        List.ItemsSource = Items;
        Items.CollectionChanged += (_, _) => EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The sub-folder (or live folder) shown.</summary>
    public LaunchItem Folder { get; }

    /// <summary>The row it was opened from (in the popup or in the menu before it), shown as selected.</summary>
    public ItemViewModel Opener { get; }

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    public IntPtr Handle { get; private set; }

    /// <summary>Creates the window handle without showing it, so it can be sized and placed first.</summary>
    public IntPtr EnsureHandle()
    {
        if (Handle != IntPtr.Zero)
            return Handle;
        Handle = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(Handle).CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        long style = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        return Handle;
    }

    /// <summary>The row of an item, when it has one on screen.</summary>
    public ListBoxItem? RowOf(ItemViewModel item) => List.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;

    const int GWL_EXSTYLE = -20;
    const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
