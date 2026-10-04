using System.Globalization;
using System.Text.RegularExpressions;
using PdfEditor.Models;
using PdfSharp.Pdf;
using static PdfEditor.Services.PdfObjects;

namespace PdfEditor.Services;

/// <summary>Links a field model to the dictionaries it came from in a specific PdfDocument instance.</summary>
public sealed class FieldBinding
{
    public required FormFieldModel Model { get; init; }
    public required PdfDictionary FieldDict { get; init; }
    public List<(WidgetModel Widget, PdfDictionary Dict)> Widgets { get; } = new();
    public string DefaultAppearance { get; init; } = "";
}

/// <summary>Reads AcroForm fields (terminal fields and their widget annotations).</summary>
public static class FormReader
{
    private const int FfReadOnly = 1;
    private const int FfMultiline = 1 << 12;
    private const int FfPassword = 1 << 13;
    private const int FfRadio = 1 << 15;
    private const int FfPushButton = 1 << 16;
    private const int FfCombo = 1 << 17;
    private const int FfEdit = 1 << 18;
    private const int FfComb = 1 << 24;

    public static List<FieldBinding> Read(PdfDocument doc)
    {
        var result = new List<FieldBinding>();
        var acroForm = GetDict(doc.Internals.Catalog, "/AcroForm");
        if (acroForm == null || GetArray(acroForm, "/Fields") is not { } fields) return result;

        var pageOfAnnot = BuildAnnotPageMap(doc);
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in fields.Elements)
        {
            if (Resolve(item) is PdfDictionary f)
                Walk(f, "", acroForm, pageOfAnnot, visited, result);
        }
        return result;
    }

    private static Dictionary<PdfDictionary, int> BuildAnnotPageMap(PdfDocument doc)
    {
        var map = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (GetArray(doc.Pages[i], "/Annots") is not { } annots) continue;
            foreach (var a in annots.Elements)
                if (Resolve(a) is PdfDictionary d) map.TryAdd(d, i);
        }
        return map;
    }

    private static void Walk(PdfDictionary node, string parentName, PdfDictionary acroForm,
        Dictionary<PdfDictionary, int> pageOfAnnot, HashSet<PdfDictionary> visited, List<FieldBinding> result)
    {
        if (!visited.Add(node)) return; // guard against cyclic structures

        var partial = GetText(node.Elements["/T"]);
        string name = partial == null ? parentName : parentName.Length == 0 ? partial : parentName + "." + partial;

        var kids = GetArray(node, "/Kids")?.Elements
            .Select(k => Resolve(k) as PdfDictionary).Where(k => k != null).Cast<PdfDictionary>().ToList()
            ?? new List<PdfDictionary>();

        if (kids.Any(k => k.Elements.ContainsKey("/T")))
        {
            foreach (var kid in kids.Where(k => k.Elements.ContainsKey("/T")))
                Walk(kid, name, acroForm, pageOfAnnot, visited, result);
            return;
        }

        // Terminal field.
        var widgetDicts = new List<PdfDictionary>();
        if (node.Elements.ContainsKey("/Rect")) widgetDicts.Add(node);
        widgetDicts.AddRange(kids);

        string ft = GetText(GetInherited(node, "/FT")) ?? "";
        int ff = (int)(GetNumber(GetInherited(node, "/Ff")) ?? 0);
        string da = GetText(GetInherited(node, "/DA")) ?? GetText(Get(acroForm, "/DA")) ?? "/Helv 0 Tf 0 g";
        int q = (int)(GetNumber(GetInherited(node, "/Q")) ?? GetNumber(Get(acroForm, "/Q")) ?? 0);

        FieldKind kind = ft switch
        {
            "Tx" => FieldKind.Text,
            "Ch" => FieldKind.Choice,
            "Sig" => FieldKind.Signature,
            "Btn" when (ff & FfPushButton) != 0 => FieldKind.PushButton,
            "Btn" when (ff & FfRadio) != 0 => FieldKind.Radio,
            "Btn" => FieldKind.CheckBox,
            _ => FieldKind.PushButton, // unknown: treat as non-editable
        };

        var options = new List<(string, string)>();
        if (GetInherited(node, "/Opt") is PdfArray opt)
        {
            foreach (var o in opt.Elements)
            {
                if (Resolve(o) is PdfArray pair && pair.Elements.Count >= 2)
                    options.Add((GetText(pair.Elements[0]) ?? "", GetText(pair.Elements[1]) ?? ""));
                else if (GetText(o) is { } s)
                    options.Add((s, s));
            }
        }

        var model = new FormFieldModel
        {
            FullName = name,
            Kind = kind,
            ReadOnly = (ff & FfReadOnly) != 0,
            Multiline = (ff & FfMultiline) != 0,
            Password = (ff & FfPassword) != 0,
            Comb = (ff & FfComb) != 0,
            IsCombo = (ff & FfCombo) != 0,
            Editable = (ff & FfEdit) != 0,
            IsSigned = kind == FieldKind.Signature && GetInherited(node, "/V") is PdfDictionary,
            MaxLength = (int)(GetNumber(GetInherited(node, "/MaxLen")) ?? 0),
            FontSize = ParseFontSize(da),
            FontFamily = FontFamilyFor(da, acroForm),
            TextColor = ToColor(ParseColor(da)),
            Alignment = q,
            Options = options,
        };

        var binding = new FieldBinding { Model = model, FieldDict = node, DefaultAppearance = da };

        foreach (var w in widgetDicts)
        {
            if (GetRect(w) is not { } rect) continue;
            int page = pageOfAnnot.TryGetValue(w, out var p) ? p : -1;
            if (page < 0) continue; // widget not on any page
            int flags = (int)(GetNumber(Get(w, "/F")) ?? 0);
            var widget = new WidgetModel
            {
                Field = model,
                PageIndex = page,
                PdfRect = rect,
                OnState = GetOnState(w) ?? "Yes",
                Hidden = (flags & 2) != 0,
            };
            model.Widgets.Add(widget);
            binding.Widgets.Add((widget, w));
        }

        model.SetInitialValue(ReadValue(node, kind, binding));
        result.Add(binding);
    }

    private static string ReadValue(PdfDictionary node, FieldKind kind, FieldBinding binding)
    {
        var v = GetInherited(node, "/V");
        if (kind is FieldKind.CheckBox or FieldKind.Radio)
        {
            var name = GetText(v);
            if (!string.IsNullOrEmpty(name)) return name;
            // Fall back to the widget appearance state.
            foreach (var (_, w) in binding.Widgets)
            {
                var asState = GetText(Get(w, "/AS"));
                if (!string.IsNullOrEmpty(asState) && asState != "Off") return asState;
            }
            return "Off";
        }
        if (v is PdfArray arr) return arr.Elements.Count > 0 ? GetText(arr.Elements[0]) ?? "" : "";
        return GetText(v) ?? "";
    }

    private static string? GetOnState(PdfDictionary widget)
    {
        if (GetDict(widget, "/AP") is not { } ap) return null;
        foreach (var key in new[] { "/N", "/D" })
        {
            if (GetDict(ap, key) is { } states && states.Stream == null)
            {
                var on = states.Elements.Keys.FirstOrDefault(k => k != "/Off");
                if (on != null) return on.TrimStart('/');
            }
        }
        return null;
    }

    /// <summary>
    /// Picks an installed font for the /DA font resource, from its /DR BaseFont when available
    /// (otherwise the resource name, e.g. /Helv, /Cour, /TiRo). Anything unrecognised uses Arial.
    /// </summary>
    private static string FontFamilyFor(string da, PdfDictionary acroForm)
    {
        var m = Regex.Match(da, @"/([^\s/]+)\s+[\d.]+\s+Tf");
        if (!m.Success) return "Arial";
        string name = m.Groups[1].Value;
        if (GetDict(acroForm, "/DR") is { } dr && GetDict(dr, "/Font") is { } fonts && GetDict(fonts, "/" + name) is { } font
            && GetText(Get(font, "/BaseFont")) is { } baseFont)
            name = baseFont;
        if (name.Contains("Cour", StringComparison.OrdinalIgnoreCase)) return "Courier New";
        if (name.StartsWith("Ti", StringComparison.OrdinalIgnoreCase) || name.Contains("Times", StringComparison.OrdinalIgnoreCase)) return "Times New Roman";
        return "Arial";
    }

    private static System.Windows.Media.Color ToColor((double R, double G, double B) c) =>
        System.Windows.Media.Color.FromRgb((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));

    public static double ParseFontSize(string da)
    {
        var m = Regex.Match(da, @"([\d.]+)\s+Tf");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    /// <summary>Parses the fill colour from a /DA string (g, rg or k operators).</summary>
    public static (double R, double G, double B) ParseColor(string da)
    {
        static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 1) : 0;
        var rg = Regex.Match(da, @"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg");
        if (rg.Success) return (P(rg.Groups[1].Value), P(rg.Groups[2].Value), P(rg.Groups[3].Value));
        var k = Regex.Match(da, @"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+k");
        if (k.Success)
        {
            double c = P(k.Groups[1].Value), m = P(k.Groups[2].Value), y = P(k.Groups[3].Value), kk = P(k.Groups[4].Value);
            return ((1 - c) * (1 - kk), (1 - m) * (1 - kk), (1 - y) * (1 - kk));
        }
        var g = Regex.Match(da, @"([\d.]+)\s+g\b");
        if (g.Success) { var x = P(g.Groups[1].Value); return (x, x, x); }
        return (0, 0, 0);
    }
}
