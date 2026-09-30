using pLaunch.Models;

namespace pLaunch.ViewModels;

/// <summary>Sizes (DIPs) of the popup and of its items for a view mode and an item size.</summary>
public sealed record ViewMetrics(
    double IconSize,
    double RowHeight,
    double TileWidth,
    double TileHeight,
    int Columns,
    double Width)
{
    /// <summary>Room for the list's margins (4 + 4), a vertical scroll bar and some slack.</summary>
    public const double Chrome = 26;
    /// <summary>Margin around each tile (2 on each side).</summary>
    public const double TileMargin = 4;

    /// <summary>Separators take a whole row of tiles.</summary>
    public double SeparatorWidth => Width - Chrome;

    public static ViewMetrics For(ViewMode view, ItemSize size)
    {
        int s = (int)size; // 0 small, 1 medium, 2 large
        switch (view)
        {
            case ViewMode.Grid:
            {
                double[] icon = [32, 48, 64], tileW = [76, 92, 112], tileH = [86, 104, 126];
                return Tiles(icon[s], tileW[s], tileH[s], columns: 4);
            }
            case ViewMode.Icons:
            {
                double[] icon = [24, 32, 48], tile = [40, 48, 64];
                return Tiles(icon[s], tile[s], tile[s], columns: 6);
            }
            default:
            {
                double[] icon = [16, 24, 32], row = [30, 38, 46], width = [280, 320, 360];
                return new ViewMetrics(icon[s], row[s], 0, 0, 1, width[s]);
            }
        }
    }

    static ViewMetrics Tiles(double icon, double tileWidth, double tileHeight, int columns) =>
        new(icon, 0, tileWidth, tileHeight, columns, columns * (tileWidth + TileMargin) + Chrome);
}
