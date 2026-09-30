#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Builds src/pLaunch/Assets/pLaunch.ico from the artwork in tools/icon-source.jpg:
// - the noisy dark-blue panel is replaced by a smooth fit of its gradient (the logo's soft shadow is kept)
// - the logo is separated (with unmixed antialiased edges) and enlarged
// - 40 px and up keep the original thick frame; 32 px and down use a thin frame and an even bigger logo
// Run from the repo root:
//   dotnet run tools/MakeIcon.cs                                  -> writes the .ico
//   dotnet run tools/MakeIcon.cs -- --preview preview.png         -> also writes a preview sheet
//   dotnet run tools/MakeIcon.cs -- --large 1.28 --small 1.40     -> logo scale of each variant
//   dotnet run tools/MakeIcon.cs -- --lighten 1.6                 -> panel brightness (1 = artwork)
//   dotnet run tools/MakeIcon.cs -- --frame-gray 1.6              -> gray frame level (0 = purple, see --frame-lighten)
//   dotnet run tools/MakeIcon.cs -- --output other.ico            -> write elsewhere
string input = Path.Combine("tools", "icon-source.jpg");
string output = Path.Combine("src", "pLaunch", "Assets", "pLaunch.ico");
string? previewPath = null;
double largeScale = 1.28, smallScale = 1.40;
// Brightness of the blue panel and of the purple frame (1 = as in the artwork); multiplying keeps the hue
double lighten = 1.6, frameLighten = 1.7;
// Frame in neutral gray: its luminance (shading and inner line kept) times this; 0 = keep the purple
double frameGray = 1.6;
for (int a = 0; a + 1 < args.Length; a += 2)
{
    var value = args[a + 1];
    switch (args[a])
    {
        case "--preview": previewPath = value; break;
        case "--large": largeScale = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
        case "--small": smallScale = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
        case "--input": input = value; break;
        case "--output": output = value; break;
        case "--lighten": lighten = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
        case "--frame-lighten": frameLighten = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
        case "--frame-gray": frameGray = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
    }
}

var src = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(input)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
int W = src.PixelWidth, H = src.PixelHeight, N = W * H;
var px = new byte[N * 4];
src.CopyPixels(px, W * 4, 0);

int R(int i) => px[i * 4 + 2];
int G(int i) => px[i * 4 + 1];
int B(int i) => px[i * 4];
double Lum(int i) => 0.299 * R(i) + 0.587 * G(i) + 0.114 * B(i);

// The frame's color from an artwork frame color: neutral gray (frameGray > 0) or brighter purple
(double B, double G, double R) FrameColor(double b, double g, double r)
{
    if (frameGray <= 0)
        return (b * frameLighten, g * frameLighten, r * frameLighten);
    double v = (0.299 * r + 0.587 * g + 0.114 * b) * frameGray;
    return (v, v, v);
}

// ---- 1. the dark-blue panel (flood fill over bluish pixels) and the area inside the inner purple line
var panel = FloodFill(400 * W + 400, i => R(i) - G(i) < 8 && B(i) > R(i) + 10);
var reachable = new bool[N]; // non-panel pixels connected to the image border
{
    var stack = new Stack<int>();
    for (int x = 0; x < W; x++) { stack.Push(x); stack.Push((H - 1) * W + x); }
    for (int y = 0; y < H; y++) { stack.Push(y * W); stack.Push(y * W + W - 1); }
    while (stack.Count > 0)
    {
        int i = stack.Pop();
        if (reachable[i] || panel[i]) continue;
        reachable[i] = true;
        int x = i % W, y = i / W;
        if (x > 0) stack.Push(i - 1);
        if (x < W - 1) stack.Push(i + 1);
        if (y > 0) stack.Push(i - W);
        if (y < H - 1) stack.Push(i + W);
    }
}
var inner = new bool[N];
for (int i = 0; i < N; i++) inner[i] = !reachable[i];
var (pMinX, pMinY, pMaxX, pMaxY) = Bounds(inner);
Console.WriteLine($"inner panel {pMinX},{pMinY} - {pMaxX},{pMaxY}");

