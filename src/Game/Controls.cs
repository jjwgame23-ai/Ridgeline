using System.Text;
using Godot;

namespace Ridgeline;

/// <summary>
/// Every key and button the game listens for: one list, with each action's default binding and where it's used,
/// the player's own bindings over the top (user://keybinds.cfg, set in the main menu's Controls), and a check for
/// two actions on one key where both could be wanted at once. KEYBINDS.md is written from this list
/// (dev arg bindsdoc=KEYBINDS.md), so it can't drift from what the game does.
/// </summary>
public static class Controls
{
    /// <summary>Where an action is used. Two actions may share a key only if they're never wanted in the same place.</summary>
    [Flags]
    public enum Where
    {
        OnFoot = 1,
        Vehicle = 2,
        Drone = 4,
        Spectating = 8,
        Anywhere = OnFoot | Vehicle | Drone | Spectating,
    }

    public sealed record Action(string Id, string Label, string Group, Where Where, string[] Defaults, string Note = "");

    /// <summary>
    /// Bindings are written as "key:W", "key:Shift", "mouse:Left". Anything not listed here is fixed: Esc (let go
    /// of the mouse, close a menu), the number keys in a menu (squad commands, a role while waiting to respawn)
    /// and a vehicle's seats (1-9).
    /// </summary>
    public static readonly Action[] All =
    {
        new("move_forward", "Move forward", "Movement", Where.OnFoot | Where.Vehicle | Where.Drone | Where.Spectating, new[] { "key:W" }, "Helicopter: collective up"),
        new("move_back", "Move back", "Movement", Where.OnFoot | Where.Vehicle | Where.Drone | Where.Spectating, new[] { "key:S" }, "Helicopter: collective down"),
        new("move_left", "Move left", "Movement", Where.OnFoot | Where.Vehicle | Where.Drone | Where.Spectating, new[] { "key:A" }, "Helicopter: pedal left"),
        new("move_right", "Move right", "Movement", Where.OnFoot | Where.Vehicle | Where.Drone | Where.Spectating, new[] { "key:D" }, "Helicopter: pedal right"),
        new("jump", "Jump / stand up", "Movement", Where.OnFoot | Where.Vehicle | Where.Drone | Where.Spectating, new[] { "key:Space" }, "Vehicle: brake · helicopter: hover · drone and free camera: up"),
        new("sprint", "Sprint", "Movement", Where.OnFoot | Where.Drone | Where.Spectating, new[] { "key:Shift" }, "While aiming: hold your breath · drone and free camera: faster"),
        new("crouch", "Crouch", "Movement", Where.OnFoot | Where.Drone | Where.Spectating, new[] { "key:C", "key:Ctrl" }, "Drone and free camera: down"),
        new("prone", "Go prone", "Movement", Where.OnFoot, new[] { "key:Z" }),
        new("lean_left", "Lean left", "Movement", Where.OnFoot, new[] { "key:Q" }),
        new("lean_right", "Lean right", "Movement", Where.OnFoot, new[] { "key:E" }),

        new("fire", "Fire", "Weapons", Where.OnFoot | Where.Vehicle | Where.Drone, new[] { "mouse:Left" }, "Drone: drop / detonate"),
        new("aim", "Aim (hold)", "Weapons", Where.OnFoot | Where.Vehicle | Where.Drone, new[] { "mouse:Right" }, "Vehicle: zoom"),
        new("reload", "Reload, keeping the magazine", "Weapons", Where.OnFoot | Where.Vehicle, new[] { "key:R" }, "Tap again straight away to drop the magazine instead"),
        new("reload_drop", "Quick reload, dropping the magazine", "Weapons", Where.OnFoot, Array.Empty<string>(), "Unbound: double-tap Reload does the same"),
        new("firemode", "Fire mode", "Weapons", Where.OnFoot | Where.Vehicle, new[] { "key:V" }, "Vehicle: ammunition"),
        new("check_ammo", "Check magazine", "Weapons", Where.OnFoot, new[] { "key:T" }),
        new("weapon1", "Primary weapon", "Weapons", Where.OnFoot, new[] { "key:1" }),
        new("weapon2", "Secondary weapon", "Weapons", Where.OnFoot, new[] { "key:2" }),
        new("grenade", "Throw a frag", "Weapons", Where.OnFoot, new[] { "key:G" }),

        new("use", "Use / get in or out", "Actions", Where.OnFoot | Where.Vehicle, new[] { "key:F" }, "Doors, vehicles"),
        new("selfaid", "Bandage yourself", "Actions", Where.OnFoot | Where.Vehicle, new[] { "key:X" }, "Helicopter: flares"),
        new("gadget", "Role tool", "Actions", Where.OnFoot | Where.Drone, new[] { "key:H" }, "Medic kit, sandbags, quadcopter · drone: bring it home / ditch it"),
        new("drone_fpv", "Fly an FPV drone", "Actions", Where.OnFoot, new[] { "key:J" }),
        new("drone_fpv_at", "Fly an anti-tank FPV", "Actions", Where.OnFoot, new[] { "key:K" }),
        new("free_look", "Look around (hold)", "Actions", Where.Vehicle, new[] { "key:Alt" }),
        new("nvg", "Night vision goggles", "Actions", Where.OnFoot | Where.Vehicle, new[] { "key:L" }, "Up or down, if your side issues them to your role"),

        new("map", "Map", "Squad", Where.Anywhere, new[] { "key:M" }, "Pick a spawn there when dead; as squad leader, click to send the squad"),
        new("squad_follow", "Squad: on me", "Squad", Where.Anywhere, new[] { "key:B" }, "Squad leader"),
        new("squad_menu", "Squad commands", "Squad", Where.Anywhere, new[] { "key:N" }, "Squad leader; then a number"),

        new("spectate_next", "Next soldier", "Spectating", Where.Spectating, new[] { "mouse:Left" }),
        new("spectate_prev", "Previous soldier", "Spectating", Where.Spectating, new[] { "mouse:Right" }),
        new("spectate_view", "Chase / eyes view", "Spectating", Where.Spectating, new[] { "key:V" }),
        new("spectate_free", "Free camera", "Spectating", Where.Spectating, new[] { "key:F" }),

        new("help", "Help", "Game", Where.Anywhere, new[] { "key:F1" }),
        new("debug_overlay", "Debug overlay", "Game", Where.Anywhere, new[] { "key:F3" }),
        new("sniper_drill", "Sniper drill (firing range)", "Game", Where.Anywhere, new[] { "key:F4" }),
        new("distant_battle", "Distant battle on/off (firing range)", "Game", Where.Anywhere, new[] { "key:F5" }),
        new("bot_debug", "Bot debug", "Game", Where.Anywhere, new[] { "key:F6" }),
        new("volume_down", "Volume down", "Game", Where.Anywhere, new[] { "key:F7" }),
        new("volume_up", "Volume up", "Game", Where.Anywhere, new[] { "key:F8" }),
        new("pause", "Pause", "Game", Where.Anywhere, new[] { "key:P" }),
        new("main_menu", "Main menu", "Game", Where.Anywhere, new[] { "key:F10" }),
        new("fullscreen", "Fullscreen", "Game", Where.Anywhere, new[] { "key:F11" }),
    };

