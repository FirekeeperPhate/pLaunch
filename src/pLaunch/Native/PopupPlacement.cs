namespace pLaunch.Native;

/// <summary>A rectangle in physical (device) pixels; Right/Bottom are exclusive like Win32 RECT.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Intersects(PixelRect o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
}

public enum ScreenEdge { Bottom, Top, Left, Right }

/// <summary>Pure placement math for the popup, all values in physical pixels.</summary>
public static class PopupPlacement
{
    /// <summary>
    /// Finds the side of the monitor that hosts the taskbar and the area the popup may use.
    /// A docked taskbar shrinks the work area; an auto-hide one does not, so its own rectangle
    /// (from ABM_GETTASKBARPOS) is used, and without it the popup assumes a bottom taskbar.
    /// </summary>
    public static (PixelRect Area, ScreenEdge Edge) AvailableArea(
        PixelRect monitor, PixelRect work, PixelRect? taskbar, int fallbackThickness)
    {
        if (work.Bottom < monitor.Bottom) return (work, ScreenEdge.Bottom);
        if (work.Top > monitor.Top) return (work, ScreenEdge.Top);
        if (work.Left > monitor.Left) return (work, ScreenEdge.Left);
        if (work.Right < monitor.Right) return (work, ScreenEdge.Right);

        if (taskbar is { } tb && tb.Intersects(monitor))
        {
            // The rectangle of a hidden taskbar is moved off screen but keeps its size
            if (tb.Width >= tb.Height)
            {
                return tb.Top <= monitor.Top
                    ? (work with { Top = monitor.Top + tb.Height }, ScreenEdge.Top)
                    : (work with { Bottom = monitor.Bottom - tb.Height }, ScreenEdge.Bottom);
            }
            return tb.Left <= monitor.Left
                ? (work with { Left = monitor.Left + tb.Width }, ScreenEdge.Left)
                : (work with { Right = monitor.Right - tb.Width }, ScreenEdge.Right);
        }

        return (work with { Bottom = monitor.Bottom - fallbackThickness }, ScreenEdge.Bottom);
    }

    /// <summary>
    /// Top-left corner for a popup of <paramref name="width"/> x <paramref name="height"/>: against the
    /// taskbar edge, centered on the cursor when it is on the taskbar (the pLaunch button was clicked or
    /// hovered), otherwise centered in the area (e.g. opened with Win+number).
    /// </summary>
    public static (int X, int Y) Compute(PixelRect area, ScreenEdge edge, int cursorX, int cursorY, int width, int height, int gap)
    {
        bool onTaskbar = edge switch
        {
            ScreenEdge.Bottom => cursorY >= area.Bottom,
            ScreenEdge.Top => cursorY < area.Top,
            ScreenEdge.Left => cursorX < area.Left,
            _ => cursorX >= area.Right,
        };

        if (edge is ScreenEdge.Bottom or ScreenEdge.Top)
        {
            int center = onTaskbar ? cursorX : area.Left + area.Width / 2;
            int x = Clamp(center - width / 2, area.Left + gap, area.Right - gap - width);
            int y = edge == ScreenEdge.Bottom ? area.Bottom - gap - height : area.Top + gap;
            return (x, y);
        }
        else
        {
            int center = onTaskbar ? cursorY : area.Top + area.Height / 2;
            int y = Clamp(center - height / 2, area.Top + gap, area.Bottom - gap - height);
            int x = edge == ScreenEdge.Left ? area.Left + gap : area.Right - gap - width;
            return (x, y);
        }
    }

    // Math.Clamp throws when min > max (popup larger than the area): prefer the top/left edge then
    static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

    /// <summary>
    /// Whether the pointer, moving from <paramref name="from"/> to <paramref name="to"/>, is on its way
    /// to an open menu: <paramref name="to"/> lies in the triangle between <paramref name="from"/> and
    /// the menu's near edge. Crossing other rows on the way there must not close the menu.
    /// </summary>
    public static bool IsHeadingFor((int X, int Y) from, (int X, int Y) to, PixelRect menu)
    {
        if (from == to)
            return false; // resting on a row: that row is meant
        int edge = menu.Left >= from.X ? menu.Left : menu.Right;
        return InTriangle(to, from, (edge, menu.Top), (edge, menu.Bottom));
    }

    static bool InTriangle((int X, int Y) p, (int X, int Y) a, (int X, int Y) b, (int X, int Y) c)
    {
        static long Side((int X, int Y) p, (int X, int Y) q, (int X, int Y) r) =>
            (long)(p.X - r.X) * (q.Y - r.Y) - (long)(q.X - r.X) * (p.Y - r.Y);
        long d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }
}
