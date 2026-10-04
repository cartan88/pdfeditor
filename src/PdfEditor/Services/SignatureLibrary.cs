using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PdfEditor.Services;

/// <summary>
/// Stores reusable signature / initials images in the user's local app data, encrypted with Windows DPAPI for the
/// current user: only this Windows account on this PC can read them, so copied files or other users get nothing.
/// Files are PNG bytes protected by DPAPI, behind a short header, with the .sig extension.
/// Plain .png signatures saved by older versions are encrypted and removed the first time the library is listed.
/// </summary>
public static class SignatureLibrary
{
    private const string Extension = ".sig";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PDFEDSIG1\n");
    // Extra input to DPAPI so these blobs can't be decrypted by code that doesn't know it's a PDF Editor signature.
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("PdfEditor.Signature.v1");

    /// <summary>The library folder (tests point this at a temporary folder).</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfEditor", "Signatures");

    public static IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<string>();
        MigratePlainFiles();
        return Directory.GetFiles(Folder, "*" + Extension)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Encrypts and stores a signature; returns its path.</summary>
    public static string Add(byte[] png)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"signature-{DateTime.Now:yyyyMMdd-HHmmss-fff}{Extension}");
        Write(path, png);
        return path;
    }

    /// <summary>Decrypts a stored signature to its PNG bytes. Throws if it isn't a signature this user can read.</summary>
    public static byte[] Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length <= Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Not a PDF Editor signature file.");
        return ProtectedData.Unprotect(data.AsSpan(Magic.Length).ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    public static void Delete(string path)
    {
        if (IsInFolder(path) && File.Exists(path))
            File.Delete(path);
    }

    private static void Write(string path, byte[] png)
    {
        var blob = ProtectedData.Protect(png, Entropy, DataProtectionScope.CurrentUser);
        var temp = path + ".tmp";
        using (var fs = File.Create(temp))
        {
            fs.Write(Magic);
            fs.Write(blob);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Encrypts plain .png signatures left by older versions. Each original is removed only after the encrypted copy
    /// has been read back and matches it exactly, and is overwritten with zeros first so the image doesn't linger
    /// in the file's old disk space (best effort: SSDs may still keep old blocks).
    /// </summary>
    private static void MigratePlainFiles()
    {
        foreach (var png in Directory.GetFiles(Folder, "*.png"))
        {
            try
            {
                var bytes = File.ReadAllBytes(png);
                var target = Path.ChangeExtension(png, Extension);
                if (!File.Exists(target)) Write(target, bytes);
                if (!Load(target).AsSpan().SequenceEqual(bytes)) continue; // keep the original if anything is off
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(png)); // keep the library's order
                using (var fs = new FileStream(png, FileMode.Open, FileAccess.Write))
                {
                    fs.Write(new byte[bytes.Length]);
                    fs.Flush(flushToDisk: true);
                }
                File.Delete(png);
            }
            catch (Exception)
            {
                // Leave this file as it is; it will be tried again next time.
            }
        }
    }

    private static bool IsInFolder(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase);
}
