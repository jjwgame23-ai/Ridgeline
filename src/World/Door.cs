using Godot;

namespace Ridgeline;

/// <summary>
/// A hinged door in a building's outside wall. It swings inward. Closed, it blocks
/// movement, sight and (mostly) sound; open, it's a gap. The player opens and closes it
/// with the use key, and bots open the ones in their way (and leave them open, as people
/// do). Doors are on their own physics layer, so the navmesh is baked as if every
/// doorway were open.
/// </summary>
public partial class Door : Node3D
{
    public static readonly List<Door> All = new();
    const float Cell = 16f;
    static readonly Dictionary<(int, int), List<Door>> _grid = new();

    public bool IsOpen { get; private set; }
    public float Width = 1.3f;
    float _angle, _target, _swing = 1f;
    StaticBody3D _leaf = null!;

    public static void Clear()
    {
        All.Clear();
        _grid.Clear();
    }

    /// <summary>A door hung at <paramref name="hinge"/>, the leaf running along <paramref name="along"/>, opening toward <paramref name="inward"/>.</summary>
    public static Door Make(Node3D parent, Vector3 hinge, Vector3 along, Vector3 inward, float width, float height, bool open, Material mat)
    {
        along = (along with { Y = 0f }).Normalized();
        var d = new Door { Width = width };
        parent.AddChild(d);
        var z = along.Cross(Vector3.Up);
        d.GlobalTransform = new Transform3D(new Basis(along, Vector3.Up, z), hinge);
        // Rotating +angle about Y swings local X toward -Z: pick the sign that swings it inward.
        d._swing = z.Dot(inward) < 0f ? 1f : -1f;
        var leaf = new StaticBody3D { CollisionLayer = Layers.Doors, CollisionMask = 0 };
        d.AddChild(leaf);
        leaf.Position = new Vector3(width / 2f, height / 2f, 0f);
        var size = new Vector3(width - 0.04f, height - 0.02f, 0.05f);
        leaf.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
        leaf.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        d._leaf = leaf;
        d.IsOpen = open;
        d._angle = d._target = open ? Mathf.DegToRad(165f) : 0f;
        d.ApplyAngle();
        d.SetProcess(false);
        All.Add(d);
        var key = ((int)MathF.Floor(hinge.X / Cell), (int)MathF.Floor(hinge.Z / Cell));
        if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<Door>();
        l.Add(d);
        return d;
    }

    Basis _base;
    bool _baseSet;

    void ApplyAngle()
    {
        if (!_baseSet) { _base = GlobalBasis; _baseSet = true; }
        GlobalBasis = new Basis(Vector3.Up, _angle * _swing) * _base;
    }

    public void SetOpen(bool open, ICombatant? by = null)
    {
        if (open == IsOpen) return;
        IsOpen = open;
        _target = open ? Mathf.DegToRad(165f) : 0f;
        SetProcess(true);
        SoundWorld.I?.Emit(open ? Snd.DoorOpen : Snd.DoorClose, Middle, 0f, by);
    }

    public void Toggle(ICombatant? by = null) => SetOpen(!IsOpen, by);

    /// <summary>The middle of the doorway (where the leaf is when closed).</summary>
    public Vector3 Middle => _base.X * (Width / 2f) + GlobalPosition + Vector3.Up * 1.05f;

    public override void _Process(double delta)
    {
        _angle = Mathf.MoveToward(_angle, _target, (float)delta * 2.6f);
        ApplyAngle();
        if (MathF.Abs(_angle - _target) < 1e-4f) SetProcess(false);
    }

    static IEnumerable<Door> Near(Vector3 p)
    {
        int ci = (int)MathF.Floor(p.X / Cell), cj = (int)MathF.Floor(p.Z / Cell);
        for (int i = -1; i <= 1; i++)
        for (int j = -1; j <= 1; j++)
            if (_grid.TryGetValue((ci + i, cj + j), out var l))
                foreach (var d in l) yield return d;
    }

    /// <summary>A bot walking this way: open a closed door just ahead of it.</summary>
    public static void OpenAhead(ICombatant who, Vector3 feet, Vector3 dir)
    {
        foreach (var d in Near(feet))
        {
            if (d.IsOpen || !IsInstanceValid(d)) continue;
            var to = (d.Middle - feet) with { Y = 0f };
            float dist = to.Length();
            if (dist > 1.8f || MathF.Abs(d.Middle.Y - 1.05f - feet.Y) > 1.2f) continue;
            if (dist > 0.6f && to.Normalized().Dot(dir) < 0.2f) continue;
            d.SetOpen(true, who);
        }
    }

    /// <summary>The player pressed use: the door they're looking at, within reach.</summary>
    public static bool Use(ICombatant who, Vector3 eye, Vector3 look)
    {
        Door? best = null;
        float bestScore = float.MaxValue;
        foreach (var d in Near(eye))
        {
            if (!IsInstanceValid(d)) continue;
            var to = d.Middle - eye;
            float dist = to.Length();
            if (dist > 2.6f) continue;
            float ang = look.AngleTo(to);
            if (ang > 0.9f) continue;
            float score = dist + ang * 2f;
            if (score < bestScore) { bestScore = score; best = d; }
        }
        if (best == null) return false;
        best.Toggle(who);
        return true;
    }
}
