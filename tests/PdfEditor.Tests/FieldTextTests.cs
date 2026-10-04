using PdfEditor.Models;
using PdfEditor.Services;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>The shared layout rules used by both the on-screen field controls and the saved appearance streams.</summary>
public class FieldTextTests
{
    private static FormFieldModel Field(double fontSize = 0, bool password = false) =>
        new() { FullName = "f", Kind = FieldKind.Text, FontSize = fontSize, Password = password };

    [Fact]
    public void FixedFontSizeIsUsedAsIs()
    {
        Assert.Equal(9, FieldText.SingleLineSize(Field(9), "A very long value that does not fit at all", 50, 20));
    }

    [Fact]
    public void AutoSizeFitsTheHeightThenShrinksToFitTheWidth()
    {
        var f = Field();
        Assert.Equal(12, FieldText.SingleLineSize(f, "Short", 200, 40)); // capped at 12
        Assert.Equal(14 * 0.7, FieldText.SingleLineSize(f, "Short", 200, 14), 6);
        double size = FieldText.SingleLineSize(f, "A much longer value that cannot fit", 150, 20);
        Assert.True(size < 12);
        Assert.True(FieldText.Width("A much longer value that cannot fit", "Arial", size) <= 150 - 2 * FieldText.Padding);
        Assert.Equal(FieldText.MinAutoSize, FieldText.SingleLineSize(f, new string('W', 200), 30, 20));
    }

    [Fact]
    public void WidthScalesWithFontSize()
    {
        double w10 = FieldText.Width("Hello world", "Arial", 10), w20 = FieldText.Width("Hello world", "Arial", 20);
        Assert.True(w10 > 0);
        Assert.Equal(2 * w10, w20, 6);
        Assert.True(FieldText.Width("WWWW", "Arial", 10) > FieldText.Width("iiii", "Arial", 10));
    }

    [Fact]
    public void WrapsAtSpacesAndSplitsLongWords()
    {
        var lines = FieldText.Wrap("one two three four five six seven", "Arial", 10, 60);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(FieldText.Width(l.TrimEnd(), "Arial", 10) <= 60));
        Assert.Equal("one two three four five six seven", string.Concat(lines));

        var split = FieldText.Wrap("Supercalifragilisticexpialidocious", "Arial", 10, 40);
        Assert.True(split.Count > 2);
        Assert.Equal("Supercalifragilisticexpialidocious", string.Concat(split));
    }

    [Fact]
    public void WrapKeepsParagraphs()
    {
        var lines = FieldText.Wrap("First\r\n\nThird", "Arial", 10, 200);
        Assert.Equal(new[] { "First", "", "Third" }, lines);
    }

    [Fact]
    public void PasswordsAreMasked()
    {
        Assert.Equal("******", FieldText.Display(Field(password: true), "secret"));
        Assert.Equal("secret", FieldText.Display(Field(), "secret"));
    }

    [Fact]
    public void CentredBaselineSitsInTheMiddleOfTheBox()
    {
        double baseline = FieldText.CenteredBaseline("Arial", 10, 20);
        Assert.InRange(baseline, 10, 15); // below the middle, by about half the cap height
        Assert.Equal(FieldText.CenteredBaseline("Arial", 10, 40) - 10, baseline, 6);
    }
}
