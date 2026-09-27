using System.Buffers.Binary;
using System.IO.Compression;
using LajmeBot.Text;

namespace LajmeBot.Images;

/// <summary>
/// Draws an original abstract editorial illustration (1600×900 PNG) for every article,
/// in the site's colours. No photos are taken from the source outlets.
/// Pure C#: a tiny anti-aliased rasteriser plus a PNG encoder, no NuGet packages.
/// </summary>
public static class HeroImageGenerator
{
    public static readonly string[] Motifs = { "chart-up", "chart-down", "bars", "network", "globe", "waves", "columns", "pitch" };

    public const int W = 1600, H = 900;

    private record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb Hex(string hex)
        {
            hex = hex.TrimStart('#');
            return new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        public Rgb Mix(Rgb o, double t) => new((byte)(R + (o.R - R) * t), (byte)(G + (o.G - G) * t), (byte)(B + (o.B - B) * t));
    }

    private sealed record Palette(Rgb Bg, Rgb Fg, Rgb A1, Rgb A2, Rgb A3);

    private static readonly Rgb Cream = new(240, 234, 222), White = new(250, 250, 247);

    private static Palette PaletteFor(string category, Rgb accent) => category switch
    {
        "bota" => new(new(16, 42, 68), Cream, accent, new(96, 131, 163), new(214, 170, 90)),
        "politike" => new(new(28, 28, 30), Cream, accent, new(120, 128, 140), new(200, 190, 170)),
        "ekonomi" => new(new(14, 59, 46), Cream, accent, new(217, 164, 65), new(120, 170, 140)),
        "teknologji" => new(new(11, 15, 26), White, accent, new(79, 179, 191), new(120, 130, 160)),
        "shkence" => new(new(10, 16, 48), White, accent, new(240, 180, 80), new(110, 130, 200)),
        "kulture" => new(new(59, 13, 20), Cream, accent, new(214, 160, 80), new(190, 130, 140)),
        "sport" => new(new(20, 90, 50), White, accent, new(30, 120, 70), new(230, 220, 120)),
        _ => new(new(24, 30, 40), Cream, accent, new(120, 140, 160), new(210, 180, 110)),
    };

    public static string AltText(string motif) => motif switch
    {
        "chart-up" => "Ilustrim abstrakt i një grafiku me vijë në rritje mbi një rrjetë të hollë",
        "chart-down" => "Ilustrim abstrakt i një grafiku me vijë në rënie mbi një rrjetë të hollë",
        "bars" => "Ilustrim abstrakt me shtylla vertikale të lartësive të ndryshme",
        "network" => "Ilustrim abstrakt i një rrjeti pikash të lidhura me vija të holla",
        "globe" => "Ilustrim abstrakt i një globi me meridianë dhe pika të theksuara",
        "waves" => "Ilustrim abstrakt i një dielli mbi vija të valëzuara",
        "columns" => "Ilustrim abstrakt i një ndërtese institucionale me kolona",
        "pitch" => "Ilustrim abstrakt i një fushe sportive parë nga lart",
        _ => "Ilustrim abstrakt editorial me forma gjeometrike",
    };

