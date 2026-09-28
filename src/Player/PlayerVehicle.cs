using Godot;

namespace Ridgeline;

/// <summary>
/// The player in a vehicle. F gets in (nearest free seat, driver first) and out;
/// the number keys change seat.
/// - Driver: W/S throttle, A/D steer, Space brake; a chase camera you can look around with.
/// - Gunner: the view is the gun sight, stabilised; the turret slews after it at its own
///   speed (the HUD shows where the barrel actually points). LMB fires, RMB zooms,
///   V cycles ammunition (and the coax), R reloads.
/// - Passenger: look around.
/// </summary>
public partial class Player
{
    public Vehicle? Ride { get; private set; }
    public int SeatIdx { get; private set; } = -1;
    public SeatRole? SeatRole => Ride == null ? null : Ride.Def.Seats[SeatIdx].Role;
    /// <summary>What the gunner has selected: an index into the turret's ammo, or the coax (== Ammo.Length).</summary>
    public int GunSel { get; private set; }
    /// <summary>Flying a gunship with missiles selected: the enemy vehicle its sight is on, if any.</summary>
    public Vehicle? MissileLock { get; private set; }
    public bool Zoomed { get; private set; }
    Camera3D? _vcam;
    Vector2 _stick;
    float _lookYaw, _lookPitch;

    public void Mount(Vehicle v, int seat)
    {
        Ride = v;
        SeatIdx = seat;
        var s = v.Def.Seats[seat];
        CollisionLayer = s.Exposed ? 2u : 0u;
        Velocity = Vector3.Zero;
        Weapon.Visible = false;
        GunSel = 0;
        Zoomed = false;
        if (_vcam == null)
        {
            _vcam = new Camera3D { Far = 5000f, Near = 0.05f, Fov = 70f };
            GetParent().AddChild(_vcam);
        }
        _vcam.MakeCurrent();
        _pitch = 0f;
        _yaw = Mathf.RadToDeg(v.GlobalRotation.Y);
        string role = s.Role switch { global::Ridgeline.SeatRole.Driver => "driving", global::Ridgeline.SeatRole.Gunner => "on the gun", _ => "passenger" };
        Hud.Toast($"{v.Def.Name} — {role} · [{Controls.Keys("use")}] get out · [1-{v.Def.Seats.Count}] change seat", 3f);
    }

