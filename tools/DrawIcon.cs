#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws src/pLaunch/Assets/pLaunch.ico: a rounded tile with a blue-violet gradient and two white upward
// chevrons ("launch"), readable on light and dark backgrounds (a hairline dark edge keeps the tile's
// shape on light ones). 24 px and up are drawn as vectors; 16 and 20 px place the chevrons on the pixel
// grid so they stay sharp.
// Run from the repo root:
//   dotnet run tools/DrawIcon.cs                            -> writes the .ico
//   dotnet run tools/DrawIcon.cs -- --preview sheet.png     -> also a preview sheet (light, dark, zoomed)
//   dotnet run tools/DrawIcon.cs -- --output other.ico      -> write elsewhere
string output = Path.Combine("src", "pLaunch", "Assets", "pLaunch.ico");
string? previewPath = null;
bool smooth = false, symbols = false;
for (int a = 0; a < args.Length; a++)
{
    switch (args[a])
    {
        case "--output": output = args[++a]; break;
        case "--preview": previewPath = args[++a]; break;
        case "--smooth": smooth = true; break; // small sizes as antialiased vectors too (for comparison)
        case "--symbols": symbols = true; break;
    }
}

// The notification area versions: the two chevrons alone, white (for a dark taskbar) and black (for a
// light one), like the Windows icons beside the clock.
//   dotnet run tools/DrawIcon.cs -- --symbols [--preview sheet.png]
if (symbols)
{
    int[] symbolSizes = [16, 20, 24, 32, 40, 48, 64];
    foreach (var (name, color) in new[] { ("pLaunchWhite.ico", Colors.White), ("pLaunchBlack.ico", Colors.Black) })
    {
        var path = Path.Combine("src", "pLaunch", "Assets", name);
        WriteIco(path, symbolSizes, s => Symbol(s, color));
        Console.WriteLine($"Wrote {path}");
    }
    if (previewPath != null)
    {
        const int SW = 900, SH = 330;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var (bg, color, top) in new[] { (C("#1F1F1F"), Colors.White, 0.0), (C("#F3F3F3"), Colors.Black, 165.0) })
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, top, SW, 165));
                double x = 20;
                foreach (var s in new[] { 64, 48, 32, 24, 20, 16 })
                {
                    dc.DrawImage(Symbol(s, color), new Rect(x, top + 20 + (64 - s) / 2.0, s, s));
                    x += s + 16;
                }
                foreach (var s in new[] { 24, 20, 16 })
                {
                    int f = 120 / s;
                    dc.DrawImage(Zoom(Symbol(s, color), f), new Rect(x + 10, top + 20, s * f, s * f));
                    x += s * f + 14;
                }
            }
        }
        var symbolSheet = new RenderTargetBitmap(SW, SH, 96, 96, PixelFormats.Pbgra32);
        symbolSheet.Render(visual);
        File.WriteAllBytes(previewPath, EncodePng(symbolSheet));
        Console.WriteLine($"Wrote {previewPath}");
    }
    return;
}

var from = C("#3B82F6");
var to = C("#7C3AED");
const byte FadedAlpha = 170; // the lower chevron

BitmapSource Icon(int size) => size > 20 || smooth ? Vector(size) : Grid(size);

// ---- the .ico: PNG entries (valid in icons since Vista)
WriteIco(output, [16, 20, 24, 32, 40, 48, 64, 256], Icon);
Console.WriteLine($"Wrote {output}");

if (previewPath != null)
{
    int[] sizes = [256, 64, 48, 40, 32, 24, 20, 16];
    int[] zoomed = [32, 24, 20, 16];
    const int W = 1180, H = 620;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
        foreach (var (bg, top) in new[] { (C("#F3F3F3"), 10.0), (C("#1F1F1F"), 310.0) })
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(10, top, W - 20, 290));
            double x = 24;
            foreach (var s in sizes)
            {
                dc.DrawImage(Icon(s), new Rect(x, top + 16 + (256 - s) / 2.0, s, s));
                x += s + 16;
            }
            // Small sizes, each pixel as a block
            double zx = x + 10;
            foreach (var s in zoomed)
            {
                int f = 128 / s;
                dc.DrawImage(Zoom(Icon(s), f), new Rect(zx, top + 16, s * f, s * f));
                zx += s * f + 10;
                if (zx > W - 140)
                    break;
            }
        }
    }
    var sheet = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
    sheet.Render(dv);
    File.WriteAllBytes(previewPath, EncodePng(sheet));
    Console.WriteLine($"Wrote {previewPath}");
}

// ---------------------------------------------------------------- drawing

// The tile: gradient, a soft light from above and a hairline darker edge
DrawingVisual Tile(int size, out Rect tile)
{
    double inset = Math.Max(1, Math.Round(size * 0.06));
    tile = new Rect(inset, inset, size - 2 * inset, size - 2 * inset);
    double radius = tile.Width * 0.24;
    var visual = new DrawingVisual();
    using var dc = visual.RenderOpen();
    dc.DrawRoundedRectangle(new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1)), null, tile, radius, radius);
    var shine = new LinearGradientBrush(Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 0.6));
    dc.DrawRoundedRectangle(shine, null, tile, radius, radius);
    var edge = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), Math.Max(1, size / 64.0));
    double e = edge.Thickness / 2;
    dc.DrawRoundedRectangle(null, edge, new Rect(tile.X + e, tile.Y + e, tile.Width - 2 * e, tile.Height - 2 * e), radius - e, radius - e);
    return visual;
}

