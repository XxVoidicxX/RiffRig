using System.IO;
using System.Text.Json;
using RiffRig.Dsp;

namespace RiffRig;

public sealed class AppSettings
{
    public string InputId { get; set; }
    public string OutputId { get; set; }
    public string SendId { get; set; }
    public int LatencyMs { get; set; } = 10;
    public float InputGainDb { get; set; } = 6;
    public float OutputGainDb { get; set; } = 0;
    public float SendGainDb { get; set; } = 0;
    public int TuningIndex { get; set; } = 0;
    public bool MuteWhileTuning { get; set; }
    public string Preset { get; set; } = "115 Metal";
    public AmpData Amp { get; set; } = AmpData.From(AmpPreset.All[2].Settings);

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RiffRig");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}

/// <summary>JSON-friendly copy of AmpSettings (System.Text.Json skips public fields by default).</summary>
public sealed class AmpData
{
    public bool Drive { get; set; } = true;
    public bool Boost { get; set; } = true;
    public bool Cab { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public float Gate { get; set; }
    public float Gain { get; set; }
    public float Bass { get; set; }
    public float Mid { get; set; }
    public float Treble { get; set; }
    public float Presence { get; set; }
    public float Volume { get; set; }

    public static AmpData From(AmpSettings s) => new()
    {
        Drive = s.Channel == AmpChannel.Drive, Boost = s.Boost, Cab = s.Cab, Enabled = s.Enabled,
        Gate = s.Gate, Gain = s.Gain, Bass = s.Bass, Mid = s.Mid, Treble = s.Treble, Presence = s.Presence, Volume = s.Volume,
    };

    public AmpSettings ToSettings() => new()
    {
        Channel = Drive ? AmpChannel.Drive : AmpChannel.Clean, Boost = Boost, Cab = Cab, Enabled = Enabled,
        Gate = Gate, Gain = Gain, Bass = Bass, Mid = Mid, Treble = Treble, Presence = Presence, Volume = Volume,
    };
}
