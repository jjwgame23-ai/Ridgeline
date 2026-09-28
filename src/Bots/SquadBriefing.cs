using Godot;

namespace Ridgeline;

/// <summary>
/// The squad's situation in words, for a player in it: what the squad is doing right now
/// (moving, a drill, a step of the attack, a crossing), what that means for *you* (your
/// team's part in it), and where you should be. The same state the bots act on, so if
/// you follow it you're doing what the squad expects of you.
/// </summary>
public sealed partial class Squad
{
    public readonly struct Briefing
    {
        public string Doing { get; init; }
        public string Task { get; init; }
        /// <summary>Where the squad wants you (null: nowhere in particular, or you're the leader).</summary>
        public Vector3? Spot { get; init; }
        /// <summary>The direction you should be watching (null: none set).</summary>
        public Vector3? Sector { get; init; }
        public string Team { get; init; }
    }

    static readonly Vector3 NoSpot = new(float.NaN, 0f, 0f);

    public Briefing Brief(ICombatant me)
    {
        bool lead = me == Leader;
        int t = TeamOf(me);
        string team = lead ? "squad leader" : t >= 0 ? TeamName(t) : "rifleman";
        string you = lead ? "You" : t >= 0 ? $"{TeamName(t)} (you)" : "You";
        var buddy = BuddyOf(me);
        string teamLine = lead ? "you lead the squad" : t >= 0 ? $"{TeamName(t)} team{(buddy != null ? $", buddy {buddy.Callsign}" : "")}" : "";
        string objName = Site?.Name ?? "the objective";
        Vector3? spot = lead ? null : SlotFor(me);
        Vector3? sector = SectorFor(me, Objective != null && ((Objective.Center - me.FeetPos) with { Y = 0f }).Length() < Objective.Radius);
        string B(Vector3 p) => $"{Comms.Bearing(me.FeetPos, p)}, {me.FeetPos.DistanceTo(p):0} m";

        // Pass NoSpot when the formation place doesn't matter (a drill, a fight, riding).
        Briefing Make(string doing, string task, Vector3? at = null, Vector3? sec = null) =>
            new() { Doing = doing, Task = task, Spot = at is Vector3 a0 && float.IsNaN(a0.X) ? null : at ?? spot, Sector = sec ?? sector, Team = teamLine };

        // Riding.
        if (me.Ride is { } ride && Members.Any(m => m != me && m.Ride == ride))
            return Make($"Mounted in the {ride.Def.Name}", ride.Task != "" ? $"Riding — {ride.Task}. Get out with the squad when it stops." : "Riding — get out with the squad when it stops.", NoSpot);

        // Drills first: they override everything else.
        switch (Current)
        {
            case Drill.Contact:
            {
                // The side the team actually goes (it was worked out from which way the map faces, and could be the other one).
                string side = FlankSide;
                string task = lead ? $"Direct the fight: {TeamName(AssaultTeam ^ 1)} suppresses, {TeamName(AssaultTeam)} flanks {side}."
                    : t == AssaultTeam ? $"{you}: FLANK {side.ToUpperInvariant()} while {TeamName(AssaultTeam ^ 1)} suppresses. Contact {B(ContactAt)}."
                    : $"{you}: GET DOWN AND SUPPRESS toward {B(ContactAt)}. Keep their heads down for the flank.";
                return Make($"CONTACT — {B(ContactAt)}", task, NoSpot, (ContactAt - me.FeetPos) with { Y = 0f });
            }
            case Drill.BreakContact:
                return Make("BREAKING CONTACT", $"Fall back away from {B(ContactAt)} in bounds — one team moves while the other covers.", NoSpot, (ContactAt - me.FeetPos) with { Y = 0f });
            case Drill.Indirect:
                return Make("INCOMING — indirect fire", $"Get off the impact area NOW: move 50 m+ away from {B(ImpactAt)}. The next rounds land in the same place.", NoSpot);
            case Drill.Consolidate:
                return Make($"Consolidating on {objName}", sector is Vector3 cs ? $"Take your sector: watch {Comms.Bearing(Vector3.Zero, cs)}. Report ammo and casualties." : "Set 360° security on the objective.");
        }

        // The deliberate attack.
        switch (Phase)
        {
            case AssaultPhase.Orp:
                return Make($"ORP — forming up for the attack on {objName}", lead ? "Form the squad up, then send support out and take the assault team to the line of departure." : "Security at the ORP: face out, stay down, wait for the plan.", lead ? null : spot);
            case AssaultPhase.Deploy:
                return t == SbfTeam
                    ? Make($"Attack on {objName}: deploying", $"{you}: SUPPORT BY FIRE — set up facing {objName} ({B(Objective!.Center)}). Hold fire until the assault goes in.", spot, (Objective.Center - me.FeetPos) with { Y = 0f })
                    : Make($"Attack on {objName}: deploying", $"{you}: to the line of departure with the leader. Wait for support to be set.", spot);
            case AssaultPhase.Assault:
                return t == SbfTeam
                    ? Make($"ASSAULT on {objName}", $"{you}: SUPPORT BY FIRE — pour fire onto {objName}. Shift off it when the assault team gets in.", spot, (Objective!.Center - me.FeetPos) with { Y = 0f })
                    : Make($"ASSAULT on {objName}", $"{you}: ASSAULT — go in on line with the leader. Clear it, then hold 50 m past it, no further.", spot);
        }

        // A danger area.
        if (Cross != Crossing.None && !lead)
        {
            int g = CrossGroup(me);
            string what = _crossWhat;
            string task = Cross switch
            {
                Crossing.Halt => g == 0 ? $"Stack up behind the leader at the edge of the {what}. Wait for the go." : $"Cover LEFT and RIGHT along the near edge of the {what}.",
                Crossing.First => g == 0 ? $"CROSS NOW — sprint over the {what}, then cover from the far side." : $"Cover Alpha while they cross. You go next.",
                _ => g == 1 ? $"CROSS NOW — sprint over the {what} to the others." : $"Far side: cover Bravo's crossing.",
            };
            return Make($"Danger area — {what}", task, spot);
        }
        if (Cross != Crossing.None) return Make($"Danger area — {_crossWhat}", "Crossing in progress.");

        // Fighting, but no drill.
        if (Engaged) return Make("In contact", $"Fight from cover. Enemy last seen {B(ContactAt)}.", NoSpot, (ContactAt - me.FeetPos) with { Y = 0f });

        // Stopped for a vehicle coming to pick us up. (It read "halted", as if something had gone wrong.)
        if (Transport is { Destroyed: false } pickup && Clock.Now - RideWaitAt < 2.0)
            return Make($"Waiting for pickup: the {pickup.Def.Name}, {B(pickup.GlobalPosition)}",
                        lead ? "Hold the squad here for the ride." : "Hold here and watch your side: the ride's on its way. Get in when it stops.");

        if (Objective == null) return Make("No orders", "Stay with the squad.");
        bool there = ((Objective.Center - me.FeetPos) with { Y = 0f }).Length() < Objective.Radius;
        // Falling back to take on replacements.
        if (_verb == "Regroup at")
            return there || ((Objective.Center - me.FeetPos) with { Y = 0f }).Length() < 40f
                ? Make($"Regrouping at {_what}", "Hold here and watch your sector: replacements come up once we're out of contact.")
                : Make($"Falling back to {_what} to regroup, {B(Objective.Center)}", lead ? "Take them back: replacements join us there." : "Keep your place: we pick up replacements there.");
        if (Defend || there)
            return Make($"{(Defend ? "Holding" : "On")} {objName}", sector is Vector3 hs ? $"Watch your sector: {Comms.Bearing(Vector3.Zero, hs)}." : "Hold and watch for them.");

        // On the move.
        string order = MarchOrder switch
        {
            March.BoundingOverwatch => "bounding overwatch",
            March.TravellingOverwatch => "travelling overwatch",
            March.Column => "in file",
            March.Herringbone => "halted",
            March.AssaultLine => "on line",
            _ => "travelling",
        };
        string mtask = MarchOrder switch
        {
            March.BoundingOverwatch when !lead && t == 1 && !_bowWaiting => $"{you}: OVERWATCH — stay set and cover Alpha's bound.",
            March.BoundingOverwatch when !lead && t == 1 => $"{you}: bound up to the leader now.",
            March.BoundingOverwatch => "Move with the leader; Bravo covers you.",
            March.TravellingOverwatch when t == 1 => $"{you}: trail about 50 m behind, ready to react.",
            March.Herringbone => sector is Vector3 hb ? $"Halted: get down and watch {Comms.Bearing(Vector3.Zero, hb)}." : "Halted: get down and watch your side.",
            _ => lead ? "Lead them in." : "Keep your place in the formation.",
        };
        return Make($"Moving to {objName} ({order}), {B(Objective.Center)}", mtask);
    }
}
