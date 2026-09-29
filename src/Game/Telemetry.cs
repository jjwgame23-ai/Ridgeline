using System.Globalization;
using System.Text;
using Godot;

namespace Ridgeline;

/// <summary>
/// telemetry=path (dev arg, Territory mode): the match as it happens, written to a JSON-lines file for
/// looking at fights afterwards (tools/replay.py makes a replay to watch; tools/telemetry.py draws the fights
/// and measures them). A folder (record.cmd passes one) gets a new file for each match, named for the map and
/// the time it started.
/// - The first line is the map: terrain heights, buildings, trees, the points and the bases, so a plot
///   can draw the ground under it; and the conditions (start hour, weather, clock speed, moon, visibility).
/// - Twice a second, everyone: where they are, what they're doing (state, stance, target, suppression,
///   health, ammo) and every vehicle and drone.
/// - As they happen: every shot (from, towards, at whom, how), hit, casualty, explosion, change of
///   state (with the reason), squad drill and capture.
/// Nothing here changes the game; with no telemetry= argument none of it runs.
/// </summary>
public static class Telemetry
{
    static StreamWriter? _w;
    public static bool On => _w != null;
    static double _nextSample, _nextFlush, _nextScore;
    const double Every = 0.5;
    static readonly StringBuilder _sb = new();
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string F(float v) => v.ToString("0.#", Inv);
    static string F2(float v) => v.ToString("0.##", Inv);
    static string T => Clock.Now.ToString("0.00", Inv);
    static string S(string s) => System.Text.Json.JsonSerializer.Serialize(s);
    static string P(Vector3 p) => $"[{F(p.X)},{F(p.Y)},{F(p.Z)}]";

