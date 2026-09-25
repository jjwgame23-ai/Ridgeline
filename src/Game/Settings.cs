using Godot;

namespace Ridgeline;

/// <summary>Player preferences, saved to user://settings.cfg.</summary>
public static class Settings
{
    const string Path = "user://settings.cfg";
    /// <summary>Master volume, 0..2 (1 = default). The default already sits 6 dB above the raw mix.</summary>
    public static float Volume = 1f;
    public static Role PlayerRole = Role.Rifleman;
    /// <summary>The battlefield Territory and KOTH are played on (a MapSpec id).</summary>
    public static string Map = "valley";
    /// <summary>Territory with a front line: points must be taken in order, linked from your own.</summary>
    public static bool FrontLine = true;
    static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) { Apply(); return; }
        Volume = (float)cfg.GetValue("audio", "volume", 1f).AsDouble();
        PlayerRole = (Role)cfg.GetValue("game", "role", (int)Role.Rifleman).AsInt32();
        Map = cfg.GetValue("game", "map", "valley").AsString();
        FrontLine = cfg.GetValue("game", "front", true).AsBool();
        Apply();
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("audio", "volume", Volume);
        cfg.SetValue("game", "role", (int)PlayerRole);
        cfg.SetValue("game", "map", Map);
        cfg.SetValue("game", "front", FrontLine);
        cfg.Save(Path);
    }

    public static void Apply()
    {
        float db = Volume <= 0.001f ? -80f : 20f * MathF.Log10(Volume) + 6f; // the master limiter keeps peaks in check
        AudioServer.SetBusVolumeDb(0, db);
    }

    public static void Nudge(float by)
    {
        Volume = Mathf.Clamp(MathF.Round((Volume + by) * 10f) / 10f, 0f, 2f);
        Apply();
        Save();
        Hud.Toast($"Volume {Volume * 100:0}%  (F7 / F8)", 1.5f);
    }
}