    const string FilePath = "user://keybinds.cfg";
    static readonly Dictionary<string, string[]> _custom = new();

    public static Action? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>What an action is bound to right now: the player's choice, else the default.</summary>
    public static string[] Current(string id) => _custom.TryGetValue(id, out var c) ? c : Find(id)?.Defaults ?? Array.Empty<string>();

    public static bool IsDefault(string id) => !_custom.ContainsKey(id);

    public static void Register()
    {
        Load();
        Apply();
    }

    /// <summary>Put the bindings into Godot's InputMap (again, after a change).</summary>
    public static void Apply()
    {
        foreach (var a in All)
        {
            if (InputMap.HasAction(a.Id)) InputMap.ActionEraseEvents(a.Id);
            else InputMap.AddAction(a.Id);
            foreach (var b in Current(a.Id))
                if (ToEvent(b) is { } e) InputMap.ActionAddEvent(a.Id, e);
        }
    }

    /// <summary>Bind <paramref name="slot"/> (0 or 1) of an action; null clears it.</summary>
    public static void Set(string id, int slot, string? binding)
    {
        var cur = Current(id).ToList();
        while (cur.Count <= slot) cur.Add("");
        cur[slot] = binding ?? "";
        var list = cur.Where(b => b != "").Distinct().ToArray();
        var def = Find(id)?.Defaults ?? Array.Empty<string>();
        if (list.SequenceEqual(def)) _custom.Remove(id); else _custom[id] = list;
        Apply();
        Save();
    }

    public static void Reset(string id)
    {
        _custom.Remove(id);
        Apply();
        Save();
    }

    public static void ResetAll()
    {
        _custom.Clear();
        Apply();
        Save();
    }

    /// <summary>
    /// Keys bound to two actions that can be wanted in the same place (on foot, in a vehicle, flying a drone,
    /// spectating): each pair once, with the binding they share.
    /// </summary>
    public static List<(Action A, Action B, string Binding)> Conflicts()
    {
        var found = new List<(Action, Action, string)>();
        for (int i = 0; i < All.Length; i++)
        for (int j = i + 1; j < All.Length; j++)
        {
            if ((All[i].Where & All[j].Where) == 0) continue;
            foreach (var b in Current(All[i].Id))
                if (Current(All[j].Id).Contains(b)) found.Add((All[i], All[j], b));
        }
        return found;
    }

    // ---------------------------------------------------------------- bindings as text

