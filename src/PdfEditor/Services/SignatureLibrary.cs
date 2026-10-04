using System.IO;

namespace PdfEditor.Services;

/// <summary>Stores reusable signature / initials images as PNG files in the user's local app data.</summary>
public static class SignatureLibrary
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfEditor", "Signatures");

    public static IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<string>();
        return Directory.GetFiles(Folder, "*.png")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public static string Add(byte[] png)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"signature-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    public static void Delete(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(Folder) && File.Exists(path))
            File.Delete(path);
    }
}
