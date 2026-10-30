using System.Text.Json;

namespace Cinema3D;

public class MovieSettings
{
    public StereoLayout StereoLayout { get; set; } = StereoLayout.Auto;
    public bool Swap { get; set; }
    public double Pos { get; set; }
    /// <summary>Номер звукового потока в файле (как у ffprobe), -1 = выбрать автоматически (русский, если есть).</summary>
    public int AudioStream { get; set; } = -1;
    /// <summary>MP3/WAV рядом с приложением; пусто — встроенная дорожка фильма.</summary>
    public string ExternalAudio { get; set; } = "";
}

/// <summary>Настройки рядом с программой: cinema3d-settings.json.</summary>
public class Settings
{
    public OutMode OutMode { get; set; } = OutMode.Dlp;
    public float Volume { get; set; } = 1f;
    public string Screen { get; set; } = "";
    public int AvOffsetMs { get; set; }
    public bool TestSwap { get; set; }
    public Dictionary<string, MovieSettings> Movies { get; set; } = new();

    static string FilePath => Path.Combine(App.Root, "cinema3d-settings.json");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt)); } catch { }
    }

    public MovieSettings For(Movie m)
    {
        if (!Movies.TryGetValue(m.Key, out var s)) Movies[m.Key] = s = new MovieSettings();
        return s;
    }
}
