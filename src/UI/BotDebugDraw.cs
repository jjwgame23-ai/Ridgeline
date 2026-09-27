using Godot;

namespace Ridgeline;

/// <summary>
/// F6: what every bot is thinking — a label over its head (state, target,
/// awareness, aim error), a line along its gun and its planned path.
/// </summary>
public partial class BotDebugDraw : Node3D
{
    public IMatch Mode = null!;

    readonly Dictionary<Bot, Label3D> _labels = new();
    readonly Dictionary<Drone, Label3D> _droneLabels = new();
    ImmediateMesh _mesh = null!;
    MeshInstance3D _lines = null!;
    bool _on;
    float _labelT;

    static readonly Color[] TeamCol = { new(0.5f, 0.66f, 1f), new(1f, 0.55f, 0.44f), new(1f, 0.83f, 0.35f) };

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        _lines = new MeshInstance3D
        {
            Mesh = _mesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                NoDepthTest = true,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_lines);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("bot_debug"))
        {
            _on = !_on;
            if (!_on) Clear();
        }
    }

    void Clear()
    {
        foreach (var l in _labels.Values) if (IsInstanceValid(l)) l.QueueFree();
        _labels.Clear();
        foreach (var l in _droneLabels.Values) if (IsInstanceValid(l)) l.QueueFree();
        _droneLabels.Clear();
        _mesh.ClearSurfaces();
    }

    public override void _Process(double delta)
    {
        if (!_on) return;
        foreach (var dead in _labels.Keys.Where(b => !IsInstanceValid(b)).ToList())
        {
            _labels[dead].QueueFree();
            _labels.Remove(dead);
        }

        _labelT -= (float)delta;
        bool relabel = _labelT <= 0f;
        if (relabel) _labelT = 0.1f;

        _mesh.ClearSurfaces();
        bool any = false;
        foreach (var b in Mode.Bots)
        {
            if (!IsInstanceValid(b)) continue;
            if (!_labels.TryGetValue(b, out var label))
            {
                label = new Label3D
                {
                    FontSize = 36, PixelSize = 0.0008f, OutlineSize = 8, FixedSize = true,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                    Modulate = TeamCol[b.Team],
                };
                AddChild(label);
                _labels[b] = label;
            }
            label.GlobalPosition = b.FeetPos + Vector3.Up * 2.3f;
            label.Visible = !(Mode.Spec.Active && Mode.Spec.Target == b && Mode.Spec.ViewMode != Spectator.View.Free);
            if (relabel)
            {
                var br = b.Brain;
                var t = br.Target;
                label.Text = !b.Alive ? $"{b.Callsign} (dead)" :
                    $"{b.Callsign} [{br.State}] {b.Health:0}hp {b.Ammo}rd{(b.Reloading ? " RELOAD" : "")}\n" +
                    $"{br.Note}\n" +
                    (t == null ? "no target" : $"→ {t.Who.Callsign} {(t.Visible ? "SEEN" : "known")} aw {t.Awareness:0.0}{(br.Reacting ? " reacting" : "")} err {br.AimErrorDeg:0.0}°") +
                    $"\nsupp {b.Suppression:0.00}";
            }
            if (!b.Alive) continue;

            if (!any) { _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines); any = true; }
            _mesh.SurfaceSetColor(TeamCol[b.Team]);
            _mesh.SurfaceAddVertex(b.EyePos);
            _mesh.SurfaceAddVertex(b.EyePos + b.Aim.Dir * 25f);

            var path = b.PathPoints;
            if (!b.Arrived && path.Length > 0)
            {
                _mesh.SurfaceSetColor(new Color(1f, 1f, 0.3f, 0.8f));
                var prev = b.FeetPos + Vector3.Up * 0.1f;
                for (int i = b.PathIndex; i < path.Length; i++)
                {
                    var p = path[i] + Vector3.Up * 0.1f;
                    _mesh.SurfaceAddVertex(prev);
                    _mesh.SurfaceAddVertex(p);
                    prev = p;
                }
            }
            if (CoverOf(b) is { } cover)
            {
                _mesh.SurfaceSetColor(new Color(0.3f, 1f, 0.4f));
                _mesh.SurfaceAddVertex(cover + Vector3.Up * 0.05f);
                _mesh.SurfaceAddVertex(cover + Vector3.Up * 1.2f);
            }
        }
        // Drones: a box round each (they're hard to see), a label, and a line to what an FPV is going for.
        foreach (var dead in _droneLabels.Keys.Where(d => !IsInstanceValid(d) || d.Dead).ToList())
        {
            if (IsInstanceValid(_droneLabels[dead])) _droneLabels[dead].QueueFree();
            _droneLabels.Remove(dead);
        }
        foreach (var d in Drone.All)
        {
            if (!IsInstanceValid(d) || d.Dead) continue;
            if (!_droneLabels.TryGetValue(d, out var dl))
            {
                dl = new Label3D { FontSize = 34, PixelSize = 0.0008f, OutlineSize = 8, FixedSize = true, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Modulate = TeamCol[d.Team] };
                AddChild(dl);
                _droneLabels[d] = dl;
            }
            var p = d.GlobalPosition;
            dl.GlobalPosition = p + Vector3.Up * 1.2f;
            var tp = d.TargetPoint();
            string kind = d.Kind switch { DroneKind.Quad => "QUAD", DroneKind.FpvAt => "AT FPV", _ => "FPV" };
            dl.Text = d.IsFpv
                ? $"{kind} {d.Operator?.Callsign} {d.Vel.Length() * 3.6f:0} km/h{(d.Terminal ? " DIVING" : "")}\n→ {(d.TargetV != null ? d.TargetV.Def.Name : d.TargetC?.Callsign ?? "point")} {p.DistanceTo(tp):0} m"
                : $"{kind} {d.Operator?.Callsign} {d.Bombs} bombs batt {d.Battery * 100f:0}%\nsees {d.Seen.Count(kv => Clock.Now - kv.Value < 3)}";
            if (!any) { _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines); any = true; }
            _mesh.SurfaceSetColor(TeamCol[d.Team]);
            const float r = 0.6f;
            var corners = new[] { new Vector3(-r, -r, -r), new Vector3(r, -r, -r), new Vector3(r, -r, r), new Vector3(-r, -r, r) };
            for (int i = 0; i < 4; i++)
            {
                var a0 = p + corners[i]; var b0 = p + corners[(i + 1) % 4];
                _mesh.SurfaceAddVertex(a0); _mesh.SurfaceAddVertex(b0);
                _mesh.SurfaceAddVertex(a0 + Vector3.Up * 2 * r); _mesh.SurfaceAddVertex(b0 + Vector3.Up * 2 * r);
                _mesh.SurfaceAddVertex(a0); _mesh.SurfaceAddVertex(a0 + Vector3.Up * 2 * r);
            }
            if (d.IsFpv)
            {
                _mesh.SurfaceSetColor(new Color(1f, 0.3f, 0.2f, 0.9f));
                _mesh.SurfaceAddVertex(p);
                _mesh.SurfaceAddVertex(tp);
            }
        }
        if (any) _mesh.SurfaceEnd();

        static Vector3? CoverOf(Bot b) => b.Brain.Cover?.Pos;
    }
}
