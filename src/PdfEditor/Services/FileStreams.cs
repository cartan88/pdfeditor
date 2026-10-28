using System.IO;
using PdfSharp.Pdf;
using static PdfEditor.Services.PdfObjects;

namespace PdfEditor.Services;

/// <summary>
/// Support for "file streams": streams whose dictionary has an /F entry saying the data lives in an external file
/// (PDF 32000-1, 7.3.8.2). PDFsharp refuses to read any document containing one, and some PDF tools add /F to
/// ordinary streams by mistake, so such files couldn't be opened at all.
///
/// <see cref="Neutralize"/> renames the top-level /F key of every stream dictionary to /f in the raw bytes (same
/// length, so all byte offsets in the cross-reference table stay valid). After parsing, <see cref="Find"/> lists those
/// streams; each is then either given the external file's data (<see cref="Embed"/>) or keeps the data stored in the
/// PDF itself (<see cref="KeepInternal"/>). Either way the result is an ordinary, self-contained PDF.
/// </summary>
public static class FileStreams
{
    /// <summary>The key /F is renamed to (same length; not a key any stream dictionary uses).</summary>
    public const string RenamedKey = "/f";

    /// <summary>External files larger than this are never read.</summary>
    public const long MaxExternalFileSize = 256L * 1024 * 1024;

    public sealed record Reference(PdfDictionary Stream, string? FileName);

    // ------------------------------------------------------------------ before parsing

    /// <summary>
    /// Returns a copy of the file with /F renamed to /f in every stream dictionary, or null if there is none.
    /// Only a top-level /F key directly in a stream's dictionary is changed; /Filter, nested dictionaries and
    /// values are left alone.
    /// </summary>
    public static byte[]? Neutralize(byte[] pdf)
    {
        byte[]? patched = null;
        int pos = 0;
        while ((pos = IndexOf(pdf, "stream"u8, pos)) >= 0)
        {
            int keyword = pos;
            pos += 6;
            // The "stream" keyword: a whole token, followed by an end of line, right after the dictionary's ">>".
            if (pos >= pdf.Length || (pdf[pos] != '\r' && pdf[pos] != '\n')) continue;
            if (keyword > 0 && !IsDelimiterOrWhite(pdf[keyword - 1])) continue; // e.g. "endstream"
            int end = keyword - 1;
            while (end >= 0 && IsWhite(pdf[end])) end--;
            if (end < 1 || pdf[end] != '>' || pdf[end - 1] != '>') continue;

            // The dictionary starts right after the object header "n g obj".
            int obj = LastToken(pdf, "obj"u8, end);
            if (obj < 0) continue;
            int p = obj + 3;
            SkipWhiteAndComments(pdf, ref p);
            if (!At(pdf, p, "<<"u8)) continue;
            try
            {
                foreach (int key in TopLevelKeys(pdf, p, end + 1))
                {
                    if (key + 2 > pdf.Length || pdf[key + 1] != 'F' || (key + 2 < pdf.Length && !IsDelimiterOrWhite(pdf[key + 2]))) continue;
                    patched ??= (byte[])pdf.Clone();
                    patched[key + 1] = (byte)'f';
                }
            }
            catch (IndexOutOfRangeException)
            {
                // Malformed dictionary: leave it to the PDF reader to report.
            }
        }
        return patched;
    }

    /// <summary>Positions of the keys of the dictionary starting at <paramref name="start"/> ("&lt;&lt;"), top level only.</summary>
    private static IEnumerable<int> TopLevelKeys(byte[] b, int start, int limit)
    {
        var keys = new List<int>();
        int p = start + 2;
        while (p < limit)
        {
            SkipWhiteAndComments(b, ref p);
            if (At(b, p, ">>"u8) || b[p] != '/') break;
            keys.Add(p);
            SkipValue(b, ref p); // the key (a name)
            SkipValue(b, ref p); // its value
        }
        return keys;
    }

    /// <summary>Skips one PDF object: name, string, hex string, dictionary, array, number or reference, keyword.</summary>
    private static void SkipValue(byte[] b, ref int p)
    {
        SkipWhiteAndComments(b, ref p);
        byte c = b[p];
        if (c == '/')
        {
            p++;
            while (p < b.Length && !IsDelimiterOrWhite(b[p])) p++;
        }
        else if (c == '(')
        {
            int depth = 0;
            for (; p < b.Length; p++)
            {
                if (b[p] == '\\') { p++; continue; }
                if (b[p] == '(') depth++;
                else if (b[p] == ')' && --depth == 0) { p++; break; }
            }
        }
        else if (At(b, p, "<<"u8))
        {
            p += 2;
            while (true)
            {
                SkipWhiteAndComments(b, ref p);
                if (At(b, p, ">>"u8)) { p += 2; break; }
                SkipValue(b, ref p);
            }
        }
        else if (c == '<')
        {
            while (b[p] != '>') p++;
            p++;
        }
        else if (c == '[')
        {
            p++;
            while (true)
            {
                SkipWhiteAndComments(b, ref p);
                if (b[p] == ']') { p++; break; }
                SkipValue(b, ref p);
            }
        }
        else if (char.IsDigit((char)c) || c == '+' || c == '-' || c == '.')
        {
            SkipRegular(b, ref p);
            // "n g R" is one value: a reference.
            int save = p;
            SkipWhiteAndComments(b, ref p);
            if (p < b.Length && char.IsDigit((char)b[p]))
            {
                SkipRegular(b, ref p);
                SkipWhiteAndComments(b, ref p);
                if (p < b.Length && b[p] == 'R' && (p + 1 >= b.Length || IsDelimiterOrWhite(b[p + 1]))) { p++; return; }
            }
            p = save;
        }
        else
        {
            if (IsDelimiterOrWhite(c)) p++; // stray delimiter: step over it
            else SkipRegular(b, ref p);     // true, false, null
        }
    }

