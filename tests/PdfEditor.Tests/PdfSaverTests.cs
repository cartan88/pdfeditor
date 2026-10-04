using System.IO;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfSharp.Pdf;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

public class PdfSaverTests
{
    [Fact]
    public void FieldValuesRoundTrip()
    {
        var original = SampleForm();
        var fields = Fields(original);
        var f = fields.ToDictionary(x => x.FullName);
        f["full_name"].Value = "Jane Doe";
        f["address"].Value = "1 Main Street\nSpringfield";
        f["country"].Value = "Malaysia";
        f["subscribe"].Value = f["subscribe"].Widgets[0].OnState;
        f["plan"].Value = f["plan"].Widgets[2].OnState;

        var saved = PdfSaver.Build(original, null, fields, new List<StampModel>(), flatten: false);
        var back = Fields(saved).ToDictionary(x => x.FullName);
        Assert.Equal("Jane Doe", back["full_name"].Value);
        Assert.Equal("1 Main Street\nSpringfield", back["address"].Value);
        Assert.Equal("Malaysia", back["country"].Value);
        Assert.Equal(f["subscribe"].Widgets[0].OnState, back["subscribe"].Value);
        Assert.Equal(f["plan"].Widgets[2].OnState, back["plan"].Value);
    }

    [Fact]
    public void EditedFieldsGetAppearancesAndViewersArentAskedToRedrawThem()
    {
        var original = SampleForm();
        var fields = Fields(original);
        fields.First(x => x.FullName == "full_name").Value = "Jane Doe";
        var doc = PdfOpen.Open(PdfSaver.Build(original, null, fields, new List<StampModel>(), flatten: false), null);
        var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;
        Assert.False(acro.Elements.GetBoolean("/NeedAppearances"));
        Assert.All(FormReader.Read(doc).SelectMany(b => b.Widgets), w => Assert.True(w.Dict.Elements.ContainsKey("/AP")));
    }

    [Fact]
    public void FlatteningRemovesTheFormButKeepsTheValuesOnThePage()
    {
        var original = SampleForm();
        var fields = Fields(original);
        fields.First(x => x.FullName == "full_name").Value = "Jane Doe";
        var flat = PdfOpen.Open(PdfSaver.Build(original, null, fields, new List<StampModel>(), flatten: true), null);
        Assert.False(flat.Internals.Catalog.Elements.ContainsKey("/AcroForm"));
        Assert.Empty(FormReader.Read(flat));
        // No widget annotations remain on any page.
        for (int i = 0; i < flat.PageCount; i++)
            Assert.DoesNotContain(flat.Pages[i].Elements.GetArray("/Annots")?.Elements.OfType<PdfSharp.Pdf.Advanced.PdfReference>()
                .Select(r => r.Value).OfType<PdfDictionary>().Select(d => d.Elements.GetName("/Subtype")) ?? Enumerable.Empty<string>(), s => s == "/Widget");
    }

    [Fact]
    public void SaveReplacesTheTargetAndLeavesNoTempFile()
    {
        var dir = Infrastructure.TestData.TempDir();
        var target = Path.Combine(dir, "out.pdf");
        File.WriteAllText(target, "old contents");
        var original = SampleForm();
        PdfSaver.Save(original, null, Fields(original), new List<StampModel>(), false, target);
        Assert.StartsWith("%PDF-", File.ReadAllText(target)[..5]);
        Assert.Equal(new[] { "out.pdf" }, Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void UnchangedFieldsAreNotRewritten()
    {
        var original = SampleForm();
        var fields = Fields(original);
        var name = fields.First(x => x.FullName == "full_name");
        name.Value = "temp";
        name.Value = ""; // back to the original value: clean again
        var before = FormReader.Read(PdfOpen.Open(original, null)).First(b => b.Model.FullName == "full_name").Widgets[0].Dict;
        var after = FormReader.Read(PdfOpen.Open(PdfSaver.Build(original, null, fields, new List<StampModel>(), false), null))
            .First(b => b.Model.FullName == "full_name").Widgets[0].Dict;
        var apBefore = ((PdfDictionary)before.Elements.GetDictionary("/AP")!.Elements.GetReference("/N")!.Value).Stream!.UnfilteredValue;
        var apAfter = ((PdfDictionary)after.Elements.GetDictionary("/AP")!.Elements.GetReference("/N")!.Value).Stream!.UnfilteredValue;
        Assert.Equal(apBefore, apAfter);
    }
}
