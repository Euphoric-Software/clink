namespace Clink.Services;

/// <summary>The look of one sample clip (<see cref="SampleClips"/>): a procedurally drawn scene, nothing captured from a screen.</summary>
internal enum SampleScene { SynthwaveDrive, Cells, Spire, Warp, Shards, Plasma, Orbits }

/// <summary>A BGRA canvas with a few drawing primitives (all CPU, no GDI), for the documentation sample clips.</summary>
internal sealed class SampleCanvas
{
    public readonly int W, H;
    public readonly byte[] Bgra;

    public SampleCanvas(int w, int h) { W = w; H = h; Bgra = new byte[w * h * 4]; }

    public delegate uint PixelFn(int x, int y);

    /// <summary>Paints every pixel from <paramref name="fn"/> (0xRRGGBB), rows in parallel.</summary>
    public void Fill(PixelFn fn)
    {
        Parallel.For(0, H, y =>
        {
            int i = y * W * 4;
            for (int x = 0; x < W; x++, i += 4)
            {
                uint c = fn(x, y);
                Bgra[i] = (byte)c; Bgra[i + 1] = (byte)(c >> 8); Bgra[i + 2] = (byte)(c >> 16); Bgra[i + 3] = 255;
            }
        });
    }

    public static uint Rgb(double r, double g, double b) => (uint)(Clamp255(r) << 16 | Clamp255(g) << 8 | Clamp255(b));
    private static int Clamp255(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)v;

    public static (double r, double g, double b) Hsv(double h, double s, double v)
    {
        h = h - Math.Floor(h);
        double c = v * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h * 6) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static uint Mix(uint a, uint b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Rgb(Lerp((a >> 16) & 255, (b >> 16) & 255, t), Lerp((a >> 8) & 255, (b >> 8) & 255, t), Lerp(a & 255, b & 255, t));
    }

