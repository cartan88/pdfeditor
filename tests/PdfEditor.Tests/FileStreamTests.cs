using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Media.Imaging;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>
/// "File streams": streams with an /F entry (data in an external file). PDFsharp can't read these at all
/// ("File streams are not yet implemented"), so such PDFs used to fail to open.
/// </summary>
public class FileStreamTests
{
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string BlueBox = "0 0 1 rg 100 600 200 100 re f";

    /// <summary>A one-page PDF built by hand (with a correct xref table) whose content stream has the given extra keys.</summary>
    private static byte[] Pdf(string streamDictExtras, string inFileData)
    {
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            $"<< /Length {inFileData.Length} {streamDictExtras} >>\nstream\n{inFileData}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string WritePdf(string folder, byte[] pdf)
    {
        var path = Path.Combine(folder, "doc.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    /// <summary>Is the page blue where the box is drawn (100,600)-(300,700) in PDF space?</summary>
    private static async Task<bool> ShowsBlueBox(byte[] pdf)
    {
        using var r = await PdfRenderer.LoadAsync(pdf, null);
        var bmp = await r.RenderAsync(0, 612); // 1 px per point
        var c = PixelAt(bmp, 200, 792 - 650);
        return c.B > 200 && c.R < 60 && c.G < 60;
    }

    private static async Task<(MainWindow Win, List<IReadOnlyList<string>> Asked)> Open(string path, bool answer)
    {
        var asked = new List<IReadOnlyList<string>>();
        var win = new MainWindow { ConfirmExternalFiles = files => { asked.Add(files); return answer; } };
        await win.OpenAsync(path);
        return (win, asked);
    }

    private static byte[]? Bytes(MainWindow win) => (byte[]?)typeof(MainWindow).GetField("_bytes", F)!.GetValue(win);
    private static string Status(MainWindow win) => ((System.Windows.Controls.TextBlock)win.FindName("StatusText")).Text;

    [Fact]
    public void PdfSharpAloneCannotReadAFileStream()
    {
        // The error users saw, for reference: PDFsharp rejects the document outright.
        var pdf = Pdf("/F (blue.txt)", "");
        var ex = Assert.Throws<NotImplementedException>(() =>
            PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify));
        Assert.Contains("File streams", ex.Message);
    }

    [Fact]
    public Task ExternalContentNextToThePdfIsEmbeddedWhenAllowed() => Ui.Run(async () =>
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "blue.txt"), BlueBox);
        var (win, asked) = await Open(WritePdf(dir, Pdf("/F (blue.txt)", "")), answer: true);

        var file = Assert.Single(Assert.Single(asked));
        Assert.Equal(Path.Combine(dir, "blue.txt"), file, ignoreCase: true);
        var bytes = Bytes(win)!;
        Assert.True(await ShowsBlueBox(bytes), "external content should be on the page");
        Assert.Contains("Embedded content from 1 external file", Status(win));
        // The working copy is an ordinary PDF: no file reference left, opens without any workaround.
        Assert.DoesNotContain("blue.txt", Encoding.ASCII.GetString(bytes));
        Assert.Empty(FileStreams.Find(PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(bytes), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify)));
    });

    [Fact]
    public Task DecliningKeepsTheDataStoredInThePdf() => Ui.Run(async () =>
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "other.txt"), "1 0 0 rg 0 0 612 792 re f"); // would paint the page red
        var (win, asked) = await Open(WritePdf(dir, Pdf("/F (other.txt)", BlueBox)), answer: false);

        Assert.Single(asked);
        Assert.True(await ShowsBlueBox(Bytes(win)!), "the PDF's own data should be used");
        Assert.Contains("weren't loaded", Status(win));
    });

    [Fact]
    public Task AStrayFEntryOnAnOrdinaryStreamIsIgnored() => Ui.Run(async () =>
    {
        // Some PDF tools write /F (e.g. annotation flags) into stream dictionaries by mistake.
        var dir = TempDir();
        var (win, asked) = await Open(WritePdf(dir, Pdf("/F 4", BlueBox)), answer: true);
        Assert.Empty(asked); // nothing to ask about: /F isn't a file specification
        Assert.True(await ShowsBlueBox(Bytes(win)!));
        Assert.DoesNotContain("external", Status(win));
    });

    [Fact]
    public Task FilesOutsideThePdfsFolderAreNeverRead() => Ui.Run(async () =>
    {
        var outer = TempDir();
        var dir = Directory.CreateDirectory(Path.Combine(outer, "docs")).FullName;
        File.WriteAllText(Path.Combine(outer, "secret.txt"), "SECRET-DATA-123");
        var absolute = Path.Combine(outer, "secret.txt").Replace('\\', '/');

        foreach (var spec in new[] { "(../secret.txt)", $"({absolute})", "(\\\\\\\\server\\\\share\\\\secret.txt)", "(file:///C:/secret.txt)",
                                     "<< /Type /Filespec /F (../secret.txt) /UF (../secret.txt) >>" })
        {
            var (win, asked) = await Open(WritePdf(dir, Pdf($"/F {spec}", BlueBox)), answer: true);
            Assert.Empty(asked);
            var bytes = Bytes(win)!;
            Assert.DoesNotContain("SECRET-DATA-123", Encoding.ASCII.GetString(bytes));
            Assert.True(await ShowsBlueBox(bytes), $"{spec}: falls back to the data in the PDF");
            typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false);
        }
    });

    [Fact]
    public Task FileSpecificationDictionariesAndSubfoldersWork() => Ui.Run(async () =>
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "parts"));
        File.WriteAllText(Path.Combine(dir, "parts", "blue.txt"), BlueBox);
        var (win, asked) = await Open(WritePdf(dir, Pdf("/F << /Type /Filespec /F (parts/blue.txt) >>", "")), answer: true);
        Assert.Single(asked);
        Assert.True(await ShowsBlueBox(Bytes(win)!));
    });

    [Fact]
    public Task CompressedExternalDataUsesFFilter() => Ui.Run(async () =>
    {
        var dir = TempDir();
        using (var fs = File.Create(Path.Combine(dir, "blue.z")))
        using (var z = new System.IO.Compression.ZLibStream(fs, System.IO.Compression.CompressionLevel.Optimal))
            z.Write(Encoding.ASCII.GetBytes(BlueBox));
        var (win, _) = await Open(WritePdf(dir, Pdf("/F (blue.z) /FFilter /FlateDecode", "")), answer: true);
        Assert.True(await ShowsBlueBox(Bytes(win)!));
    });

    [Fact]
    public Task TheDocumentCanBeEditedAndSavedAfterwards() => Ui.Run(async () =>
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "blue.txt"), BlueBox);
        var path = WritePdf(dir, Pdf("/F (blue.txt)", ""));
        var (win, _) = await Open(path, answer: true);
        var item = Item(StampKind.Text, 0, 300, 300, 80, 20, text: "Checked");
        typeof(MainWindow).GetMethod("Mutate", F)!.Invoke(win, new object[] { (Action)(() => typeof(MainWindow).GetMethod("AddStamp", F)!.Invoke(win, new object[] { item, false })) });
        var saved = Path.Combine(dir, "saved.pdf");
        Assert.True(await (Task<bool>)typeof(MainWindow).GetMethod("SaveAsync", F)!.Invoke(win, new object[] { saved, false })!);
        typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false);

        File.Delete(Path.Combine(dir, "blue.txt")); // the saved PDF no longer needs it
        var bytes = File.ReadAllBytes(saved);
        Assert.True(await ShowsBlueBox(bytes));
        Assert.Single(StampStore.Extract(bytes, null).Stamps);
    });

    [Fact]
    public void OnlyTheStreamsOwnFKeyIsRenamed()
    {
        // /Filter, a nested /F and a /F value must be left alone; the xref offsets must still work.
        var pdf = Pdf("/DecodeParms << /F 1 >> /Name /F /F (x.txt) /Filter /ASCIIHexDecode", "30 30 20 30 20 31 20 72 67>");
        var patched = FileStreams.Neutralize(pdf)!;
        var text = Encoding.ASCII.GetString(patched);
        Assert.Contains("/DecodeParms << /F 1 >> /Name /F /f (x.txt) /Filter /ASCIIHexDecode", text);
        Assert.Equal(pdf.Length, patched.Length);
        Assert.Single(FileStreams.Find(PdfOpen.Open(pdf, null)));
    }

    [Fact]
    public void OrdinaryPdfsAreUntouched()
    {
        Assert.Null(FileStreams.Neutralize(SampleForm()));
        Assert.Null(FileStreams.Neutralize(TextPages(2)));
        Assert.Empty(FileStreams.Find(PdfOpen.Open(SampleForm(), null)));
    }
}
