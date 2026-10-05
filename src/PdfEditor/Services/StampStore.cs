using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using PdfEditor.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfEditor.Services.PdfObjects;

namespace PdfEditor.Services;

/// <summary>
/// Keeps placed items (text, images, white-out) editable across save and reopen.
///
/// Saving still draws the items as ordinary page content, so every viewer shows them. Alongside, each page gets a
/// private <c>/PdfEditorStamps</c> dictionary recording which content streams and resources that drawing added,
/// plus the items themselves. On open, if those streams are still exactly where they were, they and their
/// resources are removed again and the items come back as editable objects. If another program has rewritten the
/// page in the meantime, the page is left untouched and its items simply stay part of the page.
/// </summary>
public static class StampStore
{
    private const string Key = "/PdfEditorStamps";
    private const int Version = 1;
    private static readonly string[] ResourceKinds = { "/XObject", "/Font", "/ExtGState", "/Pattern", "/Shading", "/ColorSpace" };

    // ------------------------------------------------------------------ JSON shapes

    // Bold and Italic were added after version 1 shipped; files without them read as false.
    private sealed record ItemData(string Kind, double Cx, double Cy, double W, double H, double Angle,
        string Text, double FontSize, string Color, string Font, int Image, bool Bold = false, bool Italic = false);

    /// <summary>An image used by items on the page: its SHA-256, and whether a copy of the bytes is stored (PNG) or the
    /// drawn XObject already holds them (JPEG, embedded unchanged).</summary>
    private sealed record ImageData(string Sha256, bool Stored);

    private sealed record PageData(int V, double[] Crop, int Rotation, List<ItemData> Items, List<ImageData> Images);

    // ------------------------------------------------------------------ saving

    /// <summary>Snapshot of what is on a page before stamps are drawn, so <see cref="Record"/> can tell what was added.</summary>
    public sealed class Before
    {
        internal HashSet<int> Contents = new();
        internal Dictionary<string, HashSet<string>> Resources = new();
    }

    public static Before Capture(PdfPage page)
    {
        var b = new Before { Contents = ContentIds(page) };
        foreach (var kind in ResourceKinds) b.Resources[kind] = ResourceNames(page, kind);
        return b;
    }

    /// <summary>Records the content and resources added since <paramref name="before"/> and the items that produced them.</summary>
    public static void Record(PdfDocument doc, PdfPage page, PageGeometry geometry, IEnumerable<StampModel> stamps, Before before)
    {
        var added = new PdfArray(doc);
        foreach (var item in page.Contents.Elements)
            if (item is PdfReference r && !before.Contents.Contains(r.ObjectNumber)) added.Elements.Add(r);

        var resources = new PdfDictionary(doc);
        foreach (var kind in ResourceKinds)
        {
            var names = new PdfArray(doc);
            foreach (var name in ResourceNames(page, kind).Except(before.Resources[kind]))
                names.Elements.Add(new PdfName(name));
            if (names.Elements.Count > 0) resources.Elements[kind] = names;
        }

        var images = new List<ImageData>();
        var stored = new PdfArray(doc);
        var imageIndex = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var items = new List<ItemData>();
        foreach (var s in stamps)
        {
            int image = -1;
            if (s.Kind == StampKind.Image && s.ImageData is { } bytes && !imageIndex.TryGetValue(bytes, out image))
            {
                bool jpeg = IsJpeg(bytes);
                image = images.Count;
                imageIndex[bytes] = image;
                images.Add(new ImageData(Hash(bytes), Stored: !jpeg));
                if (!jpeg)
                {
                    var blob = new PdfDictionary(doc);
                    blob.CreateStream(bytes);
                    doc.Internals.AddObject(blob);
                    stored.Elements.Add(blob.Reference!);
                }
            }
            var c = s.Color;
            items.Add(new ItemData(s.Kind.ToString(), s.CenterX, s.CenterY, s.Width, s.Height, s.Angle, s.Text, s.FontSize,
                $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}", s.FontFamily, image, s.Bold, s.Italic));
        }

        var crop = geometry.CropBox;
        var data = new PageData(Version, new[] { crop.X, crop.Y, crop.Width, crop.Height }, geometry.Rotation, items, images);
        var json = new PdfDictionary(doc);
        json.CreateStream(JsonSerializer.SerializeToUtf8Bytes(data));
        doc.Internals.AddObject(json);

        var entry = new PdfDictionary(doc);
        entry.Elements["/V"] = new PdfInteger(Version);
        entry.Elements["/Contents"] = added;
        entry.Elements["/Resources"] = resources;
        entry.Elements["/Data"] = json.Reference!;
        entry.Elements["/Images"] = stored;
        page.Elements[Key] = entry;
    }

    // ------------------------------------------------------------------ opening

    public sealed record Result(byte[] Bytes, List<StampModel> Stamps, int PagesRestored, int PagesSkipped);

    /// <summary>
    /// Removes recorded stamp content from every page where it is intact and returns the cleaned file plus the
    /// editable items. Returns the original bytes unchanged when there is nothing to restore.
    /// </summary>
    public static Result Extract(byte[] bytes, string? password)
    {
        var doc = PdfOpen.Open(bytes, password);
        if (!Enumerable.Range(0, doc.PageCount).Any(i => doc.Pages[i].Elements.ContainsKey(Key)))
            return new Result(bytes, new List<StampModel>(), 0, 0);

        var geometry = PdfOpen.ReadGeometry(doc);
        var stamps = new List<StampModel>();
        int restored = 0, skipped = 0;
        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            if (GetDict(page, Key) is not { } entry) continue;
            var items = TryRestorePage(page, entry, geometry[i]);
            page.Elements.Remove(Key);
            if (items == null) { skipped++; continue; }
            stamps.AddRange(items);
            restored++;
        }

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return new Result(ms.ToArray(), stamps, restored, skipped);
    }

