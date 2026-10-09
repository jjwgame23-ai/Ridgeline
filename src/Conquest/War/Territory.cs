namespace Ridgeline;

/// <summary>
/// Who holds the ground, on a 1 km grid. A side holds a square where it has troops within 1.5 km and nobody else does.
/// With two sides there it's contested and keeps its holder. Ground nobody is on stays with whoever held it last:
/// territory is taken by going there, and lost when an enemy comes. Each side starts holding its starting area.
/// </summary>
public sealed class Territory
{
    public const float CellM = 1000f;
    public readonly int N;
    public readonly sbyte[] Owner;
    public readonly bool[] Land, Contested;
    readonly byte[] _present;
    readonly float _half;

    public Territory(Island isl)
    {
        _half = isl.Extent / 2f;
        N = (int)MathF.Ceiling(isl.Extent / CellM);
        Owner = new sbyte[N * N];
        Land = new bool[N * N];
        Contested = new bool[N * N];
        _present = new byte[N * N];
        Array.Fill(Owner, (sbyte)-1);
        var votes = new int[N * N, 4];
        for (int i = 0; i < isl.Count; i++)
        {
            if (isl.Water(i)) continue;
            int c = CellOf(isl.X(i), isl.Z(i));
            Land[c] = true;
            votes[c, isl.StartOf[i] + 1]++;
        }
        for (int c = 0; c < N * N; c++)
        {
            int best = 0;
            for (int s = 1; s < 4; s++) if (votes[c, s] > votes[c, best]) best = s;
            Owner[c] = (sbyte)(best - 1);
        }
    }

    public int CellOf(float x, float z) =>
        Math.Clamp((int)((z + _half) / CellM), 0, N - 1) * N + Math.Clamp((int)((x + _half) / CellM), 0, N - 1);

    public float CX(int c) => (c % N + 0.5f) * CellM - _half;
    public float CZ(int c) => (c / N + 0.5f) * CellM - _half;

    public void Update(War war)
    {
        Array.Clear(_present);
        foreach (var u in war.Units)
        {
            if (!u.IsMover || war.Fit(u) < 4) continue;
            int cx = (int)((u.X + _half) / CellM), cz = (int)((u.Z + _half) / CellM);
            for (int y = cz - 2; y <= cz + 2; y++)
            for (int x = cx - 2; x <= cx + 2; x++)
            {
                if ((uint)x >= (uint)N || (uint)y >= (uint)N) continue;
                float dx = (x + 0.5f) * CellM - _half - u.X, dz = (y + 0.5f) * CellM - _half - u.Z;
                if (dx * dx + dz * dz <= 1500f * 1500f) _present[y * N + x] |= (byte)(1 << u.Side);
            }
        }
        for (int c = 0; c < N * N; c++)
        {
            if (!Land[c]) continue;
            byte p = _present[c];
            Contested[c] = p != 0 && (p & (p - 1)) != 0;
            if (p == 1) Owner[c] = 0;
            else if (p == 2) Owner[c] = 1;
            else if (p == 4) Owner[c] = 2;
        }
    }

    /// <summary>Whether a side has troops near a square right now (as of the last update).</summary>
    public bool Present(int c, int side) => (_present[c] & (1 << side)) != 0;

    /// <summary>The share of the island's land each side holds.</summary>
    public float Share(int side)
    {
        int land = 0, mine = 0;
        for (int c = 0; c < N * N; c++)
        {
            if (!Land[c]) continue;
            land++;
            if (Owner[c] == side) mine++;
        }
        return land == 0 ? 0f : (float)mine / land;
    }

    /// <summary>Squares held by an enemy of <paramref name="side"/>, or with enemy troops on them.</summary>
    public bool Hostile(int c, int side) => (Owner[c] >= 0 && Owner[c] != side) || (_present[c] & ~(1 << side) & 7) != 0;
}