// ---- 2. denoised background = normalized blur of the panel pixels only
const int radius = 14;
var mask = new float[N];
for (int i = 0; i < N; i++) mask[i] = panel[i] ? 1 : 0;
var bm = Blur(mask);
var smooth = new float[3][];
for (int c = 0; c < 3; c++)
{
    var a = new float[N];
    for (int i = 0; i < N; i++) a[i] = panel[i] ? px[i * 4 + c] : 0;
    var b = Blur(a);
    for (int i = 0; i < N; i++) b[i] = bm[i] > 1e-4f ? b[i] / bm[i] : 0;
    smooth[c] = b;
}

// ---- 3. logo: core pixels (clearly purple) + antialiased edge unmixed against the local background
var core = new bool[N];
for (int i = 0; i < N; i++) core[i] = inner[i] && R(i) - G(i) > 40;
var (lMinX, lMinY, lMaxX, lMaxY) = Bounds(core);
double logoCx = (lMinX + lMaxX) / 2.0, logoCy = (lMinY + lMaxY) / 2.0;
Console.WriteLine($"logo {lMinX},{lMinY} - {lMaxX},{lMaxY} = {(lMaxY - lMinY) * 100.0 / (pMaxY - pMinY):F0}% of the panel height");

var logo = new float[4][]; // premultiplied B, G, R, A
for (int c = 0; c < 4; c++) logo[c] = new float[N];
for (int y = 0; y < H; y++)
for (int x = 0; x < W; x++)
{
    int i = y * W + x;
    if (!inner[i]) continue;
    if (core[i])
    {
        for (int c = 0; c < 3; c++) logo[c][i] = px[i * 4 + c];
        logo[3][i] = 1;
        continue;
    }
    // nearest core pixel within 4 px
    int best = -1, bestD = int.MaxValue;
    for (int dy = -4; dy <= 4; dy++)
    for (int dx = -4; dx <= 4; dx++)
    {
        int xx = x + dx, yy = y + dy;
        if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
        int j = yy * W + xx, d = dx * dx + dy * dy;
        if (core[j] && d < bestD) { bestD = d; best = j; }
    }
    if (best < 0) continue;
    double num = 0, den = 0;
    for (int c = 0; c < 3; c++)
    {
        double bg = smooth[c][i], lc = px[best * 4 + c];
        num += (px[i * 4 + c] - bg) * (lc - bg);
        den += (lc - bg) * (lc - bg);
    }
    float alpha = den > 1 ? (float)Math.Clamp(num / den, 0, 1) : 0;
    for (int c = 0; c < 3; c++) logo[c][i] = alpha * px[best * 4 + c];
    logo[3][i] = alpha;
}

// ---- 4. background gradient without the logo shadow: quadratic fit far from the logo
double[] fit = new double[18];
{
    var ata = new double[6, 6];
    var atb = new double[3, 6];
    for (int y = pMinY; y <= pMaxY; y += 6)
    for (int x = pMinX; x <= pMaxX; x += 6)
    {
        int i = y * W + x;
        if (!panel[i] || bm[i] < 0.99f) continue;
        if (x > lMinX - 140 && x < lMaxX + 140 && y > lMinY - 140 && y < lMaxY + 140) continue;
        var f = Basis(x, y);
        for (int a = 0; a < 6; a++)
        {
            for (int b = 0; b < 6; b++) ata[a, b] += f[a] * f[b];
            for (int c = 0; c < 3; c++) atb[c, a] += f[a] * smooth[c][i];
        }
    }
    for (int c = 0; c < 3; c++)
    {
        var rhs = new double[6];
        for (int a = 0; a < 6; a++) rhs[a] = atb[c, a];
        var sol = Solve((double[,])ata.Clone(), rhs);
        Array.Copy(sol, 0, fit, c * 6, 6);
    }
}
double Grad(int c, double x, double y) { var f = Basis(x, y); double v = 0; for (int a = 0; a < 6; a++) v += fit[c * 6 + a] * f[a]; return v; }
double[] Basis(double x, double y) { double u = x / W, v = y / H; return [1, u, v, u * u, v * v, u * v]; }

