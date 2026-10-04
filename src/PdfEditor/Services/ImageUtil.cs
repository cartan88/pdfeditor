using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfEditor.Services;

public static class ImageUtil
{
    /// <summary>Longest side, in pixels, of an image placed on a page (about 8 inches at 300 DPI).</summary>
    public const int MaxPlacedDimension = 2400;

    /// <summary>Opaque lossless images larger than this are re-checked as JPEG (scans and photos saved as PNG/TIFF/BMP).</summary>
    private const int LosslessSizeLimit = 1024 * 1024;

    /// <summary>Loads an image file, applying its EXIF orientation (phone photos are often stored sideways).</summary>
    public static BitmapSource Load(string path)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource result = frame;
        var transform = OrientationTransform(ReadOrientation(frame));
        if (transform != null) result = new TransformedBitmap(frame, transform);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Reads an image file for placing on a page and returns compact PNG or JPEG bytes:
    /// the image is turned upright and scaled down to <see cref="MaxPlacedDimension"/>. JPEG photos stay JPEG
    /// (passed through untouched when no change is needed), images with transparency stay PNG, and other
    /// lossless images stay PNG unless that would be large and a JPEG is much smaller.
    /// </summary>
    public static byte[] PrepareForPlacement(string path)
    {
        var original = File.ReadAllBytes(path);
        var frame = BitmapFrame.Create(new MemoryStream(original), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        int orientation = ReadOrientation(frame);
        bool isJpeg = frame.Decoder is JpegBitmapDecoder;
        bool tooBig = Math.Max(frame.PixelWidth, frame.PixelHeight) > MaxPlacedDimension;

        // CMYK JPEGs are re-encoded: their embedding in PDFs is unreliable (often inverted colours).
        if (isJpeg && !tooBig && orientation == 1 && frame.Format != PixelFormats.Cmyk32) return original;

        BitmapSource image = frame;
        if (OrientationTransform(orientation) is { } t) image = new TransformedBitmap(image, t);
        double scale = (double)MaxPlacedDimension / Math.Max(image.PixelWidth, image.PixelHeight);
        if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();

        if (isJpeg) return ToJpeg(bgra);
        var png = ToPng(bgra);
        if (png.Length > LosslessSizeLimit && IsOpaque(bgra))
        {
            var jpeg = ToJpeg(bgra);
            if (jpeg.Length < png.Length / 2) return jpeg;
        }
        return png;
    }

    private static bool IsOpaque(BitmapSource bgra)
    {
        int stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 255) return false;
        return true;
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata meta) return 1;
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (meta.ContainsQuery(query) && meta.GetQuery(query) is ushort o) return o;
            }
            catch (Exception) { /* format without that metadata block */ }
        }
        return 1;
    }

    /// <summary>Transform that turns an image stored with the given EXIF orientation upright.</summary>
    private static Transform? OrientationTransform(int orientation)
    {
        var flipH = new ScaleTransform(-1, 1);
        var flipV = new ScaleTransform(1, -1);
        return orientation switch
        {
            2 => flipH,
            3 => new RotateTransform(180),
            4 => flipV,
            5 => new TransformGroup { Children = { new RotateTransform(90), flipH } },  // transpose
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(90), flipV } },  // transverse
            8 => new RotateTransform(270),
            _ => null,
        };
    }

    /// <summary>Decodes encoded image bytes (PNG or JPEG).</summary>
    public static BitmapSource FromBytes(byte[] data)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(data);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static byte[] ToPng(BitmapSource source) => Encode(new PngBitmapEncoder(), source);

    public static byte[] ToJpeg(BitmapSource source, int quality = 90)
    {
        // JPEG has no alpha; flatten onto white so transparent areas don't turn black.
        var white = new DrawingVisual();
        using (var dc = white.RenderOpen())
        {
            var rect = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(source, rect);
        }
        var rtb = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(white);
        return Encode(new JpegBitmapEncoder { QualityLevel = quality }, new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0));
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Prepares a scanned/photographed signature: optionally turns the paper background transparent,
    /// trims empty margins and limits the size.
    /// </summary>
    public static BitmapSource PrepareSignature(BitmapSource source, bool removeBackground, int maxDimension = 1600)
    {
        var bmp = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        if (removeBackground)
        {
            // Estimate paper brightness from the brightest pixels so off-white scans work too.
            var hist = new int[256];
            for (int i = 0; i < px.Length; i += 4) hist[Luma(px, i)]++;
            int paper = 255, count = 0, target = (w * h) / 5;
            for (int l = 255; l >= 0; l--) { count += hist[l]; if (count >= target) { paper = l; break; } }
            paper = Math.Max(paper, 120);
            double hi = paper - 18, lo = Math.Max(0, paper - 110);

            for (int i = 0; i < px.Length; i += 4)
            {
                int l = Luma(px, i);
                double a = l >= hi ? 0 : l <= lo ? 1 : (hi - l) / (hi - lo);
                px[i + 3] = (byte)Math.Round(px[i + 3] * a);
                // Darken the ink colour slightly so semi-transparent edges don't look washed out.
                if (a > 0 && a < 1)
                {
                    for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Max(0, px[i + c] - (255 - px[i + c]) * (1 - a));
                }
            }
        }

        // Trim transparent / empty margins.
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] > 12)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }

        BitmapSource result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        if (maxX >= minX && maxY >= minY)
        {
            int m = 4;
            int x0 = Math.Max(0, minX - m), y0 = Math.Max(0, minY - m);
            int x1 = Math.Min(w - 1, maxX + m), y1 = Math.Min(h - 1, maxY + m);
            result = new CroppedBitmap(result, new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
        }

        double scale = Math.Min(1.0, (double)maxDimension / Math.Max(result.PixelWidth, result.PixelHeight));
        if (scale < 1.0) result = new TransformedBitmap(result, new ScaleTransform(scale, scale));
        result = new WriteableBitmap(result);
        result.Freeze();
        return result;
    }

    private static int Luma(byte[] px, int i) => (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000;
}
