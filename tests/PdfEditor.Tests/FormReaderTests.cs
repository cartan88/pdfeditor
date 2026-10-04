using System.Windows.Media;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using PdfSharp.Pdf;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

public class FormReaderTests
{
    [Fact]
    public void ReadsTheSampleForm()
    {
        var fields = Fields(SampleForm()).ToDictionary(f => f.FullName);
        Assert.Equal(new[] { "full_name", "dob", "address", "country", "subscribe", "plan", "witness_name" }.OrderBy(n => n), fields.Keys.OrderBy(n => n));
        Assert.Equal(FieldKind.Text, fields["full_name"].Kind);
        Assert.True(fields["address"].Multiline);
        Assert.Equal(FieldKind.Choice, fields["country"].Kind);
        Assert.Equal("Singapore", fields["country"].Value);
        Assert.Equal(4, fields["country"].Options.Count);
        Assert.Equal(FieldKind.CheckBox, fields["subscribe"].Kind);
        Assert.Equal("Off", fields["subscribe"].Value);
        Assert.Equal(FieldKind.Radio, fields["plan"].Kind);
        Assert.Equal(3, fields["plan"].Widgets.Count);
        Assert.Equal("basic", fields["plan"].Value);
        Assert.Equal(1, fields["witness_name"].Widgets[0].PageIndex);
        Assert.All(fields.Values, f => Assert.False(f.IsDirty));
    }

    [Theory]
    [InlineData("/Helv 11 Tf 0 g", 11)]
    [InlineData("/Helv 0 Tf 0 g", 0)]
    [InlineData("0 g /TiRo 9.5 Tf", 9.5)]
    [InlineData("", 0)]
    public void ParsesFontSize(string da, double size) => Assert.Equal(size, FormReader.ParseFontSize(da));

    [Fact]
    public void ParsesColourOperators()
    {
        Assert.Equal((1.0, 0.0, 0.0), FormReader.ParseColor("/Helv 11 Tf 1 0 0 rg"));
        Assert.Equal((0.5, 0.5, 0.5), FormReader.ParseColor("/Helv 11 Tf .5 g"));
        var (r, g, b) = FormReader.ParseColor("/Helv 11 Tf 0 1 1 0 k"); // CMYK magenta + yellow = red
        Assert.Equal((1.0, 0.0, 0.0), (r, g, b));
        Assert.Equal((0.0, 0.0, 0.0), FormReader.ParseColor("/Helv 11 Tf"));
    }

    [Fact]
    public void ReadsFieldFontAndColourFromDefaultAppearance()
    {
        var pdf = Form(new[]
        {
            new FieldSpec("red", 100, 700, 200, 20, "/Helv 11 Tf 1 0 0 rg"),
            new FieldSpec("courier", 100, 650, 200, 20, "/Cour 10 Tf 0 g"),
            new FieldSpec("times", 100, 600, 200, 20, "/TiRo 10 Tf 0 g"),
            new FieldSpec("unknown", 100, 550, 200, 20, "/F9 10 Tf 0 g"),
        });
        var f = Fields(pdf).ToDictionary(x => x.FullName);
        Assert.Equal(Color.FromRgb(255, 0, 0), f["red"].TextColor);
        Assert.Equal("Arial", f["red"].FontFamily);
        Assert.Equal("Courier New", f["courier"].FontFamily);
        Assert.Equal("Times New Roman", f["times"].FontFamily);
        Assert.Equal("Arial", f["unknown"].FontFamily);
    }

    [Fact]
    public void ReadsFieldFlags()
    {
        var pdf = Form(new[]
        {
            new FieldSpec("comb", 100, 700, 120, 20, Ff: 1 << 24, MaxLen: 6),
            new FieldSpec("password", 100, 650, 120, 20, Ff: 1 << 13),
            new FieldSpec("readonly", 100, 600, 120, 20, Ff: 1),
            new FieldSpec("right", 100, 550, 120, 20, Q: 2),
        });
        var f = Fields(pdf).ToDictionary(x => x.FullName);
        Assert.True(f["comb"].Comb);
        Assert.Equal(6, f["comb"].MaxLength);
        Assert.True(f["password"].Password);
        Assert.True(f["readonly"].ReadOnly);
        Assert.Equal(2, f["right"].Alignment);
    }

    [Fact]
    public void DetectsDigitallySignedFields()
    {
        var unsigned = Form(new[] { new FieldSpec("sig", 100, 700, 200, 40, Ft: "/Sig") });
        Assert.False(Fields(unsigned).Single().IsSigned);

        var signed = Form(new[] { new FieldSpec("sig", 100, 700, 200, 40, Ft: "/Sig") }, configure: doc =>
        {
            var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;
            var field = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)acro.Elements.GetArray("/Fields")!.Elements[0]).Value;
            field.Elements["/V"] = new PdfDictionary(doc); // a signature value dictionary
        });
        var model = Fields(signed).Single();
        Assert.Equal(FieldKind.Signature, model.Kind);
        Assert.True(model.IsSigned);
    }

    [Fact]
    public void FieldIsCleanAgainWhenSetBackToItsOriginalValue()
    {
        var name = Fields(SampleForm()).First(f => f.FullName == "full_name");
        name.Value = "Jane";
        Assert.True(name.IsDirty);
        name.Value = "";
        Assert.False(name.IsDirty);
    }
}