BitmapSource Vector(int size)
{
    var visual = Tile(size, out var tile);
    var chevrons = new DrawingVisual();
    using (var dc = chevrons.RenderOpen())
    {
        dc.DrawDrawing(visual.Drawing);
        double s = tile.Width;
        dc.PushTransform(new TranslateTransform(tile.X, tile.Y));
        Chevron(dc, s, 0.28, Colors.White);
        Chevron(dc, s, 0.52, Color.FromArgb(FadedAlpha, 255, 255, 255));
        dc.Pop();
    }
    return RenderBitmap(chevrons, size);
}

static void Chevron(DrawingContext dc, double s, double apexY, Color color)
{
    var pen = new Pen(new SolidColorBrush(color), 0.115 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    var g = new StreamGeometry();
    using (var ctx = g.Open())
    {
        ctx.BeginFigure(new Point(0.27 * s, (apexY + 0.21) * s), false, false);
        ctx.LineTo(new Point(0.5 * s, apexY * s), true, true);
        ctx.LineTo(new Point(0.73 * s, (apexY + 0.21) * s), true, true);
    }
    dc.DrawGeometry(null, pen, g);
}

// Small sizes: each chevron is a 45° band of whole pixels: t rows thick, reaching n columns out from
// the two middle ones; the second one d rows lower; everything centred on the tile
BitmapSource Grid(int size)
{
    var (t, n, d) = size <= 16 ? (2, 4, 4) : (3, 5, 5); // 16 px, 20 px
    var tileBitmap = RenderBitmap(Tile(size, out var tile), size);
    var px = new byte[size * size * 4];
    tileBitmap.CopyPixels(px, size * 4, 0);
    int a = size / 2 - 1, b = size / 2; // the two middle columns
    int rows = d + (n - 1) + t;
    int top = (int)Math.Round(tile.Y + (tile.Height - rows) / 2.0);
    void Band(int apexRow, byte alpha)
    {
        double al = alpha / 255.0;
        for (int x = a - (n - 1); x <= b + (n - 1); x++)
        {
            int dx = x <= a ? a - x : x - b; // columns out from the middle
            for (int dy = dx; dy < dx + t; dy++)
            {
                int o = ((apexRow + dy) * size + x) * 4;
                // White over the tile (premultiplied)
                px[o] = (byte)Math.Round(255 * al + px[o] * (1 - al));
                px[o + 1] = (byte)Math.Round(255 * al + px[o + 1] * (1 - al));
                px[o + 2] = (byte)Math.Round(255 * al + px[o + 2] * (1 - al));
            }
        }
    }
    Band(top, 255);
    Band(top + d, FadedAlpha);
    var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, px, size * 4);
    result.Freeze();
    return result;
}

// The two chevrons alone, filling the icon. Up to 20 px on the pixel grid (bands of whole pixels, as in
// Grid), above as vectors: the tile's chevrons, one and a half times as big, around the same centre.
BitmapSource Symbol(int size, Color color)
{
    if (size > 20)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double s = size * 1.5;
            dc.PushTransform(new TranslateTransform((size - s) / 2, (size - s) / 2));
            Chevron(dc, s, 0.28, color);
            Chevron(dc, s, 0.52, Color.FromArgb(FadedAlpha, color.R, color.G, color.B));
            dc.Pop();
        }
        return RenderBitmap(visual, size);
    }
    var (t, n, d) = size <= 16 ? (2, 6, 5) : (3, 7, 6);
    var px = new byte[size * size * 4];
    int a = size / 2 - 1, b = size / 2;
    int top = (size - (d + (n - 1) + t)) / 2;
    void Band(int apexRow, byte alpha)
    {
        for (int x = a - (n - 1); x <= b + (n - 1); x++)
        {
            int dx = x <= a ? a - x : x - b;
            for (int dy = dx; dy < dx + t; dy++)
            {
                int o = ((apexRow + dy) * size + x) * 4;
                if (px[o + 3] >= alpha)
                    continue; // the upper chevron stays as it is where the two meet
                // Premultiplied
                px[o] = (byte)(color.B * alpha / 255);
                px[o + 1] = (byte)(color.G * alpha / 255);
                px[o + 2] = (byte)(color.R * alpha / 255);
                px[o + 3] = alpha;
            }
        }
    }
    Band(top, 255);
    Band(top + d, FadedAlpha);
    var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, px, size * 4);
    result.Freeze();
    return result;
}

// An .ico with PNG entries (valid in icons since Vista)
static void WriteIco(string path, int[] sizes, Func<int, BitmapSource> draw)
{
    var pngs = sizes.Select(s => EncodePng(draw(s))).ToList();
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(pngs[i].Length);
        writer.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs)
        writer.Write(png);
}

static BitmapSource RenderBitmap(Visual visual, int size)
{
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    bmp.Freeze();
    return bmp;
}

static BitmapSource Zoom(BitmapSource b, int f)
{
    int w = b.PixelWidth, h = b.PixelHeight;
    var src = new byte[w * h * 4];
    b.CopyPixels(src, w * 4, 0);
    var dst = new byte[w * f * h * f * 4];
    for (int y = 0; y < h * f; y++)
        for (int x = 0; x < w * f; x++)
            Array.Copy(src, ((y / f) * w + x / f) * 4, dst, (y * w * f + x) * 4, 4);
    var r = BitmapSource.Create(w * f, h * f, 96, 96, PixelFormats.Pbgra32, null, dst, w * f * 4);
    r.Freeze();
    return r;
}

static byte[] EncodePng(BitmapSource b)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(b));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
