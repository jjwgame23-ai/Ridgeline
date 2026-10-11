using System.Globalization;
using System.Text;
using Godot;

namespace Ridgeline;

/// <summary>
/// telemetry=path (dev arg, Territory mode and Conquest windows): the match as it happens, written to a JSON-lines file for
/// looking at fights afterwards (tools/replay.py makes a replay to watch; tools/telemetry.py draws the fights
/// and measures them). A folder (record.cmd passes one) gets a new file for each match, named for the map and
/// the time it started.
/// - The first line is the map: terrain heights, buildings, trees, the points and the bases, so a plot
///   can draw the ground under it; and the conditions (start hour, weather, clock speed, moon, visibility).
/// - Twice a second, everyone: where they are, what they're doing (state, stance, target, suppression,
///   health, ammo, pace, fire team, whether he leads, what his squad is doing, how many enemies have him in sight and
///   whether there's cover between him and his threat, the war soldier he is) and every vehicle and drone.
/// - As they happen: every shot (from, towards, at whom, how), near miss, hit, casualty, explosion, change of
///   state (with the reason), sighting, squad drill and capture; each person's first appearance; a Conquest window's
///   units coming in and going back.
/// These are what tools/doctrine_check.py checks against its rulebook (tools/rules.toml).
/// Nothing here changes the game; with no telemetry= argument none of it runs.
/// </summary>
/// <summary>What telemetry reads of the match it records (TerritoryMode, or a Conquest window).</summary>
public interface ITelemetryMatch
{
    Valley Map { get; }
    SiteObjective[] Points { get; }
    int[] Owner { get; }
    List<Squad>[] Squads { get; }
    Squad? PlayerSquad { get; }
    int[] Tickets { get; }
    bool[] Out { get; }
    float[] HQHold { get; }
    /// <summary>The war soldier a person is (a Conquest window), or -1.</summary>
    int SoldierId(ICombatant c) => -1;
}

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

    static ITelemetryMatch? _match;
    static readonly Dictionary<ICombatant, string> _label = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<string, ICombatant> _holder = new();
    static readonly Dictionary<string, int> _starts = new();
    static readonly Dictionary<ICombatant, int> _seenBy = new(ReferenceEqualityComparer.Instance), _seenByArmour = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<ICombatant, ICombatant> _watcher = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<Vehicle, Squad> _crewOf = new();
    static readonly HashSet<ICombatant> _announced = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The name a person goes by in the recording: his callsign, or "callsign #2" while someone else in the world goes by
    /// it, so no two people in the world at once share one. A callsign whose holder has died passes to whoever takes it
    /// up next (a battle map's man coming back is the same name, as before). (Conquest callsigns are rank and surname, and
    /// they repeat: 21 were shared among 193 men in one window, and every tool that reads a recording merged the
    /// namesakes into one man.)
    /// </summary>
    public static string Label(ICombatant c)
    {
        if (_label.TryGetValue(c, out var l)) return l;
        l = c.Callsign;
        for (int n = 2; _holder.TryGetValue(l, out var h) && h != c && !h.Dead && Combatants.Contains(h); n++) l = $"{c.Callsign} #{n}";
        _holder[l] = c;
        _label[c] = l;
        return l;
    }

    static string LabelOr(ICombatant? c) => c == null ? "" : Label(c);

    public static string? PathFromArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("telemetry=")) return a["telemetry=".Length..];
        return null;
    }

    public static void Start(string path, ITelemetryMatch m)
    {
        Stop();
        if (path.EndsWith('/') || path.EndsWith('\\') || Directory.Exists(path))
        {
            try { Directory.CreateDirectory(path); }
            catch (Exception e) { GD.PrintErr($"telemetry: can't make {path}: {e.Message}"); return; }
            path = System.IO.Path.Combine(path, $"{m.Map.Spec.Id}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.jsonl");
        }
        // A Conquest window built again round the player (a scene reload) starts telemetry again: each build gets its own
        // file, -2, -3... (The same path was opened afresh each time, so a window that moved wiped its own recording.)
        if (_starts.TryGetValue(path, out int built))
        {
            _starts[path] = built + 1;
            path = System.IO.Path.ChangeExtension(path, null) + $"-{built + 1}" + System.IO.Path.GetExtension(path);
        }
        else _starts[path] = 1;
        try { _w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16); }
        catch (Exception e) { GD.PrintErr($"telemetry: can't write {path}: {e.Message}"); return; }
        _match = m;
        _label.Clear();
        _holder.Clear();
        _announced.Clear();
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
        _match = null;
    }

    static void Line(string s)
    {
        _w!.Write(s);
        _w.Write('\n');
    }

    // ---------------------------------------------------------------- the map

    static void WriteMap(ITelemetryMatch m)
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

    public static void Tick(ITelemetryMatch m)
    {
        if (_w == null || Clock.Now < _nextSample) return;
        _nextSample = Clock.Now + Every;
        Watchers();
        _crewOf.Clear();
        foreach (var sq in m.Squads.SelectMany(l => l))
            if (sq.Vehicle is { } cv && GodotObject.IsInstanceValid(cv)) _crewOf[cv] = sq;
        PhysicsDirectSpaceState3D? space = null;
        _sb.Clear();
        _sb.Append("{\"k\":\"s\",\"t\":").Append(T).Append(",\"b\":[");
        bool first = true;
        foreach (var c in Combatants.All)
        {
            if (c.Dead) continue;
            var p = c.FeetPos;
            space ??= (c as Node3D)?.GetWorld3D()?.DirectSpaceState;
            string state = "", stance = "S", target = "", squad = "", note = "", gait = "still", team = "", doing = "";
            int seen = 0, ammo = 100, leads = 0;
            float supp = 0f;
            Squad? sq = null;
            if (c is Bot b)
            {
                state = b.Brain.State.ToString();
                stance = b.Prone ? "P" : b.Crouched ? "C" : "S";
                target = b.Brain.Target is { } tt ? Label(tt.Who) : "";
                seen = b.Brain.Target is { Visible: true } ? 1 : 0;
                supp = b.Suppression;
                sq = b.Squad;
                ammo = (int)(b.AmmoLevel * 100f);
                note = b.Brain.Note;
                if (b.Moving) gait = b.Prone ? "crawl" : b.Gait.ToString().ToLowerInvariant();
            }
            else if (c is Player pl)
            {
                state = "Player";
                stance = pl.Stance == Player.StanceKind.Prone ? "P" : pl.Stance == Player.StanceKind.Crouch ? "C" : "S";
                supp = pl.Suppression;
                sq = m.PlayerSquad;
                ammo = (int)(pl.AmmoLevel * 100f);
                if (pl.Moving) gait = pl.Stance == Player.StanceKind.Prone ? "crawl" : pl.Sprinting ? "sprint" : "move";
            }
            if (sq != null)
            {
                squad = sq.Name;
                int ft = sq.TeamQuiet(c);
                team = ft == 0 ? "A" : ft == 1 ? "B" : "";
                leads = sq.LeaderQuiet == c ? 1 : 0;
                doing = Doing(sq);
            }
            _sb.Append(first ? "" : ",").Append('[')
               .Append(S(Label(c))).Append(',').Append(c.Team).Append(',').Append(S(squad)).Append(',').Append(S(Roles.Short(c.Role))).Append(',')
               .Append(F(p.X)).Append(',').Append(F(p.Z)).Append(',').Append(F(p.Y)).Append(',')
               .Append(S(state)).Append(',').Append(S(stance)).Append(',').Append(c.Downed ? 1 : 0).Append(',')
               .Append(c.Ride != null ? 1 : 0).Append(',').Append(S(target)).Append(',').Append(seen).Append(',')
               .Append(F2(supp)).Append(',').Append((int)c.Hp).Append(',').Append(ammo).Append(',').Append(S(note))
               // 17 pace (still, walk, run, sprint, crawl, move), 18 fire team (A, B), 19 leads his squad, 20 what his squad is
               // doing (a drill, a step of the attack, or its march order), 21 enemies who have him in sight, 22 of them from
               // armour, 23 cover between him and his threat (1, 0; -1 no threat), 24 the war soldier he is (-1 none).
               .Append(',').Append(S(gait)).Append(',').Append(S(team)).Append(',').Append(leads).Append(',').Append(S(doing))
               .Append(',').Append(_seenBy.GetValueOrDefault(c)).Append(',').Append(_seenByArmour.GetValueOrDefault(c))
               .Append(',').Append(c.Downed || c.Ride != null || space == null ? -1 : CoverFromThreat(c, space))
               .Append(',').Append(m.SoldierId(c)).Append(']');
            first = false;
            if (_announced.Add(c)) Spawned(c, sq, m, space);
        }
        _sb.Append("],\"v\":[");
        first = true;
        foreach (var v in Vehicle.All)
        {
            if (!GodotObject.IsInstanceValid(v)) continue;
            var p = v.GlobalPosition;
            var crew = _crewOf.GetValueOrDefault(v);
            _sb.Append(first ? "" : ",").Append('[').Append(v.GetInstanceId()).Append(',').Append(S(v.Def.Name)).Append(',').Append(S(v.Def.Kind.ToString()))
               .Append(',').Append(v.Team).Append(',').Append(F(p.X)).Append(',').Append(F(p.Z)).Append(',').Append(F(p.Y))
               .Append(',').Append(v.Destroyed ? 1 : 0).Append(',').Append(v.Crewed ? 1 : 0).Append(',').Append(S(v.Task))
               // health %, and what's wrong with it: E engine hit, I immobile (a helicopter's tail rotor), X doomed (falling), L landed; a helicopter's collective
               .Append(',').Append((int)MathF.Round(v.Hp / v.Def.Hp * 100f))
               .Append(',').Append(S((v.EngineHit ? "E" : "") + (v.Immobile ? "I" : "") + (v.Def.Air && v.Doomed ? "X" : "") + (v.Def.Air && v.Landed ? "L" : "")))
               .Append(',').Append(F(v.Def.Air ? v.Collective * 100f : 0f))
               // 13 its crew's squad, 14 the squad its crew works with (carries or supports)
               .Append(',').Append(S(crew?.Name ?? "")).Append(',').Append(S(crew?.Supports?.Name ?? "")).Append(']');
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

    /// <summary>
    /// Who has whom in sight, by the bots' own eyes this moment: for each person, how many enemies see him, how many of
    /// them look out of armour, and the nearest of them (his threat when he has no target of his own).
    /// </summary>
    static void Watchers()
    {
        _seenBy.Clear();
        _seenByArmour.Clear();
        _watcher.Clear();
        foreach (var c in Combatants.All)
        {
            if (c is not Bot ob || !ob.Alive || ob.Senses == null) continue;
            bool armour = ob.Ride is { Destroyed: false, Def.Kind: VKind.APC or VKind.IFV or VKind.MBT or VKind.MGS or VKind.SPAA };
            foreach (var th in ob.Senses.Threats)
            {
                if (!th.Visible || th.Who.Team == ob.Team || !th.Who.Alive) continue;
                _seenBy[th.Who] = _seenBy.GetValueOrDefault(th.Who) + 1;
                if (armour) _seenByArmour[th.Who] = _seenByArmour.GetValueOrDefault(th.Who) + 1;
                if (!_watcher.TryGetValue(th.Who, out var w) || w.EyePos.DistanceSquaredTo(th.Who.ChestPos) > ob.EyePos.DistanceSquaredTo(th.Who.ChestPos))
                    _watcher[th.Who] = ob;
            }
        }
    }

    /// <summary>
    /// Whether something hard stands between a man and his threat (the man he's fighting, else the nearest enemy who has
    /// him in sight): 1 covered, 0 open to his fire, -1 no threat. One ray from the threat's eye to his chest, past the
    /// threat's own vehicle; trees don't count (a trunk stops some rounds, not a burst).
    /// </summary>
    static int CoverFromThreat(ICombatant c, PhysicsDirectSpaceState3D space)
    {
        ICombatant? threat = c is Bot b && b.Brain.Target is { } t && t.Who.Alive ? t.Who : _watcher.GetValueOrDefault(c);
        if (threat == null) return -1;
        var q = PhysicsRayQueryParameters3D.Create(threat.EyePos, Combatants.PointOn(c, Combatants.Part.Chest),
                                                   Layers.World | Layers.Vehicles | Layers.Doors | Layers.Overhead);
        if (threat.Ride is { } rv && GodotObject.IsInstanceValid(rv)) q.Exclude = new Godot.Collections.Array<Rid> { rv.GetRid() };
        return space.IntersectRay(q).Count > 0 ? 1 : 0;
    }

    /// <summary>What a squad is doing as a body: a drill under way, else the step of its attack, else its march order.</summary>
    static string Doing(Squad sq) =>
        sq.Drill != global::Ridgeline.Drill.None && Clock.Now <= sq.DrillUntil ? sq.Drill.ToString()
        : sq.Phase != AssaultPhase.None ? sq.Phase.ToString()
        : sq.MarchOrder.ToString();

    /// <summary>
    /// Someone appearing in the world (the match's start, a reinforcement, a war unit coming into a Conquest window, a man
    /// joining): where, whether the spot is inside something solid (1 the ground or a building, 2 a vehicle's hull) or
    /// under water, how far off the nearest enemy is, his health, and the war soldier he is. What the checker's spawn
    /// rules look at.
    /// </summary>
    static void Spawned(ICombatant c, Squad? sq, ITelemetryMatch m, PhysicsDirectSpaceState3D? space)
    {
        var p = c.FeetPos;
        int solid = 0;
        if (space != null && c.Ride == null)
        {
            var q = new PhysicsPointQueryParameters3D { Position = Combatants.PointOn(c, Combatants.Part.Chest), CollisionMask = Layers.World, CollideWithAreas = false };
            if (space.IntersectPoint(q, 1).Count > 0) solid = 1;
            else
            {
                q.CollisionMask = Layers.Vehicles;
                if (space.IntersectPoint(q, 1).Count > 0) solid = 2;
            }
        }
        float ground = Effects.Ground?.HeightAt(p.X, p.Z) ?? p.Y;
        float water = m.Map.Window is { } wg ? MathF.Max(0f, wg.WaterAt(p.X, p.Z) - ground) : 0f;
        float enemy = float.MaxValue;
        foreach (var o in Combatants.All)
            if (o.Alive && o.Team != c.Team) enemy = MathF.Min(enemy, o.FeetPos.DistanceTo(p));
        Line($"{{\"k\":\"spawn\",\"t\":{T},\"s\":{S(Label(c))},\"team\":{c.Team},\"sq\":{S(sq?.Name ?? "")},\"role\":{S(Roles.Short(c.Role))},\"p\":{P(p)}"
             + $",\"ground\":{F(ground)},\"water\":{F2(water)},\"solid\":{solid},\"enemy\":{(enemy == float.MaxValue ? -1 : (int)enemy)},\"hp\":{(int)c.Hp}"
             + $",\"ride\":{(c.Ride != null ? 1 : 0)},\"sid\":{m.SoldierId(c)}}}");
    }

    // ---------------------------------------------------------------- events

    /// <summary>
    /// A round passing within a few metres of someone, or striking beside him: how close, whose, what, and how ("past",
    /// "by the head", "through cover", "strike"); frag for fragments. What suppression is checked against.
    /// </summary>
    public static void Crack(ICombatant who, ICombatant? by, float dist, string weapon, string how, bool fragment)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"crack\",\"t\":{T},\"v\":{S(Label(who))},\"vt\":{who.Team},\"s\":{S(LabelOr(by))},\"st\":{by?.Team ?? -1},\"d\":{F2(dist)},\"w\":{S(weapon)},\"how\":{S(how)},\"frag\":{(fragment ? 1 : 0)}}}");
    }

    /// <summary>A bot picking someone out (a fresh contact, his awareness full): who, whom, how far, and with what (eye, a gun sight, goggles, thermal, a muzzle flash).</summary>
    public static void Spot(ICombatant who, ICombatant whom, float dist, string by)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"spot\",\"t\":{T},\"s\":{S(Label(who))},\"team\":{who.Team},\"at\":{S(Label(whom))},\"d\":{F(dist)},\"by\":{S(by)}}}");
    }

    /// <summary>A Conquest window bringing a war unit in: its name, side, how many men, and how far from the player.</summary>
    public static void Promote(string unit, int side, int men, float dist)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"promote\",\"t\":{T},\"unit\":{S(unit)},\"team\":{side},\"n\":{men},\"d\":{F(dist)}}}");
    }

    /// <summary>A man handed back to the war (his unit leaving the window, or the window moving), and the state he goes back in.</summary>
    public static void Release(ICombatant c, int soldier, string state)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"release\",\"t\":{T},\"s\":{S(Label(c))},\"team\":{c.Team},\"sid\":{soldier},\"state\":{S(state)},\"hp\":{(int)c.Hp},\"ammo\":{(int)(c.AmmoLevel * 100f)},\"p\":{P(c.FeetPos)}}}");
    }

    /// <summary>A round fired: from where, towards what point, at whom (if anyone), and how ("aimed", "suppress"...).</summary>
    public static void Shot(ICombatant? who, Vector3 from, Vector3 towards, string weapon, string how, ICombatant? at)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"shot\",\"t\":{T},\"s\":{S(LabelOr(who))},\"team\":{who?.Team ?? -1},\"p\":{P(from)},\"a\":{P(towards)},\"w\":{S(weapon)},\"m\":{S(how)},\"at\":{S(LabelOr(at))}}}");
    }

    public static void Hit(ICombatant victim, ICombatant? by, Vector3 at, string zone, string weapon, float dist, bool fragment)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"hit\",\"t\":{T},\"v\":{S(Label(victim))},\"vt\":{victim.Team},\"s\":{S(LabelOr(by))},\"p\":{P(at)},\"z\":{S(zone)},\"w\":{S(weapon)},\"d\":{F(dist)},\"frag\":{(fragment ? 1 : 0)}}}");
    }

    static void OnDowned(ICombatant victim, HitInfo hit) => Casualty("down", victim, hit);
    static void OnKilled(ICombatant victim, HitInfo hit) => Casualty("kill", victim, hit);

    static void Casualty(string kind, ICombatant victim, HitInfo hit)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"{kind}\",\"t\":{T},\"v\":{S(Label(victim))},\"vt\":{victim.Team},\"s\":{S(LabelOr(hit.Shooter))},\"st\":{hit.Shooter?.Team ?? -1},\"p\":{P(victim.FeetPos)},\"w\":{S(hit.Weapon ?? "")},\"d\":{F(hit.Distance)},\"z\":{S(hit.Zone.ToString())}}}");
    }

    public static void Explosion(Vector3 at, ICombatant? by, string weapon, float power, float fragR)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"boom\",\"t\":{T},\"p\":{P(at)},\"s\":{S(LabelOr(by))},\"team\":{by?.Team ?? -1},\"w\":{S(weapon)},\"pow\":{F2(power)},\"fr\":{F(fragR)}}}");
    }

    public static void State(Bot b, BotState from, BotState to, string note)
    {
        if (_w == null) return;
        Line($"{{\"k\":\"st\",\"t\":{T},\"s\":{S(Label(b))},\"team\":{b.Team},\"from\":\"{from}\",\"to\":\"{to}\",\"n\":{S(note)},\"p\":{P(b.FeetPos)}}}");
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
        Line($"{{\"k\":\"say\",\"t\":{T},\"s\":{S(Label(who))},\"team\":{who.Team},\"veh\":{S(v.Def.Name)},\"text\":{S(text)}}}");
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
