using Godot;

namespace Ridgeline;

/// <summary>
/// Things engineers build. A sandbag wall is ~2.6 m of waist-high bags in a
/// shallow arc: hides a crouched man, lets a standing one shoot over it, stops
/// bullets. It's ordinary world geometry, so cover finding picks it up with no
/// special cases. (The navmesh isn't rebaked around it yet: bots walking into one
/// sidestep round it.)
/// </summary>
public static class Fortifications
{
    public const int Cap = 90; // across the whole map, oldest go first
    static readonly Queue<Node3D> _all = new();
    static StandardMaterial3D? _bag;

    public static int Count => _all.Count;

    public static void Clear() => _all.Clear();

    /// <param name="facing">The direction the wall should protect against (towards the enemy).</param>
    public static Node3D? Sandbags(Node parent, IGround? ground, Vector3 at, Vector3 facing)
    {
        facing.Y = 0f;
        if (facing.LengthSquared() < 0.01f) return null;
        facing = facing.Normalized();
        _bag ??= new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.49f, 0.36f), Roughness = 1f };
        var right = facing.Cross(Vector3.Up);
        var root = new StaticBody3D { CollisionLayer = Layers.World };
        root.AddToGroup("fortification");
        parent.AddChild(root);
        float y0 = ground?.HeightAt(at.X, at.Z) ?? at.Y;
        root.GlobalPosition = at with { Y = y0 };

        var rng = new RandomNumberGenerator();
        rng.Randomize();
        // Three segments bent back at the ends, like a real fighting position.
        for (int seg = -1; seg <= 1; seg++)
        {
            var mid = right * (seg * 0.9f) - facing * (MathF.Abs(seg) * 0.25f);
            var along = (right - facing * (seg * 0.28f)).Normalized();
            var basis = Basis.LookingAt(along.Cross(Vector3.Up), Vector3.Up);
            float yLocal = (ground?.HeightAt(at.X + mid.X, at.Z + mid.Z) ?? y0) - y0;
            root.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(0.95f, 1.05f, 0.5f) },
                Transform = new Transform3D(basis, mid + Vector3.Up * (yLocal + 0.5f)),
            });
            for (int row = 0; row < 4; row++)
            for (int k = 0; k < 2; k++)
            {
                float off = (k - 0.5f) * 0.46f + (row % 2 == 1 ? 0.12f : 0f);
                if (MathF.Abs(off) > 0.5f) continue;
                var bag = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.46f, 0.25f, 0.34f) },
                    MaterialOverride = _bag,
                };
                root.AddChild(bag);
                bag.Transform = new Transform3D(basis.Rotated(Vector3.Up, rng.RandfRange(-0.06f, 0.06f)),
                    mid + along * off + Vector3.Up * (yLocal + 0.13f + row * 0.25f) + facing * rng.RandfRange(-0.03f, 0.03f));
            }
        }
        _all.Enqueue(root);
        while (_all.Count > Cap)
        {
            var old = _all.Dequeue();
            if (GodotObject.IsInstanceValid(old)) old.QueueFree();
        }
        return root;
    }

    /// <summary>Is there already a fortification within r metres?</summary>
    public static bool Near(Vector3 p, float r)
    {
        foreach (var f in _all)
            if (GodotObject.IsInstanceValid(f) && f.GlobalPosition.DistanceTo(p) < r) return true;
        return false;
    }
}