    public static void Generate(string path, string seedText, string category, string motif, string accentHex, string? tintHex, double tintAmount)
    {
        var pal = PaletteFor(category, Rgb.Hex(accentHex));
        if (!string.IsNullOrWhiteSpace(tintHex) && tintAmount > 0) pal = pal with { Bg = pal.Bg.Mix(Rgb.Hex(tintHex), tintAmount) };
        if (!Motifs.Contains(motif)) motif = Motifs[(int)(TextUtil.StableHash(seedText) % (ulong)Motifs.Length)];

        var c = new Canvas(W, H, pal.Bg);
        var rng = new Random((int)(TextUtil.StableHash(seedText) & 0x7fffffff));
        Draw(c, rng, pal, motif);
        c.FillRect(60, H - 70, 180, H - 58, pal.A1); // brand marker, bottom-left
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, c.ToPng());
    }

    private static void Grid(Canvas c, Palette p, int step, double alpha)
    {
        var col = p.Bg.Mix(p.Fg, alpha);
        for (var x = 0; x <= W; x += step) c.FillRect(x, 0, x + 1, H, col);
        for (var y = 0; y <= H; y += step) c.FillRect(0, y, W, y + 1, col);
    }

    private static void Draw(Canvas c, Random r, Palette p, string motif)
    {
        switch (motif)
        {
            case "chart-up":
            case "chart-down":
            {
                Grid(c, p, 100, 0.08);
                var up = motif == "chart-up";
                var pts = new List<(double x, double y)>();
                var n = 14;
                for (var i = 0; i <= n; i++)
                {
                    var t = i / (double)n;
                    var trend = up ? 700 - t * 460 : 230 + t * 440;
                    pts.Add((120 + t * 1360, trend + (r.NextDouble() - 0.5) * 110));
                }
                // soft area under the line
                var area = pts.Select(q => (q.x, q.y)).ToList();
                area.Add((pts[^1].x, 820)); area.Add((pts[0].x, 820));
                c.FillPolygon(area, p.Bg.Mix(p.A2, 0.18));
                c.Polyline(pts, 7, p.Fg);
                foreach (var q in pts.Skip(1).SkipLast(1)) c.Circle(q.x, q.y, 9, p.Bg.Mix(p.Fg, 0.85));
                c.Circle(pts[^1].x, pts[^1].y, 20, p.A1);
                c.Ring(pts[^1].x, pts[^1].y, 38, 3, p.A1);
                break;
            }
            case "bars":
            {
                Grid(c, p, 90, 0.06);
                var n = 9; var bw = 96; var gap = 44; var x0 = (W - (n * bw + (n - 1) * gap)) / 2;
                var hi = r.Next(n);
                for (var i = 0; i < n; i++)
                {
                    var h = 160 + r.Next(420) + i * 18;
                    var col = i == hi ? p.A1 : (i % 2 == 0 ? p.A2 : p.A3);
                    c.FillRect(x0 + i * (bw + gap), 780 - h, x0 + i * (bw + gap) + bw, 780, col);
                }
                c.FillRect(100, 780, W - 100, 786, p.Fg);
                break;
            }
            case "network":
            {
                var nodes = Enumerable.Range(0, 46).Select(_ => (x: 90 + r.NextDouble() * (W - 180), y: 80 + r.NextDouble() * (H - 200))).ToList();
                var line = p.Bg.Mix(p.Fg, 0.35);
                foreach (var a in nodes)
                    foreach (var b in nodes.OrderBy(b => (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y)).Skip(1).Take(3))
                        c.Line(a.x, a.y, b.x, b.y, 2, line);
                for (var i = 0; i < nodes.Count; i++)
                {
                    var accent = i % 7 == 0;
                    c.Circle(nodes[i].x, nodes[i].y, accent ? 13 : 7, accent ? p.A1 : p.Fg);
                    if (i % 11 == 0) c.Ring(nodes[i].x, nodes[i].y, 26, 2, p.A2);
                }
                break;
            }
            case "globe":
            {
                double cx = 800, cy = 430, rad = 320;
                c.Circle(cx, cy, rad, p.Bg.Mix(p.A2, 0.25));
                c.Ring(cx, cy, rad, 4, p.Fg);
                for (var k = 1; k <= 3; k++) c.Ellipse(cx, cy, rad * k / 4.0, rad, 2, p.Bg.Mix(p.Fg, 0.45));
                for (var k = -2; k <= 2; k++)
                {
                    var y = cy + k * rad / 3.0; var half = Math.Sqrt(Math.Max(0, rad * rad - (y - cy) * (y - cy)));
                    c.Line(cx - half, y, cx + half, y, 2, p.Bg.Mix(p.Fg, 0.45));
                }
                for (var i = 0; i < 6; i++)
                {
                    var a = r.NextDouble() * Math.PI * 2; var d = r.NextDouble() * rad * 0.85;
                    var (x, y) = (cx + Math.Cos(a) * d, cy + Math.Sin(a) * d);
                    c.Circle(x, y, 11, p.A1); c.Ring(x, y, 24, 2, p.A1);
                }
                break;
            }
            case "waves":
            {
                double sx = 380 + r.Next(840), sy = 250;
                c.Ring(sx, sy, 150, 3, p.A3);
                c.Circle(sx, sy, 115, p.A3);
                for (var k = 0; k < 7; k++)
                {
                    var pts = new List<(double, double)>();
                    var baseY = 520 + k * 50; var amp = 14 + k * 2; var ph = r.NextDouble() * 6;
                    for (var x = 0; x <= W; x += 20) pts.Add((x, baseY + Math.Sin(x / 90.0 + ph + k) * amp));
                    c.Polyline(pts, 4, p.Bg.Mix(p.Fg, 0.3 + k * 0.08));
                }
                break;
            }
            case "columns":
            {
                Grid(c, p, 100, 0.05);
                double left = 360, right = 1240, top = 330, bottom = 760;
                c.FillPolygon(new() { (left - 40, top), (right + 40, top), (800, 150) }, p.Fg);
                c.FillPolygon(new() { (left + 10, top - 12), (right - 10, top - 12), (800, 188) }, p.Bg);
                c.FillRect(left - 40, top, right + 40, top + 30, p.Fg);
                var n = 6; var cw = 60.0; var step = (right - left - cw) / (n - 1);
                for (var i = 0; i < n; i++) c.FillRect(left + i * step, top + 55, left + i * step + cw, bottom - 40, p.Fg.Mix(p.Bg, 0.15));
                c.FillRect(left - 60, bottom - 40, right + 60, bottom, p.Fg);
                c.FillRect(left - 90, bottom, right + 90, bottom + 22, p.Fg.Mix(p.Bg, 0.3));
                c.Circle(800, 250, 18, p.A1);
                break;
            }
            default: // pitch
            {
                double l = 180, t = 110, rr = 1420, b = 790;
                var line = p.Bg.Mix(p.Fg, 0.8);
                for (var k = 0; k < 8; k++) if (k % 2 == 0) c.FillRect(l + k * (rr - l) / 8, t, l + (k + 1) * (rr - l) / 8, b, p.Bg.Mix(p.A2, 0.35));
                c.RectOutline(l, t, rr, b, 4, line);
                c.Line(800, t, 800, b, 4, line);
                c.Ring(800, (t + b) / 2, 100, 4, line);
                c.RectOutline(l, (t + b) / 2 - 160, l + 170, (t + b) / 2 + 160, 4, line);
                c.RectOutline(rr - 170, (t + b) / 2 - 160, rr, (t + b) / 2 + 160, 4, line);
                c.Circle(800 + (r.NextDouble() - 0.5) * 700, t + 60 + r.NextDouble() * (b - t - 120), 18, p.A3);
                c.Circle(800, (t + b) / 2, 8, p.A1);
                break;
            }
        }
    }

    /// <summary>RGB canvas with coverage-based anti-aliasing.</summary>
    private sealed class Canvas
    {
        private readonly int _w, _h;
        private readonly byte[] _px;

        public Canvas(int w, int h, Rgb bg)
        {
            _w = w; _h = h; _px = new byte[w * h * 3];
            for (var i = 0; i < _px.Length; i += 3) { _px[i] = bg.R; _px[i + 1] = bg.G; _px[i + 2] = bg.B; }
        }

        private void Blend(int x, int y, Rgb c, double a)
        {
            if (x < 0 || y < 0 || x >= _w || y >= _h || a <= 0) return;
            if (a > 1) a = 1;
            var i = (y * _w + x) * 3;
            _px[i] = (byte)(_px[i] + (c.R - _px[i]) * a);
            _px[i + 1] = (byte)(_px[i + 1] + (c.G - _px[i + 1]) * a);
            _px[i + 2] = (byte)(_px[i + 2] + (c.B - _px[i + 2]) * a);
        }

        public void FillRect(double x0, double y0, double x1, double y1, Rgb c)
        {
            for (var y = (int)Math.Max(0, y0); y < Math.Min(_h, y1); y++)
                for (var x = (int)Math.Max(0, x0); x < Math.Min(_w, x1); x++) Blend(x, y, c, 1);
        }

        public void RectOutline(double x0, double y0, double x1, double y1, double w, Rgb c)
        {
            FillRect(x0, y0, x1, y0 + w, c); FillRect(x0, y1 - w, x1, y1, c);
            FillRect(x0, y0, x0 + w, y1, c); FillRect(x1 - w, y0, x1, y1, c);
        }

        public void Circle(double cx, double cy, double r, Rgb c)
        {
            for (var y = (int)(cy - r - 1); y <= cy + r + 1; y++)
                for (var x = (int)(cx - r - 1); x <= cx + r + 1; x++)
                {
                    var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                    Blend(x, y, c, r - d + 0.5);
                }
        }

        public void Ring(double cx, double cy, double r, double w, Rgb c) => Ellipse(cx, cy, r, r, w, c);

        public void Ellipse(double cx, double cy, double rx, double ry, double w, Rgb c)
        {
            // Approximate with a dense polyline — plenty for decorative strokes.
            var n = (int)Math.Max(48, (rx + ry) * 0.6);
            var pts = new List<(double, double)>(n + 1);
            for (var i = 0; i <= n; i++) { var a = i * Math.PI * 2 / n; pts.Add((cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry)); }
            Polyline(pts, w, c);
        }

        public void Polyline(IList<(double x, double y)> pts, double w, Rgb c)
        {
            // Draw into a coverage mask first so overlapping segments do not double-blend.
            var mask = new Dictionary<int, double>();
            for (var i = 1; i < pts.Count; i++) SegmentCoverage(pts[i - 1].x, pts[i - 1].y, pts[i].x, pts[i].y, w, mask);
            foreach (var (k, a) in mask) Blend(k % _w, k / _w, c, a);
        }

        public void Line(double x0, double y0, double x1, double y1, double w, Rgb c) =>
            Polyline(new List<(double, double)> { (x0, y0), (x1, y1) }, w, c);

        private void SegmentCoverage(double x0, double y0, double x1, double y1, double w, Dictionary<int, double> mask)
        {
            var hw = w / 2;
            int minX = (int)Math.Floor(Math.Min(x0, x1) - hw - 1), maxX = (int)Math.Ceiling(Math.Max(x0, x1) + hw + 1);
            int minY = (int)Math.Floor(Math.Min(y0, y1) - hw - 1), maxY = (int)Math.Ceiling(Math.Max(y0, y1) + hw + 1);
            double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
            for (var y = Math.Max(0, minY); y <= Math.Min(_h - 1, maxY); y++)
                for (var x = Math.Max(0, minX); x <= Math.Min(_w - 1, maxX); x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    var t = len2 == 0 ? 0 : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / len2, 0, 1);
                    double ex = x0 + t * dx - px, ey = y0 + t * dy - py;
                    var a = hw - Math.Sqrt(ex * ex + ey * ey) + 0.5;
                    if (a <= 0) continue;
                    var k = y * _w + x;
                    if (!mask.TryGetValue(k, out var old) || a > old) mask[k] = Math.Min(1, a);
                }
        }

        public void FillPolygon(List<(double x, double y)> poly, Rgb c)
        {
            // Scanline even-odd fill with 4× vertical supersampling for smooth edges.
            var minY = (int)Math.Max(0, poly.Min(p => p.y)); var maxY = (int)Math.Min(_h - 1, poly.Max(p => p.y));
            var cov = new double[_w];
            for (var y = minY; y <= maxY; y++)
            {
                Array.Clear(cov);
                for (var s = 0; s < 4; s++)
                {
                    var sy = y + (s + 0.5) / 4;
                    var xs = new List<double>();
                    for (var i = 0; i < poly.Count; i++)
                    {
                        var (ax, ay) = poly[i]; var (bx, by) = poly[(i + 1) % poly.Count];
                        if ((ay <= sy && by > sy) || (by <= sy && ay > sy)) xs.Add(ax + (sy - ay) / (by - ay) * (bx - ax));
                    }
                    xs.Sort();
                    for (var i = 0; i + 1 < xs.Count; i += 2)
                    {
                        var xa = Math.Max(0, xs[i]); var xb = Math.Min(_w, xs[i + 1]);
                        for (var x = (int)xa; x < xb; x++)
                        {
                            var left = Math.Max(xa, x); var right = Math.Min(xb, x + 1);
                            if (right > left) cov[x] += (right - left) / 4;
                        }
                    }
                }
                for (var x = 0; x < _w; x++) if (cov[x] > 0) Blend(x, y, c, cov[x]);
            }
        }

        public byte[] ToPng()
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, _w);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), _h);
            ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
            Chunk(ms, "IHDR", ihdr);
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    var stride = _w * 3;
                    var row = new byte[stride + 1];
                    var prev = new byte[stride];
                    for (var y = 0; y < _h; y++)
                    {
                        // PNG "Up" filter: flat backgrounds compress to almost nothing.
                        row[0] = 2;
                        var off = y * stride;
                        for (var i = 0; i < stride; i++) row[i + 1] = (byte)(_px[off + i] - prev[i]);
                        Buffer.BlockCopy(_px, off, prev, 0, stride);
                        z.Write(row);
                    }
                }
                Chunk(ms, "IDAT", raw.ToArray());
            }
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t); s.Write(data);
            var crc = Crc32(t, data);
            BinaryPrimitives.WriteUInt32BigEndian(len, crc);
            s.Write(len);
        }

        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        private static uint Crc32(byte[] a, byte[] b)
        {
            var c = 0xFFFFFFFFu;
            foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
            foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
