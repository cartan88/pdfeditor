using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using PdfSharp.Pdf.IO;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

public class PdfSecurityTests
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The sample form encrypted by PDFsharp (AES-128).</summary>
    private static byte[] Encrypted(string user, string owner, Action<PdfSharp.Pdf.Security.PdfSecuritySettings>? permissions = null,
        Action<PdfSharp.Pdf.Security.PdfStandardSecurityHandler>? algorithm = null)
    {
        var doc = PdfOpen.Open(SampleForm(), null);
        doc.SecuritySettings.UserPassword = user;
        doc.SecuritySettings.OwnerPassword = owner;
        permissions?.Invoke(doc.SecuritySettings);
        algorithm?.Invoke(doc.SecurityHandler);
        return Save(doc);
    }

    private static readonly Action<PdfSharp.Pdf.Security.PdfSecuritySettings> NoChanges = s =>
    {
        s.PermitModifyDocument = false;
        s.PermitAnnotations = false;
        s.PermitFormsFill = false;
        s.PermitPrint = false;
    };

    private static T Get<T>(MainWindow win, string field) => (T)typeof(MainWindow).GetField(field, F)!.GetValue(win)!;
    private static bool Enabled(MainWindow win, string name) => ((System.Windows.UIElement)win.FindName(name)).IsEnabled;
    private static string Status(MainWindow win) => ((System.Windows.Controls.TextBlock)win.FindName("StatusText")).Text;

    private static Task<bool> InvokeSave(MainWindow win, string method, params object[] args) =>
        (Task<bool>)typeof(MainWindow).GetMethod(method, F)!.Invoke(win, args)!;

    /// <summary>
    /// Rewrites the trailer so /Encrypt is a direct dictionary, as MuPDF-based tools write it.
    /// PDFsharp doesn't notice such encryption and would hand back undecrypted data.
    /// </summary>
    private static byte[] WithInlineEncrypt(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var m = Regex.Match(text, @"/Encrypt (\d+) 0 R");
        var obj = Regex.Match(text, $@"\n{m.Groups[1].Value} 0 obj\s*(<<.*?>>)\s*endobj", RegexOptions.Singleline);
        Assert.True(m.Success && obj.Success);
        int at = text.LastIndexOf("/Encrypt ", StringComparison.Ordinal); // the trailer's entry comes last
        return Encoding.Latin1.GetBytes(text[..at] + "/Encrypt " + obj.Groups[1].Value + text[(at + m.Length)..]);
    }

    private static string FirstFieldName(byte[] plain) => Fields(plain).First(f => f.FullName == "full_name").FullName;

    [Fact]
    public void PdfSharpAloneRefusesToEditWithTheOpenPassword()
    {
        // The reported bug: entering the correct open password still failed.
        var pdf = Encrypted("user123", "owner123");
        var ex = Assert.Throws<PdfReaderException>(() => PdfOpen.Open(pdf, "user123"));
        Assert.Contains("owner password is required", ex.Message);
    }

    [Fact]
    public void OpensWithTheOpenPasswordAndKeepsItWhenSaving()
    {
        var pdf = Encrypted("user123", "owner123");
        var (plain, protection) = PdfSecurity.Unlock(pdf, "user123");

        Assert.Equal("full_name", FirstFieldName(plain));
        Assert.NotNull(protection);
        Assert.Equal("user123", protection!.UserPassword);
        Assert.True(protection.OwnerPasswordReplaced);

        var fields = Fields(plain);
        fields.First(f => f.FullName == "full_name").Value = "Charles Tan";
        var saved = PdfSecurity.Protect(PdfSaver.Build(plain, null, fields, new List<StampModel>(), false), protection);

        Assert.Equal(UnlockFailure.PasswordRequired, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(saved, null)).Reason);
        var (reopened, _) = PdfSecurity.Unlock(saved, "user123");
        Assert.Equal("Charles Tan", Fields(reopened).First(f => f.FullName == "full_name").Value);
    }

    [Fact]
    public void OwnerPasswordAlsoWorks()
    {
        var (plain, protection) = PdfSecurity.Unlock(Encrypted("user123", "owner123"), "owner123");
        Assert.Equal("full_name", FirstFieldName(plain));
        Assert.Equal("owner123", protection!.OwnerPassword);
    }

    public static TheoryData<string> RecoverableAlgorithms => new() { "RC4-40 (R2)", "RC4-128 (R3)", "AES-128 (R4)" };

    private static Action<PdfSharp.Pdf.Security.PdfStandardSecurityHandler> Algorithm(string name) => name switch
    {
        "RC4-40 (R2)" => h => h.SetEncryptionToV1(),
        "RC4-128 (R3)" => h => h.SetEncryptionToV2With128Bits(),
        "AES-128 (R4)" => h => h.SetEncryptionToV4UsingAES(),
        _ => h => h.SetEncryptionToV5(),
    };

    [Theory]
    [MemberData(nameof(RecoverableAlgorithms))]
    public void OwnerPasswordUnlockKeepsTheOriginalOpenPasswordAndRestrictions(string algorithm)
    {
        // Review finding: saved copies used the owner password as the open password, so everyone given it had full rights.
        var pdf = Encrypted("user123", "owner123", NoChanges, Algorithm(algorithm));
        var (plain, protection) = PdfSecurity.Unlock(pdf, "owner123");
        Assert.Equal("user123", protection!.UserPassword); // recovered from /O
        Assert.False(protection.OpenPasswordChanged);

        var saved = PdfSecurity.Protect(plain, protection);
        Assert.Equal(UnlockFailure.WrongPassword, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(saved, "nope")).Reason);
        // The open password still opens it, still without edit rights; the owner password still edits it.
        Assert.Equal(UnlockFailure.EditingNotAllowed, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(saved, "user123")).Reason);
        Assert.False(PdfSecurity.Unlock(saved, "owner123").Protection!.OwnerPasswordReplaced);
    }

    [Fact]
    public void Aes256OpenPasswordCanBeSuppliedAndIsNeverAnOwnerPassword()
    {
        var pdf = Encrypted("user123", "owner123", NoChanges, Algorithm("AES-256 (R6)"));
        var (plain, protection) = PdfSecurity.Unlock(pdf, "owner123");
        Assert.True(protection!.OpenPasswordChanged); // R6 can't be reversed

        Assert.Null(PdfSecurity.WithOpenPassword(pdf, protection, "wrong"));
        Assert.Null(PdfSecurity.WithOpenPassword(pdf, protection, "owner123"));
        var kept = PdfSecurity.WithOpenPassword(pdf, protection, "user123");
        Assert.Equal("user123", kept!.UserPassword);
        Assert.False(kept.OpenPasswordChanged);

        // Without the open password, the saved copy opens with the owner password, but with the author's restrictions.
        var saved = PdfSecurity.Protect(plain, protection);
        Assert.Equal(UnlockFailure.EditingNotAllowed, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(saved, "owner123")).Reason);
    }

    [Fact]
    public void FormFillingPermissionDoesNotAllowChangingPageContent()
    {
        var pdf = Encrypted("", "owner123", s => { s.PermitModifyDocument = false; s.PermitAnnotations = false; s.PermitFormsFill = true; });
        Assert.False(PdfSecurity.Unlock(pdf, null).Protection!.CanModifyContent);
        Assert.True(PdfSecurity.Unlock(pdf, "owner123").Protection!.CanModifyContent);
        var modifiable = Encrypted("", "owner123", s => s.PermitModifyDocument = true);
        Assert.True(PdfSecurity.Unlock(modifiable, null).Protection!.CanModifyContent);
        Assert.True(PdfSecurity.Unlock(SampleForm(), null).Protection == null);
    }

    [Fact]
    public void UnencryptedFilesAreParsedOnce()
    {
        var result = PdfSecurity.Unlock(SampleForm(), null);
        Assert.NotNull(result.Document); // reused by the window instead of parsing the file again
        Assert.Null(PdfSecurity.Unlock(Encrypted("user123", "owner123"), "user123").Document);
    }

    [Fact]
    public void InlineEncryptInACrossReferenceStreamIsDecrypted()
    {
        var pdf = ToXrefStream(Encrypted("user123", "owner123"), inlineEncrypt: true);
        Assert.Contains("/Type /XRef", Encoding.Latin1.GetString(pdf));
        Assert.Equal(UnlockFailure.PasswordRequired, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(pdf, null)).Reason);
        var (plain, _) = PdfSecurity.Unlock(pdf, "user123");
        Assert.Equal("full_name", FirstFieldName(plain));
    }

    [Fact]
    public void CommentsInTheTrailerAreSkipped()
    {
        // A comment that looks like an /Encrypt entry must not be taken for one.
        var pdf = WithInlineEncrypt(Encrypted("user123", "owner123"));
        var text = Encoding.Latin1.GetString(pdf);
        int at = text.LastIndexOf("trailer", StringComparison.Ordinal);
        int dict = text.IndexOf("<<", at, StringComparison.Ordinal) + 2;
        pdf = RebuildXref(text[..dict] + " % /Encrypt 1 0 R /Root 999 0 R\n" + text[dict..]);
        var (plain, _) = PdfSecurity.Unlock(pdf, "user123");
        Assert.Equal("full_name", FirstFieldName(plain));
    }

    /// <summary>
    /// Rewrites a PDF with a classic xref table to use a cross-reference stream (as newer tools write), optionally
    /// with the /Encrypt dictionary inline in the stream's dictionary.
    /// </summary>
    private static byte[] ToXrefStream(byte[] pdf, bool inlineEncrypt)
    {
        var text = Encoding.Latin1.GetString(pdf);
        int xref = text.LastIndexOf("\nxref", StringComparison.Ordinal) + 1;
        var trailer = Regex.Match(text[xref..], @"trailer\s*<<(.*)>>", RegexOptions.Singleline).Groups[1].Value; // a comment may follow
        Assert.Contains("/Root", trailer);
        var offsets = Regex.Matches(text[..xref], @"(?<=\n)(\d+) 0 obj").ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Index);
        int self = offsets.Keys.Max() + 1, size = self + 1;

        string entries = Regex.Match(trailer, @"/Root\s+\d+ \d+ R").Value + " " + Regex.Match(trailer, @"/Info\s+\d+ \d+ R").Value + " " +
                         Regex.Match(trailer, @"/ID\s*\[[^\]]*\]").Value;
        var enc = Regex.Match(trailer, @"/Encrypt\s+(\d+) 0 R");
        if (inlineEncrypt)
        {
            var obj = Regex.Match(text, $@"\n{enc.Groups[1].Value} 0 obj\s*(<<.*?>>)\s*endobj", RegexOptions.Singleline).Groups[1].Value;
            entries += " /Encrypt " + obj;
        }
        else entries += " " + enc.Value;

        var data = new List<byte>();
        for (int i = 0; i < size; i++)
        {
            bool used = offsets.TryGetValue(i, out var off) || i == self;
            if (i == self) off = xref;
            data.Add((byte)(used ? 1 : 0));
            data.AddRange(BitConverter.GetBytes(used ? off : 0).Reverse());
            data.Add(0); data.Add(i == 0 ? (byte)0xFF : (byte)0);
        }
        var head = $"{self} 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] {entries} /Length {data.Count} >>\nstream\n";
        var tail = $"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n";
        return Encoding.Latin1.GetBytes(text[..xref] + head).Concat(data).Concat(Encoding.Latin1.GetBytes(tail)).ToArray();
    }

    [Fact]
    public void MissingAndWrongPasswordsAreReported()
    {
        var pdf = Encrypted("user123", "owner123");
        Assert.Equal(UnlockFailure.PasswordRequired, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(pdf, null)).Reason);
        Assert.Equal(UnlockFailure.WrongPassword, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(pdf, "nope")).Reason);
        Assert.Equal(UnlockFailure.WrongPassword, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(pdf, "USER123")).Reason);
    }

    [Fact]
    public void RestrictedFilesThatAllowFormFillingOpenWithoutAPassword()
    {
        var pdf = Encrypted("", "owner123", s => { s.PermitModifyDocument = false; s.PermitAnnotations = false; s.PermitFormsFill = true; });
        var (plain, protection) = PdfSecurity.Unlock(pdf, null);
        Assert.Equal("full_name", FirstFieldName(plain));
        Assert.Equal("", protection!.UserPassword);
    }

    [Fact]
    public void RestrictedFilesThatForbidChangesNeedTheOwnerPassword()
    {
        var pdf = Encrypted("", "owner123", s => { s.PermitModifyDocument = false; s.PermitAnnotations = false; s.PermitFormsFill = false; });
        Assert.Equal(UnlockFailure.EditingNotAllowed, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(pdf, null)).Reason);

        var (plain, protection) = PdfSecurity.Unlock(pdf, "owner123");
        Assert.Equal("full_name", FirstFieldName(plain));
        // The saved copy keeps the author's restriction.
        var saved = PdfSecurity.Protect(plain, protection!);
        Assert.Equal(UnlockFailure.EditingNotAllowed, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(saved, null)).Reason);
    }

    [Fact]
    public void InlineEncryptDictionariesAreDecrypted()
    {
        var pdf = WithInlineEncrypt(Encrypted("user123", "owner123"));
        Assert.Equal(UnlockFailure.PasswordRequired, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(pdf, null)).Reason);
        var (plain, _) = PdfSecurity.Unlock(pdf, "user123");
        Assert.Equal("full_name", FirstFieldName(plain));
    }

    [Fact]
    public void UnsignedPermissionValuesAreAccepted()
    {
        // Some producers write /P as an unsigned 32-bit number (4294963412 instead of -3884). Reported as a file
        // that wouldn't open even with the right password.
        var doc = PdfOpen.Open(SampleForm(), null);
        doc.SecuritySettings.UserPassword = "user123";
        doc.SecuritySettings.OwnerPassword = "owner123";
        doc.SecurityHandler.SetEncryptionToV2With128Bits();
        var text = Encoding.Latin1.GetString(Save(doc));
        var p = Regex.Match(text, @"/P\s*(-\d+)").Groups[1];
        var pdf = RebuildXref(text[..p.Index] + ((uint)int.Parse(p.Value)).ToString() + text[(p.Index + p.Length)..]);

        var (plain, protection) = PdfSecurity.Unlock(pdf, "user123");
        Assert.Equal("full_name", FirstFieldName(plain));
        Assert.Equal(int.Parse(p.Value), protection!.Permissions);
    }

    /// <summary>Recomputes the offsets of a single classic xref table after an edit that changed lengths.</summary>
    private static byte[] RebuildXref(string t)
    {
        int xref = t.LastIndexOf("\nxref", StringComparison.Ordinal) + 1;
        var offsets = Regex.Matches(t[..xref], @"(?<=\n)(\d+) 0 obj").ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Index);
        int size = offsets.Keys.Max() + 1;
        var sb = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f\r\n");
        for (int i = 1; i < size; i++) sb.Append(offsets.TryGetValue(i, out var off) ? $"{off:D10} 00000 n\r\n" : "0000000000 65535 f\r\n");
        int trailer = t.IndexOf("trailer", xref, StringComparison.Ordinal);
        int startxref = t.LastIndexOf("startxref", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(t[..xref] + sb + t[trailer..startxref] + $"startxref\n{xref}\n%%EOF\n");
    }

    [Fact]
    public void UnencryptedFilesAreUntouched()
    {
        var pdf = SampleForm();
        var (plain, protection) = PdfSecurity.Unlock(pdf, null);
        Assert.Same(pdf, plain);
        Assert.Null(protection);
    }

    [Fact]
    public Task MainWindowAsksForThePasswordOpensAndSavesProtected() => Ui.Run(async () =>
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "protected.pdf");
        File.WriteAllBytes(path, Encrypted("user123", "owner123"));

        var prompts = new List<string>();
        var answers = new Queue<string>(new[] { "wrong", "user123" });
        var win = new MainWindow { AskPassword = (msg, _) => { prompts.Add(msg); return new(answers.Dequeue(), false); } };
        await win.OpenAsync(path);

        Assert.Equal(2, prompts.Count);
        Assert.Contains("password protected", prompts[0]);
        Assert.Contains("not correct", prompts[1]);
        var fields = (List<FormFieldModel>)typeof(MainWindow).GetField("_fields", F)!.GetValue(win)!;
        Assert.Equal(7, fields.Count);

        fields.First(f => f.FullName == "full_name").Value = "Charles Tan";
        var save = (Task<bool>)typeof(MainWindow).GetMethod("SaveAsync", F)!.Invoke(win, new object[] { path, false })!;
        Assert.True(await save);
        typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false);

        var written = File.ReadAllBytes(path);
        Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(written, null));
        var (plain, _) = PdfSecurity.Unlock(written, "user123");
        Assert.Equal("Charles Tan", Fields(plain).First(f => f.FullName == "full_name").Value);
    });

    [Fact]
    public Task FormFillingOnlyDocumentsAllowFieldsButNotPageChanges() => Ui.Run(async () =>
    {
        var path = Path.Combine(TempDir(), "form.pdf");
        File.WriteAllBytes(path, Encrypted("", "owner123", s => { s.PermitModifyDocument = false; s.PermitAnnotations = false; s.PermitFormsFill = true; }));
        var win = new MainWindow { AskPassword = (_, _) => throw new InvalidOperationException("no prompt expected") };
        await win.OpenAsync(path);

        Assert.False(Get<bool>(win, "_readOnly"));
        Assert.False(Get<bool>(win, "_canEditContent"));
        Assert.False(Enabled(win, "ToolsBar"));
        Assert.False(Enabled(win, "SaveFlatMenu"));
        Assert.True(Enabled(win, "SaveButton"));
        Assert.Contains("(form filling only)", win.Title);
        var pages = Get<System.Collections.IList>(win, "_pages").Cast<PdfEditor.Controls.PageView>();
        Assert.Contains(pages, p => p.FieldLayer.Children.Count > 0); // the fields can still be filled in

        Assert.False(await InvokeSave(win, "SaveAsync", path, true)); // flattening rewrites page content
        Assert.Contains("filling in the form only", Status(win));

        Get<List<FormFieldModel>>(win, "_fields").First(f => f.FullName == "full_name").Value = "Charles Tan";
        Assert.True(await InvokeSave(win, "SaveAsync", path, false));
        typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false);
        var (plain, _) = PdfSecurity.Unlock(File.ReadAllBytes(path), null);
        Assert.Equal("Charles Tan", Fields(plain).First(f => f.FullName == "full_name").Value);
    });

    [Fact]
    public Task SaveAsOnAReadOnlyDocumentExplainsInsteadOfShowingADialog() => Ui.Run(async () =>
    {
        // Review finding: Ctrl+Shift+S showed the Save dialog and then silently saved nothing.
        var path = Path.Combine(TempDir(), "report.pdf");
        File.WriteAllBytes(path, ViewOnlyFile());
        var replies = new Queue<MainWindow.PasswordReply>(new MainWindow.PasswordReply[] { new("user123", false), new("", true) });
        var win = new MainWindow { AskPassword = (_, _) => replies.Dequeue() };
        await win.OpenAsync(path);
        Assert.True(Get<bool>(win, "_readOnly"));

        // Checked first so a regression fails here instead of opening a Save dialog on screen.
        Assert.False((bool)typeof(MainWindow).GetMethod("CanSave", F)!.Invoke(win, new object[] { false })!);
        Assert.False(await InvokeSave(win, "SaveAsAsync", false));
        Assert.Contains("read-only", Status(win));
    });

    [Fact]
    public Task CancellingThePasswordPromptOpensNothing() => Ui.Run(async () =>
    {
        var path = Path.Combine(TempDir(), "protected.pdf");
        File.WriteAllBytes(path, Encrypted("user123", "owner123"));
        var win = new MainWindow { AskPassword = (_, _) => null };
        await win.OpenAsync(path);
        Assert.Null(typeof(MainWindow).GetField("_bytes", F)!.GetValue(win));
    });

    /// <summary>Like the reported diagnostic report: an open password, and the author forbids all changes but allows printing.</summary>
    private static byte[] ViewOnlyFile() => Encrypted("user123", "owner123", s =>
    {
        s.PermitModifyDocument = false;
        s.PermitAnnotations = false;
        s.PermitFormsFill = false;
        s.PermitPrint = true;
    });

    [Fact]
    public void ViewOnlyOpensDocumentsWhoseAuthorForbidsChanges()
    {
        var pdf = ViewOnlyFile();
        Assert.Equal(UnlockFailure.EditingNotAllowed, Assert.Throws<PdfUnlockException>(() => PdfSecurity.Unlock(pdf, "user123")).Reason);

        var (plain, protection) = PdfSecurity.Unlock(pdf, "user123", viewOnly: true);
        Assert.Equal("full_name", FirstFieldName(plain));
        Assert.True(protection!.ViewOnly);
        Assert.True(protection.CanPrint);

        // Asking for view-only on an editable file changes nothing.
        Assert.False(PdfSecurity.Unlock(Encrypted("user123", "owner123"), "user123", viewOnly: true).Protection!.ViewOnly);
    }

    [Fact]
    public Task MainWindowOffersReadOnlyAndBlocksEditing() => Ui.Run(async () =>
    {
        var path = Path.Combine(TempDir(), "report.pdf");
        var original = ViewOnlyFile();
        File.WriteAllBytes(path, original);

        var prompts = new List<(string Message, bool OfferedReadOnly)>();
        var replies = new Queue<MainWindow.PasswordReply>(new MainWindow.PasswordReply[] { new("user123", false), new("", true) });
        var win = new MainWindow { AskPassword = (msg, offer) => { prompts.Add((msg, offer)); return replies.Dequeue(); } };
        await win.OpenAsync(path);

        Assert.Equal(2, prompts.Count);
        Assert.False(prompts[0].OfferedReadOnly); // just asks for the password first
        Assert.True(prompts[1].OfferedReadOnly);
        Assert.Contains("viewed but not changed", prompts[1].Message);

        Assert.True((bool)typeof(MainWindow).GetField("_readOnly", F)!.GetValue(win)!);
        Assert.NotNull(typeof(MainWindow).GetField("_bytes", F)!.GetValue(win));
        Assert.Contains("(read-only)", win.Title);
        Assert.False(((System.Windows.UIElement)win.FindName("ToolsBar")).IsEnabled);
        Assert.False(((System.Windows.UIElement)win.FindName("SaveButton")).IsEnabled);
        Assert.True(((System.Windows.UIElement)win.FindName("PrintButton")).IsEnabled);
        var pages = (System.Collections.IList)typeof(MainWindow).GetField("_pages", F)!.GetValue(win)!;
        Assert.All(pages.Cast<PdfEditor.Controls.PageView>(), p => Assert.Empty(p.FieldLayer.Children));

        var save = (Task<bool>)typeof(MainWindow).GetMethod("SaveAsync", F)!.Invoke(win, new object[] { path, false })!;
        Assert.False(await save);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False((bool)typeof(MainWindow).GetField("_dirty", F)!.GetValue(win)!);
    });

    [Fact]
    public Task OwnerPasswordStillUnlocksEditingAfterTheReadOnlyOffer() => Ui.Run(async () =>
    {
        var path = Path.Combine(TempDir(), "report.pdf");
        File.WriteAllBytes(path, ViewOnlyFile());
        var replies = new Queue<MainWindow.PasswordReply>(new MainWindow.PasswordReply[] { new("user123", false), new("owner123", false) });
        var win = new MainWindow { AskPassword = (_, _) => replies.Dequeue() };
        await win.OpenAsync(path);

        Assert.False((bool)typeof(MainWindow).GetField("_readOnly", F)!.GetValue(win)!);
        Assert.True(((System.Windows.UIElement)win.FindName("ToolsBar")).IsEnabled);
    });
}
