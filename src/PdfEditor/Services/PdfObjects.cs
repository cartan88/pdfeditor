using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Services;

/// <summary>Small helpers for reading the low-level PDFsharp object model.</summary>
internal static class PdfObjects
{
    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static PdfItem? Get(PdfDictionary dict, string key) => Resolve(dict.Elements[key]);

    public static PdfDictionary? GetDict(PdfDictionary dict, string key) => Get(dict, key) as PdfDictionary;

    public static PdfArray? GetArray(PdfDictionary dict, string key) => Get(dict, key) as PdfArray;

    public static string? GetText(PdfItem? item) => Resolve(item) switch
    {
        PdfString s => s.Value,
        PdfName n => n.Value.TrimStart('/'),
        PdfInteger i => i.Value.ToString(),
        PdfReal r => r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    public static double? GetNumber(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        _ => null,
    };

    /// <summary>Returns the rectangle stored under /Rect as (llx, lly, urx, ury), normalised.</summary>
    public static System.Windows.Rect? GetRect(PdfDictionary dict, string key = "/Rect")
    {
        if (GetArray(dict, key) is not { Elements.Count: 4 } arr) return null;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            var n = GetNumber(arr.Elements[i]);
            if (n == null) return null;
            v[i] = n.Value;
        }
        double x1 = Math.Min(v[0], v[2]), x2 = Math.Max(v[0], v[2]);
        double y1 = Math.Min(v[1], v[3]), y2 = Math.Max(v[1], v[3]);
        return new System.Windows.Rect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Walks up the /Parent chain looking for an inheritable field attribute.</summary>
    public static PdfItem? GetInherited(PdfDictionary dict, string key)
    {
        var d = dict;
        for (int depth = 0; d != null && depth < 32; depth++)
        {
            if (d.Elements.ContainsKey(key)) return Resolve(d.Elements[key]);
            d = GetDict(d, "/Parent");
        }
        return null;
    }

    public static PdfReference? RefOf(PdfObject obj) => obj.Reference;
}
