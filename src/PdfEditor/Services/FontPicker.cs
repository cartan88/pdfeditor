using System.Windows.Media;

namespace PdfEditor.Services;

/// <summary>Picks an installed font that can display every character of a string.</summary>
public static class FontPicker
{
    private static readonly string[] Candidates =
    {
        "Arial", "Segoe UI", "Segoe UI Symbol", "Microsoft YaHei", "Microsoft JhengHei", "SimSun",
        "Yu Gothic", "Malgun Gothic", "Nirmala UI", "Leelawadee UI", "Segoe UI Emoji",
    };

    private static readonly Dictionary<string, GlyphTypeface?> Cache = new();

    public static string PickFor(string text, string preferred = "Arial")
    {
        if (Supports(preferred, text)) return preferred;
        foreach (var c in Candidates)
            if (Supports(c, text)) return c;
        return preferred;
    }

    public static bool Supports(string family, string text)
    {
        var gt = GetGlyphTypeface(family);
        if (gt == null) return false;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (char.IsSurrogatePair(text, i)) i++;
            if (cp < 0x20 || char.IsWhiteSpace((char)Math.Min(cp, 0xFFFF))) continue;
            if (!gt.CharacterToGlyphMap.ContainsKey(cp)) return false;
        }
        return true;
    }

    private static GlyphTypeface? GetGlyphTypeface(string family)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(family, out var gt)) return gt;
            var tf = new Typeface(new FontFamily(family), System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            gt = tf.TryGetGlyphTypeface(out var g) && g.FamilyNames.Values.Any(n => n.Equals(family, StringComparison.OrdinalIgnoreCase)) ? g : null;
            Cache[family] = gt;
            return gt;
        }
    }
}