    /// <summary>Adds light around a point: full at the centre, nothing at <paramref name="radius"/>.</summary>
    public void Glow(double cx, double cy, double radius, uint color, double intensity)
    {
        int x0 = Math.Max(0, (int)(cx - radius)), x1 = Math.Min(W - 1, (int)(cx + radius)), y0 = Math.Max(0, (int)(cy - radius)), y1 = Math.Min(H - 1, (int)(cy + radius));
        double cr = (color >> 16) & 255, cg = (color >> 8) & 255, cb = color & 255;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / radius;
                if (d >= 1) continue;
                double f = (1 - d) * (1 - d) * intensity;
                int i = (y * W + x) * 4;
                Bgra[i] = (byte)Math.Min(255, Bgra[i] + cb * f); Bgra[i + 1] = (byte)Math.Min(255, Bgra[i + 1] + cg * f); Bgra[i + 2] = (byte)Math.Min(255, Bgra[i + 2] + cr * f);
            }
    }

    /// <summary>A soft-edged disc, laid over what is there (<paramref name="alpha"/> 1 = opaque).</summary>
    public void Disc(double cx, double cy, double r, uint color, double alpha = 1)
    {
        int x0 = Math.Max(0, (int)(cx - r - 1)), x1 = Math.Min(W - 1, (int)(cx + r + 1)), y0 = Math.Max(0, (int)(cy - r - 1)), y1 = Math.Min(H - 1, (int)(cy + r + 1));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double cov = Math.Clamp(r - Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) + 0.5, 0, 1) * alpha;
                if (cov > 0) Over(x, y, color, cov);
            }
    }

    /// <summary>A ring of the given width.</summary>
    public void Ring(double cx, double cy, double r, double width, uint color, double alpha = 1)
    {
        int x0 = Math.Max(0, (int)(cx - r - width)), x1 = Math.Min(W - 1, (int)(cx + r + width)), y0 = Math.Max(0, (int)(cy - r - width)), y1 = Math.Min(H - 1, (int)(cy + r + width));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r);
                double cov = Math.Clamp(width / 2 - d + 0.5, 0, 1) * alpha;
                if (cov > 0) Over(x, y, color, cov);
            }
    }

    /// <summary>A line of the given width; where lines meet the brighter one wins (no double brightness).</summary>
    public void Line(double x0, double y0, double x1, double y1, double width, uint color, double alpha = 1)
    {
        double len = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        int steps = Math.Max(1, (int)Math.Ceiling(len));
        double r = width / 2;
        double cr = ((color >> 16) & 255) * alpha, cg = ((color >> 8) & 255) * alpha, cb = (color & 255) * alpha;
        for (int s = 0; s <= steps; s++)
        {
            double px = x0 + (x1 - x0) * s / steps, py = y0 + (y1 - y0) * s / steps;
            int ax = Math.Max(0, (int)(px - r - 1)), bx = Math.Min(W - 1, (int)(px + r + 1)), ay = Math.Max(0, (int)(py - r - 1)), by = Math.Min(H - 1, (int)(py + r + 1));
            for (int y = ay; y <= by; y++)
                for (int x = ax; x <= bx; x++)
                {
                    double cov = Math.Clamp(r - Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)) + 0.5, 0, 1);
                    if (cov <= 0) continue;
                    int i = (y * W + x) * 4;
                    Bgra[i] = (byte)Math.Max(Bgra[i], cb * cov); Bgra[i + 1] = (byte)Math.Max(Bgra[i + 1], cg * cov); Bgra[i + 2] = (byte)Math.Max(Bgra[i + 2], cr * cov);
                }
        }
    }

    /// <summary>A filled triangle, opaque.</summary>
    public void Tri(double x0, double y0, double x1, double y1, double x2, double y2, uint color)
    {
        int ax = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)))), bx = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int ay = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)))), by = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-6) return;
        for (int y = ay; y <= by; y++)
            for (int x = ax; x <= bx; x++)
            {
                double w0 = ((x1 - x) * (y2 - y) - (x2 - x) * (y1 - y)) / area, w1 = ((x2 - x) * (y0 - y) - (x0 - x) * (y2 - y)) / area, w2 = 1 - w0 - w1;
                if (w0 >= 0 && w1 >= 0 && w2 >= 0) Over(x, y, color, 1);
            }
    }

    /// <summary>A convex quadrilateral (two triangles).</summary>
    public void Quad(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, uint color)
    {
        Tri(x0, y0, x1, y1, x2, y2, color);
        Tri(x0, y0, x2, y2, x3, y3, color);
    }

    private void Over(int x, int y, uint color, double a)
    {
        int i = (y * W + x) * 4;
        int b = (int)(color & 255), g = (int)((color >> 8) & 255), r = (int)((color >> 16) & 255);
        Bgra[i] = (byte)(Bgra[i] + (b - Bgra[i]) * a);
        Bgra[i + 1] = (byte)(Bgra[i + 1] + (g - Bgra[i + 1]) * a);
        Bgra[i + 2] = (byte)(Bgra[i + 2] + (r - Bgra[i + 2]) * a);
    }

    /// <summary>A soft dark edge, so the picture sits in its frame.</summary>
    public void Vignette(double strength)
    {
        Parallel.For(0, H, y =>
        {
            double ny = (y - H / 2.0) / (H / 2.0);
            for (int x = 0; x < W; x++)
            {
                double nx = (x - W / 2.0) / (W / 2.0);
                double f = 1 - strength * (nx * nx * 0.55 + ny * ny * 0.45);
                int i = (y * W + x) * 4;
                Bgra[i] = (byte)(Bgra[i] * f); Bgra[i + 1] = (byte)(Bgra[i + 1] * f); Bgra[i + 2] = (byte)(Bgra[i + 2] * f);
            }
        });
    }

    /// <summary>BGRA to NV12 (BT.709, limited range), the form the H.264 encoder takes.</summary>
    public void ToNv12(byte[] nv12)
    {
        int ySize = W * H;
        Parallel.For(0, H / 2, row =>
        {
            for (int col = 0; col < W / 2; col++)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = col * 2 + dx, y = row * 2 + dy, i = (y * W + x) * 4;
                        int b = Bgra[i], g = Bgra[i + 1], r = Bgra[i + 2];
                        nv12[y * W + x] = (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
                        sumR += r; sumG += g; sumB += b;
                    }
                int ar = sumR >> 2, ag = sumG >> 2, ab = sumB >> 2;
                int o = ySize + row * W + col * 2;
                nv12[o] = (byte)Math.Clamp(((-26 * ar - 87 * ag + 112 * ab + 128) >> 8) + 128, 16, 240);
                nv12[o + 1] = (byte)Math.Clamp(((112 * ar - 102 * ag - 10 * ab + 128) >> 8) + 128, 16, 240);
            }
        });
    }
}

