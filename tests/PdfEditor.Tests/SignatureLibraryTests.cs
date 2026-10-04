using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using PdfEditor.Dialogs;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>
/// The encrypted signature library. Every test points the library at its own temp folder; the user's real
/// signature folder is never read or written.
/// </summary>
public sealed class SignatureLibraryTests : IDisposable
{
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
    private readonly string _realFolder = SignatureLibrary.Folder;
    private readonly string _folder = TempDir();

    public SignatureLibraryTests() => SignatureLibrary.Folder = _folder;

    public void Dispose()
    {
        SignatureLibrary.Folder = _realFolder;
        try { Directory.Delete(_folder, true); } catch { /* best effort */ }
    }

    [Fact]
    public void SignaturesAreStoredEncrypted()
    {
        var png = SignaturePng();
        var path = SignatureLibrary.Add(png);
        var stored = File.ReadAllBytes(path);
        Assert.EndsWith(".sig", path);
        Assert.False(Contains(stored, PngHeader[..4]));
        Assert.False(Contains(stored, png.Skip(40).Take(32).ToArray()));
        Assert.Equal(png, SignatureLibrary.Load(path));
    }

    [Fact]
    public void DamagedOrForeignFilesAreRejected()
    {
        var path = SignatureLibrary.Add(SignaturePng());
        var stored = File.ReadAllBytes(path);

        var damaged = (byte[])stored.Clone();
        damaged[^5] ^= 0xFF;
        File.WriteAllBytes(path, damaged);
        Assert.ThrowsAny<Exception>(() => SignatureLibrary.Load(path));

        var renamed = Path.Combine(_folder, "renamed.sig");
        File.WriteAllBytes(renamed, SignaturePng());
        Assert.ThrowsAny<Exception>(() => SignatureLibrary.Load(renamed));

        // DPAPI alone (without the app's entropy) can't open it either.
        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(stored[10..], null, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void PlainPngsFromOlderVersionsAreMigratedInOrder()
    {
        var older = SignaturePng(2);
        var newer = SignaturePng(3);
        var p1 = Path.Combine(_folder, "signature-1.png");
        var p2 = Path.Combine(_folder, "signature-2.png");
        File.WriteAllBytes(p1, older); File.SetLastWriteTimeUtc(p1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllBytes(p2, newer); File.SetLastWriteTimeUtc(p2, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var list = SignatureLibrary.List();
        Assert.Equal(2, list.Count);
        Assert.False(File.Exists(p1) || File.Exists(p2));
        Assert.Equal(newer, SignatureLibrary.Load(list[0]));
        Assert.Equal(older, SignatureLibrary.Load(list[1]));
        Assert.All(Directory.GetFiles(_folder), f => Assert.False(Contains(File.ReadAllBytes(f), PngHeader)));
    }

    [Fact]
    public void APngThatIsInUseIsKeptAndMigratedLater()
    {
        var png = SignaturePng(4);
        var path = Path.Combine(_folder, "signature-locked.png");
        File.WriteAllBytes(path, png);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Single(SignatureLibrary.List());
            Assert.True(File.Exists(path));
        }
        var list = SignatureLibrary.List();
        Assert.Single(list);
        Assert.False(File.Exists(path));
        Assert.Equal(png, SignatureLibrary.Load(list[0]));
    }

    [Fact]
    public void DeleteOnlyRemovesFilesInTheLibrary()
    {
        var outside = Path.Combine(TempDir(), "elsewhere.sig");
        File.WriteAllBytes(outside, new byte[] { 1 });
        SignatureLibrary.Delete(outside);
        Assert.True(File.Exists(outside));

        var inside = SignatureLibrary.Add(SignaturePng());
        SignatureLibrary.Delete(inside);
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public Task SignatureDialogShowsAndHandsOverDecryptedSignatures() => Ui.Run(() =>
    {
        var png = SignaturePng(5);
        SignatureLibrary.Add(png);
        var dlg = new SignatureDialog();
        var list = (System.Windows.Controls.ListBox)dlg.FindName("List");
        var entry = Assert.Single(((System.Collections.IEnumerable)list.ItemsSource).Cast<SignatureDialog.Entry>());
        Assert.Equal(240, entry.Image.PixelWidth);
        list.SelectedItem = entry;
        // Use_Click hands over the PNG, then sets DialogResult, which WPF only allows while the dialog is shown.
        try { typeof(SignatureDialog).GetMethod("Use_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dlg, new object?[] { null, null }); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
        Assert.Equal(png, dlg.SelectedPng);
    });
}