    /// <summary>"key:W" or "mouse:Left" for a key or mouse button event; null for anything else.</summary>
    public static string? FromEvent(InputEvent e) => e switch
    {
        InputEventKey k => $"key:{OS.GetKeycodeString(k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode)}",
        InputEventMouseButton m => $"mouse:{m.ButtonIndex}",
        _ => null,
    };

    static InputEvent? ToEvent(string b)
    {
        int c = b.IndexOf(':');
        if (c < 0) return null;
        string kind = b[..c], name = b[(c + 1)..];
        if (kind == "key")
        {
            var key = OS.FindKeycodeFromString(name);
            return key == Key.None ? null : new InputEventKey { PhysicalKeycode = key };
        }
        if (kind == "mouse" && Enum.TryParse<MouseButton>(name, out var mb)) return new InputEventMouseButton { ButtonIndex = mb };
        return null;
    }

    /// <summary>How a binding reads: "W", "Shift", "LMB", "Mouse 4".</summary>
    public static string Name(string b)
    {
        int c = b.IndexOf(':');
        if (c < 0) return b;
        string kind = b[..c], name = b[(c + 1)..];
        if (kind == "mouse")
            return name switch
            {
                "Left" => "LMB", "Right" => "RMB", "Middle" => "MMB",
                "WheelUp" => "Wheel up", "WheelDown" => "Wheel down",
                "Xbutton1" => "Mouse 4", "Xbutton2" => "Mouse 5",
                _ => name,
            };
        return name;
    }

    /// <summary>What an action is on, for help text: "C / Ctrl", or "unbound".</summary>
    public static string Keys(string id)
    {
        var cur = Current(id);
        return cur.Length == 0 ? "unbound" : string.Join(" / ", cur.Select(Name));
    }

    /// <summary>Help text with "{action}" replaced by whatever it's bound to now.</summary>
    public static string Fill(string text)
    {
        foreach (var a in All)
            if (text.Contains("{" + a.Id + "}")) text = text.Replace("{" + a.Id + "}", Keys(a.Id));
        return text;
    }

    // ---------------------------------------------------------------- saved

    static void Load()
    {
        _custom.Clear();
        var cfg = new ConfigFile();
        if (cfg.Load(FilePath) != Error.Ok || !cfg.HasSection("binds")) return;
        foreach (var id in cfg.GetSectionKeys("binds"))
            if (Find(id) != null) _custom[id] = cfg.GetValue("binds", id).AsStringArray();
    }

    static void Save()
    {
        var cfg = new ConfigFile();
        foreach (var (id, b) in _custom) cfg.SetValue("binds", id, b);
        cfg.Save(FilePath);
    }

    /// <summary>KEYBINDS.md: every action and its default binding, by group, and the conflicts among the defaults (none, we hope).</summary>
    public static void WriteDoc(string path)
    {
        var sb = new StringBuilder();
        sb.Append("# Ridgeline key bindings\n\n");
        sb.Append("The defaults, as `src/Game/Controls.cs` defines them (this file is written from it: run the game with `-- bindsdoc=KEYBINDS.md`). ");
        sb.Append("Change any of them in the main menu, under **Controls**; your own bindings are kept in `user://keybinds.cfg`.\n\n");
        foreach (var g in All.GroupBy(a => a.Group))
        {
            sb.Append("## ").Append(g.Key).Append("\n\n| Action | Default | Notes |\n|---|---|---|\n");
            foreach (var a in g)
                sb.Append("| ").Append(a.Label).Append(" | ").Append(a.Defaults.Length == 0 ? "unbound" : string.Join(" / ", a.Defaults.Select(Name)))
                  .Append(" | ").Append(a.Note).Append(" |\n");
            sb.Append('\n');
        }
        sb.Append("## Fixed keys\n\n");
        sb.Append("- **Esc**: let go of the mouse; close the squad command menu.\n");
        sb.Append("- **1–9, 0**: in the squad command menu, pick a command; while waiting to respawn, pick your role (0 steps through the roles past 9).\n");
        sb.Append("- **1–9**: in a vehicle, change seats.\n\n");
        sb.Append("## Shared keys\n\n");
        sb.Append("Some keys do two jobs where the two can never be wanted at once (a key for a weapon on foot and a seat in a vehicle, say). ");
        sb.Append("Two actions on one key where both could be wanted in the same place are a conflict, and the Controls menu flags them.\n\n");
        var clash = new List<string>();
        for (int i = 0; i < All.Length; i++)
        for (int j = i + 1; j < All.Length; j++)
            if ((All[i].Where & All[j].Where) != 0)
                foreach (var b in All[i].Defaults)
                    if (All[j].Defaults.Contains(b)) clash.Add($"- {Name(b)}: {All[i].Label} and {All[j].Label}\n");
        sb.Append(clash.Count == 0 ? "The defaults have no conflicts.\n" : "Conflicts among the defaults:\n\n" + string.Concat(clash));
        File.WriteAllText(path, sb.ToString());
    }
}
