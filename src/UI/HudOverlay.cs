using Godot;

namespace Ridgeline;

/// <summary>Draws the sight picture (red dot or 4x scope) where the weapon actually points, plus the compass.</summary>
public partial class HudOverlay : Control
{
    /// <summary>A world position to mark on the compass (the KOTH zone).</summary>
    public static Vector3? Marker;
    /// <summary>Where your squad wants you, and which way you should be watching (the squad briefing).</summary>
    public static Vector3? Spot, Sector;

    static readonly string[] Cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    public override void _Draw()
    {
        var hud = Hud.I;
        if (hud == null) return;
        var p = hud.P;
        if (p == null || !IsInstanceValid(p) || !p.Alive) return;
        var w = p.Weapon;
        var size = GetViewportRect().Size;
        if (p.Piloting is { } dr && IsInstanceValid(dr)) { DrawDrone(size, p, dr); return; }

        if (p.Ride != null)
        {
            DrawVehicle(size, p);
            DrawCompass(size, p.Heading);
            if (Marker is Vector3 vm) DrawMarker(size, p, vm);
            return;
        }
        var aimPoint = p.Cam.GlobalPosition + w.AimDir * 300f;
        var c = p.Cam.IsPositionBehind(aimPoint) ? size / 2f : p.Cam.UnprojectPosition(aimPoint);

        if (w.Def.Guided && w.AimT > 0.55f)
        {
            // Seeker: a box on whatever it's tracking, solid when locked.
            if (w.LockTarget is Vehicle lt && !p.Cam.IsPositionBehind(lt.Center))
            {
                var q = p.Cam.UnprojectPosition(lt.Center);
                var col = w.LockProgress >= 1f ? new Color(1f, 0.25f, 0.2f) : new Color(1f, 0.85f, 0.3f, 0.5f + 0.5f * w.LockProgress);
                DrawRect(new Rect2(q - new Vector2(18, 18), new Vector2(36, 36)), col, false, 2f);
                DrawString(ThemeDB.FallbackFont, q + new Vector2(22, 5), w.LockProgress >= 1f ? "LOCK" : "tracking", HorizontalAlignment.Left, -1, 13, col);
            }
            DrawArc(c, 40f, 0f, Mathf.Tau, 48, new Color(0.1f, 1f, 0.3f, 0.6f), 1.2f);
        }
        else if (w.Def.Explosive && w.AimT > 0.55f) DrawLadder(c, size, p.Cam.Fov, (w.AimT - 0.55f) / 0.45f, w.Def);
        else if (w.Def.Scoped && w.AimT > 0.92f) DrawScope(c, size, p.Cam.Fov);
        else if (!w.Def.Scoped && w.AimT > 0.55f)
        {
            float a = (w.AimT - 0.55f) / 0.45f;
            DrawCircle(c, 5f, new Color(1f, 0.1f, 0.05f, 0.25f * a));
            DrawCircle(c, 2f, new Color(1f, 0.15f, 0.1f, a));
        }
        DrawCompass(size, p.Heading);
        if (Marker is Vector3 m) DrawMarker(size, p, m);
        DrawSquadSpot(size, p);
        DrawSquadmates(p.Cam);
    }

