using System.IO;
using System.Text.Json;

namespace PdfEditor.Services;

/// <summary>Small per-user preferences, kept in %LOCALAPPDATA%\PdfEditor\settings.json.</summary>
public sealed record UserSettings
{
    /// <summary>Font, bold and italic for newly placed text.</summary>
    public string TextFont { get; init; } = "Arial";
    public bool TextBold { get; init; }
    public bool TextItalic { get; init; }

    /// <summary>The settings file (tests point this at a temporary folder).</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfEditor", "settings.json");

    /// <summary>Reads the settings, or returns defaults if the file is missing or unreadable.</summary>
    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(FilePath)) ?? new() : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>Writes the settings; failures are ignored (preferences are a convenience).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // ignore: the next launch simply uses defaults
        }
    }
}
