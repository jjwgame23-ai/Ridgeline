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
    /// <summary>Territory: how long before the match is decided on ground held, if no side has lost by then (0: no limit).</summary>
    public static int MatchMinutes = 180;
    /// <summary>The hour a match starts at; -1 random (see Conditions).</summary>
    public static double StartHour = 12.0;
    /// <summary>"Clear", "Overcast", "Rain", "Fog", or "Random".</summary>
    public static string Weather = "Clear";
    /// <summary>World seconds per match second: 0 stops the clock, 1 is real time.</summary>
    public static float TimeScale = 4f;
    public static readonly int[] MatchLengths = { 60, 120, 180, 0 };

    // ---- graphics
    public enum DisplayMode { Windowed, Borderless, Exclusive }
    public static DisplayMode Display = DisplayMode.Windowed;
    public static bool VSync = true;
    /// <summary>Frame cap; 0 = none.</summary>
    public static int MaxFps = 0;
    /// <summary>3D resolution as a fraction of the window (the HUD stays sharp).</summary>
    public static float RenderScale = 1f;
    /// <summary>0 off, 1 2x, 2 4x.</summary>
    public static int Msaa = 0;
    /// <summary>0 off, 1 low (short range), 2 high.</summary>
    public static int Shadows = 2;
    /// <summary>Screenshot and test runs keep a window whatever the setting.</summary>
    public static bool ForceWindowed;
    public static readonly int[] FpsCaps = { 0, 60, 120, 144, 165, 240 };
    public static readonly float[] Scales = { 0.5f, 0.67f, 0.75f, 0.85f, 1f };
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
        MatchMinutes = cfg.GetValue("game", "match_minutes", 180).AsInt32();
        StartHour = cfg.GetValue("game", "start_hour", 12.0).AsDouble();
        Weather = cfg.GetValue("game", "weather", "Clear").AsString();
        TimeScale = (float)cfg.GetValue("game", "time_scale", 4.0).AsDouble();
        Display = (DisplayMode)cfg.GetValue("graphics", "display", 0).AsInt32();
        VSync = cfg.GetValue("graphics", "vsync", true).AsBool();
        MaxFps = cfg.GetValue("graphics", "max_fps", 0).AsInt32();
        RenderScale = (float)cfg.GetValue("graphics", "render_scale", 1.0).AsDouble();
        Msaa = cfg.GetValue("graphics", "msaa", 0).AsInt32();
        Shadows = cfg.GetValue("graphics", "shadows", 2).AsInt32();
        Apply();
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("audio", "volume", Volume);
        cfg.SetValue("game", "role", (int)PlayerRole);
        cfg.SetValue("game", "map", Map);
        cfg.SetValue("game", "front", FrontLine);
        cfg.SetValue("game", "match_minutes", MatchMinutes);
        cfg.SetValue("game", "start_hour", StartHour);
        cfg.SetValue("game", "weather", Weather);
        cfg.SetValue("game", "time_scale", TimeScale);
        cfg.SetValue("graphics", "display", (int)Display);
        cfg.SetValue("graphics", "vsync", VSync);
        cfg.SetValue("graphics", "max_fps", MaxFps);
        cfg.SetValue("graphics", "render_scale", RenderScale);
        cfg.SetValue("graphics", "msaa", Msaa);
        cfg.SetValue("graphics", "shadows", Shadows);
        cfg.Save(Path);
    }

    public static void Apply()
    {
        float db = Volume <= 0.001f ? -80f : 20f * MathF.Log10(Volume) + 6f; // the master limiter keeps peaks in check
        AudioServer.SetBusVolumeDb(0, db);
        ApplyGraphics();
    }

    /// <summary>Window mode, vsync, frame cap, render scale and anti-aliasing (shadows are read when the scene's sun is made).</summary>
    public static void ApplyGraphics()
    {
        if (DisplayServer.GetName() == "headless") return;
        var mode = ForceWindowed ? DisplayMode.Windowed : Display;
        var want = mode switch
        {
            DisplayMode.Borderless => DisplayServer.WindowMode.Fullscreen,
            DisplayMode.Exclusive => DisplayServer.WindowMode.ExclusiveFullscreen,
            _ => DisplayServer.WindowMode.Windowed,
        };
        if (DisplayServer.WindowGetMode() != want) DisplayServer.WindowSetMode(want);
        DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = MaxFps;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            var root = tree.Root;
            root.Scaling3DScale = Mathf.Clamp(RenderScale, 0.25f, 1f);
            root.Scaling3DMode = RenderScale < 0.99f ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
            root.Msaa3D = Msaa switch { 1 => Viewport.Msaa.Msaa2X, 2 => Viewport.Msaa.Msaa4X, _ => Viewport.Msaa.Disabled };
        }
    }

    /// <summary>F11: flip between a window and borderless fullscreen, and remember it.</summary>
    public static void ToggleFullscreen()
    {
        Display = Display == DisplayMode.Windowed ? DisplayMode.Borderless : DisplayMode.Windowed;
        ApplyGraphics();
        Save();
    }

    public static void Nudge(float by)
    {
        Volume = Mathf.Clamp(MathF.Round((Volume + by) * 10f) / 10f, 0f, 2f);
        Apply();
        Save();
        Hud.Toast($"Volume {Volume * 100:0}%  ({Controls.Keys("volume_down")} / {Controls.Keys("volume_up")})", 1.5f);
    }
}
