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

    /// <summary>
    /// A two-man fighting position's parapet, as doctrine builds one (FM 3-21.8: at least a metre thick): bags a metre
    /// deep and about waist high, 3.4 m across its front, the ends bent back. A metre of it stops a heavy machine gun,
    /// which gets through about half a metre of sand here (Penetration). The hasty sandbag wall (Sandbags) is half
    /// that, and a 12.7 mm round got through it at the joins and corners.
    /// </summary>
    public static Node3D? Parapet(Node parent, IGround? ground, Vector3 at, Vector3 facing) => Sandbags(parent, ground, at, facing, 1.0f, 1.15f);

    /// <summary>
    /// A two-man fighting position walled all round, as a dug position is earth all round: the metre-thick parapet in
    /// front (Parapet), half-metre walls down both flanks and across the rear, the rear a little lower and with a gap at
    /// one end to get in and out by. The two men are inside, a little over a metre behind the front. A burst outside throws
    /// its fragments into the walls (they're real fragments: Grenade.Spray); only one inside the position, or close enough
    /// to throw them in over the top, reaches the men. (With a front only, a shell landing behind the line sprayed men
    /// standing at ground level: 18 rounds of a preparation killed 14 men in their positions.)
    /// </summary>
    public static Node3D? Position(Node parent, IGround? ground, Vector3 at, Vector3 facing)
    {
        if (Parapet(parent, ground, at, facing) is not StaticBody3D root) return null;
        facing.Y = 0f;
        facing = facing.Normalized();
        var right = facing.Cross(Vector3.Up);
        float y0 = root.GlobalPosition.Y;
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        // Flanks: from just behind the parapet to the rear, 1.75 m either side of the middle.
        foreach (float s in new[] { -1f, 1f })
            Wall(root, ground, at, y0, right * (s * 1.75f) - facing * 1.35f, facing, 1.9f, 0.5f, 1.05f, rng);
        // The rear, lower, short of the right-hand flank by a man's width: the way in and out.
        Wall(root, ground, at, y0, -facing * 2.3f - right * 0.55f, right, 2.4f, 0.5f, 0.9f, rng);
        return root;
    }

    /// <summary>A straight run of bags: its middle (relative to the position), its direction, length, thickness and height.</summary>
    static void Wall(StaticBody3D root, IGround? ground, Vector3 at, float y0, Vector3 mid, Vector3 along, float length, float thick, float height, RandomNumberGenerator rng)
    {
        var basis = Basis.LookingAt(along.Cross(Vector3.Up), Vector3.Up);
        float yLocal = (ground?.HeightAt(at.X + mid.X, at.Z + mid.Z) ?? y0) - y0;
        root.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(length, height, thick) },
            Transform = new Transform3D(basis, mid + Vector3.Up * (yLocal + height / 2f - 0.02f)),
        });
        int across = Math.Max(1, (int)MathF.Round(length / 0.46f)), rows = Math.Max(1, (int)MathF.Round(height / 0.25f));
        var normal = along.Cross(Vector3.Up);
        for (int row = 0; row < rows; row++)
        for (int k = 0; k < across; k++)
        {
            float off = (k - (across - 1) / 2f) * 0.46f + (row % 2 == 1 ? 0.12f : 0f);
            if (MathF.Abs(off) > length / 2f) continue;
            var bag = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.46f, 0.25f, 0.34f) }, MaterialOverride = _bag };
            root.AddChild(bag);
            bag.Transform = new Transform3D(basis.Rotated(Vector3.Up, rng.RandfRange(-0.06f, 0.06f)),
                mid + along * off + Vector3.Up * (yLocal + 0.13f + row * 0.25f) + normal * rng.RandfRange(-0.03f, 0.03f));
        }
    }

    /// <param name="facing">The direction the wall should protect against (towards the enemy).</param>
    /// <param name="thick">How thick the wall is (m): half a metre of bags, or a metre for a parapet.</param>
    /// <param name="span">Each of its three segments' width (m).</param>
    public static Node3D? Sandbags(Node parent, IGround? ground, Vector3 at, Vector3 facing, float thick = 0.5f, float span = 0.95f)
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
            var mid = right * (seg * (span - 0.05f)) - facing * (MathF.Abs(seg) * 0.25f);
            var along = (right - facing * (seg * 0.28f)).Normalized();
            var basis = Basis.LookingAt(along.Cross(Vector3.Up), Vector3.Up);
            float yLocal = (ground?.HeightAt(at.X + mid.X, at.Z + mid.Z) ?? y0) - y0;
            root.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(span, 1.05f, thick) },
                Transform = new Transform3D(basis, mid + Vector3.Up * (yLocal + 0.5f)),
            });
            // Bags across the segment's width, and as many layers deep as the wall is thick.
            int layers = Math.Max(1, (int)MathF.Round(thick / 0.45f)), across = Math.Max(2, (int)MathF.Round(span / 0.46f));
            for (int layer = 0; layer < layers; layer++)
            for (int row = 0; row < 4; row++)
            for (int k = 0; k < across; k++)
            {
                float off = (k - (across - 1) / 2f) * 0.46f + (row % 2 == 1 ? 0.12f : 0f);
                if (MathF.Abs(off) > MathF.Max(0.5f, span / 2f)) continue;
                float deep = (layer - (layers - 1) / 2f) * 0.42f;
                var bag = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.46f, 0.25f, 0.34f) },
                    MaterialOverride = _bag,
                };
                root.AddChild(bag);
                bag.Transform = new Transform3D(basis.Rotated(Vector3.Up, rng.RandfRange(-0.06f, 0.06f)),
                    mid + along * off + Vector3.Up * (yLocal + 0.13f + row * 0.25f) + facing * (deep + rng.RandfRange(-0.03f, 0.03f)));
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
