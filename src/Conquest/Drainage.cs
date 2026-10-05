namespace Ridgeline;

/// <summary>
/// Where water goes on a height grid.
/// - Pits are filled first, so that every land cell has a way down to the sea: Priority-Flood with a hair of slope
///   added across flats (Barnes, Lehman and Mulla 2014).
/// - Each cell then drains to its steepest-downhill neighbour (D8).
/// - The cells are put in an order where each comes after the one it drains to (Braun and Willett 2013). That makes
///   upstream area one pass up the list, and implicit erosion one pass down it.
/// </summary>
public sealed class Drainage
{
    public readonly int N;
    public readonly double Cell;
    /// <summary>Receiver: the neighbour each cell drains to (itself at the sea).</summary>
    public readonly int[] Rec;
    /// <summary>Every cell, receivers before their donors; <see cref="Count"/> long.</summary>
    public readonly int[] Stack;
    /// <summary>Donors of cell i are Don[DonStart[i] .. DonStart[i + 1]).</summary>
    public readonly int[] DonStart, Don;
    /// <summary>Distance to the receiver, m.</summary>
    public readonly double[] Dist;
    /// <summary>Upstream area (or weighted sum) after <see cref="Accumulate"/>.</summary>
    public readonly double[] Area;
    public int Count { get; private set; }

    readonly int[] _cursor, _work;
    readonly bool[] _closed;
    readonly PriorityQueue<int, double> _pq = new();

    public Drainage(int n, double cell)
    {
        N = n;
        Cell = cell;
        int c = n * n;
        Rec = new int[c];
        Stack = new int[c];
        DonStart = new int[c + 1];
        Don = new int[c];
        Dist = new double[c];
        Area = new double[c];
        _cursor = new int[c];
        _work = new int[c];
        _closed = new bool[c];
    }

    /// <summary>Raise every pit to its spill level plus <paramref name="eps"/> per cell, so all land drains to the sea.</summary>
    public void Fill(double[] h, bool[] sea, double eps = 1e-4)
    {
        int n = N;
        Array.Copy(sea, _closed, sea.Length);
        _pq.Clear();
        for (int i = 0; i < h.Length; i++)
        {
            if (!sea[i]) continue;
            int x = i % n, y = i / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n || sea[ny * n + nx]) continue;
                _pq.Enqueue(i, Math.Max(0.0, h[i])); // the sea's surface, not its floor
                break;
            }
        }
        while (_pq.TryDequeue(out int c, out double hc))
        {
            int x = c % n, y = c / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                int j = ny * n + nx;
                if (_closed[j]) continue;
                _closed[j] = true;
                if (h[j] < hc + eps) h[j] = hc + eps;
                _pq.Enqueue(j, h[j]);
            }
        }
    }

    /// <summary>Each land cell's steepest-downhill neighbour; the sea drains to itself.</summary>
    public void Route(double[] h, bool[] sea)
    {
        int n = N;
        double cell = Cell;
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                Rec[i] = i;
                Dist[i] = cell;
                if (sea[i]) continue;
                double best = 0;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + Island.DX[k], ny = y + Island.DY[k];
                    if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                    int j = ny * n + nx;
                    double d = cell * Island.DL[k];
                    double s = (h[i] - (sea[j] ? Math.Min(0.0, h[j]) : h[j])) / d;
                    if (s > best)
                    {
                        best = s;
                        Rec[i] = j;
                        Dist[i] = d;
                    }
                }
            }
        });
    }

    /// <summary>The donor lists and the stack (depth first from every outlet, so receivers come before donors).</summary>
    public void Order()
    {
        int c = N * N;
        Array.Clear(_cursor);
        for (int i = 0; i < c; i++) if (Rec[i] != i) _cursor[Rec[i]]++;
        DonStart[0] = 0;
        for (int i = 0; i < c; i++) DonStart[i + 1] = DonStart[i] + _cursor[i];
        for (int i = 0; i < c; i++) _cursor[i] = DonStart[i];
        for (int i = 0; i < c; i++) if (Rec[i] != i) Don[_cursor[Rec[i]]++] = i;
        int ns = 0;
        for (int i = 0; i < c; i++)
        {
            if (Rec[i] != i) continue;
            int w = 0;
            _work[w++] = i;
            while (w > 0)
            {
                int k = _work[--w];
                Stack[ns++] = k;
                for (int d = DonStart[k]; d < DonStart[k + 1]; d++) _work[w++] = Don[d];
            }
        }
        Count = ns;
    }

    /// <summary>Upstream area of every cell (m²), or with <paramref name="weight"/> the upstream sum of area × weight.</summary>
    public void Accumulate(double[]? weight = null)
    {
        double a = Cell * Cell;
        for (int i = 0; i < Area.Length; i++) Area[i] = weight == null ? a : a * weight[i];
        for (int k = Count - 1; k >= 0; k--)
        {
            int i = Stack[k], r = Rec[i];
            if (r != i) Area[r] += Area[i];
        }
    }
}
