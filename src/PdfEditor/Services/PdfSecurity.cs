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
    /// True when the user unlocked with the owner password and the separate open password couldn't be kept,
    /// so saved copies open with the owner password instead.
    /// </summary>
    public bool OpenPasswordChanged { get; init; }

    /// <summary>The author doesn't allow changes and the user chose to open the document for viewing only.</summary>
    public bool ViewOnly { get; init; }

    /// <summary>Whether printing is allowed for the access the user has.</summary>
    public bool CanPrint => !OwnerPasswordReplaced || (Permissions & (1 << 2)) != 0;
}

public enum UnlockFailure { PasswordRequired, WrongPassword, EditingNotAllowed }

public sealed class PdfUnlockException(UnlockFailure reason, string message) : Exception(message)
{
    public UnlockFailure Reason { get; } = reason;
}

/// <summary>
/// Opens password-protected and permission-restricted PDFs.
/// PDFsharp only allows editing with the owner password, so a document opened with the open (user) password is
/// decrypted into a plain working copy. Every other part of the app works on that copy, and
/// <see cref="Protect"/> re-applies the protection when saving. Restrictions set by the author are respected:
/// without the owner password, editing is only allowed if the document permits changes, comments or form filling.
/// </summary>
public static class PdfSecurity
{
    // Permission bits (PDF spec table 22, 1-based bit positions).
    private const int PermPrint = 1 << 2, PermModify = 1 << 3, PermCopy = 1 << 4, PermAnnotate = 1 << 5,
        PermFillForms = 1 << 8, PermAssemble = 1 << 10, PermPrintHigh = 1 << 11;

    private static readonly FieldInfo? OpenModeField =
        typeof(PdfDocument).GetField("_openMode", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Returns plain (unencrypted) bytes to work on, and the protection to re-apply on save (null if the file
    /// wasn't encrypted). Throws <see cref="PdfUnlockException"/> when a password is needed or editing is not allowed.
    /// With <paramref name="viewOnly"/>, a document whose author forbids changes is opened anyway, marked
    /// <see cref="PdfProtection.ViewOnly"/>; the caller must then not offer saving or editing.
    /// </summary>
    public static (byte[] Bytes, PdfProtection? Protection) Unlock(byte[] bytes, string? password, bool viewOnly = false)
    {
        // PDFsharp ignores an /Encrypt dictionary written directly into the trailer and returns undecrypted data.
        bytes = InlineEncryption.MakeIndirect(bytes) ?? bytes;

        PdfDocument doc;
        bool ownerAccess;
        try
        {
            doc = PdfOpen.Open(bytes, password);
            ownerAccess = true;
        }
        catch (PdfReaderException ex) when (Is(ex, "owner password is required"))
        {
            // The password (or none, for a restricted file anyone may open) grants reading only.
            doc = PdfOpen.Open(bytes, password, PdfDocumentOpenMode.ReadOnly);
            ownerAccess = false;
        }
        catch (PdfReaderException ex) when (Is(ex, "a password is required"))
        {
            throw new PdfUnlockException(UnlockFailure.PasswordRequired, ex.Message);
        }
        catch (PdfReaderException ex) when (Is(ex, "password is invalid"))
        {
            throw new PdfUnlockException(UnlockFailure.WrongPassword, ex.Message);
        }

        var handler = doc.SecurityHandler;
        if (!handler.Elements.ContainsKey("/Filter")) return (bytes, null); // not encrypted

        // Some producers write /P as an unsigned 32-bit number (e.g. 4294963412 for -3884); only the bits matter.
        int permissions = unchecked((int)(uint)(long)(PdfObjects.GetNumber(handler.Elements["/P"]) ?? -1));
        bool aes256 = (PdfObjects.GetNumber(handler.Elements["/V"]) ?? 0) >= 5;
        bool editable = ownerAccess || (permissions & (PermModify | PermAnnotate | PermFillForms)) != 0;
        if (!editable && !viewOnly)
            throw new PdfUnlockException(UnlockFailure.EditingNotAllowed,
                "The author of this PDF does not allow it to be changed or filled in. Enter its owner (permissions) password to edit it.");

        string userPassword;
        bool openPasswordChanged = false;
        if (!ownerAccess) userPassword = password ?? "";
        else if (password == null || OpensWith(bytes, "")) userPassword = ""; // no open password
        else
        {
            // Unlocked with the owner password: the open password can't be recovered for every encryption type.
            userPassword = password;
            openPasswordChanged = true;
        }

        var protection = new PdfProtection(userPassword, ownerAccess ? password ?? "" : RandomPassword(), permissions, aes256)
        {
            OwnerPasswordReplaced = !ownerAccess,
            OpenPasswordChanged = openPasswordChanged,
            ViewOnly = !editable,
        };
        return (Decrypt(doc), protection);
    }

    /// <summary>Applies <paramref name="protection"/> to a plain document.</summary>
    public static byte[] Protect(byte[] plain, PdfProtection protection)
    {
        var doc = PdfOpen.Open(plain, null);
        var s = doc.SecuritySettings;
        s.UserPassword = protection.UserPassword;
        s.OwnerPassword = string.IsNullOrEmpty(protection.OwnerPassword) ? RandomPassword() : protection.OwnerPassword;
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
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static byte[] Decrypt(PdfDocument doc)
    {
        // Read-only documents can't be saved; the objects are already decrypted in memory, so allow writing them.
        if (OpenModeField == null) throw new InvalidOperationException("Unsupported PDFsharp version.");
        _ = doc.Pages.Count; // load the page tree while still read-only
        OpenModeField.SetValue(doc, PdfDocumentOpenMode.Modify);
        doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static bool OpensWith(byte[] bytes, string password)
    {
        try { PdfOpen.Open(bytes, password, PdfDocumentOpenMode.ReadOnly); return true; }
        catch (PdfReaderException) { return false; }
    }

    private static bool Is(Exception ex, string text) => ex.Message.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static string RandomPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Moves an /Encrypt dictionary written inline in the trailer into its own object (an incremental update).</summary>
    private static class InlineEncryption
    {
        public static byte[]? MakeIndirect(byte[] bytes)
        {
            var text = Encoding.Latin1.GetString(bytes);
            int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
            if (sx < 0) return null;
            int p = SkipWs(text, sx + 9);
            int numEnd = p;
            while (numEnd < text.Length && char.IsAsciiDigit(text[numEnd])) numEnd++;
            if (!long.TryParse(text.AsSpan(p, numEnd - p), NumberStyles.None, CultureInfo.InvariantCulture, out var xref) || xref >= text.Length)
                return null;

            // The trailer dictionary: after "trailer" for a classic table, or the xref stream's own dictionary.
            int at = (int)xref;
            if (string.CompareOrdinal(text, at, "xref", 0, 4) == 0)
            {
                at = text.IndexOf("trailer", at, StringComparison.Ordinal);
                if (at < 0) return null;
            }
            int dictStart = text.IndexOf("<<", at, StringComparison.Ordinal);
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

            var tail = Encoding.Latin1.GetBytes(sb.ToString());
            var result = new byte[bytes.Length + tail.Length];
            bytes.CopyTo(result, 0);
            tail.CopyTo(result, bytes.Length);
            return result;
        }

        /// <summary>Raw text of the value for a top-level key of a dictionary ("&lt;&lt; ... &gt;&gt;").</summary>
        private static string? FindValue(string dict, string key)
        {
            int i = 2;
            while (i < dict.Length - 2)
            {
                i = SkipWs(dict, i);
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

        private static int SkipWs(string s, int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '\0')) i++;
            return i;
        }

        private static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';
    }
}
