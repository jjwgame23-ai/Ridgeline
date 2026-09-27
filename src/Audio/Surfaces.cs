using Godot;

namespace Ridgeline;

/// <summary>What something is made of, as far as the sound of a hit or a footstep goes.</summary>
public enum Surface { Earth, Sand, Rock, Stone, Wood, Metal, Flesh }

/// <summary>
/// The surface a round hit or a boot came down on: from the collider (a person, a vehicle, a
/// tree, a door, sandbags), from the material the map builder tagged a box with (a wooden
/// floor, a shipping container), and on open ground from the terrain itself: sand in the
/// desert, rock on steep ground, paving in town, earth and turf elsewhere. Masonry, the most
/// common thing to hit in a town, is the default and isn't tagged.
/// </summary>
public static class Surfaces
{
    const string Key = "surface";

    public static Surface OfMaterial(Material m) =>
        m == Mats.Wood || m == Mats.Canvas ? Surface.Wood
        : m == Mats.Metal || m == Mats.Container || m == Mats.Truck ? Surface.Metal
        : m == Mats.Sand || m == Mats.Hesco || m == Mats.Dirt ? Surface.Earth
        : Surface.Stone;

    /// <summary>Remember what a box of the map is made of, on its collision shape.</summary>
    public static void Tag(CollisionShape3D shape, Material m)
    {
        var s = OfMaterial(m);
        if (s != Surface.Stone) shape.SetMeta(Key, (int)s);
    }

    /// <summary>The surface of a ray hit: its collider, the shape index within it, where and which way it faces.</summary>
    public static Surface Of(GodotObject? collider, int shape, Vector3 pos, Vector3 normal)
    {
        switch (collider)
        {
            case ICombatant:
                return Surface.Flesh;
            case Vehicle:
                return Surface.Metal;
            case CollisionObject3D co:
                if ((co.CollisionLayer & (Layers.Trees | Layers.Doors)) != 0) return Surface.Wood;
                if (co.IsInGroup("fortification")) return Surface.Earth; // sandbags
                if (shape >= 0)
                {
                    uint owner = co.ShapeFindOwner(shape);
                    if (co.ShapeOwnerGetOwner(owner) is Node n && n.HasMeta(Key)) return (Surface)n.GetMeta(Key).AsInt32();
                }
                if (co.IsInGroup("ground") && !co.IsInGroup("floor")) return Terrain(pos, normal);
                return Surface.Stone;
        }
        return Surface.Stone;
    }

    /// <summary>Open ground at a point: paving in town, sand in the desert, rock where it's steep, else earth and turf.</summary>
    public static Surface Terrain(Vector3 pos, Vector3 normal)
    {
        if (Valley.Current?.City is { } city && city.Zone(pos.X, pos.Z) == 2) return Surface.Stone;
        bool steep = normal.Y < 0.78f;
        if (Ridgeline.Terrain.Main is { Biome: Biome.Desert }) return steep ? Surface.Rock : Surface.Sand;
        return steep ? Surface.Rock : Surface.Earth;
    }

    /// <summary>What's underfoot at a point (a ray down from just above it).</summary>
    public static Surface Under(PhysicsDirectSpaceState3D space, Vector3 feet)
    {
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(feet + Vector3.Up * 0.3f, feet + Vector3.Down * 0.8f, Layers.World | Layers.Vehicles));
        if (hit.Count == 0) return Surface.Earth;
        return Of(hit["collider"].AsGodotObject(), hit["shape"].AsInt32(), hit["position"].AsVector3(), hit["normal"].AsVector3());
    }
}
