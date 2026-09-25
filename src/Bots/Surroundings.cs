using Godot;

namespace Ridgeline;

public enum EnvKind { Open, Forest, Urban, Interior }

/// <summary>
/// What kind of ground a soldier is on, which changes how a squad moves and fights:
/// - Open: spread out wide (a burst shouldn't get two of you), long bounds between the
///   little cover there is, sprint across gaps.
/// - Forest: a closer wedge so you keep sight of each other; medium bounds.
/// - Urban: a file along the walls, short bounds corner to corner, no sprinting down the
///   middle of the street once there's fighting about.
/// - Interior: close up, walk with the gun up, frag a room before going in.
/// Worked out from the map (streets and buildings, how many trees stand nearby, and
/// whether there's a floor overhead), so it works on any generated map.
/// </summary>
public static class Surroundings
{
    public static EnvKind At(PhysicsDirectSpaceState3D? space, Vector3 feet)
    {
        if (space != null && Indoors(space, feet)) return EnvKind.Interior;
        var v = Valley.Current;
        if (v != null)
        {
            if (v.City?.Zone(feet.X, feet.Z) == 2) return EnvKind.Urban;
            if (v.BuiltAround(feet.X, feet.Z) >= 5) return EnvKind.Urban;
        }
        var t = Terrain.Main;
        if (t != null && t.TreesNear(feet.X, feet.Z) >= 16) return EnvKind.Forest;
        return EnvKind.Open;
    }

    /// <summary>A roof or floor overhead: we're inside a building.</summary>
    public static bool Indoors(PhysicsDirectSpaceState3D space, Vector3 feet) =>
        space.IntersectRay(PhysicsRayQueryParameters3D.Create(feet + Vector3.Up * 1.7f, feet + Vector3.Up * 14f, Layers.World)).Count > 0;

    /// <summary>How far one bound (cover to cover) should go here.</summary>
    public static float BoundStep(EnvKind e) => e switch { EnvKind.Interior => 10f, EnvKind.Urban => 16f, EnvKind.Forest => 22f, _ => 36f };

    public static string Name(EnvKind e) => e switch { EnvKind.Interior => "interior", EnvKind.Urban => "urban", EnvKind.Forest => "forest", _ => "open" };
}
