using Godot;

namespace Ridgeline;

/// <summary>
/// A forward operating base: a radio, an ammo cache and a sandbag ring, built by a
/// logistics team from a supply truck's load. The side can respawn on it and restock
/// from it. It goes if it's destroyed (explosives) or overrun: enemies at the radio
/// with none of its owners near for a while.
/// </summary>
public partial class Fob : Node3D
{
    public static readonly List<Fob> All = new();
    public static event Action<Fob, string>? Lost;

    public int Team;
    public string Label = "FOB";
    public float Hp = 600f;
    public const float Radius = 30f;
    double _overrunSince = -1, _nextSupply;
    StaticBody3D _body = null!;

    public override void _EnterTree() => All.Add(this);
    public override void _ExitTree() => All.Remove(this);

    public override void _Ready()
    {
        var pole = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.15f, 0.15f) };
        var crate = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.3f, 0.2f), Roughness = 1f };
        var flag = new StandardMaterial3D { AlbedoColor = Valley.TeamColors[Math.Clamp(Team, 0, 2)] };
        _body = new StaticBody3D { CollisionLayer = Layers.World };
        AddChild(_body);
        void Box(Vector3 size, Vector3 pos, Material m, bool solid = true)
        {
            _body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = pos });
            if (solid) _body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = pos });
        }
        Box(new Vector3(0.9f, 0.7f, 0.6f), new Vector3(0f, 0.35f, 0f), crate);                      // radio set
        Box(new Vector3(0.06f, 7f, 0.06f), new Vector3(0.3f, 3.5f, 0f), pole, false);               // antenna
        Box(new Vector3(0.7f, 0.4f, 0.02f), new Vector3(0.65f, 6.6f, 0f), flag, false);             // pennant
        for (int i = 0; i < 4; i++) Box(new Vector3(1.1f, 0.8f, 0.9f), new Vector3(2.5f + (i % 2) * 1.2f, 0.4f, -1f + (i / 2) * 1.0f), crate); // ammo cache
        // Sandbags around it, leaving a gap on one side.
        for (int k = 0; k < 5; k++)
        {
            float a = k * Mathf.Tau / 6f;
            var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            Fortifications.Sandbags(GetParent(), Effects.Ground, GlobalPosition + dir * 6f, dir);
        }
    }

    /// <summary>Explosions and shells wear it down.</summary>
    public static void BlastAll(Vector3 pos, float power)
    {
        foreach (var f in All.ToArray())
        {
            float d = f.GlobalPosition.DistanceTo(pos);
            if (d > 8f) continue;
            f.Hp -= power * 90f * (1f - d / 8f);
            if (f.Hp <= 0f) f.Remove("destroyed");
        }
    }

    public override void _Process(double delta)
    {
        double now = Clock.Now;
        // Restock anyone of ours standing at the cache.
        if (now >= _nextSupply)
        {
            _nextSupply = now + 5.0;
            int enemies = 0, friends = 0;
            foreach (var c in Combatants.All)
            {
                if (!c.Alive) continue;
                float d = c.FeetPos.DistanceTo(GlobalPosition);
                if (d > Radius) continue;
                if (c.Team == Team)
                {
                    friends++;
                    if (d < 10f && c.AmmoLevel < 0.99f && c.Resupply() && c is Player) Hud.Toast("Restocked at the FOB", 1.5f);
                }
                else enemies++;
            }
            if (enemies > 0 && friends == 0)
            {
                if (_overrunSince < 0) _overrunSince = now;
                else if (now - _overrunSince > 15.0) Remove("overrun");
            }
            else _overrunSince = -1;
        }
    }

    void Remove(string how)
    {
        if (!IsInsideTree() || IsQueuedForDeletion()) return;
        Lost?.Invoke(this, how);
        Effects.I.Burn(GlobalPosition + Vector3.Up * 0.5f, 20f);
        QueueFree();
    }
}
