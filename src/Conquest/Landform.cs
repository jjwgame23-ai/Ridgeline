namespace Ridgeline;

/// <summary>
/// The island's ground, made the way real ground is made: the crust is pushed up (tectonic uplift) while rivers wear
/// it down.
/// - Rivers cut faster the more water they carry and the steeper they run: the stream power law,
///   dh/dt = U − K·A^m·S^n, where U is uplift, A the area draining through a point and S its slope. Hillslope creep
///   rounds off what lies between the rivers.
/// - Cordonnier et al. (2016, "Large scale terrain generation from tectonic uplift and fluvial erosion") make large
///   terrain this way. The erosion is solved with Braun and Willett's (2013) implicit method, which visits each cell
///   once a step and stays stable at any step length.
/// - The inputs are smooth fields:
///   - where the crust rises (a warped shape with a ragged edge, so it's one land mass with a fractal coast) and how
///     fast: most along one to three mountain ranges, less over patches of hill country, little between, which
///     becomes plains;
///   - how hard the rock is. In folded belts that's bands, where the hard beds stand as ridges and the soft ones become
///     valleys.
/// - The run goes coarse to fine (400, 200 and 100 m cells at the default size), so the big valleys form cheaply
///   first and the finer grids only carve the detail.
/// - Afterwards the sea rises over it, as it rose after the last ice age, drowning the river mouths into inlets and
///   bays. That's where natural harbours are.
/// </summary>
public static class Landform
{
    const double M = 0.45;       // m/n: real river profiles give 0.35–0.6 (Whipple and Tucker 1999); here n = 1
    const double Dt = 25_000;    // years per step
    const double Umax = 1e-3;    // m/yr: an active mountain belt
    const double K0 = 1e-5;      // m^0.1/yr: middling rock, for m = 0.45 and n = 1
    const double Kd = 0.02;      // m²/yr: soil creep on hillslopes (measured values run 0.001–0.04)
    static readonly int[] Steps = { 240, 70, 25 };

