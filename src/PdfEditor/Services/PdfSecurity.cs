using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Services;

/// <summary>How an encrypted document was protected, so the same protection can be applied when saving.</summary>
public sealed record PdfProtection(string UserPassword, string OwnerPassword, int Permissions, bool Aes256)
{
    /// <summary>True when the user unlocked with the open password, so the original owner password is unknown.</summary>
    public bool OwnerPasswordReplaced { get; init; }

    /// <summary>
    /// True when the user unlocked with the owner password and the separate open password couldn't be recovered
    /// (AES-256 files), so saved copies would open with the owner password, which gives full access.
    /// <see cref="PdfSecurity.WithOpenPassword"/> fixes that if the user knows the open password.
    /// </summary>
    public bool OpenPasswordChanged { get; init; }

    /// <summary>The author doesn't allow changes and the user chose to open the document for viewing only.</summary>
    public bool ViewOnly { get; init; }

    /// <summary>Whether printing is allowed for the access the user has.</summary>
    public bool CanPrint => !OwnerPasswordReplaced || (Permissions & PdfSecurity.PermPrint) != 0;

    /// <summary>
    /// Whether page content may be changed (placed text, signatures, images, white-out, flattening).
    /// Without the owner password that needs the "modify" permission; "fill forms" or "annotate" alone
    /// only allow filling in the form's fields.
    /// </summary>
    public bool CanModifyContent => !ViewOnly && (!OwnerPasswordReplaced || (Permissions & PdfSecurity.PermModify) != 0);
}

public enum UnlockFailure { PasswordRequired, WrongPassword, EditingNotAllowed }

public sealed class PdfUnlockException(UnlockFailure reason, string message) : Exception(message)
{
    public UnlockFailure Reason { get; } = reason;
}

/// <summary>Result of <see cref="PdfSecurity.Unlock"/>.</summary>
/// <param name="Bytes">Plain (unencrypted) bytes to work on.</param>
/// <param name="Protection">Protection to re-apply when saving; null if the file wasn't encrypted.</param>
/// <param name="Document">The already-parsed document for <paramref name="Bytes"/> when it can be reused, otherwise null.</param>
public sealed record UnlockResult(byte[] Bytes, PdfProtection? Protection, PdfDocument? Document)
{
    public void Deconstruct(out byte[] bytes, out PdfProtection? protection) => (bytes, protection) = (Bytes, Protection);
}

/// <summary>
/// Opens password-protected and permission-restricted PDFs.
/// PDFsharp only allows editing with the owner password, so a document opened with the open (user) password is
/// decrypted into a plain working copy. Every other part of the app works on that copy, and
/// <see cref="Apply"/> re-applies the protection when saving. Restrictions set by the author are respected:
/// without the owner password, filling in the form needs the "fill forms", "annotate" or "modify" permission,
/// and changing page content needs "modify" (see <see cref="PdfProtection.CanModifyContent"/>).
/// </summary>
public static class PdfSecurity
{
    // Permission bits (PDF spec table 22, 1-based bit positions).
    internal const int PermPrint = 1 << 2, PermModify = 1 << 3, PermCopy = 1 << 4, PermAnnotate = 1 << 5,
        PermFillForms = 1 << 8, PermAssemble = 1 << 10, PermPrintHigh = 1 << 11;
    // Bit 10 (extract for accessibility) is deprecated in PDF 2.0; PDFsharp always sets it, as readers must ignore it.