// shadow = how much darker the smooth background is than the fitted gradient
var shadow = new float[N];
for (int i = 0; i < N; i++)
{
    shadow[i] = 1;
    if (!inner[i] || bm[i] < 0.25f) continue;
    int x = i % W, y = i / W;
    double gl = 0.299 * Grad(2, x, y) + 0.587 * Grad(1, x, y) + 0.114 * Grad(0, x, y);
    double sl = 0.299 * smooth[2][i] + 0.587 * smooth[1][i] + 0.114 * smooth[0][i];
    shadow[i] = (float)Math.Clamp(sl / gl, 0.2, 1.0);
}
{
    double err = 0; int n = 0;
    for (int y = pMinY + 40; y <= pMaxY - 40; y += 10)
    for (int x = pMinX + 40; x <= pMaxX - 40; x += 10)
    {
        int i = y * W + x;
        if (!panel[i] || (x > lMinX - 140 && x < lMaxX + 140 && y > lMinY - 140 && y < lMaxY + 140)) continue;
        for (int c = 0; c < 3; c++) { double d = smooth[c][i] - Grad(c, x, y); err += d * d; n++; }
    }
    Console.WriteLine($"gradient fit RMS {Math.Sqrt(err / n):F2}");
}

// Panel content at an original-image point, with the logo (and its shadow) scaled by k around the panel center
double panelCx = (pMinX + pMaxX) / 2.0, panelCy = (pMinY + pMaxY) / 2.0;
(double b, double g, double r) PanelAt(double x, double y, double k)
{
    double sx = logoCx + (x - panelCx) / k, sy = logoCy + (y - panelCy) / k;
    double s = Sample(shadow, sx, sy, 1f);
    double bb = Grad(0, x, y) * s * lighten, gg = Grad(1, x, y) * s * lighten, rr = Grad(2, x, y) * s * lighten;
    double a = Sample(logo[3], sx, sy, 0f);
    if (a > 0)
    {
        bb = Sample(logo[0], sx, sy, 0f) + (1 - a) * bb;
        gg = Sample(logo[1], sx, sy, 0f) + (1 - a) * gg;
        rr = Sample(logo[2], sx, sy, 0f) + (1 - a) * rr;
    }
    return (bb, gg, rr);
}
double Sample(float[] a, double x, double y, float outside)
{
    int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
    double fx = x - x0, fy = y - y0;
    double V(int xx, int yy) => xx < 0 || yy < 0 || xx >= W || yy >= H || !inner[yy * W + xx] ? outside : a[yy * W + xx];
    return (V(x0, y0) * (1 - fx) + V(x0 + 1, y0) * fx) * (1 - fy) + (V(x0, y0 + 1) * (1 - fx) + V(x0 + 1, y0 + 1) * fx) * fy;
}
var rng = new Random(1);
byte Dither(double v) => (byte)Math.Clamp((int)Math.Round(v + rng.NextDouble() - 0.5), 0, 255);