    public static string? PathFromArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("telemetry=")) return a["telemetry=".Length..];
        return null;
    }

    public static void Start(string path, TerritoryMode m)
    {
        Stop();
        if (path.EndsWith('/') || path.EndsWith('\\') || Directory.Exists(path))
        {
            try { Directory.CreateDirectory(path); }
            catch (Exception e) { GD.PrintErr($"telemetry: can't make {path}: {e.Message}"); return; }
            path = System.IO.Path.Combine(path, $"{m.Map.Spec.Id}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.jsonl");
        }
        try { _w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16); }
        catch (Exception e) { GD.PrintErr($"telemetry: can't write {path}: {e.Message}"); return; }
        _nextSample = 0;
        _nextScore = 0;
        Combatants.Killed += OnKilled;
        Combatants.Down += OnDowned;
        Comms.Said += OnSaid;
        Radio.Heard += OnRadio;
        WriteMap(m);
        GD.Print($"telemetry: writing to {path}");
    }

    public static void Stop()
    {
        if (_w == null) return;
        Combatants.Killed -= OnKilled;
        Combatants.Down -= OnDowned;
        Comms.Said -= OnSaid;
        Radio.Heard -= OnRadio;
        _w.Flush();
        _w.Dispose();
        _w = null;
    }

    static void Line(string s)
    {
        _w!.Write(s);
        _w.Write('\n');
    }

    // ---------------------------------------------------------------- the map

    static void WriteMap(TerritoryMode m)
    {
        var map = m.Map;
        var t = map.Terrain;
        // Heights, downsampled to at most 512 a side, as 16-bit steps between the lowest and highest.
        int step = Math.Max(1, (t.Res - 1 + 511) / 512);
        int n = (t.Res - 1) / step + 1;
        float lo = float.MaxValue, hi = float.MinValue;
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float x = -t.Extent + i * step * t.Spacing, z = -t.Extent + j * step * t.Spacing;
            float v = t.HeightAt(x, z);
            h[j * n + i] = v;
            lo = MathF.Min(lo, v);
            hi = MathF.Max(hi, v);
        }
        var hb = new byte[n * n * 2];
        for (int k = 0; k < h.Length; k++)
        {
            ushort q = (ushort)Math.Clamp((int)((h[k] - lo) / MathF.Max(hi - lo, 0.01f) * 65535f), 0, 65535);
            hb[k * 2] = (byte)q;
            hb[k * 2 + 1] = (byte)(q >> 8);
        }
        // Buildings: the 4 m cells with something built on them. Trees: each trunk, to the metre.
        var cells = map.Occupied.ToArray();
        var cb = new byte[cells.Length * 4];
        for (int k = 0; k < cells.Length; k++)
        {
            BitConverter.TryWriteBytes(cb.AsSpan(k * 4), (short)cells[k].Item1);
            BitConverter.TryWriteBytes(cb.AsSpan(k * 4 + 2), (short)cells[k].Item2);
        }
        var trees = t.TreePositions().ToArray();
        var tb = new byte[trees.Length * 4];
        for (int k = 0; k < trees.Length; k++)
        {
            BitConverter.TryWriteBytes(tb.AsSpan(k * 4), (short)MathF.Round(trees[k].X));
            BitConverter.TryWriteBytes(tb.AsSpan(k * 4 + 2), (short)MathF.Round(trees[k].Y));
        }
        _sb.Clear();
        _sb.Append("{\"k\":\"map\",\"id\":").Append(S(map.Spec.Id)).Append(",\"name\":").Append(S(map.Spec.Name))
           .Append(",\"size\":").Append(F(map.Size)).Append(",\"seed\":").Append(map.Seed)
           .Append(",\"teams\":[\"ALPHA\",\"BRAVO\",\"CHARLIE\"]");
        // The conditions the match was fought in: the hour it began, the weather, how fast the clock ran,
        // the moon (fraction lit) and the visibility.
        _sb.Append(",\"cond\":{\"hour\":").Append(Conditions.StartHour.ToString("0.##", Inv)).Append(",\"weather\":").Append(S(Conditions.Weather.ToString()))
           .Append(",\"timescale\":").Append(F2(Conditions.TimeScale)).Append(",\"moon\":{\"phase\":").Append(F2(Conditions.MoonPhase))
           .Append(",\"lit\":").Append(F2(Conditions.MoonLit)).Append("},\"vis\":").Append(F(Conditions.VisibilityM))
           .Append(",\"rain\":").Append(F(SkyView.RainRate)).Append('}');
        _sb.Append(",\"hm\":{\"n\":").Append(n).Append(",\"step\":").Append(F(step * t.Spacing)).Append(",\"origin\":").Append(F(-t.Extent))
           .Append(",\"lo\":").Append(F(lo)).Append(",\"hi\":").Append(F(hi)).Append(",\"data\":\"").Append(Convert.ToBase64String(hb)).Append("\"}");
        _sb.Append(",\"buildings\":{\"cell\":4,\"data\":\"").Append(Convert.ToBase64String(cb)).Append("\"}");
        _sb.Append(",\"trees\":\"").Append(Convert.ToBase64String(tb)).Append('"');
        _sb.Append(",\"sites\":[");
        for (int i = 0; i < m.Points.Length; i++)
        {
            var s = m.Points[i].Site;
            if (i > 0) _sb.Append(',');
            _sb.Append("[").Append(S(s.Name)).Append(',').Append(F(s.Center.X)).Append(',').Append(F(s.Center.Z)).Append(',').Append(F(m.Points[i].Radius))
               .Append(',').Append(S(s.Kind)).Append(',').Append(m.Owner[i]).Append(']');
        }
        _sb.Append("],\"bases\":[");
        for (int i = 0; i < 3; i++) _sb.Append(i > 0 ? "," : "").Append('[').Append(F(map.Bases[i].X)).Append(',').Append(F(map.Bases[i].Z)).Append(']');
        _sb.Append("],\"squads\":[");
        bool first = true;
        foreach (var sq in m.Squads.SelectMany(l => l))
        {
            _sb.Append(first ? "" : ",").Append('[').Append(S(sq.Name)).Append(',').Append(S(sq.Kind.ToString())).Append(']');
            first = false;
        }
        _sb.Append("]}");
        Line(_sb.ToString());
    }

    // ---------------------------------------------------------------- twice a second: everyone

    public static void Tick(TerritoryMode m)
    {
        if (_w == null || Clock.Now < _nextSample) return;
        _nextSample = Clock.Now + Every;
        _sb.Clear();
        _sb.Append("{\"k\":\"s\",\"t\":").Append(T).Append(",\"b\":[");
        bool first = true;
        foreach (var c in Combatants.All)
        {
            if (c.Dead) continue;
            var p = c.FeetPos;
            string state = "", stance = "S", target = "", squad = "", note = "";
            int seen = 0, ammo = 100;
            float supp = 0f;
            if (c is Bot b)
            {
                state = b.Brain.State.ToString();
                stance = b.Prone ? "P" : b.Crouched ? "C" : "S";
                target = b.Brain.Target?.Who.Callsign ?? "";
                seen = b.Brain.Target is { Visible: true } ? 1 : 0;
                supp = b.Suppression;
                squad = b.Squad?.Name ?? "";
                ammo = (int)(b.AmmoLevel * 100f);
                note = b.Brain.Note;
            }
            else if (c is Player pl)
            {
                state = "Player";
                stance = pl.Stance == Player.StanceKind.Prone ? "P" : pl.Stance == Player.StanceKind.Crouch ? "C" : "S";
                supp = pl.Suppression;
                squad = m.PlayerSquad?.Name ?? "";
                ammo = (int)(pl.AmmoLevel * 100f);
            }
            _sb.Append(first ? "" : ",").Append('[')
               .Append(S(c.Callsign)).Append(',').Append(c.Team).Append(',').Append(S(squad)).Append(',').Append(S(Roles.Short(c.Role))).Append(',')
               .Append(F(p.X)).Append(',').Append(F(p.Z)).Append(',').Append(F(p.Y)).Append(',')
               .Append(S(state)).Append(',').Append(S(stance)).Append(',').Append(c.Downed ? 1 : 0).Append(',')
               .Append(c.Ride != null ? 1 : 0).Append(',').Append(S(target)).Append(',').Append(seen).Append(',')
               .Append(F2(supp)).Append(',').Append((int)c.Hp).Append(',').Append(ammo).Append(',').Append(S(note)).Append(']');
            first = false;
        }
        _sb.Append("],\"v\":[");
        first = true;
        foreach (var v in Vehicle.All)
        {
            if (!GodotObject.IsInstanceValid(v)) continue;
            var p = v.GlobalPosition;
            _sb.Append(first ? "" : ",").Append('[').Append(v.GetInstanceId()).Append(',').Append(S(v.Def.Name)).Append(',').Append(S(v.Def.Kind.ToString()))
               .Append(',').Append(v.Team).Append(',').Append(F(p.X)).Append(',').Append(F(p.Z)).Append(',').Append(F(p.Y))
               .Append(',').Append(v.Destroyed ? 1 : 0).Append(',').Append(v.Crewed ? 1 : 0).Append(',').Append(S(v.Task))
               // health %, and what's wrong with it: E engine hit, I immobile (a helicopter's tail rotor), X doomed (falling), L landed; a helicopter's collective
               .Append(',').Append((int)MathF.Round(v.Hp / v.Def.Hp * 100f))
               .Append(',').Append(S((v.EngineHit ? "E" : "") + (v.Immobile ? "I" : "") + (v.Def.Air && v.Doomed ? "X" : "") + (v.Def.Air && v.Landed ? "L" : "")))
               .Append(',').Append(F(v.Def.Air ? v.Collective * 100f : 0f)).Append(']');
            first = false;
        }
        _sb.Append("],\"d\":[");
        first = true;
        foreach (var d in Drone.All)
        {
            if (!GodotObject.IsInstanceValid(d) || d.Dead) continue;
            var p = d.GlobalPosition;
            _sb.Append(first ? "" : ",").Append('[').Append(S(d.Kind.ToString())).Append(',').Append(d.Team).Append(',').Append(F(p.X)).Append(',').Append(F(p.Z)).Append(',').Append(F(p.Y)).Append(']');
            first = false;
        }
        _sb.Append("]}");
        Line(_sb.ToString());
        // Every 5 s, the state of the match: tickets, who's out, who holds each point, each headquarters' hold,
        // and the hour and the light (lux on open ground).
        // (Recordings had none of it: the ticket race had to be rebuilt from the kills and the capture notes.)
        if (Clock.Now >= _nextScore)
        {
            _nextScore = Clock.Now + 5.0;
            Line($"{{\"k\":\"score\",\"t\":{T},\"tickets\":[{m.Tickets[0]},{m.Tickets[1]},{m.Tickets[2]}],\"out\":[{(m.Out[0] ? 1 : 0)},{(m.Out[1] ? 1 : 0)},{(m.Out[2] ? 1 : 0)}],\"owner\":[{string.Join(",", m.Owner)}],\"hq\":[{F2(m.HQHold[0])},{F2(m.HQHold[1])},{F2(m.HQHold[2])}],\"hour\":{Conditions.Hour.ToString("0.00", Inv)},\"lux\":{Conditions.Lux.ToString("G3", Inv)}}}");
        }
        if (Clock.Now > _nextFlush) { _nextFlush = Clock.Now + 5.0; _w.Flush(); }
    }

    // ---------------------------------------------------------------- events

    /// <summary>A round fired: from where, towards what point, at whom (if anyone), and how ("aimed", "suppress"...).</summary>
    public static void Shot(ICombatant? who, Vector3 from, Vector3 towards, string weapon, string how, ICombatant? at)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"shot\",\"t\":{T},\"s\":{S(who?.Callsign ?? "")},\"team\":{who?.Team ?? -1},\"p\":{P(from)},\"a\":{P(towards)},\"w\":{S(weapon)},\"m\":{S(how)},\"at\":{S(at?.Callsign ?? "")}}}");
    }

    public static void Hit(ICombatant victim, ICombatant? by, Vector3 at, string zone, string weapon, float dist, bool fragment)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"hit\",\"t\":{T},\"v\":{S(victim.Callsign)},\"vt\":{victim.Team},\"s\":{S(by?.Callsign ?? "")},\"p\":{P(at)},\"z\":{S(zone)},\"w\":{S(weapon)},\"d\":{F(dist)},\"frag\":{(fragment ? 1 : 0)}}}");
    }

    static void OnDowned(ICombatant victim, HitInfo hit) => Casualty("down", victim, hit);
    static void OnKilled(ICombatant victim, HitInfo hit) => Casualty("kill", victim, hit);

    static void Casualty(string kind, ICombatant victim, HitInfo hit)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"{kind}\",\"t\":{T},\"v\":{S(victim.Callsign)},\"vt\":{victim.Team},\"s\":{S(hit.Shooter?.Callsign ?? "")},\"st\":{hit.Shooter?.Team ?? -1},\"p\":{P(victim.FeetPos)},\"w\":{S(hit.Weapon ?? "")},\"d\":{F(hit.Distance)},\"z\":{S(hit.Zone.ToString())}}}");
    }

    public static void Explosion(Vector3 at, ICombatant? by, string weapon, float power, float fragR)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"boom\",\"t\":{T},\"p\":{P(at)},\"s\":{S(by?.Callsign ?? "")},\"team\":{by?.Team ?? -1},\"w\":{S(weapon)},\"pow\":{F2(power)},\"fr\":{F(fragR)}}}");
    }

    public static void State(Bot b, BotState from, BotState to, string note)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"st\",\"t\":{T},\"s\":{S(b.Callsign)},\"team\":{b.Team},\"from\":\"{from}\",\"to\":\"{to}\",\"n\":{S(note)},\"p\":{P(b.FeetPos)}}}");
    }

    public static void Drill(Squad sq, Drill d, Vector3 at)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"drill\",\"t\":{T},\"sq\":{S(sq.Name)},\"team\":{sq.Team},\"d\":\"{d}\",\"p\":{P(at)}}}");
    }

    /// <summary>A report on the radio net (enemy armour, air defence, a heavy fight).</summary>
    static void OnRadio(RadioReport r) =>
        Line($"{{\"k\":\"radio\",\"t\":{T},\"team\":{r.Team},\"kind\":\"{r.Kind}\",\"p\":{P(r.Pos)},\"v\":{S(r.Vehicle?.Def.Name ?? "")},\"vk\":\"{r.Vehicle?.Def.Kind}\",\"s\":{S(r.From)}}}");

    /// <summary>What an aircrew says (the rest of the chatter would swamp the file).</summary>
    static void OnSaid(ICombatant who, string text)
    {
        if (who.Ride is not { Def.Air: true } v) return;
        Line($"{{\"k\":\"say\",\"t\":{T},\"s\":{S(who.Callsign)},\"team\":{who.Team},\"veh\":{S(v.Def.Name)},\"text\":{S(text)}}}");
    }

    /// <summary>A squad's new orders (whoever gave them: the commander, the player).</summary>
    public static void Order(Squad sq)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"order\",\"t\":{T},\"sq\":{S(sq.Name)},\"team\":{sq.Team},\"o\":{S(sq.OrderText)}}}");
    }

    /// <summary>A danger-area crossing: e is start, smoke (at where the threat was), crossed or abandoned.</summary>
    public static void Cross(Squad sq, string e, string what, Vector3 at)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"cross\",\"t\":{T},\"sq\":{S(sq.Name)},\"team\":{sq.Team},\"e\":\"{e}\",\"what\":{S(what)},\"p\":{P(at)}}}");
    }

    /// <summary>Replacements joining a squad: n men, at a spawn; how: wiped out, at a spawn, or sent up.</summary>
    public static void Reinforce(Squad sq, int n, string at, string how)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"reinf\",\"t\":{T},\"sq\":{S(sq.Name)},\"team\":{sq.Team},\"n\":{n},\"at\":{S(at)},\"how\":{S(how)}}}");
    }

    public static void Note(string text)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"note\",\"t\":{T},\"text\":{S(text)}}}");
    }
}
