using Godot;

namespace Ridgeline;

/// <summary>
/// The drone operator's kit, for the player. You fly from where you stand, eyes on the
/// screen: your body stays put and you hear with your own ears, not the drone's.
/// - H: put the quad up (or take it back if it's already flying). WASD flies it relative to
///   where the camera points (Shift for full speed), Space / C climb and descend, the mouse
///   turns it and tilts the camera. LMB lets go of a grenade. H again: it flies itself home
///   and lands at your feet, and you get the grenades it didn't drop back.
/// - J: an FPV. It flies where the camera points; W / S throttle; LMB sets the charge off,
///   and it goes off by itself when it hits something. H ditches it.
/// Getting hit takes your eyes off the screen. Stocks refill at a logistics truck or a FOB.
/// </summary>
public partial class Player
{
    public const int PQuads = 2, PBombs = 6, PFpvs = 3, PAtFpvs = 2;
    public Drone? Piloting { get; private set; }
    public int DroneQuads = PQuads, DroneBombs = PBombs, DroneFpvs = PFpvs, DroneAtFpvs = PAtFpvs;
    Drone? _myQuad;
    Camera3D? _droneCam;
    AudioListener3D? _ears;
    float _pilotHp, _fpvRoll;
    double _restockCheckAt;

    void FlyQuad()
    {
        if (_myQuad is { Dead: false } q && IsInstanceValid(q)) { TakeControl(q); return; }
        if (DroneQuads <= 0) { Hud.Toast("No quads left — a logistics truck or a FOB has more", 2.5f); return; }
        q = Launch(DroneKind.Quad);
        int load = Math.Min(2, DroneBombs);
        q.Bombs = load;
        DroneBombs -= load;
        q.CamPitch = -30f;
        _myQuad = q;
        TakeControl(q);
        Hud.Toast($"Quad up — {load} grenade{(load == 1 ? "" : "s")} on it", 2f);
    }

    void FlyFpv(bool at = false)
    {
        if (Piloting != null) return;
        if ((at ? DroneAtFpvs : DroneFpvs) <= 0) { Hud.Toast($"No {(at ? "AT " : "")}FPVs left — a logistics truck or a FOB has more", 2.5f); return; }
        if (at) DroneAtFpvs--; else DroneFpvs--;
        var d = Launch(at ? DroneKind.FpvAt : DroneKind.Fpv);
        d.AimAt = FeetPos + new Vector3(0f, 0f, 0f);
        d.CamPitch = 15f;
        d.Throttle = 0.5f;
        TakeControl(d);
        Hud.Toast(at ? "AT FPV armed and away — dive onto the roof" : "FPV armed and away", 2f);
    }

    Drone Launch(DroneKind kind)
    {
        var fwd = -GlobalBasis.Z with { Y = 0f };
        var d = new Drone { Kind = kind, Team = Team, Operator = this, Manual = true, Yaw = _yaw };
        GetParent().AddChild(d);
        d.GlobalPosition = FeetPos + Vector3.Up * 1.2f + fwd.Normalized() * 0.8f;
        return d;
    }

    void TakeControl(Drone d)
    {
        Piloting = d;
        d.Manual = true;
        d.Goal = null;
        _pilotHp = Hp;
        _fpvRoll = 0f;
        _droneCam = new Camera3D { Fov = d.IsFpv ? 95f : 70f, Near = 0.05f, Far = 5000f };
        d.AddChild(_droneCam);
        d.OwnCamera(_droneCam);
        _droneCam.MakeCurrent();
        if (_ears == null) { _ears = new AudioListener3D(); Head.AddChild(_ears); }
        _ears.MakeCurrent();
        SoundWorld.Ears = _ears;
        Weapon.Visible = false;
    }

    void StopPiloting()
    {
        var d = Piloting;
        Piloting = null;
        if (d != null && IsInstanceValid(d) && !d.Dead && d.Kind == DroneKind.Quad)
        {
            // Let go of the sticks: it comes home by itself.
            d.Manual = false;
            d.StickMove = Vector3.Zero;
            d.StickClimb = 0f;
            d.Goal = FeetPos;
            d.GoalAgl = 40f;
        }
        else if (d != null && IsInstanceValid(d) && !d.Dead) d.Crash(); // an FPV with nobody on the sticks falls
        if (_droneCam != null && IsInstanceValid(_droneCam)) _droneCam.QueueFree();
        _droneCam = null;
        Cam.MakeCurrent();
        _ears?.ClearCurrent();
        SoundWorld.Ears = null;
        Weapon.Visible = true;
    }

