using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using PdfEditor.Models;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfEditor.Services.PdfObjects;

namespace PdfEditor.Services;

/// <summary>Writes form values and placed stamps into a fresh copy of the original document.</summary>
public static class PdfSaver
{
    /// <summary>Line height used for text stamps, as a multiple of the font size (kept in sync with the editor).</summary>
    public const double LineSpacing = 1.2;
    public const double TextPadding = 1.0;

    private static readonly PropertyInfo? XFormPdfForm =
        typeof(XForm).GetProperty("PdfForm", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Save(byte[] original, string? password, IReadOnlyList<FormFieldModel> fields,
        IReadOnlyList<StampModel> stamps, bool flatten, string outputPath)
    {
        var bytes = Build(original, password, fields, stamps, flatten);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            // A move within the same folder replaces the target atomically, so an interrupted save never leaves a truncated file.
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    /// <summary>Produces the edited document in memory (used for saving and printing).</summary>
    public static byte[] Build(byte[] original, string? password, IReadOnlyList<FormFieldModel> fields,
        IReadOnlyList<StampModel> stamps, bool flatten)
    {
        var doc = PdfOpen.Open(original, password);
        var geometry = PdfOpen.ReadGeometry(doc);
        var bindings = FormReader.Read(doc);

        // The fresh document is read with the same traversal, so fields line up by position.
        if (bindings.Count == fields.Count)
        {
            for (int i = 0; i < bindings.Count; i++)
            {
                if (bindings[i].Model.FullName != fields[i].FullName) continue;
                var source = fields[i];
                if (source.IsDirty || (flatten && source.Kind is FieldKind.Text or FieldKind.Choice && NeedsAppearance(bindings[i])))
                    ApplyField(doc, bindings[i], source.Value);
            }
        }

        var wrapped = new HashSet<int>();
        if (flatten && bindings.Count > 0)
        {
            Flatten(doc, bindings, wrapped);
        }
        else if (bindings.Count > 0 && GetDict(doc.Internals.Catalog, "/AcroForm") is { } acro)
        {
            // Ask viewers to refresh field appearances so they look native in every reader.
            acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
        }

        foreach (var group in stamps.GroupBy(s => s.PageIndex))
        {
            if (group.Key < 0 || group.Key >= doc.PageCount) continue;
            var page = doc.Pages[group.Key];
            if (wrapped.Add(group.Key)) WrapExistingContent(page);
            DrawStamps(page, geometry[group.Key], group);
        }

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static bool NeedsAppearance(FieldBinding b) =>
        b.Widgets.Any(w => GetDict(w.Dict, "/AP") is not { } ap || GetDict(ap, "/N") == null);

    // ---------------------------------------------------------------- form values

    private static void ApplyField(PdfDocument doc, FieldBinding b, string value)
    {
        var field = b.FieldDict;
        var model = b.Model;
        switch (model.Kind)
        {
            case FieldKind.CheckBox:
            case FieldKind.Radio:
            {
                string state = string.IsNullOrEmpty(value) ? "Off" : value;
                field.Elements["/V"] = new PdfName("/" + state);
                foreach (var (widget, dict) in b.Widgets)
                    dict.Elements["/AS"] = new PdfName("/" + (widget.OnState == state ? state : "Off"));
                break;
            }
            case FieldKind.Text:
            case FieldKind.Choice:
            {
                field.Elements["/V"] = MakeString(value);
                if (model.Kind == FieldKind.Choice)
                {
                    int idx = model.Options.FindIndex(o => o.Export == value);
                    if (idx >= 0)
                    {
                        var arr = new PdfArray(doc);
                        arr.Elements.Add(new PdfInteger(idx));
                        field.Elements["/I"] = arr;
                    }
                    else field.Elements.Remove("/I");
                }
                string display = value;
                if (model.Kind == FieldKind.Choice)
                {
                    var opt = model.Options.FirstOrDefault(o => o.Export == value);
                    if (opt.Display != null) display = opt.Display;
                }
                foreach (var (widget, dict) in b.Widgets)
                {
                    try { BuildTextAppearance(doc, b, dict, display); }
                    catch { dict.Elements.Remove("/AP"); } // viewer will regenerate via NeedAppearances
                }
                break;
            }
        }
    }

    private static PdfString MakeString(string value)
    {
        bool ascii = value.All(c => c < 0x7F);
        return ascii ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);
    }

    private static void BuildTextAppearance(PdfDocument doc, FieldBinding b, PdfDictionary widget, string text)
    {
        if (XFormPdfForm == null || GetRect(widget) is not { } rect) return;
        var model = b.Model;
        var mk = GetDict(widget, "/MK");
        int r = (((int)(mk != null ? GetNumber(Get(mk, "/R")) ?? 0 : 0) % 360) + 360) % 360;
        double w = r % 180 == 0 ? rect.Width : rect.Height;
        double h = r % 180 == 0 ? rect.Height : rect.Width;
        if (w <= 0 || h <= 0) return;

        var form = new XForm(doc, new XRect(0, 0, w, h));
        using (var g = XGraphics.FromForm(form))
        {
            if (mk != null && ReadColor(Get(mk, "/BG")) is { } bg)
                g.DrawRectangle(new XSolidBrush(bg), 0, 0, w, h);
            if (mk != null && ReadColor(Get(mk, "/BC")) is { } bc)
            {
                double bw = GetDict(widget, "/BS") is { } bs ? GetNumber(Get(bs, "/W")) ?? 1 : 1;
                if (bw > 0) g.DrawRectangle(new XPen(bc, bw), bw / 2, bw / 2, w - bw, h - bw);
            }

            if (model.Password) text = new string('*', text.Length);
            if (text.Length > 0)
            {
                var (cr, cg, cb) = FormReader.ParseColor(b.DefaultAppearance);
                var brush = new XSolidBrush(XColor.FromArgb(255, (int)(cr * 255), (int)(cg * 255), (int)(cb * 255)));
                string family = FontPicker.PickFor(text);
                var align = model.Alignment switch { 1 => XStringAlignment.Center, 2 => XStringAlignment.Far, _ => XStringAlignment.Near };
                double pad = 2;

                if (model.Comb && model.MaxLength > 0 && !model.Multiline)
                {
                    double fs = model.FontSize > 0 ? model.FontSize : Math.Clamp(h * 0.65, 4, 12);
                    var font = MakeFont(family, fs);
                    double cell = w / model.MaxLength;
                    var fmt = new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.Center };
                    for (int i = 0; i < Math.Min(text.Length, model.MaxLength); i++)
                        g.DrawString(text[i].ToString(), font, brush, new XRect(i * cell, 0, cell, h), fmt);
                }
                else if (model.Multiline)
                {
                    double fs = model.FontSize > 0 ? model.FontSize : 10;
                    var tf = new XTextFormatter(g)
                    {
                        Alignment = model.Alignment switch { 1 => XParagraphAlignment.Center, 2 => XParagraphAlignment.Right, _ => XParagraphAlignment.Left },
                    };
                    tf.DrawString(text.Replace("\r\n", "\n"), MakeFont(family, fs), brush,
                        new XRect(pad, pad, Math.Max(1, w - 2 * pad), Math.Max(1, h - 2 * pad)), XStringFormats.TopLeft);
                }
                else
                {
                    double fs = model.FontSize;
                    if (fs <= 0)
                    {
                        fs = Math.Clamp(h * 0.7, 4, 12);
                        while (fs > 4 && g.MeasureString(text, MakeFont(family, fs)).Width > w - 2 * pad) fs -= 0.5;
                    }
                    var fmt = new XStringFormat { Alignment = align, LineAlignment = XLineAlignment.Center };
                    g.DrawString(text, MakeFont(family, fs), brush, new XRect(pad, 0, Math.Max(1, w - 2 * pad), h), fmt);
                }
            }
        }

        form.DrawingFinished();
        if (XFormPdfForm.GetValue(form) is not PdfDictionary pdfForm) return;
        // PDFsharp writes these only when saving; set them now so flattening can use the form.
        var bboxArr = new PdfArray(doc);
        foreach (var v in new[] { 0, 0, w, h }) bboxArr.Elements.Add(new PdfReal(v));
        pdfForm.Elements["/BBox"] = bboxArr;
        pdfForm.Elements["/Subtype"] = new PdfName("/Form");
        if (r != 0)
        {
            double rad = r * Math.PI / 180;
            var m = new PdfArray(doc);
            foreach (var v in new[] { Math.Round(Math.Cos(rad)), Math.Round(Math.Sin(rad)), -Math.Round(Math.Sin(rad)), Math.Round(Math.Cos(rad)), 0, 0 })
                m.Elements.Add(new PdfReal(v));
            pdfForm.Elements["/Matrix"] = m;
        }
        if (pdfForm.Reference == null) doc.Internals.AddObject(pdfForm);
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = pdfForm.Reference;
        widget.Elements["/AP"] = ap;
    }

    private static XFont MakeFont(string family, double size) =>
        new(family, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    private static XColor? ReadColor(PdfItem? item)
    {
        if (Resolve(item) is not PdfArray a) return null;
        var v = a.Elements.Select(e => GetNumber(e) ?? 0).ToArray();
        static int B(double x) => (int)Math.Round(Math.Clamp(x, 0, 1) * 255);
        return v.Length switch
        {
            1 => XColor.FromArgb(255, B(v[0]), B(v[0]), B(v[0])),
            3 => XColor.FromArgb(255, B(v[0]), B(v[1]), B(v[2])),
            4 => XColor.FromArgb(255, B((1 - v[0]) * (1 - v[3])), B((1 - v[1]) * (1 - v[3])), B((1 - v[2]) * (1 - v[3]))),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- flattening

    private static void Flatten(PdfDocument doc, List<FieldBinding> bindings, HashSet<int> wrapped)
    {
        var widgetsByPage = bindings.SelectMany(b => b.Widgets).GroupBy(w => w.Widget.PageIndex);
        var allWidgets = new HashSet<PdfDictionary>(bindings.SelectMany(b => b.Widgets.Select(w => w.Dict)), ReferenceEqualityComparer.Instance);
        int counter = 0;

        foreach (var group in widgetsByPage)
        {
            var page = doc.Pages[group.Key];
            var ops = new StringBuilder();
            var resources = page.Resources;
            // Let PDFsharp create/convert the typed resource map so later XGraphics drawing shares it.
            var xobjects = (PdfDictionary)resources.Elements.GetValue("/XObject", VCF.Create)!;

            foreach (var (widget, dict) in group)
            {
                if (widget.Hidden || GetDict(dict, "/AP") is not { } ap) continue;
                var normal = GetDict(ap, "/N");
                if (normal != null && !IsFormXObject(normal))
                {
                    var state = GetText(Get(dict, "/AS")) ?? "Off";
                    normal = GetDict(normal, "/" + state);
                }
                if (normal == null || !IsFormXObject(normal)) continue;

                var bbox = GetRect(normal, "/BBox");
                if (bbox is not { Width: > 0, Height: > 0 } box) continue;
                var fm = ReadMatrix(normal);
                var tb = TransformBounds(box, fm);
                var rect = widget.PdfRect;
                if (tb.Width <= 0 || tb.Height <= 0) continue;
                double sx = rect.Width / tb.Width, sy = rect.Height / tb.Height;
                double ex = rect.X - tb.X * sx, ey = rect.Y - tb.Y * sy;

                if (!normal.Elements.ContainsKey("/Subtype")) normal.Elements["/Subtype"] = new PdfName("/Form");
                if (!normal.Elements.ContainsKey("/Type")) normal.Elements["/Type"] = new PdfName("/XObject");
                if (normal.Reference == null) doc.Internals.AddObject(normal);

                string name = $"/PdfEdFlat{group.Key}_{counter++}";
                xobjects.Elements[name] = normal.Reference;
                ops.Append(CultureInfo.InvariantCulture, $"q {N(sx)} 0 0 {N(sy)} {N(ex)} {N(ey)} cm {name} Do Q\n");
            }

            if (wrapped.Add(group.Key)) WrapExistingContent(page);
            if (ops.Length > 0) AppendContent(page, ops.ToString());
        }

        // Remove the widget annotations and the interactive form itself.
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (GetArray(doc.Pages[i], "/Annots") is not { } annots) continue;
            for (int k = annots.Elements.Count - 1; k >= 0; k--)
                if (Resolve(annots.Elements[k]) is PdfDictionary d && allWidgets.Contains(d))
                    annots.Elements.RemoveAt(k);
        }
        doc.Internals.Catalog.Elements.Remove("/AcroForm");
    }

    // PDFsharp fills the stream of forms it generated only when saving, so also accept a /BBox.
    private static bool IsFormXObject(PdfDictionary d) => d.Stream != null || d.Elements.ContainsKey("/BBox");

    private static System.Windows.Media.Matrix ReadMatrix(PdfDictionary form)
    {
        if (GetArray(form, "/Matrix") is { Elements.Count: 6 } a)
        {
            var v = a.Elements.Select(e => GetNumber(e) ?? 0).ToArray();
            return new System.Windows.Media.Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
        }
        return System.Windows.Media.Matrix.Identity;
    }

    private static System.Windows.Rect TransformBounds(System.Windows.Rect r, System.Windows.Media.Matrix m)
    {
        var pts = new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight }.Select(m.Transform).ToArray();
        double x1 = pts.Min(p => p.X), x2 = pts.Max(p => p.X), y1 = pts.Min(p => p.Y), y2 = pts.Max(p => p.Y);
        return new System.Windows.Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Wraps the page's existing content in q/Q so anything appended starts from a clean graphics state.</summary>
    private static void WrapExistingContent(PdfPage page)
    {
        if (page.Contents.Elements.Count == 0) return;
        var pre = page.Contents.PrependContent();
        SetStream(pre, "q\n");
        AppendContent(page, "Q\n");
    }

    private static void AppendContent(PdfPage page, string ops)
    {
        var c = page.Contents.AppendContent();
        SetStream(c, ops);
    }

    private static void SetStream(PdfDictionary d, string ops)
    {
        var bytes = Encoding.ASCII.GetBytes(ops);
        if (d.Stream == null) d.CreateStream(bytes);
        else d.Stream.Value = bytes;
    }

    // ---------------------------------------------------------------- stamps

    private static void DrawStamps(PdfPage page, PageGeometry geo, IEnumerable<StampModel> stamps)
    {
        using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var toPage = geo.ViewToXGraphics;
        foreach (var s in stamps)
        {
            var m = System.Windows.Media.Matrix.Identity;
            m.Rotate(s.Angle);
            m.Translate(s.CenterX, s.CenterY);
            m.Append(toPage);

            var state = g.Save();
            g.MultiplyTransform(new XMatrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY));
            double x = -s.Width / 2, y = -s.Height / 2;
            switch (s.Kind)
            {
                case StampKind.Whiteout:
                    g.DrawRectangle(XBrushes.White, x, y, s.Width, s.Height);
                    break;
                case StampKind.Image when s.ImageData != null:
                    using (var img = XImage.FromStream(new MemoryStream(s.ImageData)))
                        g.DrawImage(img, x, y, s.Width, s.Height);
                    break;
                case StampKind.Text:
                    DrawTextStamp(g, s, x, y);
                    break;
            }
            g.Restore(state);
        }
    }

    private static void DrawTextStamp(XGraphics g, StampModel s, double x, double y)
    {
        var lines = s.Text.Replace("\r\n", "\n").Split('\n');
        string family = FontPicker.PickFor(s.Text, s.FontFamily);
        var font = MakeFont(family, s.FontSize);
        var c = s.Color;
        var brush = new XSolidBrush(XColor.FromArgb(c.A, c.R, c.G, c.B));
        double lineHeight = s.FontSize * LineSpacing;

        // Place the baseline the way WPF does for a fixed line height: split the extra space by ascent/descent ratio.
        var metrics = font.Metrics;
        double ascent = metrics.Ascent, descent = Math.Abs(metrics.Descent);
        double baselineRatio = ascent + descent > 0 ? ascent / (ascent + descent) : 0.8;
        double emHeight = s.FontSize * (ascent + descent) / metrics.UnitsPerEm;
        double baselineOffset = (lineHeight - emHeight) / 2 + emHeight * baselineRatio;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            g.DrawString(lines[i], font, brush, x + TextPadding, y + TextPadding + i * lineHeight + baselineOffset, XStringFormats.BaseLineLeft);
        }
    }
}
