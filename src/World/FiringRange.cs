using Godot;

namespace Ridgeline;

public partial class FiringRange : Node3D
{
    public static readonly float[] Distances = { 25f, 50f, 75f, 100f, 150f, 200f, 300f, 400f, 500f, 600f, 800f };

    public Terrain Terrain = null!;
    StandardMaterial3D _wood = null!;

    public override void _Ready()
    {
        _wood = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.22f, 0.14f), Roughness = 1f };
        var concrete = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.54f, 0.5f), Roughness = 1f };
        var sand = new StandardMaterial3D { AlbedoColor = new Color(0.52f, 0.47f, 0.34f), Roughness = 1f };
        var paint = new Color(0.92f, 0.9f, 0.82f);

        float g0 = Terrain.HeightAt(0f, 0f);
        StaticBox(new Vector3(40f, 0.3f, 7f), new Vector3(0f, g0 - 0.05f, 3f), concrete);
        for (int k = -3; k <= 3; k++)
        {
            float x = k * 5f;
            StaticBox(new Vector3(1.6f, 0.08f, 0.8f), new Vector3(x, g0 + 0.95f, -0.2f), _wood); // bench top
            StaticBox(new Vector3(1.5f, 0.85f, 0.1f), new Vector3(x, g0 + 0.5f, -0.55f), _wood);  // bench front
            StaticBox(new Vector3(2.6f, 0.55f, 0.6f), new Vector3(x + 2.5f, g0 + 0.4f, -1.4f), sand); // sandbags
        }

        for (int k = 0; k < Distances.Length; k++)
        {
            float d = Distances[k];
            float x = (k % 2 == 0 ? -1f : 1f) * (4f + (k % 4) * 6f);
            AddTarget(x, -d, 1.4f, new Vector2(0.5f, 0.8f), paint, d);
            if (d >= 400f) AddTarget(x + (x < 0 ? -3f : 3f), -d, 1.6f, new Vector2(1f, 1f), paint, d);

            float g = Terrain.HeightAt(x, -d);
            var label = new Label3D
            {
                Text = $"{d:0} m",
                FontSize = 64,
                OutlineSize = 12,
                PixelSize = 0.0022f * Mathf.Max(1f, d / 35f),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(1f, 0.9f, 0.3f),
            };
            AddChild(label);
            label.GlobalPosition = new Vector3(x, g + 2.7f + d * 0.004f, -d);
        }
    }

    void AddTarget(float x, float z, float width, Vector2 plate, Color paint, float d)
    {
        float g = Terrain.HeightAt(x, z);
        float top = g + 2.05f;
        StaticBox(new Vector3(0.08f, 2.3f, 0.08f), new Vector3(x - width / 2f, g + 0.95f, z), _wood);
        StaticBox(new Vector3(0.08f, 2.3f, 0.08f), new Vector3(x + width / 2f, g + 0.95f, z), _wood);
        StaticBox(new Vector3(width + 0.1f, 0.08f, 0.08f), new Vector3(x, top, z), _wood);
        SteelTarget.Create(this, new Vector3(x, top - 0.04f, z), 0f, plate, paint, d);
    }

    void StaticBox(Vector3 size, Vector3 pos, Material mat)
    {
        var body = new StaticBody3D { CollisionLayer = 1 };
        AddChild(body);
        body.GlobalPosition = pos;
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
    }
}
