using System.Windows.Media;
using PdfEditor.Models;

namespace PdfEditor.Services;

/// <summary>
/// Layout rules for text in form fields, shared by the on-screen controls (FieldOverlay) and the saved
/// appearance streams (PdfSaver), so a value looks the same while typing and in the saved file.
/// All values are in points.
/// </summary>
public static class FieldText
{
    /// <summary>Space between the field edge and its text, on every side.</summary>
    public const double Padding = 2;
    public const double MinAutoSize = 4;
    public const double MaxAutoSize = 12;
    public const double DefaultMultilineSize = 10;

    public static string Family(FormFieldModel f, string text) => FontPicker.PickFor(text, f.FontFamily);

    /// <summary>What a field shows for a value: password fields are masked.</summary>
    public static string Display(FormFieldModel f, string value) => f.Password ? new string('*', value.Length) : value;

    /// <summary>Font size for a single-line field: the /DA size, or auto (fits the height, then shrinks to fit the width).</summary>
    public static double SingleLineSize(FormFieldModel f, string text, double width, double height)
    {
        if (f.FontSize > 0) return f.FontSize;
        double size = Math.Clamp(height * 0.7, MinAutoSize, MaxAutoSize);
        string family = Family(f, text);
        while (size > MinAutoSize && Width(text, family, size) > width - 2 * Padding) size -= 0.5;
        return size;
    }

    public static double MultilineSize(FormFieldModel f) => f.FontSize > 0 ? f.FontSize : DefaultMultilineSize;

    public static double CombSize(FormFieldModel f, double height) => f.FontSize > 0 ? f.FontSize : Math.Clamp(height * 0.65, MinAutoSize, MaxAutoSize);

    /// <summary>Width of one line from the font's advance widths (the same metrics PDFsharp writes into the file).</summary>
    public static double Width(string text, string family, double size)
    {
        var gt = FontPicker.GetGlyphTypeface(family);
        if (gt == null) return text.Length * size * 0.5;
        double em = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (char.IsSurrogatePair(text, i)) i++;
            em += gt.CharacterToGlyphMap.TryGetValue(cp, out var glyph) ? gt.AdvanceWidths[glyph] : 0.5;
        }
        return em * size;
    }

    /// <summary>
    /// Distance from the top of a box to the baseline of one vertically centred line, laid out the way WPF does
    /// (line box = the font's line spacing, centred; baseline at the font's baseline within it).
    /// </summary>
    public static double CenteredBaseline(string family, double size, double height)
    {
        var ff = new FontFamily(family);
        return (height - ff.LineSpacing * size) / 2 + ff.Baseline * size;
    }

    /// <summary>Distance from the top of a line box to its baseline.</summary>
    public static double Baseline(string family, double size) => new FontFamily(family).Baseline * size;

    /// <summary>
    /// Breaks multi-line text into lines that fit <paramref name="width"/>, like a wrapping WPF TextBox:
    /// at spaces, keeping trailing spaces on the line, and splitting words that are wider than the box.
    /// </summary>
    public static List<string> Wrap(string text, string family, double size, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = "";
            foreach (var token in System.Text.RegularExpressions.Regex.Split(paragraph, @"(?<= )"))
            {
                if (token.Length == 0) continue;
                if (line.Length > 0 && Width((line + token).TrimEnd(), family, size) > width)
                {
                    lines.Add(line);
                    line = "";
                }
                string word = token;
                while (line.Length == 0 && word.Length > 1 && Width(word.TrimEnd(), family, size) > width)
                {
                    int n = word.Length - 1;
                    while (n > 1 && Width(word[..n], family, size) > width) n--;
                    lines.Add(word[..n]);
                    word = word[n..];
                }
                line += word;
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Distance between baselines of consecutive lines.</summary>
    public static double LineHeight(string family, double size) => new FontFamily(family).LineSpacing * size;
}