    private static readonly FieldInfo? OpenModeField =
        typeof(PdfDocument).GetField("_openMode", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Unlocks a document with the given password (null = none). Throws <see cref="PdfUnlockException"/> when a
    /// password is needed or wrong, or when the author forbids all changes; with <paramref name="viewOnly"/> such a
    /// document is opened anyway, marked <see cref="PdfProtection.ViewOnly"/>, and the caller must not offer editing.
    /// </summary>
    public static UnlockResult Unlock(byte[] bytes, string? password, bool viewOnly = false)
    {
        // PDFsharp ignores an /Encrypt dictionary written directly into the trailer and returns undecrypted data.
        bytes = InlineEncryption.MakeIndirect(bytes) ?? bytes;

        // PDFsharp asks its password provider whenever the password it has isn't good enough for the open mode
        // (and, if the provider gives up, returns the document still encrypted). So "no request" means accepted:
        // in Modify mode only the owner password (or no encryption) is accepted, in Import (read) mode either password.
        var doc = TryOpen(bytes, password, PdfDocumentOpenMode.Modify);
        bool ownerAccess = doc != null;
        doc ??= TryOpen(bytes, password, PdfDocumentOpenMode.Import);
        if (doc == null)
            throw password == null
                ? new PdfUnlockException(UnlockFailure.PasswordRequired, "A password is required to open this PDF.")
                : new PdfUnlockException(UnlockFailure.WrongPassword, "The password is not correct.");

        var handler = doc.SecurityHandler;
        if (!handler.Elements.ContainsKey("/Filter")) return new UnlockResult(bytes, null, doc); // not encrypted

        // Some producers write /P as an unsigned 32-bit number (e.g. 4294963412 for -3884); only the bits matter.
        int permissions = unchecked((int)(uint)(long)(PdfObjects.GetNumber(handler.Elements["/P"]) ?? -1));
        int revision = (int)(PdfObjects.GetNumber(handler.Elements["/R"]) ?? 0);
        bool aes256 = (PdfObjects.GetNumber(handler.Elements["/V"]) ?? 0) >= 5;
        bool editable = ownerAccess || (permissions & (PermModify | PermAnnotate | PermFillForms)) != 0;
        if (!editable && !viewOnly)
            throw new PdfUnlockException(UnlockFailure.EditingNotAllowed,
                "The author of this PDF does not allow it to be changed or filled in. Enter its owner (permissions) password to edit it.");

        string userPassword;
        bool openPasswordChanged = false;
        if (!ownerAccess) userPassword = password ?? "";
        else if (password == null || IsOpenPassword(bytes, "")) userPassword = ""; // no open password
        else if (RecoverUserPasswords(handler, password, revision).FirstOrDefault(c => IsOpenPassword(bytes, c)) is { } recovered)
            userPassword = recovered;
        else
        {
            // AES-256 (R5/R6) can't be reversed; saved copies open with the owner password unless the user supplies the open one.
            userPassword = password;
            openPasswordChanged = true;
        }

        var protection = new PdfProtection(userPassword, ownerAccess ? password ?? "" : RandomPassword(), permissions, aes256)
        {
            OwnerPasswordReplaced = !ownerAccess,
            OpenPasswordChanged = openPasswordChanged,
            ViewOnly = !editable,
        };
        return new UnlockResult(Decrypt(doc, ownerAccess), protection, null);
    }

    /// <summary>
    /// For a document unlocked with the owner password whose open password couldn't be recovered: returns the
    /// protection with <paramref name="openPassword"/> as the open password if it really is the document's open
    /// password, otherwise null.
    /// </summary>
    public static PdfProtection? WithOpenPassword(byte[] original, PdfProtection protection, string openPassword)
    {
        if (openPassword.Length == 0 || openPassword == protection.OwnerPassword) return null;
        original = InlineEncryption.MakeIndirect(original) ?? original;
        return IsOpenPassword(original, openPassword) && TryOpen(original, openPassword, PdfDocumentOpenMode.Modify) == null
            ? protection with { UserPassword = openPassword, OpenPasswordChanged = false }
            : null;
    }

    /// <summary>Applies <paramref name="protection"/> to a plain document opened for modification, before it is saved.</summary>
    public static void Apply(PdfDocument doc, PdfProtection protection)
    {
        var s = doc.SecuritySettings;
        s.UserPassword = protection.UserPassword;
        s.OwnerPassword = string.IsNullOrEmpty(protection.OwnerPassword) || protection.OwnerPassword == protection.UserPassword
            ? RandomPassword() // never let the open password grant owner rights
            : protection.OwnerPassword;
        int p = protection.Permissions;
        s.PermitPrint = (p & PermPrint) != 0;
        s.PermitModifyDocument = (p & PermModify) != 0;
        s.PermitExtractContent = (p & PermCopy) != 0;
        s.PermitAnnotations = (p & PermAnnotate) != 0;
        s.PermitFormsFill = (p & PermFillForms) != 0;
        s.PermitAssembleDocument = (p & PermAssemble) != 0;
        s.PermitFullQualityPrint = (p & PermPrintHigh) != 0;
        if (protection.Aes256) doc.SecurityHandler.SetEncryptionToV5();
        else doc.SecurityHandler.SetEncryptionToV4UsingAES();
    }

    /// <summary>Applies <paramref name="protection"/> to plain document bytes.</summary>
    public static byte[] Protect(byte[] plain, PdfProtection protection)
    {
        var doc = PdfOpen.Open(plain, null);
        Apply(doc, protection);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Opens the document if <paramref name="password"/> suffices for <paramref name="mode"/>, otherwise returns null.</summary>
    private static PdfDocument? TryOpen(byte[] bytes, string? password, PdfDocumentOpenMode mode)
    {
        bool asked = false;
        var doc = PdfOpen.Open(bytes, password, mode, args => { asked = true; args.Abort = true; });
        return asked ? null : doc;
    }

    private static bool IsOpenPassword(byte[] bytes, string password) => TryOpen(bytes, password, PdfDocumentOpenMode.Import) != null;

    private static byte[] Decrypt(PdfDocument doc, bool openedForModify)
    {
        if (!openedForModify)
        {
            // PDFsharp won't save a document opened for reading, but its objects are already decrypted in memory.
            if (OpenModeField == null)
                throw new NotSupportedException("This version of PDFsharp can't edit documents opened with the open password.");
            _ = doc.Pages.Count; // load the page tree while still in read mode
            OpenModeField.SetValue(doc, PdfDocumentOpenMode.Modify);
        }
        doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static string RandomPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // ------------------------------------------------------------------ open password recovery (R2–R4)

    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    /// <summary>
    /// Candidates for the open password, recovered from /O with the owner password (PDF 1.7 algorithm 7), so saved
    /// copies keep the original open password. Only possible for revisions 2–4. The caller checks each candidate:
    /// for keys shorter than 128 bits, writers differ on whether the 50 MD5 rounds hash the whole digest or only
    /// the key-length prefix.
    /// </summary>
    private static IEnumerable<string> RecoverUserPasswords(PdfSharp.Pdf.Security.PdfStandardSecurityHandler handler, string ownerPassword, int revision)
    {
        if (revision is < 2 or > 4) yield break;
        if (handler.Elements["/O"] is not PdfString { Encoding: PdfStringEncoding.RawEncoding } o || o.Value.Length < 32) yield break;
        var owner = o.Value.Take(32).Select(c => (byte)c).ToArray();
        int keyLength = revision == 2 ? 5 : (int)(PdfObjects.GetNumber(handler.Elements["/Length"]) ?? 40) / 8;
        if (keyLength is < 5 or > 16) yield break;

        foreach (bool wholeDigest in revision >= 3 && keyLength < 16 ? new[] { true, false } : new[] { true })
            if (RecoverUserPassword(owner, ownerPassword, revision, keyLength, wholeDigest) is { } candidate)
                yield return candidate;
    }

    private static string? RecoverUserPassword(byte[] owner, string ownerPassword, int revision, int keyLength, bool wholeDigest)
    {
        // Algorithm 3, steps a–d: the RC4 key from the padded owner password.
        var hash = MD5.HashData(Pad(Encoding.Latin1.GetBytes(ownerPassword)));
        if (revision >= 3)
            for (int i = 0; i < 50; i++) hash = MD5.HashData(wholeDigest ? hash : hash[..keyLength]);
        var key = hash[..keyLength];

        var data = owner;
        if (revision == 2) data = Rc4(key, data);
        else
            for (int i = 19; i >= 0; i--)
                data = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), data);

        // data is the padded open password: strip the padding suffix.
        for (int len = 0; len <= 32; len++)
            if (data.AsSpan(len).SequenceEqual(Padding.AsSpan(0, 32 - len)))
                return Encoding.Latin1.GetString(data, 0, len);
        return null;
    }

    private static byte[] Pad(byte[] password)
    {
        var result = new byte[32];
        int n = Math.Min(32, password.Length);
        Array.Copy(password, result, n);
        Array.Copy(Padding, 0, result, n, 32 - n);
        return result;
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return output;
    }

    /// <summary>Moves an /Encrypt dictionary written inline in the trailer into its own object (an incremental update).</summary>
    private static class InlineEncryption
    {
        /// <summary>How much of the file is read for the trailer dictionary (it is small; large values are rare).</summary>
        private const int TrailerWindow = 64 * 1024;

        public static byte[]? MakeIndirect(byte[] bytes)
        {
            // Only the end of the file and the latest trailer are decoded, never the whole document.
            int tailStart = Math.Max(0, bytes.Length - 1024);
            var tail = Encoding.Latin1.GetString(bytes, tailStart, bytes.Length - tailStart);
            int sx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
            if (sx < 0) return null;
            int p = SkipWs(tail, sx + 9);
            int numEnd = p;
            while (numEnd < tail.Length && char.IsAsciiDigit(tail[numEnd])) numEnd++;
            if (!long.TryParse(tail.AsSpan(p, numEnd - p), NumberStyles.None, CultureInfo.InvariantCulture, out var xref) || xref >= bytes.Length)
                return null;

            // The trailer dictionary: after "trailer" for a classic table, or the xref stream's own dictionary.
            int at = (int)xref;
            if (bytes.AsSpan(at).StartsWith("xref"u8))
            {
                int t = bytes.AsSpan(at).IndexOf("trailer"u8);
                if (t < 0) return null;
                at += t;
            }
            var text = Encoding.Latin1.GetString(bytes, at, Math.Min(TrailerWindow, bytes.Length - at));
            int dictStart = text.IndexOf("<<", StringComparison.Ordinal);
            if (dictStart < 0) return null;
            int dictEnd = SkipObject(text, dictStart);
            if (dictEnd < 0) return null;
            var trailer = text[dictStart..dictEnd];

            var enc = FindValue(trailer, "/Encrypt");
            if (enc == null || !enc.StartsWith("<<", StringComparison.Ordinal)) return null; // absent or already indirect
            var root = FindValue(trailer, "/Root");
            var size = FindValue(trailer, "/Size");
            if (root == null || size == null || !int.TryParse(size.Trim(), out var objNum)) return null;
            var info = FindValue(trailer, "/Info");
            var id = FindValue(trailer, "/ID");

            var sb = new StringBuilder();
            sb.Append('\n');
            long objOffset = bytes.Length + sb.Length;
            sb.Append(CultureInfo.InvariantCulture, $"{objNum} 0 obj\n{enc}\nendobj\n");
            long xrefOffset = bytes.Length + sb.Length;
            sb.Append("xref\n0 1\n0000000000 65535 f\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"{objNum} 1\n{objOffset:D10} 00000 n\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objNum + 1} /Root {root}");
            if (info != null) sb.Append(" /Info ").Append(info);
            if (id != null) sb.Append(" /ID ").Append(id);
            sb.Append(CultureInfo.InvariantCulture, $" /Encrypt {objNum} 0 R /Prev {xref} >>\nstartxref\n{xrefOffset}\n%%EOF\n");

            var appended = Encoding.Latin1.GetBytes(sb.ToString());
            var result = new byte[bytes.Length + appended.Length];
            bytes.CopyTo(result, 0);
            appended.CopyTo(result, bytes.Length);
            return result;
        }

        /// <summary>Raw text of the value for a top-level key of a dictionary ("&lt;&lt; ... &gt;&gt;").</summary>
        private static string? FindValue(string dict, string key)
        {
            int i = 2;
            while (i < dict.Length - 2)
            {
                i = SkipWs(dict, i);
                if (i >= dict.Length - 2) break;
                if (dict[i] != '/') { i = SkipObject(dict, i); if (i < 0) return null; continue; }
                int nameEnd = i + 1;
                while (nameEnd < dict.Length && !IsDelimiter(dict[nameEnd]) && !char.IsWhiteSpace(dict[nameEnd])) nameEnd++;
                var name = dict[i..nameEnd];
                int valStart = SkipWs(dict, nameEnd);
                int valEnd = SkipValue(dict, valStart);
                if (valEnd < 0) return null;
                if (name == key) return dict[valStart..valEnd].Trim();
                i = valEnd;
            }
            return null;
        }

        /// <summary>A value, treating "n g R" as one value.</summary>
        private static int SkipValue(string s, int i)
        {
            int end = SkipObject(s, i);
            if (end < 0 || !char.IsAsciiDigit(s[i])) return end;
            var m = System.Text.RegularExpressions.Regex.Match(s[i..], @"^\d+\s+\d+\s+R\b");
            return m.Success ? i + m.Length : end;
        }

        /// <summary>Index just past the object starting at <paramref name="i"/> (dictionary, array, string, name, number...).</summary>
        private static int SkipObject(string s, int i)
        {
            if (i >= s.Length) return -1;
            char c = s[i];
            if (c == '<' && i + 1 < s.Length && s[i + 1] == '<')
            {
                i += 2;
                while (i < s.Length)
                {
                    i = SkipWs(s, i);
                    if (i + 1 < s.Length && s[i] == '>' && s[i + 1] == '>') return i + 2;
                    i = SkipObject(s, i);
                    if (i < 0) return -1;
                }
                return -1;
            }
            if (c == '[')
            {
                i++;
                while (i < s.Length)
                {
                    i = SkipWs(s, i);
                    if (i < s.Length && s[i] == ']') return i + 1;
                    i = SkipObject(s, i);
                    if (i < 0) return -1;
                }
                return -1;
            }
            if (c == '(')
            {
                int depth = 0;
                for (; i < s.Length; i++)
                {
                    if (s[i] == '\\') { i++; continue; }
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')' && --depth == 0) return i + 1;
                }
                return -1;
            }
            if (c == '<')
            {
                int end = s.IndexOf('>', i);
                return end < 0 ? -1 : end + 1;
            }
            if (c == '/') i++;
            int start = i;
            while (i < s.Length && !IsDelimiter(s[i]) && !char.IsWhiteSpace(s[i])) i++;
            return i > start || c == '/' ? i : i + 1;
        }

        /// <summary>Skips white space and comments ('%' to the end of the line) between tokens.</summary>
        private static int SkipWs(string s, int i)
        {
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i]) || s[i] == '\0') i++;
                else if (s[i] == '%')
                    while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                else break;
            }
            return i;
        }

        private static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';
    }
}
