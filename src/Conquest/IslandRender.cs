using System.Text;
using Godot;

namespace Ridgeline;

/// <summary>
/// Pictures of a generated island:
/// - the map, drawn like a topographic map: land cover, shading lit from the north-west, contours every 100 m,
///   rivers, roads, towns, nodes, and the starting areas;
/// - the layers it was made from;
/// - a page with the map, names and the report.
/// </summary>
public static class IslandRender
{
    public const int MapPx = 2048;

    static Color Hex(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    static Color CoverColor(Cover c) => c switch
    {
        Cover.Lake => Hex(0xa6cfe8),
        Cover.Beach => Hex(0xf2e4b0),
        Cover.Marsh => Hex(0xc9dfd2),
        Cover.Rock => Hex(0xd6d1c8),
        Cover.Grass => Hex(0xe9ecd2),
        Cover.Garrigue => Hex(0xdde6c4),
        Cover.Maquis => Hex(0xc0d7a2),
        Cover.Pine => Hex(0x98c387),
        Cover.Oak => Hex(0xa8ce90),
        Cover.Montane => Hex(0x86b57f),
        Cover.Fields => Hex(0xf4edcc),
        Cover.Orchards => Hex(0xe4e8ba),
        Cover.Urban => Hex(0xb9ada3),
        _ => Hex(0xbfe0ef),
    };

    static Color SeaColor(float depth) => depth < 200f
        ? Hex(0xd4ecf7).Lerp(Hex(0x9cc7e4), depth / 200f)
        : Hex(0x9cc7e4).Lerp(Hex(0x6a9fcd), MathF.Min(1f, (depth - 200f) / 1200f));

    public static void Map(Island isl, string path) => Draw(isl).Save(path);

    /// <summary>Map pixels for a point in metres east and south of the island's centre.</summary>
    public static (float X, float Y) Px(Island isl, float x, float z) =>
        ((x + isl.Extent / 2f) / isl.Extent * MapPx, (z + isl.Extent / 2f) / isl.Extent * MapPx);

    /// <summary>The map, ready to draw more on.</summary>
    public static Canvas Draw(Island isl)
    {
        int n = isl.N, W = MapPx;
        float s = (float)W / n, cell = isl.Cell;
        var cv = new Canvas(W, W);
        var light = new Vector3(-1f, 1.2f, -1f).Normalized();
        Parallel.For(0, W, py =>
        {
            for (int px = 0; px < W; px++)
            {
                float cx = Math.Clamp((px + 0.5f) / s - 0.5f, 0f, n - 1f), cy = Math.Clamp((py + 0.5f) / s - 0.5f, 0f, n - 1f);
                int i = CellAt(n, cx, cy);
                float h = Grid.Sample(isl.Height, n, cx, cy, 0f);
                Color col;
                if (isl.Sea(i)) col = SeaColor(-h);
                else
                {
                    col = CoverColor(isl.Land[i]);
                    float hx = Grid.Sample(isl.Height, n, MathF.Min(cx + 0.5f, n - 1f), cy, h) - Grid.Sample(isl.Height, n, MathF.Max(cx - 0.5f, 0f), cy, h);
                    float hz = Grid.Sample(isl.Height, n, cx, MathF.Min(cy + 0.5f, n - 1f), h) - Grid.Sample(isl.Height, n, cx, MathF.Max(cy - 0.5f, 0f), h);
                    var nrm = new Vector3(-hx / cell, 1f, -hz / cell).Normalized();
                    float shade = Math.Clamp(1f + 1.6f * (nrm.Dot(light) - light.Y), 0.55f, 1.2f);
                    col = new Color(col.R * shade, col.G * shade, col.B * shade);
                    if (!isl.Water(i))
                    {
                        float hr = Grid.Sample(isl.Height, n, MathF.Min(cx + 1f / s, n - 1f), cy, h);
                        float hd = Grid.Sample(isl.Height, n, cx, MathF.Min(cy + 1f / s, n - 1f), h);
                        int b = (int)MathF.Floor(h / 100f);
                        if (b != (int)MathF.Floor(hr / 100f) || b != (int)MathF.Floor(hd / 100f))
                        {
                            int top = (int)MathF.Floor(MathF.Max(h, MathF.Max(hr, hd)) / 100f);
                            col = col.Lerp(Hex(0x8a5a2b), top % 5 == 0 ? 0.55f : 0.28f);
                        }
                    }
                }
                if (isl.StartOf[i] >= 0) col = col.Lerp(Valley.TeamColors[isl.StartOf[i]], 0.18f);
                // The coastline.
                int ir = CellAt(n, MathF.Min(cx + 1f / s, n - 1f), cy), id = CellAt(n, cx, MathF.Min(cy + 1f / s, n - 1f));
                if (isl.Sea(i) != isl.Sea(ir) || isl.Sea(i) != isl.Sea(id)) col = Hex(0x3b6e9c);
                cv.Set(px, py, col);
            }
        });

        // A 10 km grid.
        for (float km = 10f; km * 1000f < isl.Extent; km += 10f)
        {
            float p = km * 1000f / cell * s;
            cv.Line(p, 0f, p, W, 1f, Hex(0x444444), 0.12f);
            cv.Line(0f, p, W, p, 1f, Hex(0x444444), 0.12f);
        }

        Vector2 P(int i) => new((i % n + 0.5f) * s, (i / n + 0.5f) * s);

        // Rivers, small first so the big ones lie on top.
        var streams = new List<int>();
        for (int i = 0; i < isl.Count; i++) if (isl.Area[i] >= 3e6f && !isl.Sea(i) && isl.Land[i] != Cover.Lake) streams.Add(i);
        streams.Sort((a, b) => isl.Area[a].CompareTo(isl.Area[b]));
        foreach (int i in streams)
        {
            int r = isl.Receiver[i];
            if (r == i) continue;
            float w = Math.Clamp(0.6f + 0.5f * MathF.Log2(isl.Area[i] / 3e6f), 0.6f, 4f) * s / 1.6f;
            var a = P(i);
            var b = P(r);
            cv.Line(a.X, a.Y, b.X, b.Y, w, Hex(0x3d7fc1));
        }

        foreach (var (fx, fz) in isl.Farms)
            cv.Disc((fx + isl.Extent / 2f) / cell * s, (fz + isl.Extent / 2f) / cell * s, 0.7f, Hex(0x5a4a3a), 0.75f);

        // Roads: tracks, then secondary, then main, each over a darker casing.
        foreach (var cls in new[] { RoadClass.Track, RoadClass.Secondary, RoadClass.Main })
        {
            var (casing, fill, w) = cls switch
            {
                RoadClass.Main => (Hex(0x7a1f16), Hex(0xd2402e), 2.2f),
                RoadClass.Secondary => (Hex(0x8a5410), Hex(0xeb9a2a), 1.6f),
                _ => (Hex(0x6b6257), Hex(0x6b6257), 0.9f),
            };
            foreach (var road in isl.Roads.Where(r => r.Class == cls))
                for (int k = 1; k < road.Cells.Count; k++)
                {
                    var a = P(road.Cells[k - 1]);
                    var b = P(road.Cells[k]);
                    if (cls != RoadClass.Track) cv.Line(a.X, a.Y, b.X, b.Y, w + 1.2f, casing);
                }
            foreach (var road in isl.Roads.Where(r => r.Class == cls))
                for (int k = 1; k < road.Cells.Count; k++)
                {
                    var a = P(road.Cells[k - 1]);
                    var b = P(road.Cells[k]);
                    cv.Line(a.X, a.Y, b.X, b.Y, w, fill);
                }
        }
        foreach (var x in isl.Crossings)
        {
            var p = P(x.Cell);
            if (x.Bridge) cv.Disc(p.X, p.Y, 2.2f, Hex(0x111111));
            else cv.Ring(p.X, p.Y, 2.2f, 1f, Hex(0x2d6aa3));
        }

        // Starting areas' edges: dashed and cased in white, as boundaries are drawn, so one isn't taken for a river.
        var edges = new List<int>();
        for (int i = 0; i < isl.Count; i++)
        {
            int t = isl.StartOf[i];
            if (t < 0) continue;
            int x = i % n, y = i / n;
            bool edge = (x > 0 && isl.StartOf[i - 1] != t && !isl.Water(i - 1)) || (x < n - 1 && isl.StartOf[i + 1] != t && !isl.Water(i + 1))
                        || (y > 0 && isl.StartOf[i - n] != t && !isl.Water(i - n)) || (y < n - 1 && isl.StartOf[i + n] != t && !isl.Water(i + n));
            if (edge && ((x / 3 + y / 3) & 1) == 0) edges.Add(i);
        }
        foreach (int i in edges)
        {
            var p = P(i);
            cv.Disc(p.X, p.Y, 3f, Hex(0xffffff), 0.8f);
        }
        foreach (int i in edges)
        {
            var p = P(i);
            cv.Disc(p.X, p.Y, 1.9f, Valley.TeamColors[isl.StartOf[i]]);
        }

        foreach (var pk in isl.Peaks)
        {
            var p = P(pk.Cell);
            cv.Triangle(p.X, p.Y, 4f, Hex(0x4a2f17));
        }
        foreach (var t in isl.Towns.OrderBy(t => t.Population))
        {
            var p = P(t.Cell);
            float r = t.Kind switch { TownKind.City => 6f, TownKind.Town => 4.2f, TownKind.Village => 2.6f, _ => 1.5f };
            if (t.Port) cv.Ring(p.X, p.Y, r + 3f, 1.6f, Hex(0x1f5f9e));
            cv.Disc(p.X, p.Y, r, Hex(0x1a1a1a));
            if (t.Kind >= TownKind.Town) cv.Disc(p.X, p.Y, r * 0.55f, Hex(0xffffff));
        }
        foreach (var node in isl.Nodes)
        {
            var p = P(node.Cell);
            cv.Diamond(p.X, p.Y, 7.5f, Hex(0x111111));
            cv.Diamond(p.X, p.Y, 5.5f, node.Kind == NodeKind.Fuel ? Hex(0x8e44ad) : Hex(0xc0560f));
        }
        return cv;
    }

    static int CellAt(int n, float cx, float cy) =>
        Math.Clamp((int)MathF.Round(cy), 0, n - 1) * n + Math.Clamp((int)MathF.Round(cx), 0, n - 1);

    /// <summary>Four panels: relief, where the crust rose, rain, and drainage.</summary>
    public static void Layers(Island isl, string path)
    {
        int n = isl.N, P = MapPx / 2;
        var cv = new Canvas(MapPx, MapPx);
        float maxH = 1f, maxRain = 1f;
        for (int i = 0; i < isl.Count; i++)
        {
            if (isl.Water(i)) continue;
            maxH = MathF.Max(maxH, isl.Height[i]);
            maxRain = MathF.Max(maxRain, isl.Rain[i]);
        }
        Parallel.For(0, P, py =>
        {
            for (int px = 0; px < P; px++)
            {
                int i = CellAt(n, (px + 0.5f) * n / P - 0.5f, (py + 0.5f) * n / P - 0.5f);
                bool sea = isl.Sea(i);
                float h = isl.Height[i] / maxH;
                // Relief: green lowlands through tan and brown to grey peaks.
                Color relief = sea ? SeaColor(-isl.Height[i])
                    : h < 0.25f ? Hex(0x6f9e5a).Lerp(Hex(0xc9c27a), h / 0.25f)
                    : h < 0.6f ? Hex(0xc9c27a).Lerp(Hex(0x9a6b3c), (h - 0.25f) / 0.35f)
                    : Hex(0x9a6b3c).Lerp(Hex(0xeeeeee), (h - 0.6f) / 0.4f);
                Color uplift = sea ? Hex(0x20262c) : Hex(0x2b1d0e).Lerp(Hex(0xffb347), Math.Clamp(isl.Uplift[i] / 1.3f, 0f, 1f))
                    .Lerp(Hex(0x7a7aff), Math.Clamp((isl.Rock[i] - 1f) / 4f, 0f, 0.35f));
                Color rain = sea ? Hex(0xf2f2f2) : Hex(0xf3e3b5).Lerp(Hex(0x1f4fa8), Math.Clamp((isl.Rain[i] - 250f) / (maxRain - 250f), 0f, 1f));
                float a = MathF.Log10(MathF.Max(isl.Area[i], isl.CellArea) / isl.CellArea) / 5f;
                Color drain = sea ? Hex(0x0b1320) : Hex(0x101010).Lerp(Hex(0x8fd3ff), Math.Clamp(a, 0f, 1f));
                cv.Set(px, py, relief);
                cv.Set(px + P, py, uplift);
                cv.Set(px, py + P, rain);
                cv.Set(px + P, py + P, drain);
            }
        });
        cv.Save(path);
    }

    /// <summary>A page with the map, names on it (zoom with the wheel, drag to pan) and the report below.</summary>
    public static void Page(Island isl, string pngPath, string report, string outPath)
    {
        float s = (float)MapPx / isl.N;
        string X(int i) => ((i % isl.N + 0.5f) * s).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        string Y(int i) => ((i / isl.N + 0.5f) * s).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(Esc(isl.Name)).Append(" (island ").Append(isl.Seed).Append(")</title><style>");
        sb.Append("body{margin:0;background:#1d2126;color:#e8e6e1;font:14px/1.45 system-ui,sans-serif}");
        sb.Append("h1{font-size:18px;margin:10px 16px;font-weight:600}#view{position:relative;height:82vh;overflow:hidden;cursor:grab;background:#d4ecf7}");
        sb.Append("#map{position:absolute;left:0;top:0;width:2048px;height:2048px;transform-origin:0 0}#map img,#map svg{position:absolute;left:0;top:0;width:2048px;height:2048px}");
        sb.Append("svg text{font-family:Georgia,serif;paint-order:stroke;stroke:#fffdf6;stroke-width:3px;stroke-linejoin:round;fill:#1b1b1b}");
        sb.Append("svg .river{fill:#1f5f9e;font-style:italic}svg .hill{fill:#5b3a1b;font-size:9px}svg .node{font-size:10px}");
        sb.Append("pre{padding:12px 16px;white-space:pre-wrap;max-width:1100px}</style></head><body>");
        sb.Append("<h1>").Append(Esc(isl.Name)).Append(": island ").Append(isl.Seed).Append(", ").Append(isl.Climate.Name).Append(". Wheel to zoom, drag to pan, hover for details.</h1>");
        sb.Append("<div id=view><div id=map><img alt=\"island map\" src=\"data:image/png;base64,");
        sb.Append(Convert.ToBase64String(File.ReadAllBytes(pngPath)));
        sb.Append("\"><svg viewBox=\"0 0 2048 2048\">");
        foreach (var t in isl.Towns)
        {
            string tip = $"{t.Name}: {t.Kind.ToString().ToLowerInvariant()}, {t.Population:N0} people{(t.Port ? ", port" : "")}{(t.Hilltop ? ", hilltop" : "")}, {t.Elevation:0} m";
            sb.Append("<g><title>").Append(Esc(tip)).Append("</title><circle cx=").Append(X(t.Cell)).Append(" cy=").Append(Y(t.Cell)).Append(" r=5 fill=transparent />");
            int size = t.Kind switch { TownKind.City => 22, TownKind.Town => 15, TownKind.Village => t.Population >= 1000 ? 11 : 0, _ => 0 };
            if (size > 0)
                sb.Append("<text x=").Append(X(t.Cell)).Append(" y=").Append(Y(t.Cell)).Append(" dx=7 dy=-6 font-size=").Append(size)
                  .Append(t.Kind == TownKind.City ? " font-weight=bold" : "").Append('>').Append(Esc(t.Name)).Append("</text>");
            sb.Append("</g>");
        }
        foreach (var p in isl.Peaks)
        {
            bool named = !p.Name.StartsWith("Hill ");
            sb.Append("<g><title>").Append(Esc($"{p.Name}: {p.Height:0} m, {p.Relief:0} m above the ground round it")).Append("</title><text class=hill x=")
              .Append(X(p.Cell)).Append(" y=").Append(Y(p.Cell)).Append(" dx=5 dy=11").Append(named ? " font-size=12 font-weight=bold" : "").Append('>')
              .Append(Esc(named ? $"{p.Name} {p.Height:0}" : p.Name)).Append("</text></g>");
        }
        foreach (var r in isl.Rivers)
        {
            if (r.Cells.Count < 8) continue;
            int at = r.Cells[r.Cells.Count * 2 / 5];
            sb.Append("<g><title>").Append(Esc($"{r.Name}: {r.LengthKm:0} km, basin {r.AreaKm2:0} km², mean flow {r.FlowM3s:0.0} m³/s at the mouth"))
              .Append("</title><text class=river x=").Append(X(at)).Append(" y=").Append(Y(at)).Append(" dx=4 font-size=12>").Append(Esc(r.Name)).Append("</text></g>");
        }
        foreach (var node in isl.Nodes)
            sb.Append("<g><title>").Append(Esc($"{node.Name}: {(node.Kind == NodeKind.Fuel ? "fuel" : "ore")}, richness {node.Richness:0.00}{(node.Start >= 0 ? $", in {KothMode.TeamNames[node.Start]}'s starting area" : "")}"))
              .Append("</title><text class=node x=").Append(X(node.Cell)).Append(" y=").Append(Y(node.Cell)).Append(" dx=9 dy=4 fill=").Append(node.Kind == NodeKind.Fuel ? "#6c2a8a" : "#9a3f08")
              .Append('>').Append(Esc(node.Name)).Append("</text></g>");
        foreach (var st in isl.Starts)
        {
            var port = isl.Towns[st.Port];
            var col = Valley.TeamColors[st.Team];
            sb.Append("<text x=").Append(X(port.Cell)).Append(" y=").Append(Y(port.Cell)).Append(" dx=-30 dy=26 font-size=18 font-weight=bold fill=#")
              .Append(col.ToHtml(false)).Append('>').Append(KothMode.TeamNames[st.Team]).Append("</text>");
        }
        for (int km = 10; km * 1000 < isl.Extent; km += 10)
        {
            string p = (km * 1000f / isl.Cell * s).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            sb.Append("<text x=").Append(p).Append(" y=14 font-size=10 text-anchor=middle>").Append(km).Append(" km</text>");
            sb.Append("<text x=4 y=").Append(p).Append(" font-size=10>").Append(km).Append("</text>");
        }
        sb.Append("</svg></div></div><pre>").Append(Esc(report)).Append("</pre>");
        sb.Append("<script>const v=document.getElementById('view'),m=document.getElementById('map');let s=Math.min(v.clientWidth,v.clientHeight)/2048,x=0,y=0;");
        sb.Append("function u(){m.style.transform=`translate(${x}px,${y}px) scale(${s})`}u();");
        sb.Append("v.addEventListener('wheel',e=>{e.preventDefault();const r=v.getBoundingClientRect(),px=e.clientX-r.left,py=e.clientY-r.top,k=e.deltaY<0?1.2:1/1.2;x=px-(px-x)*k;y=py-(py-y)*k;s*=k;u()},{passive:false});");
        sb.Append("let d=null;v.addEventListener('mousedown',e=>{d=[e.clientX-x,e.clientY-y];v.style.cursor='grabbing'});addEventListener('mouseup',()=>{d=null;v.style.cursor='grab'});");
        sb.Append("addEventListener('mousemove',e=>{if(d){x=e.clientX-d[0];y=e.clientY-d[1];u()}});</script></body></html>");
        File.WriteAllText(outPath, sb.ToString());
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>An RGB raster with anti-aliased drawing, saved as PNG.</summary>
    public sealed class Canvas
    {
        public readonly int W, H;
        readonly Color[] _px;

        public Canvas(int w, int h)
        {
            W = w;
            H = h;
            _px = new Color[w * h];
        }

        public void Set(int x, int y, Color c) => _px[y * W + x] = c;

        public void Blend(int x, int y, Color c, float a)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H || a <= 0f) return;
            int i = y * W + x;
            _px[i] = _px[i].Lerp(c, MathF.Min(1f, a));
        }

        public void Line(float x0, float y0, float x1, float y1, float width, Color c, float alpha = 1f)
        {
            float hw = width / 2f, vx = x1 - x0, vy = y1 - y0, l2 = vx * vx + vy * vy;
            int minX = (int)MathF.Floor(MathF.Min(x0, x1) - hw - 1f), maxX = (int)MathF.Ceiling(MathF.Max(x0, x1) + hw + 1f);
            int minY = (int)MathF.Floor(MathF.Min(y0, y1) - hw - 1f), maxY = (int)MathF.Ceiling(MathF.Max(y0, y1) + hw + 1f);
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float t = l2 > 0f ? Math.Clamp(((px - x0) * vx + (py - y0) * vy) / l2, 0f, 1f) : 0f;
                float dx = px - x0 - t * vx, dy = py - y0 - t * vy;
                float cover = Math.Clamp(hw + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                if (cover > 0f) Blend(x, y, c, cover * alpha);
            }
        }

        public void Disc(float cx, float cy, float r, Color c, float alpha = 1f)
        {
            for (int y = (int)(cy - r - 1f); y <= (int)(cy + r + 1f); y++)
            for (int x = (int)(cx - r - 1f); x <= (int)(cx + r + 1f); x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                Blend(x, y, c, Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f) * alpha);
            }
        }

        public void Ring(float cx, float cy, float r, float w, Color c)
        {
            for (int y = (int)(cy - r - w - 1f); y <= (int)(cy + r + w + 1f); y++)
            for (int x = (int)(cx - r - w - 1f); x <= (int)(cx + r + w + 1f); x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                Blend(x, y, c, Math.Clamp(w / 2f + 0.5f - MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - r), 0f, 1f));
            }
        }

        public void Diamond(float cx, float cy, float r, Color c)
        {
            for (int y = (int)(cy - r - 1f); y <= (int)(cy + r + 1f); y++)
            for (int x = (int)(cx - r - 1f); x <= (int)(cx + r + 1f); x++)
                Blend(x, y, c, Math.Clamp(r + 0.5f - (MathF.Abs(x + 0.5f - cx) + MathF.Abs(y + 0.5f - cy)), 0f, 1f));
        }

        /// <summary>A filled triangle pointing up, its apex <paramref name="r"/> above the point.</summary>
        public void Triangle(float cx, float cy, float r, Color c)
        {
            for (int y = (int)(cy - r - 1f); y <= (int)(cy + r + 1f); y++)
            for (int x = (int)(cx - r - 1f); x <= (int)(cx + r + 1f); x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                if (dy < -r || dy > 0.6f * r) continue;
                if (MathF.Abs(dx) <= (dy + r) / 1.6f) Blend(x, y, c, 1f);
            }
        }

        public void Save(string path) => ToImage().SavePng(path);

        public Image ToImage()
        {
            var bytes = new byte[W * H * 3];
            for (int i = 0; i < _px.Length; i++)
            {
                bytes[i * 3] = (byte)(Math.Clamp(_px[i].R, 0f, 1f) * 255f + 0.5f);
                bytes[i * 3 + 1] = (byte)(Math.Clamp(_px[i].G, 0f, 1f) * 255f + 0.5f);
                bytes[i * 3 + 2] = (byte)(Math.Clamp(_px[i].B, 0f, 1f) * 255f + 0.5f);
            }
            return Image.CreateFromData(W, H, false, Image.Format.Rgb8, bytes);
        }
    }
}