    public void Dismount(Vector3 at)
    {
        Ride?.OwnView(false);
        Ride = null;
        SeatIdx = -1;
        var ground = at;
        var q = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 3f, at + Vector3.Down * 6f, Layers.World);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) ground = hit["position"].AsVector3();
        GlobalPosition = ground + Vector3.Up * 0.1f;
        CollisionLayer = Body.Dead ? 0u : 2u;
        Weapon.Visible = Alive;
        if (_vcam != null) { _vcam.QueueFree(); _vcam = null; }
        Cam.MakeCurrent();
    }

    public void SyncSeat(Vector3 eye, float yawRad)
    {
        GlobalPosition = eye - Vector3.Up * 1.6f;
    }

    /// <summary>F on foot: get into the nearest friendly (or empty) vehicle within reach.</summary>
    bool TryBoard()
    {
        Vehicle? best = null;
        float bestD = 5.5f;
        foreach (var v in Vehicle.All)
        {
            if (v.Destroyed) continue;
            var local = v.ToLocal(GlobalPosition);
            float d = new Vector2(MathF.Max(0f, MathF.Abs(local.X) - v.Def.Hull.X / 2f), MathF.Max(0f, MathF.Abs(local.Z) - v.Def.Hull.Z / 2f)).Length();
            if (d < bestD && v.Occupants.All(o => o == null || o.Team == Team)) { bestD = d; best = v; }
        }
        if (best == null) return false;
        int seat = best.FreeSeat(global::Ridgeline.SeatRole.Driver);
        if (seat < 0) { Hud.Toast("No free seats", 1.5f); return true; }
        best.Enter(this, seat);
        return true;
    }

    readonly bool[] _numWas = new bool[10];

    /// <summary>
    /// Flying: mouse = cyclic (it drifts back to centre when you let go), W/S = collective,
    /// A/D = pedals, Space = hover assist (levels out and holds height), X = flares,
    /// LMB = rockets (gunships). Hold Alt to look around. A chase camera follows behind.
    /// </summary>
    void Fly(Vehicle v, SeatDef seat, Camera3D cam, bool captured, float dt)
    {
        bool hover = captured && Input.IsActionPressed("jump");
        _stick *= MathF.Exp(-dt * (hover ? 4f : 0.9f));
        v.CyclicRoll = _stick.X;
        v.CyclicPitch = -_stick.Y;
        if (captured)
        {
            if (Input.IsActionPressed("move_forward")) v.Collective += dt * 0.45f;
            if (Input.IsActionPressed("move_back")) v.Collective -= dt * 0.45f;
            v.Pedal = (Input.IsActionPressed("move_left") ? 1f : 0f) - (Input.IsActionPressed("move_right") ? 1f : 0f);
        }
        if (hover)
        {
            // Hover assist: hold height, and lean against the drift.
            var vel = v.Velocity3;
            float want = (9.81f - vel.Y * 1.5f) / (v.Def.Lift * (v.EngineHit ? 0.62f : 1f));
            v.Collective = Mathf.MoveToward(v.Collective, want, dt * 0.8f);
            var local = v.GlobalBasis.Inverse() * vel;
            _stick = new Vector2(Mathf.Clamp(-local.X * 0.05f, -0.5f, 0.5f), Mathf.Clamp(-local.Z * 0.05f, -0.5f, 0.5f));
        }
        v.Collective = Mathf.Clamp(v.Collective, 0f, 1f);
        if (captured && Input.IsActionJustPressed("selfaid")) v.PopFlares();
        // The pods: rockets, where the nose points; or a missile at the enemy vehicle the nose is on (V switches).
        MissileLock = null;
        if (seat.Turret >= 0)
        {
            var pt = v.Turrets[seat.Turret];
            if (captured && Input.IsActionJustPressed("firemode") && pt.Def.Ammo.Length > 1)
            {
                v.SelectAmmo(seat.Turret, (pt.AmmoIdx + 1) % pt.Def.Ammo.Length);
                Hud.Toast(pt.Weapon.Guided ? $"{pt.Weapon.Name}: put the nose on an enemy vehicle" : pt.Weapon.Name, 1.5f);
            }
            if (pt.Weapon.Guided)
            {
                MissileLock = v.MissileLock(v.Forward, 12f);
                if (captured && Input.IsActionJustPressed("fire"))
                {
                    if (MissileLock == null) Hud.Toast("No lock — put the nose on an enemy vehicle", 1.5f);
                    else if (v.LaunchMissile(MissileLock))
                    {
                        // A laser or radio missile is steered from here: the target has to stay in sight until it hits.
                        var mw = pt.Weapon;
                        v.GuidingUntil = mw.FireAndForget ? Clock.Now : Clock.Now + v.Center.DistanceTo(MissileLock.Center) / mw.Speed + 0.5;
                        Hud.Toast(mw.FireAndForget ? "Missile away — fire and forget" : "Missile away — keep the target in sight until it hits", 2f);
                    }
                }
            }
            else if (captured && Input.IsActionPressed("fire")) v.Fire(seat.Turret, false);
        }

        // Chase camera: behind and above, turning with the aircraft; Alt to look around.
        bool look = captured && Input.IsActionPressed("free_look");
        if (look) { _lookYaw = _yaw - Mathf.RadToDeg(v.GlobalRotation.Y); _lookPitch = _pitch; }
        else { _lookYaw = Mathf.MoveToward(_lookYaw, 0f, dt * 120f); _lookPitch = Mathf.MoveToward(_lookPitch, -8f, dt * 60f); _yaw = Mathf.RadToDeg(v.GlobalRotation.Y) + _lookYaw; _pitch = _lookPitch; }
        var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(_lookPitch), v.GlobalRotation.Y + Mathf.DegToRad(_lookYaw), 0f));
        float dist = v.Def.Hull.Z * 1.3f + 6f;
        var focus = v.Center + Vector3.Up * 1.5f;
        var pos = focus + basis.Z * dist + Vector3.Up * 3f;
        cam.GlobalPosition = cam.GlobalPosition.Lerp(pos, 1f - MathF.Exp(-dt * 10f));
        cam.LookAt(focus - basis.Z * 30f, Vector3.Up);
        cam.Fov = 75f;
    }

    void VehicleProcess(float dt)
    {
        var v = Ride!;
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        if (captured && Input.IsActionJustPressed("use")) { v.Leave(this); return; }
        for (int i = 0; i < Math.Min(9, v.Def.Seats.Count); i++)
        {
            bool down = captured && Input.IsPhysicalKeyPressed(Key.Key1 + i);
            if (down && !_numWas[i] && i != SeatIdx)
            {
                if (v.Occupants[i] == null) v.Enter(this, i);
                else Hud.Toast("That seat's taken", 1f);
            }
            _numWas[i] = down;
        }
        if (Ride == null) return;
        var seat = v.Def.Seats[SeatIdx];
        var cam = _vcam!;
        bool sight = seat.Role == global::Ridgeline.SeatRole.Gunner && !seat.Exposed;
        v.OwnView(sight);
        cam.CullMask = sight ? 0xFFFFFu & ~2u : 0xFFFFFu;
        var look = Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitch), Mathf.DegToRad(_yaw), 0f));
        var lookDir = -look.Z;

        switch (seat.Role)
        {
            case global::Ridgeline.SeatRole.Driver when v.Def.Air:
                Fly(v, seat, cam, captured, dt);
                break;
            case global::Ridgeline.SeatRole.Driver:
            {
                var input = captured ? Input.GetVector("move_left", "move_right", "move_forward", "move_back") : Vector2.Zero;
                v.Throttle = -input.Y;
                v.Steer = -input.X;
                v.Brake = captured && Input.IsActionPressed("jump");
                // Chase camera, orbiting with the mouse.
                float dist = v.Def.Hull.Z * 0.9f + 4f;
                var focus = v.Center + Vector3.Up * 1.2f;
                _pitch = Mathf.Clamp(_pitch, -35f, 20f);
                var pos = focus - lookDir * dist + Vector3.Up * 1.5f;
                cam.GlobalPosition = cam.GlobalPosition.Lerp(pos, 1f - MathF.Exp(-dt * 12f));
                cam.LookAt(focus + lookDir * 6f, Vector3.Up);
                cam.Fov = 75f;
                break;
            }
            case global::Ridgeline.SeatRole.Gunner:
            {
                var t = v.Turrets[seat.Turret];
                // The sight sits on the gun, just above the barrel (a pintle gunner looks over his gun).
                var eye = t.Def.Exposed ? t.YawNode.GlobalPosition + Vector3.Up * 0.8f
                                        : t.PitchNode.GlobalPosition + t.YawNode.GlobalBasis.Y * 0.3f;
                cam.GlobalTransform = new Transform3D(look, eye);
                Zoomed = captured && Input.IsActionPressed("aim");
                float zoomFov = v.Def.Kind == VKind.MBT || v.Def.Kind == VKind.MGS ? 9f : v.Def.Kind == VKind.IFV ? 14f : 30f;
                cam.Fov = Mathf.Lerp(cam.Fov, Zoomed ? zoomFov : 65f, 1f - MathF.Exp(-dt * 14f));
                // Where the sight is laid: whatever's under the crosshair.
                var excl = new Godot.Collections.Array<Rid> { v.GetRid() };
                var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, eye + lookDir * 2500f, 0xFFFFFFFF, excl));
                var aim = hit.Count > 0 ? hit["position"].AsVector3() : eye + lookDir * 2500f;
                t.AimAt = aim;
                int n = t.Def.Ammo.Length + (t.Def.Coax != null ? 1 : 0);
                if (captured && Input.IsActionJustPressed("firemode") && n > 1)
                {
                    GunSel = (GunSel + 1) % n;
                    bool coax = GunSel >= t.Def.Ammo.Length;
                    if (!coax) v.SelectAmmo(seat.Turret, GunSel);
                    Hud.Toast(coax ? t.Def.Coax!.Name : t.Def.Ammo[GunSel].Name, 1.2f);
                }
                if (captured && Input.IsActionJustPressed("reload")) v.Reload(seat.Turret);
                bool isCoax = GunSel >= t.Def.Ammo.Length;
                var w = isCoax ? t.Def.Coax! : t.Def.Ammo[GunSel];
                bool trigger = captured && (w.Mag > 1 ? Input.IsActionPressed("fire") : Input.IsActionJustPressed("fire"));
                if (trigger) v.Fire(seat.Turret, isCoax, eye.DistanceTo(aim));
                break;
            }
            default:
            {
                cam.GlobalTransform = new Transform3D(look, v.SeatWorld(SeatIdx));
                cam.Fov = 75f;
                break;
            }
        }
    }
}
