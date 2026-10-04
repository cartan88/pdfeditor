using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfEditor.Services;

public static class ImageUtil
{
    public static BitmapSource Load(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(Path.GetFullPath(path));
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static BitmapSource FromPng(byte[] png)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(png);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static byte[] ToPng(BitmapSource source)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
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