    public static void Build(Island isl, Random rng)
    {
        var shape = new Shape(rng, isl.Seed, isl.Extent / 2000f);
        var detail = new Noise2(isl.Seed + 31);
        double[] h = Array.Empty<double>(), prev = h;
        bool[] sea = Array.Empty<bool>();
        int prevN = 0;
        float ext = isl.Extent;
        for (int l = 0; l < Steps.Length; l++)
        {
            int n = isl.N >> (Steps.Length - 1 - l);
            double cell = ext / (double)n;
            var s = new bool[n * n];
            var U = new double[n * n];
            var K = new double[n * n];
            Parallel.For(0, n, y =>
            {
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    float kx = (float)(((x + 0.5) * cell - ext / 2.0) / 1000.0);
                    float ky = (float)(((y + 0.5) * cell - ext / 2.0) / 1000.0);
                    float m = shape.Mask(kx, ky);
                    s[i] = m <= 0f || x == 0 || y == 0 || x == n - 1 || y == n - 1;
                    U[i] = s[i] ? 0.0 : Umax * shape.Uplift(kx, ky, m);
                    K[i] = K0 * shape.Erodibility(kx, ky);
                }
            });
            Tidy(s, U, n, cell);
            sea = s;
            h = new double[n * n];
            if (prevN == 0)
            {
                // A low dome to start from, so the first rivers run outward.
                for (int i = 0; i < h.Length; i++)
                    if (!sea[i]) h[i] = 2.0 + 30.0 * U[i] / Umax + 2.0 * detail.Get(i % n * 0.37f, i / n * 0.37f);
            }
            else Upsample(prev, prevN, h, n, sea, detail);
            new Eroder(n, cell).Run(h, sea, U, K, Steps[l]);
            prev = h;
            prevN = n;
            if (l == Steps.Length - 1)
                for (int i = 0; i < h.Length; i++)
                {
                    isl.Uplift[i] = (float)(U[i] / Umax);
                    isl.Rock[i] = (float)(K[i] / K0);
                }
        }
        Finish(isl, h, sea, rng);
    }

    /// <summary>
    /// The sea is what's joined to the open sea round the edge: water the coastline encloses becomes low ground (a
    /// basin). Specks of land under 0.6 km² go under, too small to keep at this scale.
    /// </summary>
    static void Tidy(bool[] sea, double[] U, int n, double cell)
    {
        int c = n * n;
        var seen = new bool[c];
        var queue = new Queue<int>();
        for (int i = 0; i < c; i++)
        {
            int x = i % n, y = i / n;
            if (sea[i] && (x == 0 || y == 0 || x == n - 1 || y == n - 1))
            {
                seen[i] = true;
                queue.Enqueue(i);
            }
        }
        Flood(queue, seen, n, j => sea[j]);
        for (int i = 0; i < c; i++)
            if (sea[i] && !seen[i])
            {
                sea[i] = false;
                U[i] = 0.12 * Umax;
            }

        Array.Clear(seen);
        var comp = new List<int>();
        int minCells = (int)Math.Ceiling(0.6e6 / (cell * cell));
        for (int i = 0; i < c; i++)
        {
            if (sea[i] || seen[i]) continue;
            comp.Clear();
            seen[i] = true;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                comp.Add(k);
                ForEach4(k, n, j =>
                {
                    if (sea[j] || seen[j]) return;
                    seen[j] = true;
                    queue.Enqueue(j);
                });
            }
            if (comp.Count >= minCells) continue;
            foreach (int k in comp)
            {
                sea[k] = true;
                U[k] = 0.0;
            }
        }
    }

    static void Flood(Queue<int> queue, bool[] seen, int n, Func<int, bool> through)
    {
        while (queue.Count > 0)
            ForEach4(queue.Dequeue(), n, j =>
            {
                if (seen[j] || !through(j)) return;
                seen[j] = true;
                queue.Enqueue(j);
            });
    }

    static void ForEach4(int i, int n, Action<int> f)
    {
        int x = i % n, y = i / n;
        if (x > 0) f(i - 1);
        if (x < n - 1) f(i + 1);
        if (y > 0) f(i - n);
        if (y < n - 1) f(i + n);
    }

    /// <summary>The coarser grid's heights onto the finer one, with fresh small relief for the finer grid to carve.</summary>
    static void Upsample(double[] prev, int pn, double[] h, int n, bool[] sea, Noise2 detail)
    {
        double f = (double)pn / n;
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (sea[i]) continue;
                double px = (x + 0.5) * f - 0.5, py = (y + 0.5) * f - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(px), 0, pn - 2), y0 = Math.Clamp((int)Math.Floor(py), 0, pn - 2);
                double fx = Math.Clamp(px - x0, 0.0, 1.0), fy = Math.Clamp(py - y0, 0.0, 1.0);
                int j = y0 * pn + x0;
                double top = prev[j] + (prev[j + 1] - prev[j]) * fx;
                double bot = prev[j + pn] + (prev[j + pn + 1] - prev[j + pn]) * fx;
                double v = Math.Max(0.0, top + (bot - top) * fy);
                v += (0.5 + 0.03 * v) * detail.Fbm(x * 0.21f, y * 0.21f, 3);
                h[i] = Math.Max(v, 0.1);
            }
        });
    }

    sealed class Eroder
    {
        readonly Drainage _d;
        readonly double[] _tmp;
        readonly int _n;
        readonly double _cell;

        public Eroder(int n, double cell)
        {
            _d = new Drainage(n, cell);
            _tmp = new double[n * n];
            _n = n;
            _cell = cell;
        }

        public void Run(double[] h, bool[] sea, double[] U, double[] K, int steps)
        {
            double creep = Kd * Dt / (_cell * _cell); // explicit diffusion: stable below 0.25
            for (int s = 0; s < steps; s++)
            {
                _d.Fill(h, sea);
                _d.Route(h, sea);
                _d.Order();
                _d.Accumulate();
                // Implicit stream power with n = 1: each cell's new height from its receiver's new height, which is
                // already done (receivers come first in the stack).
                for (int k = 0; k < _d.Count; k++)
                {
                    int i = _d.Stack[k];
                    if (sea[i]) continue;
                    int r = _d.Rec[i];
                    if (r == i)
                    {
                        h[i] += U[i] * Dt;
                        continue;
                    }
                    double f = K[i] * Dt * Math.Exp(M * Math.Log(_d.Area[i])) / _d.Dist[i];
                    h[i] = (h[i] + U[i] * Dt + f * h[r]) / (1.0 + f);
                }
                Creep(h, sea, creep);
            }
        }

        void Creep(double[] h, bool[] sea, double c)
        {
            int n = _n;
            Parallel.For(1, n - 1, y =>
            {
                for (int x = 1; x < n - 1; x++)
                {
                    int i = y * n + x;
                    _tmp[i] = sea[i] ? h[i] : h[i] + c * (h[i - 1] + h[i + 1] + h[i - n] + h[i + n] - 4.0 * h[i]);
                }
            });
            Parallel.For(1, n - 1, y => Array.Copy(_tmp, y * n + 1, h, y * n + 1, n - 2));
        }
    }

    static void Finish(Island isl, double[] h, bool[] sea, Random rng)
    {
        int n = isl.N, c = n * n;
        double cell = isl.Cell;
        var clim = isl.Climate;

        // The highest summit into the climate's range. With n = 1 the stream power and creep equations are linear in
        // the uplift rate, so scaling the heights is the same as having uplifted faster or slower.
        double max = 1.0;
        for (int i = 0; i < c; i++) if (!sea[i]) max = Math.Max(max, h[i]);
        double scale = (clim.PeakMinM + rng.NextDouble() * (clim.PeakMaxM - clim.PeakMinM)) / max;
        for (int i = 0; i < c; i++) if (!sea[i]) h[i] *= scale;

        // Scree: nothing steeper than about 40° stands on a cell this size, so the excess slides to the cell below.
        Talus(h, sea, n, cell, 40.0);

        // The sea rises 20–50 m and drowns the lowest valleys into inlets. (It rose about 120 m after the last ice
        // age; most of that drowned shelf is already sea in the uplift field.)
        double rise = 20.0 + 30.0 * rng.NextDouble();
        var wasSea = (bool[])sea.Clone();
        for (int i = 0; i < c; i++) if (!sea[i]) h[i] -= rise;
        var seen = (bool[])sea.Clone();
        var queue = new Queue<int>();
        for (int i = 0; i < c; i++) if (sea[i]) queue.Enqueue(i);
        Flood(queue, seen, n, j => h[j] <= 0.0);
        for (int i = 0; i < c; i++)
        {
            if (seen[i]) sea[i] = true;
            else if (h[i] < 0.5) h[i] = 0.5; // a hollow below sea level but cut off from it: it will hold a lake
        }

        // The sea floor: a shelf sloping out at about 2.5% for the first 4 km, steeper beyond, down to 1,800 m.
        var land = new bool[c];
        for (int i = 0; i < c; i++) land[i] = !sea[i];
        // Smoothed over a kilometre: distance on a grid comes in polygons, which would show as terraces.
        var dist = Grid.BoxMean(Grid.Distance(land, n, (float)cell), n, Math.Max(1, (int)(1000.0 / cell)));
        var floor = new Noise2(isl.Seed + 41);
        for (int i = 0; i < c; i++)
        {
            if (!sea[i]) continue;
            double km = dist[i] / 1000.0;
            double depth = km < 4.0 ? 25.0 * km : 100.0 + 70.0 * (km - 4.0);
            depth = Math.Min(1800.0, depth) * (0.85 + 0.15 * floor.Fbm(i % n * 0.02f, i / n * 0.02f, 3));
            // Drowned valleys keep their own floors; the old sea floor is the rise further down.
            h[i] = wasSea[i] ? -(rise + depth) : Math.Min(h[i], -0.5);
        }

        isl.SeaRise = (float)rise;
        for (int i = 0; i < c; i++)
        {
            isl.Height[i] = (float)h[i];
            isl.Land[i] = sea[i] ? Cover.Sea : Cover.Grass;
        }
    }

    static void Talus(double[] h, bool[] sea, int n, double cell, double maxDeg)
    {
        double t = Math.Tan(maxDeg * Math.PI / 180.0);
        for (int pass = 0; pass < 8; pass++)
            for (int y = 1; y < n - 1; y++)
            for (int x = 1; x < n - 1; x++)
            {
                int i = y * n + x;
                if (sea[i]) continue;
                for (int k = 0; k < 8; k++)
                {
                    int j = (y + Island.DY[k]) * n + x + Island.DX[k];
                    if (sea[j]) continue;
                    double lim = t * cell * Island.DL[k], drop = h[i] - h[j];
                    if (drop <= lim) continue;
                    double m = (drop - lim) * 0.25;
                    h[i] -= m;
                    h[j] += m;
                }
            }
    }

    /// <summary>The smooth fields the ground is made from, in km from the middle of the map.</summary>
    sealed class Shape
    {
        readonly float _cx, _cy, _cos, _sin, _aspect, _r, _warp, _rough, _half, _sCos, _sSin, _sWave;
        readonly Noise2 _wx, _wy, _coast, _ramp, _un, _kn, _sw, _hills, _fold;
        readonly List<(List<(float Ax, float Ay, float Bx, float By)> Segs, float S2, float Amp)> _crests = new();

        public Shape(Random rng, int seed, float half)
        {
            float F(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            _half = half;
            _wx = new Noise2(seed + 11);
            _wy = new Noise2(seed + 12);
            _coast = new Noise2(seed + 13);
            _ramp = new Noise2(seed + 14);
            _un = new Noise2(seed + 15);
            _kn = new Noise2(seed + 16);
            _sw = new Noise2(seed + 17);
            _hills = new Noise2(seed + 18);
            _fold = new Noise2(seed + 19);
            _cx = F(-0.04f, 0.04f) * half;
            _cy = F(-0.04f, 0.04f) * half;
            float ang = F(0f, MathF.PI);
            _cos = MathF.Cos(ang);
            _sin = MathF.Sin(ang);
            // About 100 km along its length, inside a 128 km square of sea.
            _aspect = F(1.05f, 1.45f);
            _r = F(0.76f, 0.84f) * half;
            _warp = F(0.08f, 0.13f) * half;
            _rough = F(0.2f, 0.28f);
            float sa = F(0f, MathF.PI);
            _sCos = MathF.Cos(sa);
            _sSin = MathF.Sin(sa);
            _sWave = F(5f, 11f);

            // One to three mountain ranges, most along the island's length: crests of four bends each.
            int ranges = 1 + (rng.NextDouble() < 0.7 ? 1 : 0) + (rng.NextDouble() < 0.35 ? 1 : 0);
            for (int k = 0; k < ranges; k++)
            for (int tries = 0; tries < 60; tries++)
            {
                float u = F(-0.55f, 0.55f) * _r, v = F(-0.5f, 0.5f) * _r / _aspect;
                float x = _cx + u * _cos - v * _sin, y = _cy + u * _sin + v * _cos;
                if (Mask(x, y) < 0.3f) continue;
                float dir = ang + (rng.NextDouble() < 0.2 ? MathF.PI / 2f : 0f) + F(-0.45f, 0.45f);
                float len = F(0.5f, 1.2f) * _r, sig = F(4f, 8f), amp = F(0.6f, 1f);
                float dx = MathF.Cos(dir), dy = MathF.Sin(dir);
                var segs = new List<(float, float, float, float)>();
                float px = x - dx * len / 2f, py = y - dy * len / 2f;
                for (int s = 1; s <= 4; s++)
                {
                    float t = s / 4f - 0.5f, j = s < 4 ? F(-1f, 1f) * len * 0.08f : 0f;
                    float qx = x + dx * len * t - dy * j, qy = y + dy * len * t + dx * j;
                    segs.Add((px, py, qx, qy));
                    px = qx;
                    py = qy;
                }
                _crests.Add((segs, 2f * sig * sig, amp));
                break;
            }
        }

        /// <summary>Above 0 on the island: a warped ellipse with a ragged, fractal edge, kept off the map's borders.</summary>
        public float Mask(float x, float y)
        {
            float wx = x + _warp * _wx.Fbm(x / 40f, y / 40f, 3), wy = y + _warp * _wy.Fbm(x / 40f, y / 40f, 3);
            float dx = wx - _cx, dy = wy - _cy;
            float u = dx * _cos + dy * _sin, v = (-dx * _sin + dy * _cos) * _aspect;
            float m = 1f - MathF.Sqrt(u * u + v * v) / _r + _rough * _coast.Fbm(x / 18f, y / 18f, 6, 0.55f);
            float edge = _half - MathF.Max(MathF.Abs(x), MathF.Abs(y));
            return MathF.Min(m, (edge - 4f) / 12f);
        }

        /// <summary>
        /// Relative uplift, 0..1.3. It rises from the coast quickly (cliffs) or slowly (coastal plains). It's highest
        /// along the ranges and middling over patches of hill country. Between them it's low, and that becomes plains
        /// and basins.
        /// </summary>
        public float Uplift(float x, float y, float m)
        {
            if (m <= 0f) return 0f;
            float ramp = 0.03f + 0.12f * (0.5f + 0.5f * _ramp.Fbm(x / 25f, y / 25f, 3));
            float edge = Smooth(m / ramp), inland = Smooth(m / 0.6f);
            float hills = Smooth(0.5f + 0.9f * _hills.Fbm(x / 24f, y / 24f, 3));
            float u = edge * (0.05f + 0.1f * inland + 0.4f * hills + 0.95f * Ranges(x, y)) * (0.8f + 0.35f * _un.Fbm(x / 14f, y / 14f, 4));
            return Math.Clamp(u, 0f, 1.3f);
        }

        /// <summary>
        /// Rock erodibility relative to middling rock, about 0.4..2.5. In folded belts there are beds of harder and
        /// softer rock; elsewhere the rock just varies.
        /// </summary>
        public float Erodibility(float x, float y)
        {
            float along = (x * _sCos + y * _sSin) / _sWave + 0.8f * _sw.Fbm(x / 20f, y / 20f, 3);
            float fold = Smooth(0.5f + _fold.Fbm(x / 30f, y / 30f, 2));
            return MathF.Exp(MathF.Log(2.5f) * (0.45f * fold * MathF.Sin(along * MathF.Tau) + 0.45f * _kn.Fbm(x / 16f, y / 16f, 4)));
        }

        float Ranges(float x, float y)
        {
            float keep = 1f;
            foreach (var (segs, s2, amp) in _crests)
            {
                float d2 = float.MaxValue;
                foreach (var (ax, ay, bx, by) in segs) d2 = MathF.Min(d2, SegDist2(x, y, ax, ay, bx, by));
                keep *= 1f - amp * MathF.Exp(-d2 / s2);
            }
            return 1f - keep;
        }

        static float SegDist2(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float t = Math.Clamp(((px - ax) * vx + (py - ay) * vy) / MathF.Max(1e-6f, vx * vx + vy * vy), 0f, 1f);
            float dx = px - ax - t * vx, dy = py - ay - t * vy;
            return dx * dx + dy * dy;
        }

        static float Smooth(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
