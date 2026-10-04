using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

public class ImageUtilTests
{
    /// <summary>
    /// A 200x100 image with a dark block in its stored top-left corner, saved with each EXIF orientation.
    /// After loading, the block must be in the corner the EXIF spec says the image is displayed with.
    /// </summary>
    [Theory]
    [InlineData(1, "TL", false)]
    [InlineData(2, "TR", false)] // mirrored
    [InlineData(3, "BR", false)] // rotated 180
    [InlineData(4, "BL", false)] // flipped
    [InlineData(5, "TL", true)]  // transposed
    [InlineData(6, "TR", true)]  // rotate 90 clockwise to display
    [InlineData(7, "BR", true)]  // transversed
    [InlineData(8, "BL", true)]  // rotate 90 counter-clockwise to display
    public Task AppliesExifOrientation(int orientation, string corner, bool swapped) => Ui.Run(() =>
    {
        var px = new byte[200 * 100 * 4];
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 200; x++)
            {
                int i = (y * 200 + x) * 4;
                byte v = x < 40 && y < 30 ? (byte)0 : (byte)255;
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
            }
        var path = Jpeg(TempDir(), $"o{orientation}.jpg", BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null, px, 800), orientation, 100);

        var img = ImageUtil.Load(path);
        Assert.Equal(swapped ? (100, 200) : (200, 100), (img.PixelWidth, img.PixelHeight));
        int w = img.PixelWidth, h = img.PixelHeight;
        bool Dark(int x, int y) => PixelAt(img, x, y).G < 100;
        string found = Dark(5, 5) ? "TL" : Dark(w - 6, 5) ? "TR" : Dark(5, h - 6) ? "BL" : Dark(w - 6, h - 6) ? "BR" : "none";
        Assert.Equal(corner, found);
    });

    [Fact]
    public Task SmallJpegsArePassedThroughUnchanged() => Ui.Run(() =>
    {
        var path = Jpeg(TempDir(), "small.jpg", Photo(800, 600), quality: 85);
        Assert.Equal(File.ReadAllBytes(path), ImageUtil.PrepareForPlacement(path));
    });

    [Fact]
    public Task LargePhotosAreScaledDownAndStayJpeg() => Ui.Run(() =>
    {
        var path = Jpeg(TempDir(), "big.jpg", Photo(4000, 3000), orientation: 6);
        var data = ImageUtil.PrepareForPlacement(path);
        var img = ImageUtil.FromBytes(data);
        Assert.True(data[0] == 0xFF && data[1] == 0xD8, "stored as JPEG");
        Assert.Equal((1800, 2400), (img.PixelWidth, img.PixelHeight)); // capped at 2400, turned upright
        Assert.True(data.Length < new FileInfo(path).Length);
    });

    [Fact]
    public Task PhotosSavedAsPngBecomeJpeg() => Ui.Run(() =>
    {
        var path = Path.Combine(TempDir(), "photo.png");
        File.WriteAllBytes(path, Encode(new PngBitmapEncoder(), Photo(3000, 2000)));
        var data = ImageUtil.PrepareForPlacement(path);
        Assert.True(data[0] == 0xFF && data[1] == 0xD8, "large opaque PNG photo re-encoded as JPEG");
        Assert.True(data.Length < new FileInfo(path).Length / 4);
    });

    [Fact]
    public Task ImagesWithTransparencyStayPng() => Ui.Run(() =>
    {
        var path = Path.Combine(TempDir(), "logo.png");
        File.WriteAllBytes(path, Encode(new PngBitmapEncoder(), Photo(3000, 3000, transparentCentre: true)));
        var data = ImageUtil.PrepareForPlacement(path);
        Assert.Equal(0x89, data[0]);
        var img = ImageUtil.FromBytes(data);
        Assert.Equal(2400, Math.Max(img.PixelWidth, img.PixelHeight));
        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var centre = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(1200, 1200, 1, 1), centre, 4, 0);
        Assert.Equal(0, centre[3]); // still transparent
    });

    [Fact]
    public Task JpegEncodingPutsTransparentAreasOnWhite() => Ui.Run(() =>
    {
        var jpeg = ImageUtil.ToJpeg(Photo(400, 400, transparentCentre: true));
        var c = PixelAt(ImageUtil.FromBytes(jpeg), 200, 200);
        Assert.True(c.R > 240 && c.G > 240 && c.B > 240, $"centre is {c}, not white");
    });

    [Fact]
    public Task SignaturePreparationRemovesPaperAndTrims() => Ui.Run(() =>
    {
        // Dark strokes on an off-white "scan" with a wide margin.
        int w = 600, h = 300;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool ink = x > 200 && x < 400 && Math.Abs(y - 150 - 30 * Math.Sin(x / 20.0)) < 4;
                byte v = ink ? (byte)30 : (byte)235;
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
            }
        var result = ImageUtil.PrepareSignature(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4), removeBackground: true);
        Assert.InRange(result.PixelWidth, 200, 220);   // trimmed to the strokes plus a small margin
        Assert.InRange(result.PixelHeight, 60, 80);
        var bgra = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
        var corner = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), corner, 4, 0);
        Assert.Equal(0, corner[3]); // paper is transparent
    });
}
