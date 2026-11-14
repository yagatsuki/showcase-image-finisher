using System.IO;
using System.Text.Json;

namespace ShowcaseImageFinisher;

/// <summary>The user's choices in the settings card, persisted between runs.</summary>
public sealed class AppSettings
{
    public string ModelId { get; set; } = "isnet-general-use";
    public string FormatTag { get; set; } = "Webp";
    public string Suffix { get; set; } = "_finished";
    public bool UseCustomDir { get; set; }
    public string CustomDir { get; set; } = "";
    public bool Overwrite { get; set; }
    public bool Defringe { get; set; }
    public bool LargestSubjectOnly { get; set; }
    public int Padding { get; set; }
}

public static class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShowcaseImageFinisher", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* a broken settings file must never block startup */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
        catch { /* settings are a convenience, not worth an error dialog */ }
    }
}