    private static List<StampModel>? TryRestorePage(PdfPage page, PdfDictionary entry, PageGeometry geometry)
    {
        try
        {
            if (GetNumber(Get(entry, "/V")) is not { } v || (int)v != Version) return null;
            if (GetArray(entry, "/Contents") is not { } added || GetDict(entry, "/Data") is not { Stream: { } dataStream }) return null;
            var data = JsonSerializer.Deserialize<PageData>(dataStream.UnfilteredValue);
            if (data == null) return null;

            // The recorded streams must all still be in the page's content; otherwise the page was rewritten elsewhere.
            var addedIds = added.Elements.OfType<PdfReference>().Select(r => r.ObjectNumber).ToHashSet();
            var contentIds = ContentIds(page);
            if (addedIds.Count == 0 || !addedIds.IsSubsetOf(contentIds)) return null;

            // Images: PNGs from the stored copies, JPEGs from the drawn XObjects (embedded unchanged).
            var storedBlobs = GetArray(entry, "/Images")?.Elements.Select(e => (Resolve(e) as PdfDictionary)?.Stream?.UnfilteredValue).ToList() ?? new();
            var recordedXObjects = (GetDict(entry, "/Resources") is { } res && GetArray(res, "/XObject") is { } xs)
                ? xs.Elements.Select(GetText).Where(n => n != null).ToList() : new List<string?>();
            var imageBytes = new List<byte[]?>();
            int blob = 0;
            foreach (var image in data.Images)
            {
                byte[]? found = image.Stored
                    ? (blob < storedBlobs.Count ? storedBlobs[blob++] : null)
                    : FindJpeg(page, recordedXObjects!, image.Sha256);
                if (found == null || Hash(found) != image.Sha256) return null;
                imageBytes.Add(found);
            }

            // Put the items back where they were relative to the page, even if its crop box or rotation changed since.
            var oldGeometry = new PageGeometry(geometry.Index, new System.Windows.Rect(data.Crop[0], data.Crop[1], data.Crop[2], data.Crop[3]),
                data.Rotation, geometry.MediaHeight);
            var remap = oldGeometry.ViewToPdf * geometry.PdfToView;
            double turn = geometry.Rotation - oldGeometry.Rotation;

            var items = new List<StampModel>();
            foreach (var d in data.Items)
            {
                if (!Enum.TryParse<StampKind>(d.Kind, out var kind)) return null;
                var m = new StampModel
                {
                    Kind = kind, PageIndex = geometry.Index,
                    ImageData = d.Image >= 0 && d.Image < imageBytes.Count ? imageBytes[d.Image] : null,
                    Text = d.Text ?? "", FontSize = d.FontSize, FontFamily = d.Font ?? "Arial", Bold = d.Bold, Italic = d.Italic,
                    Color = (Color)ColorConverter.ConvertFromString(d.Color),
                };
                if (kind == StampKind.Image && m.ImageData == null) return null;
                var centre = remap.Transform(new System.Windows.Point(d.Cx, d.Cy));
                m.Width = d.W; m.Height = d.H; m.CenterX = centre.X; m.CenterY = centre.Y; m.Angle = d.Angle + turn;
                items.Add(m);
            }

            // Everything checks out: take the drawn content and its resources off the page.
            var contents = page.Contents.Elements;
            for (int k = contents.Count - 1; k >= 0; k--)
                if (contents[k] is PdfReference r && addedIds.Contains(r.ObjectNumber)) contents.RemoveAt(k);
            if (GetDict(entry, "/Resources") is { } recorded)
                foreach (var kind in ResourceKinds)
                    if (GetArray(recorded, kind) is { } names && GetDict(page.Resources, kind) is { } dict)
                        foreach (var n in names.Elements.Select(GetText).Where(n => n != null))
                            dict.Elements.Remove("/" + n);
            return items;
        }
        catch (Exception)
        {
            return null; // anything unexpected: leave the page as it is
        }
    }

    private static byte[]? FindJpeg(PdfPage page, List<string> names, string sha256)
    {
        if (GetDict(page.Resources, "/XObject") is not { } xobjects) return null;
        foreach (var name in names)
            if (GetDict(xobjects, "/" + name) is { Stream: { } s } && s.Value is { } raw && IsJpeg(raw) && Hash(raw) == sha256)
                return raw;
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static HashSet<int> ContentIds(PdfPage page) =>
        page.Contents.Elements.OfType<PdfReference>().Select(r => r.ObjectNumber).ToHashSet();

    private static HashSet<string> ResourceNames(PdfPage page, string kind) =>
        GetDict(page.Resources, kind)?.Elements.Keys.ToHashSet() ?? new HashSet<string>();

    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8;

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
}
