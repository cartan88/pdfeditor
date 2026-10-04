using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>Placed items survive save and reopen as editable objects.</summary>
public class StampStoreTests
{
    private static (byte[] Original, List<FormFieldModel> Fields, List<StampModel> Stamps, byte[] Jpeg) Setup()
    {
        var original = SampleForm();
        var png = SignaturePng();
        var jpeg = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, new FormatConvertedBitmap(Photo(400, 300), PixelFormats.Bgr24, null, 0));
        var stamps = new List<StampModel>
        {
            Item(StampKind.Text, 0, 300, 120, 150, 40, angle: 30, text: "Approved\nby J. Smith", size: 14, color: Color.FromRgb(0xC0, 0x1C, 0x1C)),
            Item(StampKind.Text, 0, 90, 300, 14, 18, text: "✓", size: 14, font: "Segoe UI Symbol"),
            Item(StampKind.Whiteout, 0, 420, 560, 120, 24),
            Item(StampKind.Image, 0, 330, 640, 160, 60, angle: 270, data: png),
            Item(StampKind.Image, 0, 470, 720, 120, 90, data: jpeg),
            Item(StampKind.Image, 1, 300, 200, 120, 90, angle: 15, data: jpeg), // same JPEG on the rotated second page
            Item(StampKind.Text, 1, 200, 400, 80, 20, text: "Page two", color: Color.FromRgb(0x1A, 0x3A, 0x9C)),
        };
        return (original, Fields(original), stamps, jpeg);
    }

    private static void AssertSame(StampModel expected, StampModel actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.PageIndex, actual.PageIndex);
        Assert.Equal(expected.CenterX, actual.CenterX, 6);
        Assert.Equal(expected.CenterY, actual.CenterY, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
        Assert.Equal(expected.Angle, actual.Angle, 6);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.FontSize, actual.FontSize);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.FontFamily, actual.FontFamily);
        Assert.Equal(expected.ImageData, actual.ImageData);
    }

    [Fact]
    public void EveryKindOfItemComesBackIdentical()
    {
        var (original, fields, stamps, _) = Setup();
        var result = StampStore.Extract(PdfSaver.Build(original, null, fields, stamps, flatten: false), null);
        Assert.Equal(2, result.PagesRestored);
        Assert.Equal(0, result.PagesSkipped);
        Assert.Equal(stamps.Count, result.Stamps.Count);
        for (int i = 0; i < stamps.Count; i++) AssertSame(stamps[i], result.Stamps[i]);
    }

    [Fact]
    public Task CleanedPagesRenderExactlyLikeTheOriginal() => Ui.Run(async () =>
    {
        var (original, fields, stamps, _) = Setup();
        var saved = PdfSaver.Build(original, null, fields, stamps, flatten: false);
        var cleaned = StampStore.Extract(saved, null).Bytes;
        for (int page = 0; page < 2; page++)
        {
            var orig = await RenderPage(original, page);
            Assert.True(MeanDiff(orig, await RenderPage(cleaned, page)) < 0.05, $"page {page + 1} still shows drawn items after cleaning");
            Assert.True(MeanDiff(orig, await RenderPage(saved, page)) > 0.5, $"page {page + 1}: saved file doesn't show the items");
        }
    });

    [Fact]
    public void JpegsAreStoredOnceAndFilesDontGrowOverCycles()
    {
        var (original, fields, stamps, jpeg) = Setup();
        var saved = PdfSaver.Build(original, null, fields, stamps, flatten: false);
        Assert.Equal(1, Count(saved, jpeg.Take(256).ToArray()));

        var sizes = new List<int> { saved.Length };
        var current = saved;
        for (int i = 0; i < 3; i++)
        {
            var r = StampStore.Extract(current, null);
            current = PdfSaver.Build(r.Bytes, null, fields, r.Stamps, flatten: false);
            sizes.Add(current.Length);
        }
        Assert.True(sizes.Max() - sizes.Min() < 2048, "sizes: " + string.Join(", ", sizes));
        var final = StampStore.Extract(current, null).Stamps;
        for (int i = 0; i < stamps.Count; i++) AssertSame(stamps[i], final[i]);
    }

    [Fact]
    public void FlattenedCopiesKeepItemsPermanent()
    {
        var (original, fields, stamps, _) = Setup();
        var flat = PdfSaver.Build(original, null, fields, stamps, flatten: true);
        var result = StampStore.Extract(flat, null);
        Assert.Empty(result.Stamps);
        Assert.Same(flat, result.Bytes);
    }

    [Fact]
    public Task PagesRewrittenElsewhereAreLeftAlone() => Ui.Run(async () =>
    {
        var (original, fields, stamps, _) = Setup();
        var doc = PdfOpen.Open(PdfSaver.Build(original, null, fields, stamps, flatten: false), null);
        // Simulate another program rewriting page 1: one of the recorded content streams disappears.
        var recorded = (PdfReference)doc.Pages[0].Elements.GetDictionary("/PdfEditorStamps")!.Elements.GetArray("/Contents")!.Elements[^1];
        var contents = doc.Pages[0].Contents.Elements;
        for (int k = contents.Count - 1; k >= 0; k--)
            if (contents[k] is PdfReference r && r.ObjectNumber == recorded.ObjectNumber) contents.RemoveAt(k);
        var tampered = Save(doc);

        var result = StampStore.Extract(tampered, null);
        Assert.Equal(1, result.PagesSkipped);
        Assert.Equal(1, result.PagesRestored);
        Assert.All(result.Stamps, s => Assert.Equal(1, s.PageIndex));
        Assert.True(MeanDiff(await RenderPage(tampered, 0), await RenderPage(result.Bytes, 0)) < 0.05, "skipped page changed");
    });

    [Fact]
    public void ItemsFollowACropBoxChange()
    {
        var (original, fields, stamps, _) = Setup();
        var doc = PdfOpen.Open(PdfSaver.Build(original, null, fields, stamps, flatten: false), null);
        var crop = PdfOpen.ReadGeometry(doc)[0].CropBox;
        doc.Pages[0].Elements["/CropBox"] = new PdfArray(doc, new PdfReal(crop.Left + 50), new PdfReal(crop.Top + 50), new PdfReal(crop.Right), new PdfReal(crop.Bottom));
        var item = StampStore.Extract(Save(doc), null).Stamps.First(s => s.Text.StartsWith("Approved"));
        Assert.Equal(300 - 50, item.CenterX, 6); // the view's left edge moved 50 pt right
        Assert.Equal(120, item.CenterY, 6);      // the top edge didn't move
    }

    [Fact]
    public void FilesWithoutRecordsAreReturnedUnchanged()
    {
        var original = SampleForm();
        var result = StampStore.Extract(original, null);
        Assert.Same(original, result.Bytes);
        Assert.Empty(result.Stamps);
    }
}
