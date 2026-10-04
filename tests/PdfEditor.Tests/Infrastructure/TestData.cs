using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfEditor.Tests.Infrastructure;

/// <summary>Test inputs. The repo's sample PDF is only ever read; anything written goes to a temp folder.</summary>
internal static class TestData
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "TestData");

    /// <summary>The sample form: 7 fields (text, date, multi-line, drop-down, check box, radio group) and a /Rotate 90 second page.</summary>
    public static byte[] SampleForm() => File.ReadAllBytes(Path.Combine(Dir, "sample-form.pdf"));

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PdfEditor.Tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string TempCopyOfSample()
    {
        var path = Path.Combine(TempDir(), "sample-form.pdf");
        File.WriteAllBytes(path, SampleForm());
        return path;
    }

    public static byte[] Save(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    public static List<FormFieldModel> Fields(byte[] pdf) => FormReader.Read(PdfOpen.Open(pdf, null)).Select(b => b.Model).ToList();

    // ------------------------------------------------------------------ synthetic forms

    public sealed record FieldSpec(string Name, double X, double Y, double W, double H, string Da = "/Helv 0 Tf 0 g",
        int Q = 0, int Ff = 0, int MaxLen = 0, int Page = 0, string Ft = "/Tx");

    /// <summary>A form with the given text fields, built by hand so each /DA, /Q, /Ff and /MaxLen is exactly as specified.</summary>
    public static byte[] Form(IEnumerable<FieldSpec> specs, int pages = 1, Action<PdfDocument>? configure = null)
    {
        var doc = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var p = doc.AddPage();
            p.Width = XUnit.FromPoint(612);
            p.Height = XUnit.FromPoint(792);
        }
        var fields = new PdfArray(doc);
        foreach (var s in specs)
        {
            var page = doc.Pages[s.Page];
            var f = new PdfDictionary(doc);
            f.Elements["/Type"] = new PdfName("/Annot");
            f.Elements["/Subtype"] = new PdfName("/Widget");
            f.Elements["/FT"] = new PdfName(s.Ft);
            f.Elements["/T"] = new PdfString(s.Name);
            f.Elements["/DA"] = new PdfString(s.Da);
            f.Elements["/F"] = new PdfInteger(4);
            if (s.Q != 0) f.Elements["/Q"] = new PdfInteger(s.Q);
            if (s.Ff != 0) f.Elements["/Ff"] = new PdfInteger(s.Ff);
            if (s.MaxLen != 0) f.Elements["/MaxLen"] = new PdfInteger(s.MaxLen);
            f.Elements["/Rect"] = new PdfArray(doc, new PdfReal(s.X), new PdfReal(s.Y), new PdfReal(s.X + s.W), new PdfReal(s.Y + s.H));
            f.Elements["/P"] = page.Reference!;
            doc.Internals.AddObject(f);
            var annots = page.Elements.GetArray("/Annots") ?? new PdfArray(doc);
            annots.Elements.Add(f.Reference!);
            page.Elements["/Annots"] = annots;
            fields.Elements.Add(f.Reference!);
        }
        var acro = new PdfDictionary(doc);
        acro.Elements["/Fields"] = fields;
        doc.Internals.Catalog.Elements["/AcroForm"] = acro;
        configure?.Invoke(doc);
        return Save(doc);
    }

    /// <summary>Pages of plain text (and a couple of shapes) for rendering tests.</summary>
    public static byte[] TextPages(int count, Action<int, PdfPage>? configure = null)
    {
        var doc = new PdfDocument();
        var font = new XFont("Arial", 9);
        for (int n = 0; n < count; n++)
        {
            var p = doc.AddPage();
            p.Width = XUnit.FromPoint(612);
            p.Height = XUnit.FromPoint(792);
            using (var g = XGraphics.FromPdfPage(p))
            {
                g.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 200, 40, 40)), 40, 40, 120, 60);
                g.DrawEllipse(new XPen(XColors.Blue, 2), 400, 60, 150, 90);
                for (int line = 0; line < 60; line++)
                    g.DrawString($"Page {n + 1} line {line + 1}: The quick brown fox jumps over the lazy dog", font, XBrushes.Black, 50, 140 + line * 10.5);
            }
            configure?.Invoke(n, p);
        }
        return Save(doc);
    }

    // ------------------------------------------------------------------ images

    public static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A signature-like stroke on a transparent background.</summary>
    public static byte[] SignaturePng(int seed = 1, int w = 240, int h = 90)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool ink = Math.Abs(y - h / 2 - h * 0.22 * Math.Sin((x + seed * 7) / 18.0)) < 5;
                px[i] = 160; px[i + 1] = 40; px[i + 2] = 20; px[i + 3] = ink ? (byte)255 : (byte)0;
            }
        return Encode(new PngBitmapEncoder(), BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4));
    }

    /// <summary>A noisy, photo-like image with four coloured quadrants (red TL, green TR, blue BL, yellow BR).</summary>
    public static BitmapSource Photo(int w, int h, bool transparentCentre = false)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool l = x < w / 2, t = y < h / 2;
                (int r, int g, int b) = (l, t) switch { (true, true) => (220, 30, 30), (false, true) => (30, 180, 40), (true, false) => (30, 50, 210), _ => (230, 210, 30) };
                int n = rnd.Next(-25, 26);
                px[i] = (byte)Math.Clamp(b + n, 0, 255); px[i + 1] = (byte)Math.Clamp(g + n, 0, 255); px[i + 2] = (byte)Math.Clamp(r + n, 0, 255);
                px[i + 3] = transparentCentre && Math.Abs(x - w / 2) < w / 8 && Math.Abs(y - h / 2) < h / 8 ? (byte)0 : (byte)255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Writes a JPEG of <paramref name="source"/> with the given EXIF orientation and returns its path.</summary>
    public static string Jpeg(string dir, string name, BitmapSource source, int orientation = 1, int quality = 92)
    {
        var meta = new BitmapMetadata("jpg");
        meta.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0), null, meta, null));
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    // ------------------------------------------------------------------ pixels

    public static byte[] Pixels(BitmapSource b)
    {
        var c = new FormatConvertedBitmap(b, PixelFormats.Bgr32, null, 0);
        var px = new byte[c.PixelWidth * c.PixelHeight * 4];
        c.CopyPixels(px, c.PixelWidth * 4, 0);
        return px;
    }

    public static double MeanDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return double.MaxValue;
        long d = 0;
        for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
        return (double)d / a.Length;
    }

    public static async Task<byte[]> RenderPage(byte[] pdf, int page, int width = 600)
    {
        using var r = await PdfRenderer.LoadAsync(pdf, null);
        return Pixels(await r.RenderAsync(page, width));
    }

    public static Color PixelAt(BitmapSource b, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(b, PixelFormats.Bgr32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    public static bool Contains(byte[] hay, byte[] needle) => Count(hay, needle) > 0;

    public static int Count(byte[] hay, byte[] needle)
    {
        int n = 0;
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) { n++; i += needle.Length - 1; }
        }
        return n;
    }

    public static StampModel Item(StampKind kind, int page, double cx, double cy, double w, double h, double angle = 0,
        string text = "", double size = 12, Color? color = null, string font = "Arial", byte[]? data = null)
    {
        var m = new StampModel { Kind = kind, PageIndex = page, Text = text, FontSize = size, Color = color ?? Colors.Black, FontFamily = font, ImageData = data };
        m.Width = w; m.Height = h; m.CenterX = cx; m.CenterY = cy; m.Angle = angle;
        return m;
    }
}
