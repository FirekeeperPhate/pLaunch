using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using pLaunch.Models;
using pLaunch.Native;
using pLaunch.ViewModels;
using pLaunch.Views;

namespace pLaunch.Tests;

/// <summary>
/// The side menus must never become the active window (the popup would lose the focus and close before a
/// click in them does anything, as in 0.5.1), and the popup's window stays off screen when minimized.
/// </summary>
public sealed class MenuWindowTests
{
    static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new Exception("On the STA thread: " + error.Message, error);
    }

    [Fact]
    public void MenuRows_NeverTakeTheFocus() => OnSta(() =>
    {
        var folder = new LaunchItem { Kind = ItemKind.Group, Name = "Tools", Children = [] };
        var menu = new FolderMenu(folder, new ItemViewModel(folder));
        Assert.False(menu.List.Focusable);
        menu.UseRowStyle(new Style(typeof(ListBoxItem)));
        menu.Items.Add(new ItemViewModel(new LaunchItem { Kind = ItemKind.File, Name = "Notepad", Target = @"C:\Windows\notepad.exe" }));
        // A real row, laid out as on screen (without showing the window)
        menu.Root.Measure(new Size(320, 400));
        menu.Root.Arrange(new Rect(0, 0, 320, 400));
        menu.UpdateLayout();
        var row = Assert.IsType<ListBoxItem>(menu.List.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.False(row.Focusable);
        Assert.False(row.Focus()); // what a click does to a row: here it cannot take the focus
        menu.Close();
    });

    [Fact]
    public void MenuWindow_RefusesToBeActivatedByAClick()
    {
        bool handled = false;
        var answer = FolderMenu.WndProc(IntPtr.Zero, 0x0021 /* WM_MOUSEACTIVATE */, IntPtr.Zero, IntPtr.Zero, ref handled);
        Assert.True(handled);
        Assert.Equal(3 /* MA_NOACTIVATE */, answer.ToInt32());

        handled = false;
        FolderMenu.WndProc(IntPtr.Zero, 0x0200 /* WM_MOUSEMOVE */, IntPtr.Zero, IntPtr.Zero, ref handled);
        Assert.False(handled); // everything else goes on as usual
    }

    const uint NoSize = 0x1, NoMove = 0x2;

    // At 150 %: a minimized window is 237 x 39 (SM_CXMINIMIZED x SM_CYMINIMIZED)
    [Theory]
    [InlineData(0u, 0, 969, 237, 39, true)]          // minimizing: the little title bar above the taskbar
    [InlineData(NoSize, 237, 969, 0, 0, true)]       // the minimized bar moved (lined up again)
    [InlineData(0u, 720, 790, 480, 400, false)]      // restoring: the popup's size, it goes where it is sent
    [InlineData(0u, 720, 900, 480, 90, false)]       // restoring a tiny popup (one row): still not the bar
    [InlineData(NoMove, 0, 969, 237, 39, false)]     // not moving
    [InlineData(0u, -32000, -32000, 237, 39, false)] // off screen already
    public void MinimizedPopup_IsParkedOffScreen_ButRestoresNormally(uint flags, int x, int y, int cx, int cy, bool park)
    {
        Assert.Equal(park, TaskbarTab.ShouldPark(flags, x, y, cx, cy, [(237, 39)]));
    }

    [Fact]
    public void MinimizedBar_IsRecognizedAtAnyOfTheScales()
    {
        // Signed in at 150 % (237 x 39), now at 125 %: the bar is sized for the new scale
        (int, int)[] sizes = [(237, 39), (198, 33)];
        Assert.True(TaskbarTab.ShouldPark(0, 0, 975, 198, 33, sizes));
        Assert.False(TaskbarTab.ShouldPark(0, 0, 975, 400, 90, sizes));
    }

    // A drag held on the taskbar: whose button is it (the names as Windows gives them)
    [Theory]
    [InlineData("pLaunch - 1 running window", "pLaunch", true)]
    [InlineData("pLaunch - 1 finestra in esecuzione", "pLaunch", true)]
    [InlineData("pLaunch", "pLaunch", true)]
    [InlineData("pLaunch \x2013 Work - 1 running window", "pLaunch", false)]   // a named list's button
    [InlineData("pLaunch \x2013 Work - 1 running window", "pLaunch \x2013 Work", true)]
    [InlineData("pLaunch \x2013 Work - B - 1 running window", "pLaunch \x2013 Work", false)] // list "Work - B"
    [InlineData("Notepad - 1 running window", "pLaunch", false)]
    public void DragOntoTheTaskbar_FindsTheListsOwnButton(string buttonName, string title, bool own)
    {
        Assert.Equal(own, TaskbarHitTest.IsButtonOf(buttonName, title));
    }

    // What the watching timer sees, tick after tick: button, pointer, on the taskbar or not
    static NativeMethods.POINT At(int x, int y) => new() { X = x, Y = y };
    static readonly NativeMethods.POINT Desktop = At(500, 300), Button = At(1150, 1040);

    static List<DragHoverDetector.Step> Steps(DragHoverDetector detector, params (bool Down, NativeMethods.POINT At, bool OnTaskbar)[] ticks) =>
        ticks.Select(t => detector.Tick(t.Down, t.At, t.OnTaskbar, popupOpen: false)).ToList();

    [Fact]
    public void DragHeldOnTheTaskbar_ReadsTheButtonsWhenItStarts_ThenLooksOnce()
    {
        var steps = Steps(new DragHoverDetector(),
            (true, Desktop, false),         // pressed on a file
            (true, At(540, 380), false),    // moving: a drag
            (true, At(900, 800), false),
            (true, Button, true),           // on the taskbar
            (true, Button, true), (true, Button, true), (true, Button, true), // resting
            (true, Button, true), (true, Button, true));                      // still there: not looked at again
        Assert.Equal(DragHoverDetector.Step.ReadButtons, steps[1]);
        Assert.Equal(DragHoverDetector.Step.Probe, steps[3 + DragHoverDetector.RestTicks]);
        Assert.Single(steps, s => s == DragHoverDetector.Step.ReadButtons);
        Assert.Single(steps, s => s == DragHoverDetector.Step.Probe);
    }

    [Fact]
    public void ClicksAndHoldsAreNotDrags()
    {
        // A click that does not move, anywhere: Explorer is never asked anything
        Assert.All(Steps(new DragHoverDetector(), (true, Desktop, false), (true, Desktop, false), (true, At(503, 302), false), (false, Desktop, false)),
            s => Assert.Equal(DragHoverDetector.Step.None, s));
        // A press on the taskbar itself, held on the button (or dragging a button around)
        Assert.All(Steps(new DragHoverDetector(), (true, Button, true), (true, Button, true), (true, Button, true), (true, Button, true), (true, At(1300, 1040), true), (true, Button, true)),
            s => Assert.Equal(DragHoverDetector.Step.None, s));
    }

    [Fact]
    public void DragMovingAlongTheTaskbar_IsLookedAtWhereItRests_AndAgainAfterTheNextPress()
    {
        var detector = new DragHoverDetector();
        var steps = Steps(detector,
            (true, Desktop, false), (true, At(700, 700), false),
            (true, At(900, 1040), true), (true, At(1000, 1040), true), (true, At(1100, 1040), true), // passing over
            (true, Button, true), (true, Button, true), (true, Button, true), (true, Button, true));
        Assert.Equal(DragHoverDetector.Step.Probe, steps[^1]);
        Assert.Single(steps, s => s == DragHoverDetector.Step.Probe);

        Assert.Equal(DragHoverDetector.Step.None, detector.Tick(false, Button, true, false)); // dropped
        steps = Steps(detector, (true, Desktop, false), (true, At(700, 700), false));
        Assert.Equal(DragHoverDetector.Step.ReadButtons, steps[1]); // a new drag starts over
    }

    [Fact]
    public void NothingIsLookedAt_WhileTheListIsOpen()
    {
        var detector = new DragHoverDetector();
        detector.Tick(true, Desktop, false, popupOpen: true);
        for (int i = 0; i < 6; i++)
            Assert.Equal(DragHoverDetector.Step.None, detector.Tick(true, Button, true, popupOpen: true));
    }

    // ---- the taskbar button's life (the shell faked: requests and removals are counted)

    const int WM_DESTROY = 0x0002;
    static readonly IntPtr Window = new(0x1234);

    sealed class FakeShell
    {
        public int Requests, Removals;
        public TaskbarButton Button()
        {
            var ms = TimeSpan.FromMilliseconds(15);
            return new TaskbarButton(Window, hwnd => { Assert.Equal(Window, hwnd); Requests++; }, hwnd => { Assert.Equal(Window, hwnd); Removals++; })
            {
                Delays = [ms, ms, ms, ms],
            };
        }
    }

    /// <summary>Runs the dispatcher (the button's timer) for a while.</summary>
    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void TaskbarButton_IsAskedForUntilTheShellConfirmsIt() => OnSta(() =>
    {
        var shell = new FakeShell();
        var button = shell.Button();
        button.Request();
        Pump(400);
        Assert.Equal(4, shell.Requests); // never confirmed: every attempt, then it gives up
        Assert.False(button.IsShown);

        shell.Requests = 0;
        button.Request();
        for (int i = 0; i < 50 && shell.Requests == 0; i++)
            Pump(5);
        button.HandleMessage(TaskbarTab.TaskbarButtonCreatedMessage, IntPtr.Zero); // after the first request
        int asked = shell.Requests;
        Pump(300);
        Assert.True(button.IsShown);
        Assert.Equal(asked, shell.Requests); // confirmed: not asked any more
        Assert.InRange(asked, 1, 3);
    });

    [Fact]
    public void TaskbarButton_IsAskedForAgainWhenExplorerRestarts() => OnSta(() =>
    {
        var shell = new FakeShell();
        var button = shell.Button();
        button.HandleMessage(TaskbarTab.TaskbarButtonCreatedMessage, IntPtr.Zero);
        button.HandleMessage(TaskbarTab.TaskbarCreatedMessage, IntPtr.Zero);
        Assert.False(button.IsShown);
        Pump(40);
        Assert.True(shell.Requests >= 1);
        button.HandleMessage(TaskbarTab.TaskbarButtonCreatedMessage, IntPtr.Zero);
        Assert.True(button.IsShown);
    });

    [Fact]
    public void TaskbarButton_IsTakenAwayWithTheWindow_AndNeverAskedForAfterwards() => OnSta(() =>
    {
        var shell = new FakeShell();
        var button = shell.Button();
        button.Request();
        button.HandleMessage(WM_DESTROY, IntPtr.Zero);
        Assert.Equal(1, shell.Removals);
        Pump(200);
        Assert.Equal(0, shell.Requests); // the pending request was dropped

        button.Request();
        button.HandleMessage(TaskbarTab.TaskbarCreatedMessage, IntPtr.Zero);
        Pump(200);
        Assert.Equal(0, shell.Requests); // a shell would keep a button for a window that is gone
    });
}