// ---- 5. LARGE variant: the original frame, new panel content
var shapeMask = new bool[N];
for (int i = 0; i < N; i++) shapeMask[i] = Lum(i) < 110;
var outside = new bool[N];
{
    var stack = new Stack<int>();
    for (int x = 0; x < W; x++) { stack.Push(x); stack.Push((H - 1) * W + x); }
    for (int y = 0; y < H; y++) { stack.Push(y * W); stack.Push(y * W + W - 1); }
    while (stack.Count > 0)
    {
        int i = stack.Pop();
        if (outside[i] || shapeMask[i]) continue;
        outside[i] = true;
        int x = i % W, y = i / W;
        if (x > 0) stack.Push(i - 1);
        if (x < W - 1) stack.Push(i + 1);
        if (y > 0) stack.Push(i - W);
        if (y < H - 1) stack.Push(i + W);
    }
}
var large = (byte[])px.Clone();
for (int y = 0; y < H; y++)
for (int x = 0; x < W; x++)
{
    int i = y * W + x;
    if (inner[i])
    {
        var (b, g, r) = PanelAt(x, y, largeScale);
        large[i * 4] = Dither(b); large[i * 4 + 1] = Dither(g); large[i * 4 + 2] = Dither(r);
    }
    else if (!outside[i])
    {
        var (fb, fg, fr) = FrameColor(px[i * 4], px[i * 4 + 1], px[i * 4 + 2]);
        large[i * 4] = Dither(fb); large[i * 4 + 1] = Dither(fg); large[i * 4 + 2] = Dither(fr);
    }
    if (outside[i]) large[i * 4 + 3] = 0;
}
for (int y = 1; y < H - 1; y++)
for (int x = 1; x < W - 1; x++)
{
    int i = y * W + x;
    if (!outside[i]) continue;
    int n = -1;
    foreach (int j in new[] { i - 1, i + 1, i - W, i + W }) if (!outside[j]) { n = j; break; }
    if (n < 0) continue;
    double a = Math.Clamp((225 - Lum(i)) / (225 - Lum(n)), 0, 1);
    for (int c = 0; c < 3; c++) large[i * 4 + c] = large[n * 4 + c];
    large[i * 4 + 3] = (byte)(a * 255);
}
var (sMinX, sMinY, sMaxX, sMaxY) = Bounds(Enumerable.Range(0, N).Select(i => !outside[i]).ToArray());
int side = Math.Max(sMaxX - sMinX + 1, sMaxY - sMinY + 1) + 4;
var largeSquare = new byte[side * side * 4];
int ox = (side - (sMaxX - sMinX + 1)) / 2, oy = (side - (sMaxY - sMinY + 1)) / 2;
for (int y = Math.Max(0, sMinY - 1); y <= Math.Min(H - 1, sMaxY + 1); y++)
for (int x = Math.Max(0, sMinX - 1); x <= Math.Min(W - 1, sMaxX + 1); x++)
{
    int dx = x - sMinX + ox, dy = y - sMinY + oy;
    if (dx < 0 || dy < 0 || dx >= side || dy >= side) continue;
    Array.Copy(large, (y * W + x) * 4, largeSquare, (dy * side + dx) * 4, 4);
}
var largeMaster = Frozen(BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, largeSquare, side * 4));

// corner radius of the original outer shape, measured along the top-left diagonal
int diag = 0;
while (outside[(sMinY + diag) * W + sMinX + diag]) diag++;
double cornerRatio = diag / (1 - 1 / Math.Sqrt(2)) / (sMaxY - sMinY + 1);
Console.WriteLine($"corner radius = {cornerRatio * 100:F1}% of the height");

// ---- 6. SMALL variant: thin frame in the original's purple, panel filling the rest
const int S = 512;
var smallPx = new byte[S * S * 4];
Color frameTop = Color.FromRgb((byte)R((sMinY + 30) * W + W / 2), (byte)G((sMinY + 30) * W + W / 2), (byte)B((sMinY + 30) * W + W / 2));
int bottomIdx = (sMaxY - 30) * W + W / 2;
Color frameBottom = Color.FromRgb((byte)R(bottomIdx), (byte)G(bottomIdx), (byte)B(bottomIdx));
double margin = 6, frame = 26; // in 512 px units: frame = ~5%
double outerR = cornerRatio * (S - 2 * margin), innerR = Math.Max(0, outerR - frame);
for (int y = 0; y < S; y++)
for (int x = 0; x < S; x++)
{
    // 4x4 supersampling for clean edges
    double sb = 0, sg = 0, sr = 0, sa = 0;
    for (int yy = 0; yy < 4; yy++)
    for (int xx = 0; xx < 4; xx++)
    {
        double fx = x + (xx + 0.5) / 4, fy = y + (yy + 0.5) / 4;
        if (RoundedDistance(fx, fy, margin, margin, S - margin, S - margin, outerR) > 0) continue;
        double b, g, r;
        if (RoundedDistance(fx, fy, margin + frame, margin + frame, S - margin - frame, S - margin - frame, innerR) > 0)
        {
            double t = fy / S;
            (b, g, r) = FrameColor(
                frameTop.B + (frameBottom.B - frameTop.B) * t,
                frameTop.G + (frameBottom.G - frameTop.G) * t,
                frameTop.R + (frameBottom.R - frameTop.R) * t);
        }
        else
        {
            // map the inner square onto the original panel (uniformly, by height)
            double u = (fx - S / 2.0) / (S - 2 * (margin + frame)), v = (fy - S / 2.0) / (S - 2 * (margin + frame));
            double scale = pMaxY - pMinY;
            (b, g, r) = PanelAt(panelCx + u * scale, panelCy + v * scale, smallScale);
        }
        sb += b; sg += g; sr += r; sa += 1;
    }
    int o = (y * S + x) * 4;
    if (sa == 0) continue;
    smallPx[o] = Dither(sb / sa); smallPx[o + 1] = Dither(sg / sa); smallPx[o + 2] = Dither(sr / sa);
    smallPx[o + 3] = (byte)Math.Round(sa / 16 * 255);
}
var smallMaster = Frozen(BitmapSource.Create(S, S, 96, 96, PixelFormats.Bgra32, null, smallPx, S * 4));

