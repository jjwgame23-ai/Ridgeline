using Godot;

namespace Ridgeline;

/// <summary>
/// Blockout weapon models. Every model is built so its sight line runs along
/// local -Z through (0, 0): with the rig pivoting at the eye, the optic then
/// always looks exactly where the bullet goes.
/// </summary>
public static class WeaponModels
{
    static StandardMaterial3D Mat(Color c, float metal, float rough) => new() { AlbedoColor = c, Metallic = metal, Roughness = rough };

    static void Add(Node3D parent, Mesh mesh, Vector3 pos, Material m, Vector3 rotDeg)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = m, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(mi);
        mi.Position = pos;
        mi.RotationDegrees = rotDeg;
    }

    static void Box(Node3D p, Vector3 size, Vector3 pos, Material m, float pitchDeg = 0f) =>
        Add(p, new BoxMesh { Size = size }, pos, m, new Vector3(pitchDeg, 0, 0));

    static void Tube(Node3D p, float r, float len, Vector3 center, Material m) =>
        Add(p, new CylinderMesh { TopRadius = r, BottomRadius = r, Height = len, RadialSegments = 12 }, center, m, new Vector3(90, 0, 0));

    static void Ring(Node3D p, float inner, float outer, Vector3 center, Material m) =>
        Add(p, new TorusMesh { InnerRadius = inner, OuterRadius = outer, Rings = 20, RingSegments = 8 }, center, m, new Vector3(90, 0, 0));

    public static (Node3D, Node3D) Carbine()
    {
        var root = new Node3D { Name = "Carbine" };
        var metal = Mat(new Color(0.09f, 0.09f, 0.1f), 0.6f, 0.45f);
        var poly = Mat(new Color(0.14f, 0.13f, 0.11f), 0f, 0.8f);

        Box(root, new Vector3(0.045f, 0.065f, 0.26f), new Vector3(0, -0.078f, -0.10f), metal); // upper
        Box(root, new Vector3(0.04f, 0.06f, 0.16f), new Vector3(0, -0.12f, -0.08f), metal);   // lower
        Box(root, new Vector3(0.055f, 0.058f, 0.30f), new Vector3(0, -0.078f, -0.38f), poly);  // handguard
        Tube(root, 0.0085f, 0.22f, new Vector3(0, -0.075f, -0.63f), metal);                   // barrel
        Box(root, new Vector3(0.028f, 0.03f, 0.05f), new Vector3(0, -0.075f, -0.755f), metal); // muzzle device
        Box(root, new Vector3(0.028f, 0.15f, 0.07f), new Vector3(0, -0.19f, -0.13f), poly, 12f); // magazine
        Box(root, new Vector3(0.028f, 0.09f, 0.04f), new Vector3(0, -0.17f, 0.0f), poly, -18f);  // grip
        Tube(root, 0.014f, 0.18f, new Vector3(0, -0.08f, 0.1f), metal);                        // buffer tube
        Box(root, new Vector3(0.045f, 0.085f, 0.12f), new Vector3(0, -0.1f, 0.2f), poly);       // stock
        // Red dot: open rings so you can see through it; the dot itself is drawn by the HUD.
        Box(root, new Vector3(0.022f, 0.022f, 0.05f), new Vector3(0, -0.035f, -0.17f), metal);
        Ring(root, 0.0175f, 0.0205f, new Vector3(0, 0, -0.15f), metal);
        Ring(root, 0.0175f, 0.0205f, new Vector3(0, 0, -0.19f), metal);

        var muzzle = new Node3D();
        root.AddChild(muzzle);
        muzzle.Position = new Vector3(0, -0.075f, -0.79f);
        return (root, muzzle);
    }

    public static (Node3D, Node3D) Marksman()
    {
        var root = new Node3D { Name = "Marksman" };
        var metal = Mat(new Color(0.08f, 0.08f, 0.08f), 0.6f, 0.45f);
        var tan = Mat(new Color(0.42f, 0.36f, 0.26f), 0f, 0.85f);

        Box(root, new Vector3(0.05f, 0.075f, 0.3f), new Vector3(0, -0.085f, -0.10f), metal);
        Box(root, new Vector3(0.045f, 0.065f, 0.18f), new Vector3(0, -0.135f, -0.08f), metal);
        Box(root, new Vector3(0.058f, 0.06f, 0.38f), new Vector3(0, -0.085f, -0.44f), tan);
        Tube(root, 0.011f, 0.36f, new Vector3(0, -0.082f, -0.8f), metal);
        Box(root, new Vector3(0.032f, 0.034f, 0.06f), new Vector3(0, -0.082f, -1.0f), metal);
        Box(root, new Vector3(0.03f, 0.13f, 0.08f), new Vector3(0, -0.2f, -0.14f), metal, 6f);
        Box(root, new Vector3(0.03f, 0.1f, 0.04f), new Vector3(0, -0.18f, 0.02f), tan, -18f);
        Box(root, new Vector3(0.05f, 0.1f, 0.26f), new Vector3(0, -0.11f, 0.2f), tan);
        // Scope: hidden once fully aimed, when the HUD draws the sight picture instead.
        Tube(root, 0.02f, 0.28f, new Vector3(0, 0, -0.19f), metal);
        Tube(root, 0.027f, 0.07f, new Vector3(0, 0, -0.35f), metal);
        Tube(root, 0.023f, 0.05f, new Vector3(0, 0, -0.06f), metal);
        Box(root, new Vector3(0.03f, 0.05f, 0.03f), new Vector3(0, -0.04f, -0.12f), metal);
        Box(root, new Vector3(0.03f, 0.05f, 0.03f), new Vector3(0, -0.04f, -0.26f), metal);

        var muzzle = new Node3D();
        root.AddChild(muzzle);
        muzzle.Position = new Vector3(0, -0.082f, -1.04f);
        return (root, muzzle);
    }

    public static (Node3D, Node3D) Lmg()
    {
        var root = new Node3D { Name = "Lmg" };
        var metal = Mat(new Color(0.09f, 0.09f, 0.09f), 0.6f, 0.45f);
        var poly = Mat(new Color(0.15f, 0.14f, 0.12f), 0f, 0.8f);
        var pouch = Mat(new Color(0.3f, 0.3f, 0.22f), 0f, 0.95f);

        Box(root, new Vector3(0.06f, 0.08f, 0.34f), new Vector3(0, -0.085f, -0.12f), metal);      // receiver
        Box(root, new Vector3(0.05f, 0.05f, 0.3f), new Vector3(0, -0.08f, -0.45f), poly);         // handguard
        Tube(root, 0.011f, 0.34f, new Vector3(0, -0.08f, -0.75f), metal);                        // barrel
        Box(root, new Vector3(0.03f, 0.03f, 0.05f), new Vector3(0, -0.08f, -0.94f), metal);       // flash hider
        Box(root, new Vector3(0.1f, 0.12f, 0.12f), new Vector3(-0.06f, -0.17f, -0.14f), pouch);   // belt pouch
        Box(root, new Vector3(0.03f, 0.09f, 0.04f), new Vector3(0, -0.18f, 0.02f), poly, -18f);  // grip
        Box(root, new Vector3(0.05f, 0.09f, 0.22f), new Vector3(0, -0.11f, 0.2f), poly);          // stock
        Box(root, new Vector3(0.012f, 0.14f, 0.012f), new Vector3(-0.03f, -0.15f, -0.62f), metal, 25f); // bipod legs, folded
        Box(root, new Vector3(0.012f, 0.14f, 0.012f), new Vector3(0.03f, -0.15f, -0.62f), metal, 25f);
        // Iron sights: a rear notch and a front post on the sight line.
        Box(root, new Vector3(0.03f, 0.03f, 0.012f), new Vector3(0, -0.02f, -0.02f), metal);
        Box(root, new Vector3(0.004f, 0.04f, 0.006f), new Vector3(0, -0.035f, -0.86f), metal);

        var muzzle = new Node3D();
        root.AddChild(muzzle);
        muzzle.Position = new Vector3(0, -0.08f, -0.97f);
        return (root, muzzle);
    }

    public static (Node3D, Node3D) Launcher()
    {
        var root = new Node3D { Name = "Launcher" };
        var metal = Mat(new Color(0.1f, 0.1f, 0.1f), 0.5f, 0.5f);
        var poly = Mat(new Color(0.16f, 0.15f, 0.12f), 0f, 0.85f);

        Tube(root, 0.024f, 0.3f, new Vector3(0, -0.1f, -0.3f), metal);                      // barrel
        Box(root, new Vector3(0.05f, 0.06f, 0.16f), new Vector3(0, -0.1f, -0.08f), poly);    // breech
        Box(root, new Vector3(0.03f, 0.1f, 0.04f), new Vector3(0, -0.19f, -0.04f), poly, -15f); // grip
        Box(root, new Vector3(0.04f, 0.08f, 0.2f), new Vector3(0, -0.12f, 0.12f), poly);     // stock
        // Leaf sight, flipped up: just the two side rails, so it doesn't hide the target; the HUD draws the range ladder.
        Box(root, new Vector3(0.004f, 0.06f, 0.004f), new Vector3(-0.022f, -0.04f, -0.2f), metal);
        Box(root, new Vector3(0.004f, 0.06f, 0.004f), new Vector3(0.022f, -0.04f, -0.2f), metal);

        var muzzle = new Node3D();
        root.AddChild(muzzle);
        muzzle.Position = new Vector3(0, -0.1f, -0.46f);
        return (root, muzzle);
    }

    /// <summary>A shoulder-fired rocket tube: long, fat, over the shoulder, with a simple sight on the side.</summary>
    public static (Node3D, Node3D) Tube()
    {
        var root = new Node3D { Name = "Tube" };
        var olive = Mat(new Color(0.25f, 0.28f, 0.18f), 0f, 0.85f);
        var metal = Mat(new Color(0.1f, 0.1f, 0.1f), 0.5f, 0.5f);
        Tube(root, 0.05f, 1.1f, new Vector3(0.05f, -0.06f, -0.2f), olive);
        Tube(root, 0.06f, 0.1f, new Vector3(0.05f, -0.06f, -0.75f), metal);
        Box(root, new Vector3(0.03f, 0.1f, 0.04f), new Vector3(0.05f, -0.16f, -0.1f), metal, -10f);
        Box(root, new Vector3(0.01f, 0.05f, 0.01f), new Vector3(0f, -0.02f, -0.35f), metal);
        var muzzle = new Node3D();
        root.AddChild(muzzle);
        muzzle.Position = new Vector3(0.05f, -0.06f, -0.8f);
        return (root, muzzle);
    }
}
