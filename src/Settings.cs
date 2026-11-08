using System.IO;
using System.Text.Json;

namespace ValRec;

public class Settings
{
    public int Fps { get; set; } = 60;
    public int Mbps { get; set; } = 12;
    public bool Audio { get; set; } = true;
    public ColorAdjust Color { get; set; } = ColorAdjust.Obs();
    public bool BlurQuality { get; set; }
    public int ExportMbps { get; set; } = 20;
    public string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VAL Rec");

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ValRec", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
