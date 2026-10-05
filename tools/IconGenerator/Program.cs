using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the PDF Editor icon at each Windows icon size and packs them into one .ico (PNG-compressed entries).
// Sizes up to 24 px use a simplified drawing with thicker strokes so they stay legible.
static class P
{
    static readonly Color Accent = Color.FromRgb(0x25, 0x63, 0xEB);  // the app's accent blue
    static readonly Color Ink = Color.FromRgb(0x1A, 0x3A, 0x9C);     // signature ink, as in the app's ink colours
    static readonly Color PageEdge = Color.FromRgb(0xB8, 0xC0, 0xCC);
    static readonly Color Fold = Color.FromRgb(0xDD, 0xE3, 0xEA);
    static readonly Color Label = Color.FromRgb(0xA9, 0xB2, 0xBF);
    static readonly Color FieldFill = Color.FromRgb(0xDB, 0xE7, 0xFF);

    static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen Stroke(Color c, double w) { var p = new Pen(B(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }

    /// <summary>The page outline with a folded top-right corner, in a 256-unit design space.</summary>
    static Geometry Page(double left, double top, double right, double bottom, double fold, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(left + r, top), true, true);
            c.LineTo(new Point(right - fold, top), true, true);
            c.LineTo(new Point(right, top + fold), true, true);
            c.LineTo(new Point(right, bottom - r), true, true);
            c.ArcTo(new Point(right - r, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(left + r, bottom), true, true);
            c.ArcTo(new Point(left, bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(left, top + r), true, true);
            c.ArcTo(new Point(left + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    static Geometry FoldFlap(double right, double top, double fold)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(right - fold, top), true, true);
            c.LineTo(new Point(right - fold, top + fold - 6), true, true);
            c.QuadraticBezierTo(new Point(right - fold, top + fold), new Point(right - fold + 6, top + fold), true, true);
            c.LineTo(new Point(right, top + fold), true, true);
        }
        g.Freeze();
        return g;
    }

    static Geometry Signature(double x0, double y, double width, double height)
    {
        // A loose cursive scrawl: two loops and a long tail.
        var g = new StreamGeometry();
        double s = width / 120.0, h = height / 40.0;
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0, y + 10 * h), false, false);
            c.BezierTo(new Point(x0 + 8 * s, y - 22 * h), new Point(x0 + 26 * s, y - 22 * h), new Point(x0 + 20 * s, y + 6 * h), true, true);
            c.BezierTo(new Point(x0 + 16 * s, y + 24 * h), new Point(x0 + 36 * s, y + 18 * h), new Point(x0 + 44 * s, y - 2 * h), true, true);
            c.BezierTo(new Point(x0 + 50 * s, y - 18 * h), new Point(x0 + 64 * s, y - 14 * h), new Point(x0 + 60 * s, y + 4 * h), true, true);
            c.BezierTo(new Point(x0 + 57 * s, y + 18 * h), new Point(x0 + 76 * s, y + 16 * h), new Point(x0 + 86 * s, y + 2 * h), true, true);
            c.BezierTo(new Point(x0 + 96 * s, y - 10 * h), new Point(x0 + 108 * s, y - 4 * h), new Point(x0 + 120 * s, y - 8 * h), true, true);
        }
        g.Freeze();
        return g;
    }