BitmapSource Final(int size) => size <= 32 ? Scale(smallMaster, size) : Scale(largeMaster, size);

// ---- 7. the .ico: PNG entries (valid in icons since Vista)
int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 256];
var pngs = icoSizes.Select(s => EncodePng(Final(s))).ToList();
using (var stream = File.Create(output))
using (var writer = new BinaryWriter(stream))
{
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)icoSizes.Length);
    int offset = 6 + 16 * icoSizes.Length;
    for (int i = 0; i < icoSizes.Length; i++)
    {
        writer.Write((byte)(icoSizes[i] >= 256 ? 0 : icoSizes[i]));
        writer.Write((byte)(icoSizes[i] >= 256 ? 0 : icoSizes[i]));
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
Console.WriteLine($"Wrote {output}");

// ---- 8. optional preview sheet
if (previewPath != null)
{
    int[] sizes = [64, 48, 40, 32, 24, 20, 16];
    var dv = new DrawingVisual();
    int sheetW = 1020, sheetH = 560;
    using (var dc = dv.RenderOpen())
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, sheetW, sheetH));
        var face = new Typeface("Segoe UI Semibold");
        void Label(string t, double x, double y) => dc.DrawText(new FormattedText(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 17, Brushes.Black, 1.0), new Point(x, y));
        Label("40 px and up", 20, 10);
        dc.DrawImage(Scale(largeMaster, 256), new Rect(20, 40, 256, 256));
        Label("32 px and down", 310, 10);
        dc.DrawImage(Scale(smallMaster, 256), new Rect(310, 40, 256, 256));
        Label("32 px and 16 px, zoomed x6", 600, 10);
        DrawPixelated(dc, Final(32), 600, 40, 6);
        DrawPixelated(dc, Final(16), 810, 40, 6);
        Label("Taskbar: 64 / 48 / 40 / 32 / 24 / 20 / 16", 20, 330);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, 360, sheetW, 100));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 460, sheetW, 100));
        for (int row = 0; row < 2; row++)
        {
            double x = 20, cy = 360 + row * 100 + 50;
            foreach (var s in sizes)
            {
                dc.DrawImage(Final(s), new Rect(x, cy - s / 2.0, s, s));
                x += s + 26;
            }
        }
    }
    var sheet = new RenderTargetBitmap(sheetW, sheetH, 96, 96, PixelFormats.Pbgra32);
    sheet.Render(dv);
    Save(sheet, previewPath);
    Console.WriteLine($"Wrote {previewPath}");
}

// ------------------------------------------------------------------ helpers

bool[] FloodFill(int seed, Func<int, bool> ok)
{
    var m = new bool[N];
    var stack = new Stack<int>();
    stack.Push(seed); m[seed] = true;
    while (stack.Count > 0)
    {
        int i = stack.Pop();
        int x = i % W, y = i / W;
        void Try(int j) { if (!m[j] && ok(j)) { m[j] = true; stack.Push(j); } }
        if (x > 0) Try(i - 1);
        if (x < W - 1) Try(i + 1);
        if (y > 0) Try(i - W);
        if (y < H - 1) Try(i + W);
    }
    return m;
}

