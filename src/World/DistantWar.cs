using Godot;

namespace Ridgeline;

/// <summary>
/// Stand-in for the fights you're not part of. A few engagement sites around
/// the map flare up and die down on their own, trading rifle fire, machine-gun
/// bursts and the odd explosion — all emitted as real world-space sound events,
/// so they arrive late, dull and echoing, from the right direction.
///
/// This is the seed of the abstract battle sim (see DESIGN.md). Explosions
/// already leave permanent craters out there; later, abstract fights will
/// leave bodies and wrecks the same way.
/// </summary>
public partial class DistantWar : Node3D
{
    public Terrain Terrain = null!;
    public bool Enabled = true;

    sealed class Site
    {
        public Vector3 A, B;       // the two sides' positions
        public bool Active;
        public float Until, Next, Intensity;
    }

    sealed class Burst
    {
        public Vector3 Pos;
        public Snd Kind;
        public int Left;
        public float Interval, Next;
    }

    readonly List<Site> _sites = new();
    readonly List<Burst> _bursts = new();
    readonly RandomNumberGenerator _rng = new();
    float _t;

    public override void _Ready()
    {
        _rng.Seed = 7;
        for (int i = 0; i < 5; i++)
        {
            float ang = _rng.RandfRange(0f, Mathf.Tau), dist = _rng.RandfRange(550f, 1500f);
            var c = new Vector3(MathF.Sin(ang) * dist, 0f, -MathF.Cos(ang) * dist);
            float ang2 = _rng.RandfRange(0f, Mathf.Tau);
            var axis = new Vector3(MathF.Cos(ang2), 0f, MathF.Sin(ang2));
            float sep = _rng.RandfRange(120f, 300f);
            _sites.Add(new Site
            {
                A = Ground(c - axis * sep / 2f),
                B = Ground(c + axis * sep / 2f),
                Active = _rng.Randf() < 0.5f,
                Until = _rng.RandfRange(10f, 90f),
                Intensity = _rng.RandfRange(0.6f, 1.4f),
            });
        }
    }

    Vector3 Ground(Vector3 p)
    {
        p.X = Mathf.Clamp(p.X, -950f, 950f);
        p.Z = Mathf.Clamp(p.Z, -950f, 950f);
        p.Y = Terrain.HeightAt(p.X, p.Z) + 1.4f;
        return p;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;

        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            var b = _bursts[i];
            b.Next -= dt;
            while (b.Next <= 0f && b.Left > 0)
            {
                SoundWorld.I.Emit(b.Kind, b.Pos);
                b.Left--;
                b.Next += b.Interval * _rng.RandfRange(0.9f, 1.1f);
            }
            if (b.Left == 0) _bursts.RemoveAt(i);
        }

        if (!Enabled) return;
        foreach (var site in _sites)
        {
            if (_t > site.Until)
            {
                site.Active = !site.Active;
                site.Until = _t + (site.Active ? _rng.RandfRange(45f, 200f) : _rng.RandfRange(30f, 150f));
            }
            if (!site.Active) continue;
            site.Next -= dt * site.Intensity;
            if (site.Next > 0f) continue;
            // Real firefights are mostly lulls and aimed shots, with the odd burst.
            site.Next = _rng.RandfRange(0.6f, 4.5f);

            var side = _rng.Randf() < 0.5f ? site.A : site.B;
            var pos = Ground(side + new Vector3(_rng.RandfRange(-45f, 45f), 0f, _rng.RandfRange(-45f, 45f)));
            float roll = _rng.Randf();
            if (roll < 0.52f) Add(pos, Snd.Rifle556, _rng.RandiRange(1, 3), _rng.RandfRange(0.3f, 0.9f));    // aimed semi
            else if (roll < 0.8f) Add(pos, Snd.Rifle556, _rng.RandiRange(3, 7), 60f / 800f);             // auto burst
            else if (roll < 0.93f) Add(pos, Snd.Rifle762, _rng.RandiRange(5, 15), 60f / 650f);           // machine gun
            else
            {
                var at = Ground(site.A.Lerp(site.B, _rng.RandfRange(0.2f, 0.8f)) + new Vector3(_rng.RandfRange(-30f, 30f), 0f, _rng.RandfRange(-30f, 30f)));
                at.Y -= 1.4f;
                SoundWorld.I.Emit(Snd.Explosion, at);
                Effects.I.Explosion(at, Terrain.NormalAt(at.X, at.Z), 1.8f);
            }
        }
    }

    void Add(Vector3 pos, Snd kind, int count, float interval) =>
        _bursts.Add(new Burst { Pos = pos, Kind = kind, Left = count, Interval = interval, Next = 0f });
}