    static void DrawPen(DrawingContext dc, Point tip, double angle, double length, double thickness)
    {
        // Drawn pointing right from the tip, then rotated into place.
        dc.PushTransform(new RotateTransform(angle, tip.X, tip.Y));
        double t = thickness, nib = t * 1.25;
        var body = new Rect(tip.X + nib, tip.Y - t / 2, length - nib, t);
        dc.DrawRoundedRectangle(B(Accent), null, body, t * 0.18, t * 0.18);
        dc.DrawRectangle(B(Color.FromRgb(0x1E, 0x4F, 0xC4)), null, new Rect(body.Right - t * 0.9, body.Top, t * 0.9, t)); // cap band
        dc.DrawRectangle(B(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), null, new Rect(body.Left, body.Top + t * 0.15, body.Width - t * 0.9, t * 0.18)); // highlight
        var nibGeo = new StreamGeometry();
        using (var c = nibGeo.Open())
        {
            c.BeginFigure(new Point(tip.X, tip.Y), true, true);
            c.LineTo(new Point(tip.X + nib, tip.Y - t / 2), true, true);
            c.LineTo(new Point(tip.X + nib, tip.Y + t / 2), true, true);
        }
        dc.DrawGeometry(B(Color.FromRgb(0xE8, 0xD9, 0xB0)), null, nibGeo); // wooden/metal cone
        var point = new StreamGeometry();
        using (var c = point.Open())
        {
            c.BeginFigure(new Point(tip.X, tip.Y), true, true);
            c.LineTo(new Point(tip.X + nib * 0.42, tip.Y - t * 0.17), true, true);
            c.LineTo(new Point(tip.X + nib * 0.42, tip.Y + t * 0.17), true, true);
        }
        dc.DrawGeometry(B(Ink), null, point);
        dc.Pop();
    }

    /// <summary>Full detail, for 32 px and up. Design space 256 x 256.</summary>
    static void DrawFull(DrawingContext dc)
    {
        // Soft shadow under the page.
        dc.DrawGeometry(B(Color.FromArgb(0x30, 0x10, 0x20, 0x40)), null, Page(42, 22, 210, 244, 46, 14));
        var page = Page(36, 14, 204, 238, 46, 14);
        dc.DrawGeometry(Brushes.White, Stroke(PageEdge, 5), page);
        dc.DrawGeometry(B(Fold), Stroke(PageEdge, 5), FoldFlap(204, 14, 46));

        // Two form rows: label + blue field box.
        foreach (double y in new[] { 82.0, 122.0 })
        {
            dc.DrawRoundedRectangle(B(Label), null, new Rect(58, y + 7, 30, 9), 4.5, 4.5);
            dc.DrawRoundedRectangle(B(FieldFill), Stroke(Accent, 4), new Rect(98, y, 84, 24), 4, 4);
        }
        // Signature line and signature.
        dc.DrawLine(Stroke(Label, 4), new Point(58, 206), new Point(182, 206));
        dc.DrawGeometry(null, Stroke(Ink, 7), Signature(60, 182, 110, 34));
        // Pen resting across the bottom-right corner, nib at the end of the signature.
        DrawPen(dc, new Point(170, 186), -42, 104, 26);
    }

    /// <summary>Simplified, for 16-24 px: page, one field, a bold signature stroke. Design space 256 x 256.</summary>
    static void DrawSmall(DrawingContext dc)
    {
        var page = Page(30, 10, 214, 246, 58, 20);
        dc.DrawGeometry(Brushes.White, Stroke(Color.FromRgb(0x8C, 0x96, 0xA5), 16), page);
        dc.DrawGeometry(B(Fold), Stroke(Color.FromRgb(0x8C, 0x96, 0xA5), 16), FoldFlap(214, 10, 58));
        dc.DrawRoundedRectangle(B(FieldFill), Stroke(Accent, 16), new Rect(62, 92, 120, 46), 8, 8);
        dc.DrawGeometry(null, Stroke(Ink, 20), Signature(62, 194, 122, 40));
    }

    static BitmapSource Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            if (size <= 24) DrawSmall(dc); else DrawFull(dc);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static byte[] Png(BitmapSource b)
    {
        var e = new PngBitmapEncoder();
        e.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        e.Save(ms);
        return ms.ToArray();
    }

    static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }

    /// <summary>Usage: dotnet run --project tools/IconGenerator -- src/PdfEditor/Assets</summary>
    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = sizes.Select(s => (s, Png(Render(s)))).ToList();
        var ico = Path.Combine(outDir, "app.ico");
        WriteIco(ico, images);
        Console.WriteLine($"wrote {Path.GetFullPath(ico)} ({new FileInfo(ico).Length / 1024} KB, sizes {string.Join(", ", sizes)})");
    }
}