/// <summary>Draws one frame of a sample clip's scene. Pure arithmetic over the canvas: the same scene, time and seed always give the same picture.</summary>
internal static class SampleFrames
{
    public static void Draw(SampleCanvas c, SampleScene scene, double t, int seed)
    {
        switch (scene)
        {
            case SampleScene.SynthwaveDrive: Synthwave(c, t); break;
            case SampleScene.Cells: Cells(c, t, seed); break;
            case SampleScene.Spire: Spire(c, t, seed); break;
            case SampleScene.Warp: Warp(c, t, seed); break;
            case SampleScene.Shards: Shards(c, t, seed); break;
            case SampleScene.Plasma: Plasma(c, t); break;
            case SampleScene.Orbits: Orbits(c, t, seed); break;
        }
        c.Vignette(0.45);
    }

    private static double Frac(double v) => v - Math.Floor(v);

    // ---------------------------------------------------------------- NeonDrift: a synthwave sunset over a neon grid

    private static void Synthwave(SampleCanvas c, double t)
    {
        int w = c.W, h = c.H;
        double hz = h * 0.56, sunX = w / 2.0, sunY = hz - h * 0.17, sunR = h * 0.25;
        c.Fill((x, y) =>
        {
            if (y >= hz) return SampleCanvas.Mix(0x2a0a4a, 0x07001a, (y - hz) / (h - hz));
            double v = y / hz;
            uint sky = v < 0.6 ? SampleCanvas.Mix(0x0b0030, 0x6a0f86, v / 0.6) : SampleCanvas.Mix(0x6a0f86, 0xff3d8b, (v - 0.6) / 0.4);
            // the sun: yellow to pink, cut by stripes that widen towards the bottom
            double dx = x - sunX, dy = y - sunY, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < sunR)
            {
                double down = (y - (sunY - sunR)) / (2 * sunR);
                bool cut = down > 0.45 && Frac((y - sunY) / 17.0) < (down - 0.45) * 1.15;
                if (!cut) return SampleCanvas.Mix(0xffe45c, 0xff2e93, down);
            }
            if (d < sunR * 1.5 && d >= sunR) sky = SampleCanvas.Mix(sky, 0xff7a3d, (1 - (d - sunR) / (sunR * 0.5)) * 0.55);
            // mountains on both sides of the road
            double edge = Math.Abs(x - w / 2.0) / (w / 2.0);
            double mount = hz - (h * 0.05 + h * 0.09 * Math.Abs(Math.Sin(x * 0.009 + 1.3)) + h * 0.05 * Math.Abs(Math.Sin(x * 0.023))) * Math.Clamp(edge * 1.8 - 0.25, 0, 1);
            if (y > mount) return y < mount + 2 ? 0xff5cd8 : SampleCanvas.Mix(0x14002e, 0x2a0a4a, (y - mount) / 90);
            return sky;
        });
        // the grid: lines of constant depth rushing towards the viewer, and the lines to the vanishing point
        for (int i = 0; i < 16; i++)
        {
            double u = (i + Frac(t * 1.1)) / 16.0, y = hz + (h - hz) * u * u;
            c.Line(0, y, w, y, 1 + 2.5 * u, 0xc23cff, 0.55 + 0.4 * u);
            c.Line(0, y, w, y, 1 + 1.2 * u, 0x2ee6ff, 0.35 * u);
        }
        for (int j = -14; j <= 14; j++)
        {
            double bx = w / 2.0 + j * 190;
            c.Line(w / 2.0 + j * 12, hz, bx, h, 2.5, 0xc23cff, 0.7);
            c.Line(w / 2.0 + j * 12, hz, bx, h, 1.1, 0x2ee6ff, 0.5);
        }
        c.Glow(sunX, hz, w * 0.45, 0xff2e93, 0.22);
        // the car: a low dark wedge with glowing tail lights, bobbing a little
        double carX = w / 2.0 + Math.Sin(t * 1.3) * 60, carY = h * 0.78 + Math.Sin(t * 7) * 2;
        c.Glow(carX, carY + 30, 260, 0xff2e93, 0.35);
        c.Quad(carX - 150, carY + 40, carX - 110, carY - 10, carX + 110, carY - 10, carX + 150, carY + 40, 0x0a0016);
        c.Quad(carX - 80, carY - 10, carX - 55, carY - 48, carX + 55, carY - 48, carX + 80, carY - 10, 0x14002a);
        c.Line(carX - 146, carY + 38, carX + 146, carY + 38, 3, 0x2ee6ff);
        c.Line(carX - 108, carY - 9, carX + 108, carY - 9, 2, 0xff5cd8);
        foreach (int s in new[] { -1, 1 })
        {
            c.Glow(carX + s * 112, carY + 12, 70, 0xff1744, 1.1);
            c.Disc(carX + s * 112, carY + 12, 8, 0xffd0d8);
        }
    }

    // ---------------------------------------------------------------- Cellwars: two teams of glowing cells

    private static void Cells(SampleCanvas c, double t, int seed)
    {
        var rnd = new Random(seed);
        c.Fill((x, y) => SampleCanvas.Mix(0x04141f, 0x0b2a35, y / (double)c.H + 0.1 * Math.Sin(x * 0.01 + t)));
        for (int gx = 0; gx < c.W; gx += 80) c.Line(gx, 0, gx, c.H, 1, 0x1a4a55, 0.35);
        for (int gy = 0; gy < c.H; gy += 80) c.Line(0, gy, c.W, gy, 1, 0x1a4a55, 0.35);
        for (int i = 0; i < 150; i++)
        {
            double px = Frac(rnd.NextDouble() + t * (0.01 + 0.03 * rnd.NextDouble())) * c.W, py = (rnd.NextDouble() * c.H + Math.Sin(t * 0.8 + i) * 20 + c.H) % c.H;
            c.Disc(px, py, 1.5 + rnd.NextDouble() * 2, 0x66ffee, 0.55);
        }
        for (int i = 0; i < 26; i++)
        {
            bool teamA = i % 2 == 0;
            double fx = rnd.NextDouble(), fy = rnd.NextDouble(), sp = 0.15 + rnd.NextDouble() * 0.35, ph = rnd.NextDouble() * 6.28;
            double cx = c.W * (0.06 + 0.88 * (0.5 + 0.5 * Math.Sin(fx * 6.28 + t * sp + ph))), cy = c.H * (0.08 + 0.84 * (0.5 + 0.5 * Math.Cos(fy * 6.28 + t * sp * 0.8 + ph)));
            double r = 26 + rnd.NextDouble() * 52 + 4 * Math.Sin(t * 3 + i);
            uint hue = teamA ? 0x19f2c4u : 0xff3ea5u;
            c.Glow(cx, cy, r * 2.6, hue, 0.5);
            c.Disc(cx, cy, r, SampleCanvas.Mix(hue, 0x000000, 0.55));
            c.Ring(cx, cy, r, 3, hue);
            c.Disc(cx - r * 0.25, cy - r * 0.25, r * 0.28, hue, 0.55);   // the nucleus
            c.Disc(cx + r * 0.2, cy + r * 0.15, r * 0.14, 0xffffff, 0.5);
        }
    }

    // ---------------------------------------------------------------- Voidspire: a lone tower under a purple sky

    private static void Spire(SampleCanvas c, double t, int seed)
    {
        var rnd = new Random(seed);
        int w = c.W, h = c.H;
        double hz = h * 0.78;
        c.Fill((x, y) =>
        {
            double d = Math.Sqrt(Math.Pow((x - w * 0.5) / w, 2) + Math.Pow((y - h * 0.55) / h, 2));
            uint sky = SampleCanvas.Mix(0x3b1175, 0x03000c, d * 1.7);
            return y >= hz ? SampleCanvas.Mix(0x0b0620, 0x020008, (y - hz) / (h - hz)) : sky;
        });
        c.Glow(w * 0.2, h * 0.3, w * 0.35, 0x4b2cc8, 0.55);
        c.Glow(w * 0.82, h * 0.22, w * 0.3, 0xb520a8, 0.45);
        for (int i = 0; i < 260; i++)
        {
            double sx = rnd.NextDouble() * w, sy = rnd.NextDouble() * hz, tw = 0.5 + 0.5 * Math.Sin(t * (1 + rnd.NextDouble() * 3) + i);
            c.Disc(sx, sy, 0.8 + rnd.NextDouble() * 1.4, 0xdde6ff, 0.35 + 0.6 * tw);
        }
        double cx = w * 0.5, tip = h * 0.1;
        double pulse = 0.75 + 0.25 * Math.Sin(t * 3);
        // the beam, the spire, its windows
        for (int i = 0; i < 40; i++) c.Line(cx, tip, cx + (i - 20) * 40, -40, 2, 0x7a5cff, 0.07);
        c.Glow(cx, tip, 300, 0x58e0ff, 0.9 * pulse);
        c.Tri(cx - 70, hz, cx + 70, hz, cx, tip, 0x090418);
        c.Tri(cx - 30, hz - 120, cx + 30, hz - 120, cx, tip + 30, 0x120830);
        c.Line(cx - 70, hz, cx, tip, 3, 0x6a3cff, 0.8);
        c.Line(cx + 70, hz, cx, tip, 3, 0x6a3cff, 0.8);
        for (int k = 1; k < 9; k++)
        {
            double y = hz - k * (hz - tip) / 9.5, half = 70 * (1 - (hz - y) / (hz - tip));
            c.Line(cx - half * 0.75, y, cx + half * 0.75, y, 2, 0x58e0ff, 0.35 + 0.3 * Math.Sin(t * 2 + k));
        }
        c.Disc(cx, tip + 4, 7, 0xffffff);
        // rings turning around the spire
        for (int r = 0; r < 3; r++)
        {
            double ry = hz - (hz - tip) * (0.28 + 0.2 * r), rw = 190 - 35 * r, a = t * (0.9 - 0.25 * r) + r;
            for (int s = 0; s < 90; s++)
            {
                double a0 = a + s * 6.283 / 90, a1 = a + (s + 1) * 6.283 / 90;
                if (Math.Sin(a0) < 0 && r != 1) continue;
                c.Line(cx + Math.Cos(a0) * rw, ry + Math.Sin(a0) * rw * 0.22, cx + Math.Cos(a1) * rw, ry + Math.Sin(a1) * rw * 0.22, 2.5, 0xb36bff, 0.85);
            }
        }
        // the ground: a dark plain with a faint reflection of the glow
        c.Glow(cx, hz + 8, 420, 0x6a3cff, 0.25);
        for (int i = 0; i < 6; i++) c.Line(0, hz + 14 + i * i * 4, w, hz + 14 + i * i * 4, 1, 0x3a1c8c, 0.4);
    }

    // ---------------------------------------------------------------- Hyperwarp: stars streaking past

    private static void Warp(SampleCanvas c, double t, int seed)
    {
        var rnd = new Random(seed);
        int w = c.W, h = c.H;
        double cx = w * 0.5 + Math.Sin(t * 0.7) * 30, cy = h * 0.5 + Math.Cos(t * 0.5) * 18, maxR = Math.Sqrt(cx * cx + cy * cy) + 60;
        c.Fill((x, y) =>
        {
            double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxR;
            return SampleCanvas.Mix(0x0a4fb0, 0x00010a, Math.Pow(d, 0.6) * 1.2);
        });
        c.Glow(cx, cy, 520, 0x39d8ff, 0.8);
        for (int i = 0; i < 520; i++)
        {
            double ang = rnd.NextDouble() * 6.2832, r0 = rnd.NextDouble(), sp = 0.35 + rnd.NextDouble() * 0.9, hue = 0.5 + rnd.NextDouble() * 0.14;
            double p = Frac(r0 + t * sp * 0.45);
            double r = p * p * maxR, rBack = Math.Max(0, (p - 0.035 * sp) * (p - 0.035 * sp)) * maxR;
            double ca = Math.Cos(ang), sa = Math.Sin(ang);
            var (rr, gg, bb) = SampleCanvas.Hsv(hue, 0.25 + 0.5 * (1 - p), 1);
            double bright = Math.Min(1, 0.25 + p * 1.2);
            c.Line(cx + ca * rBack, cy + sa * rBack, cx + ca * r, cy + sa * r, 1 + p * 3.2, SampleCanvas.Rgb(rr, gg, bb), bright);
        }
        // a hint of a cockpit frame: two thin diagonals and a ring
        c.Ring(cx, cy, 36 + 6 * Math.Sin(t * 4), 2, 0x9ef2ff, 0.55);
        c.Ring(cx, cy, 62, 1.5, 0x9ef2ff, 0.3);
        c.Line(0, h, w * 0.18, h * 0.78, 4, 0x23608a, 0.8);
        c.Line(w, h, w * 0.82, h * 0.78, 4, 0x23608a, 0.8);
    }

    // ---------------------------------------------------------------- Shardfall: glassy shards raining down

    private static void Shards(SampleCanvas c, double t, int seed)
    {
        var rnd = new Random(seed);
        int w = c.W, h = c.H;
        c.Fill((x, y) => SampleCanvas.Mix(0x1a0206, 0x7a1d12, Math.Pow(y / (double)h, 1.6)));
        c.Glow(w * 0.5, h * 1.05, w * 0.6, 0xff8a1f, 0.75);
        c.Glow(w * 0.5, h * 0.1, w * 0.4, 0xff3b30, 0.18);
        for (int i = 0; i < 70; i++)
        {
            double depth = 0.25 + rnd.NextDouble() * 0.75, size = 14 + 70 * depth * (0.4 + rnd.NextDouble());
            double x0 = rnd.NextDouble() * w, speed = 60 + 220 * depth, y0 = rnd.NextDouble() * (h + 200);
            double cx = x0 + Math.Sin(t * 0.6 + i) * 30, cy = (y0 + t * speed) % (h + 200) - 100;
            double rot = rnd.NextDouble() * 6.28 + t * (rnd.NextDouble() - 0.5) * 2.5;
            double[] ang = { rot, rot + 2.2 + rnd.NextDouble() * 0.5, rot + 3.9 + rnd.NextDouble() * 0.6 };
            double[] rad = { size, size * (0.5 + rnd.NextDouble() * 0.4), size * (0.7 + rnd.NextDouble() * 0.5) };
            var (r, g, b) = SampleCanvas.Hsv(0.0 + rnd.NextDouble() * 0.12, 0.85 - 0.3 * depth, 0.35 + 0.65 * depth);
            uint fill = SampleCanvas.Rgb(r, g, b), edge = SampleCanvas.Rgb(Math.Min(255, r * 1.5 + 60), Math.Min(255, g * 1.5 + 60), Math.Min(255, b * 1.5 + 40));
            double ax = cx + Math.Cos(ang[0]) * rad[0], ay = cy + Math.Sin(ang[0]) * rad[0], bx = cx + Math.Cos(ang[1]) * rad[1], by = cy + Math.Sin(ang[1]) * rad[1], dx = cx + Math.Cos(ang[2]) * rad[2], dy = cy + Math.Sin(ang[2]) * rad[2];
            c.Tri(ax, ay, bx, by, dx, dy, fill);
            c.Tri(ax, ay, bx, by, cx, cy, SampleCanvas.Mix(fill, edge, 0.35));   // a lighter facet
            c.Line(ax, ay, bx, by, 1.6, edge, 0.9);
            c.Line(bx, by, dx, dy, 1.2, edge, 0.5);
            c.Line(dx, dy, ax, ay, 1.2, edge, 0.5);
        }
        c.Disc(w * 0.5, h * 0.94, 90, 0xffb347, 0.9);
        c.Glow(w * 0.5, h * 0.94, 380, 0xffa030, 0.9);
    }

    // ---------------------------------------------------------------- Lumenrun: a flowing plasma

    private static void Plasma(SampleCanvas c, double t)
    {
        c.Fill((x, y) =>
        {
            double v = Math.Sin(x * 0.011 + t * 1.1) + Math.Sin(y * 0.016 - t * 1.4) + Math.Sin((x + y) * 0.007 + t * 0.8)
                + Math.Sin(Math.Sqrt((x - 640.0) * (x - 640.0) + (y - 360.0) * (y - 360.0)) * 0.02 - t * 2.2);
            var (r, g, b) = SampleCanvas.Hsv(0.55 + v * 0.1 + t * 0.03, 0.8, 0.55 + 0.4 * Math.Sin(v * 1.6) * 0.5 + 0.2);
            return SampleCanvas.Rgb(r, g, b);
        });
        for (int i = 0; i < 5; i++)
        {
            double px = c.W * (0.5 + 0.38 * Math.Sin(t * (0.7 + 0.2 * i) + i * 1.9)), py = c.H * (0.5 + 0.34 * Math.Cos(t * (0.6 + 0.17 * i) + i));
            c.Glow(px, py, 190, 0xffffff, 0.55);
            c.Disc(px, py, 9, 0xffffff);
        }
    }

    // ---------------------------------------------------------------- Orbitfall: moons around a golden planet

    private static void Orbits(SampleCanvas c, double t, int seed)
    {
        var rnd = new Random(seed);
        int w = c.W, h = c.H;
        double cx = w * 0.5, cy = h * 0.52;
        c.Fill((x, y) => SampleCanvas.Mix(0x050a24, 0x0d1f4a, y / (double)h));
        for (int i = 0; i < 120; i++) c.Disc(rnd.NextDouble() * w, rnd.NextDouble() * h, 0.8 + rnd.NextDouble() * 1.2, 0xc8d8ff, 0.25 + 0.5 * rnd.NextDouble());
        c.Glow(cx, cy, 380, 0xffb020, 0.8);
        for (int k = 1; k <= 5; k++)
            for (int s = 0; s < 120; s++)
            {
                double a0 = s * 6.2832 / 120, a1 = (s + 1) * 6.2832 / 120, o = 70 + k * 62;
                c.Line(cx + Math.Cos(a0) * o, cy + Math.Sin(a0) * o * 0.62, cx + Math.Cos(a1) * o, cy + Math.Sin(a1) * o * 0.62, 1.4, 0x2b6fb0, 0.5);
            }
        c.Disc(cx, cy, 58, 0xffc13a);
        c.Disc(cx + 10, cy - 6, 46, 0xffd96a, 0.7);
        c.Disc(cx - 16, cy + 12, 14, 0xe0961c, 0.7);
        for (int k = 1; k <= 5; k++)
        {
            double orbit = 70 + k * 62, speed = 1.8 / Math.Sqrt(k), phase = rnd.NextDouble() * 6.28;
            bool warm = k % 2 == 1;
            uint col = warm ? 0xff7a3du : 0x35e0d0u;
            for (int s = 24; s >= 0; s--)   // the comet tail, fading
            {
                double a = phase + (t - s * 0.025) * speed;
                c.Disc(cx + Math.Cos(a) * orbit, cy + Math.Sin(a) * orbit * 0.62, 7 - s * 0.24, col, 0.5 * (1 - s / 25.0));
            }
            double ang = phase + t * speed, mx = cx + Math.Cos(ang) * orbit, my = cy + Math.Sin(ang) * orbit * 0.62;
            c.Glow(mx, my, 60, col, 0.9);
            c.Disc(mx, my, 10, col);
            c.Disc(mx - 3, my - 3, 4, 0xffffff, 0.8);
        }
    }
}
