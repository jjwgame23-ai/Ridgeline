using Godot;

namespace Ridgeline;

/// <summary>What a built model exposes to the vehicle: the turret pivots and muzzles, and wheels to spin.</summary>
public sealed class VehicleRig
{
    public Node3D Root = null!;
    public readonly List<(Node3D Yaw, Node3D Pitch, Node3D Barrel, Node3D Muzzle, Node3D? Coax)> Turrets = new();
    public readonly List<Node3D> Wheels = new();
    public Node3D? Rotor, TailRotor;
    public readonly List<MeshInstance3D> Meshes = new();
}

/// <summary>
/// Blockout vehicles from boxes and cylinders, one design language per faction:
/// Alpha (0) angular and long, with slab-sided turrets; Bravo (1) low, rounded
/// dome turrets set well forward; Charlie (2) boxy, with big turret bustles. Hull
/// forward is -Z. The team's colour runs along the hull as a recognition stripe.
/// </summary>
public static class VehicleModels
{
    static StandardMaterial3D Mat(Color c, float rough = 0.85f, float metal = 0.15f) => new() { AlbedoColor = c, Roughness = rough, Metallic = metal };

    public static VehicleRig Build(VehicleDef d, int team)
    {
        var rig = new VehicleRig { Root = new Node3D { Name = "Model" } };
        var paint = Mat(d.Paint);
        var dark = Mat(d.Paint.Darkened(0.35f));
        var black = Mat(new Color(0.08f, 0.08f, 0.08f), 0.9f);
        var glass = Mat(new Color(0.15f, 0.2f, 0.25f), 0.2f, 0.4f);
        var canvas = Mat(d.Paint.Lerp(new Color(0.45f, 0.42f, 0.3f), 0.5f), 1f, 0f);
        var stripe = Mat(Valley.TeamColors[Math.Clamp(team, 0, 2)]);
        var h = d.Hull;
        float y0 = d.GroundClear;
        void Box(Node3D p, Vector3 size, Vector3 pos, Material m, Vector3? rotDeg = null)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m };
            p.AddChild(mi);
            mi.Position = pos;
            if (rotDeg is Vector3 r) mi.RotationDegrees = r;
            rig.Meshes.Add(mi);
        }
        void Cyl(Node3D p, float r0, float r1, float len, Vector3 pos, Vector3 rotDeg, Material m, int segs = 12)
        {
            var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = r0, BottomRadius = r1, Height = len, RadialSegments = segs }, MaterialOverride = m };
            p.AddChild(mi);
            mi.Position = pos;
            mi.RotationDegrees = rotDeg;
            rig.Meshes.Add(mi);
        }

        bool truck = d.Kind is VKind.Transport or VKind.Logistics;
        if (d.Air)
        {
            Heli(rig, d, paint, dark, black, glass, stripe, Box, Cyl);
            foreach (var t in d.Turrets) rig.Turrets.Add(Turret(rig, d, t, paint, dark, black, Box, Cyl));
            return rig;
        }
        if (d.Static)
        {
            // Baseplate, sandbag ring, and the tube (the turret) on its bipod.
            Cyl(rig.Root, 0.35f, 0.35f, 0.06f, new Vector3(0f, 0.03f, 0f), Vector3.Zero, dark, 12);
            var bag = Mat(new Color(0.55f, 0.49f, 0.36f), 1f, 0f);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.Tau / 8f;
                Box(rig.Root, new Vector3(0.9f, 0.45f, 0.35f), new Vector3(MathF.Cos(a) * 1.7f, 0.22f, MathF.Sin(a) * 1.7f), bag, new Vector3(0f, -Mathf.RadToDeg(a) + 90f, 0f));
            }
            foreach (var t in d.Turrets) rig.Turrets.Add(Turret(rig, d, t, paint, dark, black, Box, Cyl));
            return rig;
        }
        if (truck)
        {
            // Cab, chassis, bed; canvas over the transport, crates on the logistics truck.
            float cabL = 2.0f;
            Box(rig.Root, new Vector3(h.X * 0.9f, 0.3f, h.Z), new Vector3(0f, y0 + 0.15f, 0f), black);
            Box(rig.Root, new Vector3(h.X, 1.5f, cabL), new Vector3(0f, y0 + 1.05f, -h.Z / 2f + cabL / 2f), paint);
            Box(rig.Root, new Vector3(h.X * 0.92f, 0.5f, 0.05f), new Vector3(0f, y0 + 1.45f, -h.Z / 2f - 0.01f), glass);
            Box(rig.Root, new Vector3(h.X, 0.5f, h.Z - cabL - 0.2f), new Vector3(0f, y0 + 0.55f, cabL / 2f + 0.1f), dark);
            if (d.Kind == VKind.Transport)
                Box(rig.Root, new Vector3(h.X, 1.7f, h.Z - cabL - 0.3f), new Vector3(0f, y0 + 1.65f, cabL / 2f + 0.1f), canvas);
            else
                for (int i = 0; i < 4; i++)
                    Box(rig.Root, new Vector3(1.0f, 0.8f, 1.1f), new Vector3(i % 2 == 0 ? -0.55f : 0.55f, y0 + 1.2f, 0.4f + (i / 2) * 1.4f), Mat(new Color(0.35f, 0.3f, 0.2f)));
            Box(rig.Root, new Vector3(0.06f, 0.3f, cabL * 0.8f), new Vector3(-h.X / 2f - 0.03f, y0 + 1.2f, -h.Z / 2f + cabL / 2f), stripe);
            Box(rig.Root, new Vector3(0.06f, 0.3f, cabL * 0.8f), new Vector3(h.X / 2f + 0.03f, y0 + 1.2f, -h.Z / 2f + cabL / 2f), stripe);
        }
        else if (d.Kind == VKind.LTV)
        {
            Box(rig.Root, new Vector3(h.X, 0.9f, h.Z), new Vector3(0f, y0 + 0.5f, 0f), paint);
            Box(rig.Root, new Vector3(h.X * 0.95f, 0.5f, h.Z * 0.35f), new Vector3(0f, y0 + 0.9f, -h.Z * 0.3f), paint, new Vector3(-8f, 0, 0)); // bonnet
            Box(rig.Root, new Vector3(h.X * 0.9f, 0.05f, 0.6f), new Vector3(0f, y0 + 1.35f, -h.Z * 0.08f), glass, new Vector3(-60f, 0, 0));
            if (d.Style != 2) Box(rig.Root, new Vector3(h.X, 0.1f, h.Z * 0.45f), new Vector3(0f, y0 + 1.75f, h.Z * 0.02f), dark); // roof (the WMIK is open)
            Box(rig.Root, new Vector3(0.06f, 0.25f, h.Z * 0.5f), new Vector3(-h.X / 2f - 0.03f, y0 + 0.75f, 0f), stripe);
            Box(rig.Root, new Vector3(0.06f, 0.25f, h.Z * 0.5f), new Vector3(h.X / 2f + 0.03f, y0 + 0.75f, 0f), stripe);
        }
        else
        {
            // Armoured hull: a box with a sloped glacis; tracks or wheels outside it.
            float bodyH = h.Y;
            float glacisL = d.Style == 1 ? 1.6f : 1.2f;
            Box(rig.Root, new Vector3(h.X * (d.Tracked ? 0.72f : 0.95f), bodyH, h.Z - glacisL * 0.6f), new Vector3(0f, y0 + bodyH / 2f, glacisL * 0.3f), paint);
            Box(rig.Root, new Vector3(h.X * (d.Tracked ? 0.72f : 0.95f), bodyH * 0.75f, glacisL), new Vector3(0f, y0 + bodyH * 0.55f, -h.Z / 2f + glacisL * 0.5f), paint,
                new Vector3(d.Style == 1 ? 28f : d.Style == 2 ? 12f : 20f, 0f, 0f));
            if (d.Tracked)
            {
                // Tracks with side skirts; a fender shelf over them.
                foreach (float s in new[] { -1f, 1f })
                {
                    Box(rig.Root, new Vector3(h.X * 0.16f, bodyH * 0.8f, h.Z * 0.96f), new Vector3(s * h.X * 0.42f, y0 + bodyH * 0.25f - 0.05f, 0f), black);
                    Box(rig.Root, new Vector3(h.X * 0.17f, 0.12f, h.Z * 0.98f), new Vector3(s * h.X * 0.42f, y0 + bodyH * 0.72f, 0f), dark);
                    if (d.Style != 1) Box(rig.Root, new Vector3(0.05f, bodyH * 0.45f, h.Z * 0.8f), new Vector3(s * h.X * 0.51f, y0 + bodyH * 0.45f, 0f), paint); // skirts
                    int n = 6;
                    for (int i = 0; i < n; i++)
                    {
                        var w = new Node3D();
                        rig.Root.AddChild(w);
                        w.Position = new Vector3(s * h.X * 0.42f, y0 + 0.05f, -h.Z * 0.4f + i * h.Z * 0.8f / (n - 1));
                        Cyl(w, 0.36f, 0.36f, h.X * 0.14f, Vector3.Zero, new Vector3(0, 0, 90), dark, 10);
                        rig.Wheels.Add(w);
                    }
                }
            }
            Box(rig.Root, new Vector3(0.06f, 0.35f, h.Z * 0.45f), new Vector3(-h.X * (d.Tracked ? 0.36f : 0.48f) - 0.03f, y0 + bodyH * 0.7f, 0.3f), stripe);
            Box(rig.Root, new Vector3(0.06f, 0.35f, h.Z * 0.45f), new Vector3(h.X * (d.Tracked ? 0.36f : 0.48f) + 0.03f, y0 + bodyH * 0.7f, 0.3f), stripe);
            if (d.Passengers > 0) Box(rig.Root, new Vector3(1.2f, 1.1f, 0.08f), new Vector3(0f, y0 + bodyH * 0.45f, h.Z / 2f + 0.03f), dark); // rear ramp
        }

        if (!d.Tracked)
        {
            float r = truck ? 0.55f : d.Kind == VKind.LTV ? 0.45f : 0.6f;
            for (int a = 0; a < d.Axles; a++)
            {
                float z = d.Axles == 2 ? (a == 0 ? -h.Z * 0.32f : h.Z * 0.32f)
                        : truck && a == 0 ? -h.Z * 0.36f
                        : -h.Z * 0.38f + a * h.Z * 0.76f / (d.Axles - 1);
                if (truck && a > 0) z = h.Z * 0.1f + (a - 1) * 1.4f;
                foreach (float s in new[] { -1f, 1f })
                {
                    var w = new Node3D();
                    rig.Root.AddChild(w);
                    w.Position = new Vector3(s * (h.X / 2f - 0.15f), r, z);
                    Cyl(w, r, r, 0.4f, Vector3.Zero, new Vector3(0, 0, 90), black, 14);
                    rig.Wheels.Add(w);
                }
            }
        }

        foreach (var t in d.Turrets) rig.Turrets.Add(Turret(rig, d, t, paint, dark, black, Box, Cyl));
        return rig;
    }

    /// <summary>
    /// A helicopter: fuselage with a glazed nose, tail boom and fin, skids (or wheels on the
    /// Hind), a spinning main rotor and tail rotor. Gunships are slim with stub wings.
    /// </summary>
    static void Heli(VehicleRig rig, VehicleDef d, Material paint, Material dark, Material black, Material glass, Material stripe,
        Action<Node3D, Vector3, Vector3, Material, Vector3?> box, Action<Node3D, float, float, float, Vector3, Vector3, Material, int> cyl)
    {
        var h = d.Hull;
        bool gun = d.Kind == VKind.AH;
        float y0 = d.GroundClear;
        float bodyL = h.Z * 0.5f, bodyH = gun ? 1.5f : 1.9f, bodyW = gun ? 1.1f : 2.2f;
        if (d.Style == 1 && !gun) bodyW *= 1.05f;
        // Cabin, a rounded-ish nose, the boom and fin.
        box(rig.Root, new Vector3(bodyW, bodyH, bodyL), new Vector3(0f, y0 + bodyH / 2f + 0.2f, -h.Z * 0.12f), paint, null);
        box(rig.Root, new Vector3(bodyW * 0.85f, bodyH * 0.7f, 1.2f), new Vector3(0f, y0 + bodyH * 0.45f + 0.2f, -h.Z * 0.12f - bodyL / 2f - 0.5f), glass, new Vector3(d.Style == 1 ? 15f : 25f, 0f, 0f));
        box(rig.Root, new Vector3(0.45f, 0.5f, h.Z * 0.45f), new Vector3(0f, y0 + bodyH * 0.8f, h.Z * 0.3f), paint, null);
        box(rig.Root, new Vector3(0.12f, 1.6f, 1.0f), new Vector3(0f, y0 + bodyH * 0.8f + 0.7f, h.Z * 0.5f - 0.4f), dark, new Vector3(-15f, 0f, 0f));
        box(rig.Root, new Vector3(0.05f, 0.3f, bodyL * 0.6f), new Vector3(-bodyW / 2f - 0.03f, y0 + bodyH * 0.6f, -h.Z * 0.12f), stripe, null);
        box(rig.Root, new Vector3(0.05f, 0.3f, bodyL * 0.6f), new Vector3(bodyW / 2f + 0.03f, y0 + bodyH * 0.6f, -h.Z * 0.12f), stripe, null);
        box(rig.Root, new Vector3(0.9f, 0.5f, 1.8f), new Vector3(0f, y0 + bodyH + 0.35f, -h.Z * 0.1f), dark, null); // engines
        if (gun) box(rig.Root, new Vector3(3.6f, 0.12f, 0.8f), new Vector3(0f, y0 + 1.1f, -h.Z * 0.1f), paint, null); // stub wings
        // Undercarriage.
        if (d.Style == 1 || gun && d.Style != 2)
            foreach (var p in new[] { new Vector3(-0.9f, 0f, -h.Z * 0.28f), new Vector3(0.9f, 0f, -h.Z * 0.28f), new Vector3(0f, 0f, h.Z * 0.2f) })
                cyl(rig.Root, 0.3f, 0.3f, 0.2f, p + Vector3.Up * 0.3f, new Vector3(0, 0, 90), black, 10);
        else
            foreach (float s in new[] { -1f, 1f })
                box(rig.Root, new Vector3(0.1f, 0.1f, bodyL * 1.1f), new Vector3(s * (bodyW / 2f + 0.1f), 0.08f, -h.Z * 0.12f), black, null);
        // Rotors.
        rig.Rotor = new Node3D();
        rig.Root.AddChild(rig.Rotor);
        rig.Rotor.Position = new Vector3(0f, y0 + bodyH + 0.8f, -h.Z * 0.1f);
        int blades = d.Style == 1 ? 5 : 4;
        for (int i = 0; i < blades; i++)
            box(rig.Rotor, new Vector3(0.35f, 0.05f, d.RotorRadius), new Vector3(0f, 0f, 0f), dark, new Vector3(0f, i * 360f / blades, 0f));
        foreach (var bl in rig.Rotor.GetChildren().OfType<MeshInstance3D>())
            bl.Position = bl.Basis.Z * (d.RotorRadius / 2f); // hub at the centre, blades out
        rig.TailRotor = new Node3D();
        rig.Root.AddChild(rig.TailRotor);
        rig.TailRotor.Position = new Vector3(0.3f, y0 + bodyH * 0.8f + 0.9f, h.Z * 0.5f - 0.3f);
        box(rig.TailRotor, new Vector3(0.05f, 2.2f, 0.18f), Vector3.Zero, dark, null);
        box(rig.TailRotor, new Vector3(0.05f, 0.18f, 2.2f), Vector3.Zero, dark, null);
    }

    static (Node3D, Node3D, Node3D, Node3D, Node3D?) Turret(VehicleRig rig, VehicleDef d, TurretDef t, Material paint, Material dark, Material black,
        Action<Node3D, Vector3, Vector3, Material, Vector3?> box, Action<Node3D, float, float, float, Vector3, Vector3, Material, int> cyl)
    {
        var yaw = new Node3D { Name = "TurretYaw" };
        rig.Root.AddChild(yaw);
        yaw.Position = t.Mount;
        var s = t.Size;
        if (t.Fixed)
        {
            // Rocket pods under the stub wings; the muzzle between them.
            foreach (float side in new[] { -1f, 1f })
                cyl(yaw, 0.18f, 0.18f, 1.1f, new Vector3(side * s.X * 0.4f, -0.1f, 0f), new Vector3(90, 0, 0), dark, 10);
        }
        else if (d.Air)
        {
            box(yaw, s, new Vector3(0f, 0f, 0f), dark, null); // chin turret / door-gun mount
        }
        else if (d.Static)
        {
            cyl(yaw, 0.02f, 0.02f, 0.7f, new Vector3(0.25f, 0.3f, -0.25f), new Vector3(30, 0, 30), black, 6); // bipod
        }
        else if (d.Kind == VKind.SPAA)
        {
            box(yaw, s, new Vector3(0f, s.Y / 2f, 0f), paint, null);
            box(yaw, new Vector3(1.4f, 0.1f, 0.9f), new Vector3(0f, s.Y + 0.6f, s.Z * 0.4f), dark, new Vector3(-15f, 0f, 0f)); // search radar
            cyl(yaw, 0.05f, 0.05f, 0.6f, new Vector3(0f, s.Y + 0.3f, s.Z * 0.4f), Vector3.Zero, black, 6);
        }
        else if (t.Exposed)
        {
            // A pintle or ring mount: a post and a small shield.
            cyl(yaw, 0.05f, 0.06f, 0.5f, new Vector3(0f, 0.25f, 0f), Vector3.Zero, black, 8);
            box(yaw, new Vector3(0.7f, 0.45f, 0.04f), new Vector3(0f, 0.6f, -0.35f), dark, null);
        }
        else if (d.Style == 1)
        {
            // Bravo: a low dome.
            cyl(yaw, s.X * 0.35f, s.X * 0.5f, s.Y, new Vector3(0f, s.Y / 2f, 0f), Vector3.Zero, paint, 16);
            cyl(yaw, s.X * 0.18f, s.X * 0.35f, s.Y * 0.35f, new Vector3(0f, s.Y * 1.1f, 0.1f), Vector3.Zero, paint, 12);
        }
        else
        {
            box(yaw, s, new Vector3(0f, s.Y / 2f, 0f), paint, null);
            // Alpha: a wedge front; Charlie: a big box bustle at the back.
            if (d.Style == 0) box(yaw, new Vector3(s.X * 0.9f, s.Y * 0.8f, s.Z * 0.35f), new Vector3(0f, s.Y * 0.45f, -s.Z * 0.55f), paint, new Vector3(-25f, 0f, 0f));
            else box(yaw, new Vector3(s.X * 0.8f, s.Y * 0.8f, s.Z * 0.45f), new Vector3(0f, s.Y * 0.45f, s.Z * 0.65f), dark, null);
            box(yaw, new Vector3(0.5f, 0.25f, 0.5f), new Vector3(s.X * 0.25f, s.Y + 0.12f, s.Z * 0.1f), dark, null); // commander's cupola
        }
        var pitch = new Node3D { Name = "Gun" };
        yaw.AddChild(pitch);
        pitch.Position = t.Fixed || d.Air ? Vector3.Zero : t.Exposed ? new Vector3(0f, 0.55f, 0f) : new Vector3(0f, s.Y * 0.55f, -s.Z * 0.45f);
        var barrel = new Node3D { Name = "Barrel" };
        pitch.AddChild(barrel);
        if (d.Kind == VKind.SPAA)
            foreach (float side in new[] { -1f, 1f }) // twin guns either side of the turret
                cyl(barrel, t.BarrelRadius, t.BarrelRadius * 1.15f, t.BarrelLen, new Vector3(side * s.X * 0.55f, 0f, -t.BarrelLen / 2f), new Vector3(90, 0, 0), black, 10);
        else if (!t.Fixed)
            cyl(barrel, t.BarrelRadius, t.BarrelRadius * 1.15f, t.BarrelLen, new Vector3(0f, 0f, -t.BarrelLen / 2f), new Vector3(90, 0, 0), black, 10);
        if (t.BarrelLen > 3f) cyl(barrel, t.BarrelRadius * 1.6f, t.BarrelRadius * 1.6f, 0.5f, new Vector3(0f, 0f, -t.BarrelLen * 0.55f), new Vector3(90, 0, 0), dark, 10); // bore evacuator
        box(pitch, new Vector3(Math.Max(0.25f, t.BarrelRadius * 6f), Math.Max(0.2f, t.BarrelRadius * 5f), 0.5f), new Vector3(0f, 0f, -0.1f), dark, null); // mantlet
        var muzzle = new Node3D();
        barrel.AddChild(muzzle);
        muzzle.Position = new Vector3(0f, 0f, -t.BarrelLen - 0.05f);
        Node3D? coax = null;
        if (t.Coax != null)
        {
            coax = new Node3D();
            pitch.AddChild(coax);
            coax.Position = new Vector3(0.35f, 0f, -0.5f);
            cyl(pitch, 0.02f, 0.02f, 0.4f, new Vector3(0.35f, 0f, -0.35f), new Vector3(90, 0, 0), black, 6);
        }
        return (yaw, pitch, barrel, muzzle, coax);
    }
}