    public override void _Input(InputEvent e)
    {
        // Flying: the mouse belongs to the drone, not your head.
        if (Piloting is not { } d || e is not InputEventMouseMotion mm || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        GetViewport().SetInputAsHandled();
        if (d.Kind == DroneKind.Quad)
        {
            d.Yaw -= mm.Relative.X * 0.12f;
            d.CamPitch = Mathf.Clamp(d.CamPitch - mm.Relative.Y * 0.12f, -90f, 10f);
        }
        else
        {
            d.Yaw -= mm.Relative.X * 0.1f;
            d.CamPitch = Mathf.Clamp(d.CamPitch - mm.Relative.Y * 0.1f, -85f, 60f);
            _fpvRoll = Mathf.Clamp(_fpvRoll - mm.Relative.X * 0.4f, -35f, 35f);
        }
    }

    /// <summary>On foot, every frame: J launches an FPV; a quad let go of comes home and lands; stocks refill at the truck.</summary>
    void DroneTick(float dt, bool captured)
    {
        if (Kit != Role.DroneOperator) return;
        if (captured && Input.IsActionJustPressed("drone_fpv")) FlyFpv();
        if (captured && Input.IsActionJustPressed("drone_fpv_at")) FlyFpv(at: true);
        if (_myQuad != null && (!IsInstanceValid(_myQuad) || _myQuad.Dead))
        {
            _myQuad = null;
            DroneQuads = Math.Max(0, DroneQuads - 1);
            Hud.Toast($"Quad lost — {DroneQuads} left", 2.5f);
        }
        if (_myQuad is { Manual: false } q)
        {
            var home = FeetPos;
            float off = ((q.GlobalPosition - home) with { Y = 0f }).Length();
            q.Goal = home;
            q.GoalAgl = off < 6f ? 1.2f : 40f;
            if (off < 3f && q.GlobalPosition.Y - home.Y < 3f)
            {
                DroneBombs += q.Bombs;
                q.QueueFree();
                _myQuad = null;
                Hud.Toast("Quad recovered", 1.5f);
            }
        }
        if (Clock.Now > _restockCheckAt)
        {
            _restockCheckAt = Clock.Now + 1.0;
            bool full = DroneQuads >= PQuads && DroneBombs >= PBombs && DroneFpvs >= PFpvs && DroneAtFpvs >= PAtFpvs;
            if (!full && (Vehicle.All.Any(v => !v.Destroyed && v.Def.Kind == VKind.Logistics && v.Team == Team && v.GlobalPosition.DistanceTo(FeetPos) < 12f)
                          || Fob.All.Any(f => f.Team == Team && f.GlobalPosition.DistanceTo(FeetPos) < 15f)))
            {
                DroneQuads = Math.Max(DroneQuads, PQuads);
                DroneBombs = Math.Max(DroneBombs, PBombs);
                DroneFpvs = Math.Max(DroneFpvs, PFpvs);
                DroneAtFpvs = Math.Max(DroneAtFpvs, PAtFpvs);
                SoundWorld.I.Emit(Snd.Bag, EyePos, 0f, this);
                Hud.Toast("Drones restocked", 2f);
            }
        }
    }

    /// <summary>Flying: the sticks, the camera, and whatever takes you off them.</summary>
    void PilotProcess(float dt)
    {
        var d = Piloting!;
        if (!IsInstanceValid(d) || d.Dead)
        {
            Hud.Toast(d is { IsFpv: true } ? "Signal lost — impact" : "Signal lost", 2f);
            StopPiloting();
            return;
        }
        bool hurt = Hp < _pilotHp - 1f;
        _pilotHp = Hp;
        if (!Alive || Downed || Ride != null || hurt)
        {
            Hud.Toast("You're hit — eyes off the screen!", 2f);
            StopPiloting();
            return;
        }
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        if (d.Kind == DroneKind.Quad)
        {
            var inp = captured ? Input.GetVector("move_left", "move_right", "move_forward", "move_back") : Vector2.Zero;
            var mv = new Basis(Vector3.Up, Mathf.DegToRad(d.Yaw)) * new Vector3(inp.X, 0f, inp.Y);
            d.StickMove = mv * (captured && Input.IsActionPressed("sprint") ? 1f : 0.5f);
            d.StickClimb = captured ? (Input.IsActionPressed("jump") ? 1f : 0f) - (Input.IsActionPressed("crouch") ? 1f : 0f) : 0f;
            if (captured && Input.IsActionJustPressed("fire"))
                Hud.Toast(d.Drop() ? $"Drop! {d.Bombs} left on the drone" : "Nothing left to drop", 1.5f);
            if (captured && Input.IsActionJustPressed("gadget")) { Hud.Toast("Quad coming home", 1.5f); StopPiloting(); return; }
            _droneCam!.Position = Vector3.Down * 0.12f;
            _droneCam.Rotation = new Vector3(Mathf.DegToRad(d.CamPitch), Mathf.DegToRad(d.Yaw), 0f);
        }
        else
        {
            if (captured)
            {
                float thr = (Input.IsActionPressed("move_forward") ? 1f : 0f) - (Input.IsActionPressed("move_back") ? 1f : 0f);
                d.Throttle = Mathf.Clamp(d.Throttle + thr * dt * 0.7f, 0.25f, 1f);
                if (Input.IsActionJustPressed("fire")) { d.Blow(); Hud.Toast("Detonated", 1.5f); StopPiloting(); return; }
                if (Input.IsActionJustPressed("gadget")) { d.Crash(); Hud.Toast("FPV ditched", 1.5f); StopPiloting(); return; }
            }
            _fpvRoll = Mathf.MoveToward(_fpvRoll, 0f, dt * 40f);
            var fwd = new Basis(Vector3.Up, Mathf.DegToRad(d.Yaw)) * Vector3.Forward;
            _droneCam!.Position = fwd * 0.14f;
            _droneCam.Rotation = new Vector3(Mathf.DegToRad(d.CamPitch), Mathf.DegToRad(d.Yaw), Mathf.DegToRad(_fpvRoll));
        }
    }
}
