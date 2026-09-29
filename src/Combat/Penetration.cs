using Godot;

namespace Ridgeline;

/// <summary>What something is made of, as far as a bullet is concerned.</summary>
public enum PenClass : byte { Masonry, Partition, Adobe, Sand, Wood, Roof, Sheet, Container, Car, Glass }

/// <summary>
/// Rounds through walls, doors, cars and trees. A round's penetration (Projectile.Pen, the same mm-of-steel
/// scale the armour uses, falling off with its speed) is spent getting through: a solid material costs in
/// proportion to how much of it is in the way along the round's path (measured exactly, from the box or trunk
/// it struck), a hollow thing (a car, a shipping container, a sheet-metal wall) a fixed amount for crossing it.
/// What's left comes out of the far side: slower, so it hits for less, and a little off line.
///
/// Calibrated on 5.56 mm at the muzzle (6 in Pen): through about 30 cm of an inside wall (plaster and light
/// block), 60 cm of timber, 15 cm of mud brick, 12 cm of sandbag, 8 cm of brick or concrete; a car body, a
/// container or a sheet of corrugated iron easily. An outside wall (25 cm of masonry) stops rifle and machine
/// gun rounds; a heavy machine gun or an autocannon goes through it. The sandbag wall stops them all. Fragments
/// get through a door or a sheet of tin close to the burst, and little else.
/// (Every one of these used to stop every round.)
/// </summary>
public static class Penetration
{
    static readonly StringName Key = "pen", Ground = "ground", Floor = "floor", Fort = "fortification";

    /// <summary>Remember what a box of the map is made of (masonry, the default, isn't tagged).</summary>
    public static void Tag(CollisionShape3D cs, Material m, Vector3 size)
    {
        var c = Classify(m, size);
        if (c != PenClass.Masonry) cs.SetMeta(Key, (int)c);
    }

    static PenClass Classify(Material m, Vector3 size)
    {
        if (m == Mats.Wood) return PenClass.Wood;
        if (m == Mats.Metal) return PenClass.Sheet;
        if (m == Mats.Container) return PenClass.Container;
        if (m == Mats.Truck) return PenClass.Car;
        if (m == Mats.Sand || m == Mats.Hesco || m == Mats.Dirt) return PenClass.Sand;
        if (m == Mats.Roof || m == Mats.Roof2) return PenClass.Roof;
        if (m == Mats.Glass || m == Mats.Canvas) return PenClass.Glass;
        // An inside wall: a thin upright slab (the buildings' outside walls are 25 cm, their partitions 15-20).
        if (MathF.Min(size.X, size.Z) <= 0.205f && size.Y >= 1f) return PenClass.Partition;
        return m == Mats.Adobe || m == Mats.Adobe2 || m == Mats.Adobe3 ? PenClass.Adobe : PenClass.Masonry;
    }

    /// <summary>A solid material: penetration spent per metre of it. A hollow thing: the cost of crossing it.</summary>
    static (float PerMetre, float Crossing) Resist(PenClass c) => c switch
    {
        PenClass.Partition => (20f, 0f),
        PenClass.Adobe => (40f, 0f),
        PenClass.Sand => (50f, 0f),
        PenClass.Wood => (10f, 0f),
        PenClass.Roof => (15f, 0f),
        PenClass.Sheet => (0f, 1.2f),
        PenClass.Container => (0f, 1.5f),
        PenClass.Car => (0f, 3f),
        PenClass.Glass => (0f, 0.2f),
        _ => (75f, 0f), // brick, block, concrete, stone
    };

    /// <summary>What the thing a ray hit is made of; null for what nothing gets through (the ground, steel, a vehicle's armour: that has its own rules).</summary>
    static PenClass? ClassOf(GodotObject? collider, int shape, out CollisionShape3D? node)
    {
        node = null;
        if (collider is not CollisionObject3D co) return null;
        if (co.IsInGroup(Ground) && !co.IsInGroup(Floor)) return null;
        if (co is Vehicle || co is SteelTarget || co is ICombatant) return null;
        if (shape >= 0 && co.ShapeOwnerGetOwner(co.ShapeFindOwner(shape)) is CollisionShape3D cs) node = cs;
        if ((co.CollisionLayer & (Layers.Trees | Layers.Doors)) != 0) return PenClass.Wood;
        if (co.IsInGroup(Fort)) return PenClass.Sand;
        return node != null && node.HasMeta(Key) ? (PenClass)node.GetMeta(Key).AsInt32() : PenClass.Masonry;
    }