    // ------------------------------------------------------------------ after parsing

    /// <summary>The streams that had an /F entry (renamed by <see cref="Neutralize"/>), with the file each names.</summary>
    public static List<Reference> Find(PdfDocument doc) =>
        doc.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Stream != null && d.Elements.ContainsKey(RenamedKey))
            .Select(d => new Reference(d, FileNameOf(Get(d, RenamedKey))))
            .ToList();

    /// <summary>The file name in a file specification: a string, or a dictionary's /UF or /F.</summary>
    private static string? FileNameOf(PdfItem? spec) => Resolve(spec) switch
    {
        PdfString s => s.Value,
        PdfDictionary d => GetText(Get(d, "/UF")) ?? GetText(Get(d, "/F")),
        _ => null,
    };

    /// <summary>
    /// Where an external file may be read from: only a relative path to a file in the PDF's own folder or below it.
    /// Absolute paths, "..", network paths and URLs are refused, so a PDF can't pull in arbitrary files from the PC
    /// (which would then be embedded and shared when the PDF is saved).
    /// </summary>
    public static string? SafeLocalPath(string? fileName, string? pdfFolder)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrEmpty(pdfFolder)) return null;
        if (fileName.Contains("://", StringComparison.Ordinal) || fileName.Contains(':') || fileName.StartsWith('/') || fileName.StartsWith('\\'))
            return null;
        var relative = fileName.Replace('/', Path.DirectorySeparatorChar);
        if (relative.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
            return null;
        var folder = Path.GetFullPath(pdfFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return null;
        var info = new FileInfo(full);
        return info.Exists && info.Length <= MaxExternalFileSize ? full : null;
    }

    /// <summary>Uses the external file's bytes as the stream's data (with its /FFilter and /FDecodeParms).</summary>
    public static void Embed(Reference r, byte[] data)
    {
        var e = r.Stream.Elements;
        e.Remove("/Filter");
        e.Remove("/DecodeParms");
        if (e.ContainsKey("/FFilter")) e["/Filter"] = e["/FFilter"];
        if (e.ContainsKey("/FDecodeParms")) e["/DecodeParms"] = e["/FDecodeParms"];
        r.Stream.Stream!.Value = data;
        e["/Length"] = new PdfInteger(data.Length);
        RemoveFileKeys(e);
    }

    /// <summary>Keeps the data stored in the PDF and drops the file reference.</summary>
    public static void KeepInternal(Reference r) => RemoveFileKeys(r.Stream.Elements);

    private static void RemoveFileKeys(PdfDictionary.DictionaryElements e)
    {
        e.Remove(RenamedKey);
        e.Remove("/FFilter");
        e.Remove("/FDecodeParms");
        e.Remove("/DL");
    }

    // ------------------------------------------------------------------ byte helpers

    private static bool IsWhite(byte c) => c is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0;

    private static bool IsDelimiterOrWhite(byte c) =>
        IsWhite(c) || c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static void SkipRegular(byte[] b, ref int p)
    {
        while (p < b.Length && !IsDelimiterOrWhite(b[p])) p++;
    }

    private static void SkipWhiteAndComments(byte[] b, ref int p)
    {
        while (p < b.Length)
        {
            if (IsWhite(b[p])) p++;
            else if (b[p] == '%') { while (p < b.Length && b[p] != '\r' && b[p] != '\n') p++; }
            else break;
        }
    }

    private static bool At(byte[] b, int p, ReadOnlySpan<byte> s) => p >= 0 && p + s.Length <= b.Length && b.AsSpan(p, s.Length).SequenceEqual(s);

    private static int IndexOf(byte[] b, ReadOnlySpan<byte> s, int from)
    {
        int i = b.AsSpan(from).IndexOf(s);
        return i < 0 ? -1 : from + i;
    }

    /// <summary>The last whole-token occurrence of <paramref name="s"/> before <paramref name="before"/>.</summary>
    private static int LastToken(byte[] b, ReadOnlySpan<byte> s, int before)
    {
        for (int i = b.AsSpan(0, Math.Max(0, before)).LastIndexOf(s); i >= 0; i = i == 0 ? -1 : b.AsSpan(0, i).LastIndexOf(s))
        {
            bool startOk = i == 0 || IsDelimiterOrWhite(b[i - 1]);
            bool endOk = i + s.Length >= b.Length || IsDelimiterOrWhite(b[i + s.Length]);
            if (startOk && endOk) return i;
        }
        return -1;
    }
}