    /// <summary>Your squad: a small green triangle over each of them, so you can tell them from the rest of the side.</summary>
    void DrawSquadmates(Camera3D cam)
    {
        var sq = TerritoryMode.I?.PlayerSquad;
        if (sq == null) return;
        foreach (var c in sq.Members)
        {
            // The downed too, in red: they're the ones to look for. (Alive is false for a man who's down, so they were
            // never marked, and the red below was never drawn.)
            if (c is Player || c.Dead || !GodotObject.IsInstanceValid((GodotObject)c) || c.Ride != null) continue;
            var head = c.FeetPos + Vector3.Up * 2.15f;
            if (cam.IsPositionBehind(head)) continue;
            float d = head.DistanceTo(cam.GlobalPosition);
            if (d > 600f) continue;
            var q = cam.UnprojectPosition(head);
            // Right beside the camera the projection runs off to huge values, and the marker's triangle can't be drawn
            // (Godot: "triangulation failed", every frame).
            if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !GetViewportRect().Grow(200f).HasPoint(q)) continue;
            float s = Mathf.Clamp(9f - d * 0.02f, 4f, 9f);
            var col = c.Downed ? new Color(1f, 0.4f, 0.3f, 0.9f) : c == sq.Leader ? new Color(0.6f, 1f, 0.5f, 0.95f) : new Color(0.35f, 0.95f, 0.35f, 0.85f);
            DrawColoredPolygon(new[] { q + new Vector2(-s, -s * 1.3f), q + new Vector2(s, -s * 1.3f), q }, col);
            if (d < 60f) DrawString(ThemeDB.FallbackFont, q + new Vector2(-30, -s * 1.3f - 4f), c.Callsign, HorizontalAlignment.Center, 60, 11, col);
        }
    }

    /// <summary>Your place in the squad's plan: a green diamond where you should be, and your sector on the compass.</summary>
    void DrawSquadSpot(Vector2 size, Player p)
    {
        var green = new Color(0.45f, 1f, 0.45f, 0.9f);
        if (Sector is Vector3 sec && sec.LengthSquared() > 0.01f)
        {
            float bearing = (Mathf.RadToDeg(MathF.Atan2(sec.X, -sec.Z)) + 360f) % 360f;
            float rel = BotAim.Wrap(bearing - p.Heading);
            const float span = 90f, width = 520f;
            if (MathF.Abs(rel) < span / 2f)
            {
                float x = size.X / 2f + rel / span * width;
                DrawRect(new Rect2(x - 14f, 30f, 28f, 3f), green);
            }
        }
        if (Spot is not Vector3 spot) return;
        float d = ((spot - p.FeetPos) with { Y = 0f }).Length();
        if (d < 6f) return;
        var cam = p.Cam;
        var at = spot + Vector3.Up * 1f;
        if (cam.IsPositionBehind(at)) return;
        var q = cam.UnprojectPosition(at);
        DrawColoredPolygon(new[] { q + new Vector2(0, -7), q + new Vector2(7, 0), q + new Vector2(0, 7), q + new Vector2(-7, 0) }, green with { A = 0.55f });
        DrawString(ThemeDB.FallbackFont, q + new Vector2(10, 5), $"your spot {d:0} m", HorizontalAlignment.Left, -1, 12, green);
    }

    /// <summary>
    /// The 40 mm leaf sight: a mark per range. Put the mark for the target's range on
    /// the target. Zeroed at 100 m, so nearer marks sit above the centre, further below.
    /// </summary>
    void DrawLadder(Vector2 c, Vector2 size, float fov, float a, WeaponDef def)
    {
        float pxPerRad = size.Y * 0.5f / MathF.Tan(Mathf.DegToRad(fov * 0.5f));
        float v = def.MuzzleVel;
        float El(float r) => def.Drag > 0f ? Ballistics.ZeroAngle(v, def.Drag, r) : 0.5f * MathF.Asin(Mathf.Clamp(9.81f * r / (v * v), 0f, 1f));
        float zero = El(def.ZeroM);
        var ink = new Color(0.1f, 1f, 0.3f, 0.85f * a);
        DrawLine(c + new Vector2(0, -12), c + new Vector2(0, 12), ink, 1.2f);
        foreach (int r in def.Rocket ? new[] { 100, 150, 200, 250, 300, 400 } : new[] { 50, 100, 150, 200, 250, 300 })
        {
            float y = (El(r) - zero) * pxPerRad;
            float half = r % 100 == 0 ? 14f : 8f;
            DrawLine(c + new Vector2(-half, y), c + new Vector2(half, y), ink, 1.5f);
            DrawString(ThemeDB.FallbackFont, c + new Vector2(half + 4, y + 5), $"{r}", HorizontalAlignment.Left, -1, 12, ink);
        }
    }

    /// <summary>In a vehicle: the state of it, and for the gunner the sight — crosshair where you look, a ring where the gun actually points.</summary>
    void DrawVehicle(Vector2 size, Player p)
    {
        var v = p.Ride!;
        var font = ThemeDB.FallbackFont;
        var seat = v.Def.Seats[p.SeatIdx];
        var cam = GetViewport().GetCamera3D();
        var lines = new List<string>
        {
            $"{v.Def.Name} ({v.Def.ClassName}) · {MathF.Abs(v.Speed) * 3.6f:0} km/h · hull {v.Status()}",
            "seats: " + string.Join("  ", v.Def.Seats.Select((st, i) => $"[{i + 1}] {(st.Role == SeatRole.Driver ? "DRV" : st.Role == SeatRole.Gunner ? "GUN" : "PAX")}{(v.Occupants[i] == null ? "" : v.Occupants[i] == p ? " (you)" : " ■")}")),
        };
        if (seat.Role == SeatRole.Gunner && cam != null)
        {
            var t = v.Turrets[seat.Turret];
            var c = size / 2f;
            var ink = new Color(0.1f, 1f, 0.3f, 0.9f);
            DrawLine(c + new Vector2(-40, 0), c + new Vector2(-8, 0), ink, 1.5f);
            DrawLine(c + new Vector2(40, 0), c + new Vector2(8, 0), ink, 1.5f);
            DrawLine(c + new Vector2(0, 8), c + new Vector2(0, 40), ink, 1.5f);
            var gunPt = t.Muzzle.GlobalPosition + t.Forward * 400f;
            if (!cam.IsPositionBehind(gunPt))
            {
                var g = cam.UnprojectPosition(gunPt);
                DrawArc(g, 10f, 0f, Mathf.Tau, 24, new Color(1f, 0.9f, 0.3f, 0.9f), 1.5f);
            }
            bool coax = p.GunSel >= t.Def.Ammo.Length;
            string ammo = coax
                ? $"{t.Def.Coax!.Name} {t.CoaxLoaded}/{t.Def.Coax.Mag} +{t.CoaxStock}{(t.CoaxReloadT > 0f ? $"  RELOADING {t.CoaxReloadT:0.0}s" : "")}"
                : $"{t.Weapon.Name} {t.Loaded[t.AmmoIdx]}{(t.Weapon.Mag > 1 ? $"/{t.Weapon.Mag}" : "")} +{t.Stock[t.AmmoIdx]}{(t.Reloading ? $"  LOADING {t.ReloadT:0.0}s" : "")}";
            if (t.Def.Indirect && t.AimAt is Vector3 lay)
                lines.Add($"laying on {lay.DistanceTo(t.YawNode.GlobalPosition):0} m, bearing {Mathf.PosMod(-Mathf.RadToDeg(MathF.Atan2(lay.X - t.YawNode.GlobalPosition.X, -(lay.Z - t.YawNode.GlobalPosition.Z))), 360f):000} · {(t.OutOfRange ? "OUT OF RANGE" : t.Laid ? $"ON TARGET, charge {Mathf.RoundToInt((t.Charge - 0.4f) / 0.15f)} — fire" : "laying...")} · look at the spot to aim");
            lines.Add($"{ammo} · " + Controls.Fill("[{firemode}] ammo · [{reload}] reload · [{aim}] zoom") + (v.TurretDown[seat.Turret] ? " · TURRET JAMMED" : ""));
        }
        else if (seat.Role == SeatRole.Driver && v.Def.Air)
        {
            lines.Add($"alt {v.Agl:0} m · {v.AirSpeed * 3.6f:0} km/h · climb {v.Velocity3.Y:+0.0;-0.0} m/s · collective {v.Collective * 100:0}% · flares {v.FlaresLeft}" +
                      (seat.Turret >= 0 ? $" · rockets {v.Turrets[seat.Turret].Loaded[0]}" + (v.MissileIdx >= 0 ? $" · missiles {v.MissilesLeft}" : "") : "") + (v.Landed ? " · ON THE GROUND" : ""));
            bool missilesOn = seat.Turret >= 0 && v.Turrets[seat.Turret].Weapon.Guided;
            lines.Add(Controls.Fill("[mouse] cyclic · [{move_forward}/{move_back}] collective · [{move_left}/{move_right}] pedals · [{jump}] hover assist · [{selfaid}] flares · [{free_look}] look"
                + (seat.Turret >= 0 ? (missilesOn ? " · [{fire}] missile (nose on a vehicle)" : " · [{fire}] rockets") + (v.MissileIdx >= 0 ? " · [{firemode}] rockets/missiles" : "") : "")));
            // The missile sight: the vehicle it would go for.
            if (p.MissileLock is { } lk && cam != null && !cam.IsPositionBehind(lk.TopPoint))
            {
                var q = cam.UnprojectPosition(lk.TopPoint);
                var col = new Color(1f, 0.35f, 0.25f, 0.95f);
                DrawRect(new Rect2(q - new Vector2(16, 16), new Vector2(32, 32)), col, false, 2f);
                DrawString(font, q + new Vector2(20, -8), $"{lk.Def.ClassName} {v.Center.DistanceTo(lk.Center) / 1000f:0.0} km", HorizontalAlignment.Left, -1, 14, col);
            }
            else if (missilesOn) DrawString(font, new Vector2(size.X / 2f - 60f, size.Y / 2f + 60f), "NO LOCK", HorizontalAlignment.Left, -1, 16, new Color(1f, 1f, 1f, 0.6f));
            if (Clock.Now < v.GuidingUntil)
                DrawString(font, new Vector2(size.X / 2f - 110f, size.Y / 2f + 84f), $"GUIDING — keep it in sight, {v.GuidingUntil - Clock.Now:0.0} s", HorizontalAlignment.Left, -1, 16, new Color(1f, 0.85f, 0.3f, 0.95f));
            if (Clock.Now - v.MissileWarning < 4.0)
            {
                bool blink = (int)(Clock.Now * 4) % 2 == 0;
                DrawString(font, new Vector2(size.X / 2f - 120f, size.Y * 0.3f), $"MISSILE LAUNCH — [{Controls.Keys("selfaid")}] FLARES", HorizontalAlignment.Left, -1, 22, blink ? Colors.Red : Colors.Orange);
            }
            // The radar warning receiver: an air defence gun is locking on (it fires a few seconds later).
            else if (Clock.Now - v.RadarWarning < 0.5 && v.RadarFrom is { } rdr && GodotObject.IsInstanceValid(rdr))
            {
                bool blink = (int)(Clock.Now * 6) % 2 == 0;
                DrawString(font, new Vector2(size.X / 2f - 120f, size.Y * 0.3f), $"RADAR LOCK — {Comms.Bearing(v.Center, rdr.Center).ToUpperInvariant()} — GET LOW", HorizontalAlignment.Left, -1, 22, blink ? Colors.Red : Colors.Orange);
            }
            // Where the rockets will go: along the pods.
            if (seat.Turret >= 0 && cam != null)
            {
                var pt = v.Turrets[seat.Turret].Muzzle.GlobalPosition + v.Turrets[seat.Turret].Forward * 700f;
                if (!cam.IsPositionBehind(pt))
                {
                    var q = cam.UnprojectPosition(pt);
                    DrawArc(q, 12f, 0f, Mathf.Tau, 20, new Color(0.1f, 1f, 0.3f, 0.9f), 1.5f);
                    DrawLine(q + new Vector2(-20, 0), q + new Vector2(-12, 0), new Color(0.1f, 1f, 0.3f, 0.9f), 1.5f);
                    DrawLine(q + new Vector2(20, 0), q + new Vector2(12, 0), new Color(0.1f, 1f, 0.3f, 0.9f), 1.5f);
                }
            }
        }
        else if (seat.Role == SeatRole.Driver) lines.Add(Controls.Fill("[{move_forward}/{move_back}] drive · [{move_left}/{move_right}] steer · [{jump}] brake · [{use}] get out"));
        float y = size.Y - 30f - lines.Count * 20f;
        foreach (var l in lines)
        {
            DrawString(font, new Vector2(20, y + 1), l, HorizontalAlignment.Left, -1, 16, Colors.Black);
            DrawString(font, new Vector2(19, y), l, HorizontalAlignment.Left, -1, 16, new Color(0.9f, 0.95f, 0.85f));
            y += 20f;
        }
    }

    void DrawScope(Vector2 c, Vector2 size, float fov)
    {
        float r = size.Y * 0.44f;
        // Everything outside the eyepiece is black: one very thick ring.
        DrawArc(c, r + 1500f, 0f, Mathf.Tau, 256, Colors.Black, 3000f);
        DrawArc(c, r, 0f, Mathf.Tau, 256, new Color(0, 0, 0, 0.55f), 14f);

        var ink = new Color(0, 0, 0, 0.92f);
        float inner = r * 0.12f;
        DrawLine(c + new Vector2(-r, 0), c + new Vector2(-inner, 0), ink, 4f);
        DrawLine(c + new Vector2(r, 0), c + new Vector2(inner, 0), ink, 4f);
        DrawLine(c + new Vector2(0, r), c + new Vector2(0, inner), ink, 4f);
        DrawLine(c + new Vector2(0, -r), c + new Vector2(0, -inner), ink, 4f);
        DrawLine(c + new Vector2(-inner, 0), c + new Vector2(inner, 0), ink, 1.2f);
        DrawLine(c + new Vector2(0, -inner), c + new Vector2(0, inner), ink, 1.2f);

        // Mil dots for holdover: 1 mil = 1 m at 1000 m.
        float pxPerMil = size.Y * 0.5f / MathF.Tan(Mathf.DegToRad(fov * 0.5f)) * 0.001f;
        for (int k = 1; k <= 5; k++)
        {
            float o = k * pxPerMil;
            if (o > inner) break;
            DrawCircle(c + new Vector2(0, o), 1.8f, ink);
            DrawCircle(c + new Vector2(0, -o), 1.8f, ink);
            DrawCircle(c + new Vector2(o, 0), 1.8f, ink);
            DrawCircle(c + new Vector2(-o, 0), 1.8f, ink);
        }
    }

    void DrawMarker(Vector2 size, Player p, Vector3 target)
    {
        var d = target - p.FeetPos;
        float bearing = (Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360f) % 360f;
        float rel = BotAim.Wrap(bearing - p.Heading);
        const float span = 90f, width = 520f;
        float x = size.X / 2f + Mathf.Clamp(rel, -span / 2f, span / 2f) / span * width;
        var c = new Vector2(x, 12f);
        var col = new Color(1f, 0.85f, 0.2f, MathF.Abs(rel) > span / 2f ? 0.45f : 1f);
        DrawColoredPolygon(new[] { c + new Vector2(0, -6), c + new Vector2(6, 0), c + new Vector2(0, 6), c + new Vector2(-6, 0) }, col);
    }

    /// <summary>The drone's screen: a reticle, the flight data, and the grain of a cheap video link.</summary>
    void DrawDrone(Vector2 size, Player p, Drone d)
    {
        var font = ThemeDB.FallbackFont;
        var c = size / 2f;
        var ink = new Color(1f, 1f, 1f, 0.85f);
        var pos = d.GlobalPosition;
        var hit = d.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(pos, pos + Vector3.Down * 500f, Layers.World | Layers.Trees));
        float agl = hit.Count > 0 ? pos.Y - hit["position"].AsVector3().Y : 0f;
        float range = ((pos - p.FeetPos) with { Y = 0f }).Length();
        // The link gets worse with range: snow.
        float snow = Mathf.Clamp((range - 900f) / 1500f, 0f, 0.8f) + (d.IsFpv ? 0.08f : 0.02f);
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)Time.GetTicksMsec();
        int n = (int)(snow * 900);
        for (int i = 0; i < n; i++)
        {
            var at = new Vector2(rng.Randf() * size.X, rng.Randf() * size.Y);
            float g = rng.Randf();
            DrawRect(new Rect2(at, new Vector2(rng.RandfRange(1f, 4f), 1.5f)), new Color(g, g, g, 0.35f));
        }
        if (d.Kind == DroneKind.Quad)
        {
            DrawLine(c + new Vector2(-24, 0), c + new Vector2(-6, 0), ink, 1.5f);
            DrawLine(c + new Vector2(24, 0), c + new Vector2(6, 0), ink, 1.5f);
            DrawLine(c + new Vector2(0, -24), c + new Vector2(0, -6), ink, 1.5f);
            DrawLine(c + new Vector2(0, 24), c + new Vector2(0, 6), ink, 1.5f);
            // Straight down: where a grenade let go of now would land (near enough, from low and slow).
            var cam = GetViewport().GetCamera3D();
            if (cam != null && hit.Count > 0)
            {
                var fall = MathF.Sqrt(2f * MathF.Max(agl, 0f) / 9.81f);
                var land = hit["position"].AsVector3() + (d.Vel with { Y = 0f }) * fall * 0.5f;
                if (!cam.IsPositionBehind(land))
                {
                    var q = cam.UnprojectPosition(land);
                    DrawArc(q, 9f, 0f, Mathf.Tau, 24, new Color(1f, 0.3f, 0.2f, 0.9f), 1.5f);
                    DrawLine(q + new Vector2(-4, 0), q + new Vector2(4, 0), new Color(1f, 0.3f, 0.2f, 0.9f), 1.5f);
                }
            }
        }
        else
        {
            DrawArc(c, 10f, 0f, Mathf.Tau, 24, ink, 1.5f);
            DrawLine(c + new Vector2(-60, 0), c + new Vector2(-20, 0), ink, 1.2f);
            DrawLine(c + new Vector2(60, 0), c + new Vector2(20, 0), ink, 1.2f);
        }
        string kind = d.Kind switch { DroneKind.Quad => "QUAD", DroneKind.FpvAt => "AT FPV", _ => "FPV" };
        var lines = new List<string>
        {
            $"{kind}   ALT {agl:0} m   SPD {d.Vel.Length() * 3.6f:0} km/h   DIST {range:0} m",
            d.Kind == DroneKind.Quad ? $"BATT {d.Battery * 100f:0}%   GRENADES {d.Bombs}   CAM {d.CamPitch:0}°" : $"THROTTLE {d.Throttle * 100f:0}%   PITCH {d.CamPitch:0}°",
            $"stock: {p.DroneQuads} quad · {p.DroneBombs} grenades · {p.DroneFpvs} FPV · {p.DroneAtFpvs} AT FPV",
            Controls.Fill(d.Kind == DroneKind.Quad ? "{move_forward} {move_left} {move_back} {move_right} fly ({sprint} fast) · {jump}/{crouch} up/down · {fire} drop · {gadget} send it home" : "mouse steer · {move_forward}/{move_back} throttle · {fire} detonate · {gadget} ditch"),
        };
        for (int i = 0; i < lines.Count; i++)
            DrawString(font, new Vector2(24, size.Y - 100 + i * 20), lines[i], HorizontalAlignment.Left, -1, 15, i == 3 ? new Color(1, 1, 1, 0.55f) : ink);
        if (d.Kind == DroneKind.Quad && d.Battery < 0.2f)
            DrawString(font, c + new Vector2(-60, -60), "LOW BATTERY", HorizontalAlignment.Left, -1, 18, new Color(1f, 0.3f, 0.2f));
        DrawCompass(size, ((-d.Yaw) % 360f + 360f) % 360f);
    }

    void DrawCompass(Vector2 size, float heading)
    {
        var font = ThemeDB.FallbackFont;
        const float span = 90f, width = 520f, y = 18f;
        float cx = size.X / 2f;
        var col = new Color(1, 1, 1, 0.75f);
        int start = (int)MathF.Floor((heading - span / 2f) / 5f) * 5;
        for (int deg = start; deg <= heading + span / 2f + 5f; deg += 5)
        {
            float x = cx + (deg - heading) / span * width;
            if (x < cx - width / 2f || x > cx + width / 2f) continue;
            int n = ((deg % 360) + 360) % 360;
            bool major = n % 15 == 0;
            DrawLine(new Vector2(x, y), new Vector2(x, y + (major ? 10f : 5f)), col, 1.5f);
            string? label = n % 45 == 0 ? Cardinals[n / 45] : major ? n.ToString() : null;
            if (label != null) DrawString(font, new Vector2(x - 20f, y + 26f), label, HorizontalAlignment.Center, 40f, 13, col);
        }
        DrawLine(new Vector2(cx, y - 6f), new Vector2(cx, y + 12f), new Color(1f, 0.7f, 0.2f), 2f);
        DrawString(font, new Vector2(cx - 30f, y + 44f), $"{heading:000}°", HorizontalAlignment.Center, 60f, 12, col);
    }
}
