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
    private static byte[] Encrypted(string user, string owner, Action<PdfSharp.Pdf.Security.PdfSecuritySettings>? permissions = null)
    {
        var doc = PdfOpen.Open(SampleForm(), null);
        doc.SecuritySettings.UserPassword = user;
        doc.SecuritySettings.OwnerPassword = owner;
        permissions?.Invoke(doc.SecuritySettings);
        return Save(doc);
    }

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
        Assert.True(protection.OpenPasswordChanged);
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
        var answers = new Queue<string?>(new[] { "wrong", "user123" });
        var win = new MainWindow { AskPassword = msg => { prompts.Add(msg); return answers.Dequeue(); } };
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
    public Task CancellingThePasswordPromptOpensNothing() => Ui.Run(async () =>
    {
        var path = Path.Combine(TempDir(), "protected.pdf");
        File.WriteAllBytes(path, Encrypted("user123", "owner123"));
        var win = new MainWindow { AskPassword = _ => null };
        await win.OpenAsync(path);
        Assert.Null(typeof(MainWindow).GetField("_bytes", F)!.GetValue(win));
    });
}