(int, int, int, int) Bounds(bool[] m)
{
    int minX = W, minY = H, maxX = 0, maxY = 0;
    for (int i = 0; i < N; i++)
    {
        if (!m[i]) continue;
        int x = i % W, y = i / W;
        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
    }
    return (minX, minY, maxX, maxY);
}

float[] Blur(float[] a)
{
    var t = (float[])a.Clone();
    for (int pass = 0; pass < 3; pass++) { t = BoxH(t); t = BoxV(t); }
    return t;
}
float[] BoxH(float[] a)
{
    var o = new float[a.Length];
    for (int y = 0; y < H; y++)
    {
        double sum = 0; int row = y * W;
        for (int x = -radius; x <= radius; x++) sum += a[row + Math.Clamp(x, 0, W - 1)];
        for (int x = 0; x < W; x++)
        {
            o[row + x] = (float)(sum / (2 * radius + 1));
            sum += a[row + Math.Min(x + radius + 1, W - 1)] - a[row + Math.Max(x - radius, 0)];
        }
    }
    return o;
}
float[] BoxV(float[] a)
{
    var o = new float[a.Length];
    for (int x = 0; x < W; x++)
    {
        double sum = 0;
        for (int y = -radius; y <= radius; y++) sum += a[Math.Clamp(y, 0, H - 1) * W + x];
        for (int y = 0; y < H; y++)
        {
            o[y * W + x] = (float)(sum / (2 * radius + 1));
            sum += a[Math.Min(y + radius + 1, H - 1) * W + x] - a[Math.Max(y - radius, 0) * W + x];
        }
    }
    return o;
}

static double[] Solve(double[,] a, double[] b)
{
    int n = b.Length;
    for (int col = 0; col < n; col++)
    {
        int piv = col;
        for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
        for (int c = 0; c < n; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
        (b[col], b[piv]) = (b[piv], b[col]);
        for (int r = 0; r < n; r++)
        {
            if (r == col) continue;
            double f = a[r, col] / a[col, col];
            for (int c = 0; c < n; c++) a[r, c] -= f * a[col, c];
            b[r] -= f * b[col];
        }
    }
    var x = new double[n];
    for (int i = 0; i < n; i++) x[i] = b[i] / a[i, i];
    return x;
}

/// <summary>Signed distance to a rounded rectangle (negative inside).</summary>
static double RoundedDistance(double x, double y, double l, double t, double r, double b, double radius)
{
    double cx = (l + r) / 2, cy = (t + b) / 2, hx = (r - l) / 2 - radius, hy = (b - t) / 2 - radius;
    double qx = Math.Abs(x - cx) - hx, qy = Math.Abs(y - cy) - hy;
    double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
    return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
}

static void DrawPixelated(DrawingContext dc, BitmapSource b, double x, double y, int zoom)
{
    var group = new DrawingGroup();
    RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
    group.Children.Add(new ImageDrawing(b, new Rect(0, 0, b.PixelWidth * zoom, b.PixelHeight * zoom)));
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(x, y, b.PixelWidth * zoom, b.PixelHeight * zoom));
    dc.PushTransform(new TranslateTransform(x, y));
    dc.DrawDrawing(group);
    dc.Pop();
}

static BitmapSource Frozen(BitmapSource b) { b.Freeze(); return b; }

static BitmapSource Scale(BitmapSource s, int size)
{
    BitmapSource cur = s;
    while (cur.PixelWidth / 2 >= size)
        cur = Resize(cur, cur.PixelWidth / 2);
    return cur.PixelWidth == size ? cur : Resize(cur, size);
}

static BitmapSource Resize(BitmapSource s, int size)
{
    var dv = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
    using (var dc = dv.RenderOpen())
        dc.DrawImage(s, new Rect(0, 0, size, size));
    var r = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    r.Render(dv);
    r.Freeze();
    return r;
}

static byte[] EncodePng(BitmapSource b)
{
    var e = new PngBitmapEncoder();
    e.Frames.Add(BitmapFrame.Create(b));
    using var ms = new MemoryStream();
    e.Save(ms);
    return ms.ToArray();
}

static void Save(BitmapSource b, string path) => File.WriteAllBytes(path, EncodePng(b));