    /// <summary>
    /// How far a line entering <paramref name="node"/>'s shape at <paramref name="entry"/>, heading
    /// <paramref name="dir"/>, runs inside it: a box, or an upright cylinder (a trunk). Negative if there's no telling.
    /// </summary>
    static float Chord(CollisionShape3D node, Vector3 entry, Vector3 dir)
    {
        var inv = node.GlobalTransform.AffineInverse();
        var lp = inv * entry;
        var ld = (inv.Basis * dir).Normalized();
        switch (node.Shape)
        {
            case BoxShape3D box:
            {
                var h = box.Size * 0.5f;
                float t = float.MaxValue;
                for (int a = 0; a < 3; a++)
                {
                    float d = ld[a];
                    if (MathF.Abs(d) < 1e-6f) continue;
                    t = MathF.Min(t, ((d > 0f ? h[a] : -h[a]) - lp[a]) / d);
                }
                return t == float.MaxValue ? -1f : MathF.Max(0f, t);
            }
            case CylinderShape3D cyl:
            {
                float a = ld.X * ld.X + ld.Z * ld.Z;
                if (a < 1e-4f) return -1f; // straight down its length
                float b = 2f * (lp.X * ld.X + lp.Z * ld.Z), c = lp.X * lp.X + lp.Z * lp.Z - cyl.Radius * cyl.Radius;
                float disc = b * b - 4f * a * c;
                return disc < 0f ? 0f : MathF.Max(0f, (-b + MathF.Sqrt(disc)) / (2f * a));
            }
        }
        return -1f;
    }

    /// <summary>
    /// Does this round get through what it just struck? If so, where it comes out and how it's travelling then.
    /// </summary>
    public static bool Through(Projectile p, Godot.Collections.Dictionary hit, out Vector3 exit, out Vector3 vel, out PenClass what)
    {
        exit = vel = default;
        what = PenClass.Masonry;
        if (p.Explosive || p.Rocket || p.Homing != null || p.Pen <= 0f) return false;
        if (ClassOf(hit["collider"].AsGodotObject(), hit["shape"].AsInt32(), out var node) is not PenClass cls || node == null) return false;
        what = cls;
        var pos = hit["position"].AsVector3();
        float speed = p.Vel.Length();
        var dir = p.Vel / speed;
        float len = Chord(node, pos, dir);
        if (len < 0f || len > 4f) return false;
        var (per, crossing) = Resist(cls);
        float cost = per * len + crossing;
        float cap = p.Pen * MathF.Pow(speed / p.MuzzleSpeed, 1.2f);
        if (cost >= cap) return false;
        float left = 1f - cost / cap;
        // Out with what it had left: at the next thing it strikes, capability (which goes as speed^1.2) is cap - cost.
        // (With v2 = speed*sqrt(left) it kept cap*left^0.6, more than it had, and went through a second car, or a
        // car and then a wall, that it shouldn't have.)
        float v2 = speed * MathF.Pow(left, 1f / 1.2f);
        if (v2 < 150f) return false;
        // It comes out a little off line, more the more it had to push through.
        float off = Mathf.DegToRad(1.5f + 9f * (1f - left)) * MathF.Sqrt(Random.Shared.NextSingle());
        var perp = dir.Cross(MathF.Abs(dir.Y) < 0.95f ? Vector3.Up : Vector3.Right).Normalized();
        var d2 = dir.Rotated(perp.Rotated(dir, Random.Shared.NextSingle() * Mathf.Tau), off).Normalized();
        exit = pos + dir * len + d2 * 0.02f;
        vel = d2 * v2;
        return true;
    }

    /// <summary>
    /// For cover: would a rifle round (5.56 at 100 m) get through what this ray hit? Then it's something to hide
    /// behind, not something to stop bullets: an inside wall, a door, a car, a shed.
    /// </summary>
    public static bool RifleGoesThrough(Godot.Collections.Dictionary hit, Vector3 dir, out Vector3 exit)
    {
        exit = default;
        if (ClassOf(hit["collider"].AsGodotObject(), hit["shape"].AsInt32(), out var node) is not PenClass cls || node == null) return false;
        var pos = hit["position"].AsVector3();
        float len = Chord(node, pos, dir);
        if (len < 0f || len > 4f) return false;
        var (per, crossing) = Resist(cls);
        if (per * len + crossing >= RifleAt100) return false;
        exit = pos + dir * (len + 0.02f);
        return true;
    }

    /// <summary>Penetration of 5.56 mm at about 100 m.</summary>
    const float RifleAt100 = 5.3f;
}
