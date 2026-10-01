using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace pLaunch.Native;

/// <summary>
/// Where the pointer is on the taskbar. Windows 11 opens a window when something is dragged onto its
/// taskbar button and held there, but not for everything: a program or a shortcut to one is offered
/// "Pin to taskbar" instead (the buttons even move aside, leaving a gap under the pointer), and the window
/// never opens. pLaunch looks for itself.
/// </summary>
internal static class TaskbarHitTest
{
    /// <summary>Whether the point is on a taskbar (of any monitor). Cheap: no call into Explorer.</summary>
    public static bool IsTaskbarAt(NativeMethods.POINT point) => TaskbarAt(point) != IntPtr.Zero;

    static IntPtr TaskbarAt(NativeMethods.POINT point)
    {
        var hwnd = GetAncestor(WindowFromPoint(point), GA_ROOT);
        return hwnd != IntPtr.Zero && WindowInterop.ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? hwnd : IntPtr.Zero;
    }

    static readonly Condition TaskButtons = new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer");

    /// <summary>
    /// Where the taskbar buttons of the window titled <paramref name="title"/> are, on every taskbar (screen
    /// pixels). Asks Explorer (UI Automation): not on the UI thread. The buttons are looked for among the
    /// taskbars': asked for the element at a point, UI Automation stops at the taskbar itself.
    /// </summary>
    public static List<System.Windows.Rect> ButtonsOf(string title)
    {
        var found = new List<System.Windows.Rect>();
        foreach (var taskbar in Taskbars())
        {
            try
            {
                foreach (AutomationElement button in AutomationElement.FromHandle(taskbar).FindAll(TreeScope.Descendants, TaskButtons))
                {
                    if (IsButtonOf(button.Current.Name, title))
                        found.Add(button.Current.BoundingRectangle);
                }
            }
            catch (Exception)
            {
                // That taskbar went away meanwhile, or Explorer is busy or restarting: UI Automation fails in
                // many ways (timeouts included), and none of them may take pLaunch down. No button there.
            }
        }
        return found;
    }

    static IEnumerable<IntPtr> Taskbars()
    {
        var primary = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
            yield return primary;
        for (var other = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); other != IntPtr.Zero;
             other = FindWindowEx(IntPtr.Zero, other, "Shell_SecondaryTrayWnd", null))
            yield return other;
    }

    public static bool Contains(IEnumerable<System.Windows.Rect> rects, NativeMethods.POINT point) =>
        rects.Any(r => r.Contains(point.X, point.Y));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    /// <summary>
    /// Whether a taskbar button's name is that of the window titled <paramref name="title"/>: the title, or
    /// the title and " - " with how many windows run (in the language of Windows). The named lists are
    /// titled "pLaunch – Name" (an en dash), so the first list never takes their buttons for its own; nor
    /// does a list "A" the button of a list "A - B" (the count never has a " - " in it).
    /// </summary>
    public static bool IsButtonOf(string buttonName, string title) =>
        buttonName == title
        || buttonName.StartsWith(title + " - ", StringComparison.Ordinal) && !buttonName[(title.Length + 3)..].Contains(" - ");

    const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(NativeMethods.POINT point);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
