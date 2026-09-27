"""
Ridgeline telemetry: the fights of a match, measured and drawn.

    python tools/telemetry.py match.jsonl [--out DIR] [--min-shots 12]

Reads the JSON-lines file a Territory match writes with the dev arg telemetry=<file>
(see src/Game/Telemetry.cs) and:

- splits the fighting into engagements: shots that follow on from each other (within 25 s,
  and within 120 m of where earlier shots came from or went), chained together;
- measures each one: how long it lasted and at what range, shots, hits and casualties by side
  and when, lulls in the fire, and what the people in it did: how far they moved, how long
  they sat in one spot, what they spent it doing (states, stances), how suppressed they were;
  and flags standoffs (fire going on, nobody moving, nobody getting hit);
- draws each one as a storyboard: top-down frames over the real ground (hill shading,
  buildings, trees, the points) with where everyone was, their last stretch of movement and
  fire, and a timeline of fire, casualties and movement under it;
- writes summary.txt and overview.png (the whole map, every engagement on it).

Needs numpy and Pillow.
"""
import argparse
import base64
import bisect
import collections
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

TEAM_COL = [(70, 130, 255), (235, 80, 65), (235, 195, 30)]
TEAM_NAME = ["ALPHA", "BRAVO", "CHARLIE"]
STATES = ["Advance", "Investigate", "Engage", "TakeCover", "InCover", "Hold", "Flank", "Search", "Evade", "Aid"]
MOVE_SPEED = 0.5      # m/s: faster than this between samples counts as moving
LINK_TIME = 25.0      # s: shots this close in time...
LINK_DIST = 120.0     # m: ...and this close in space belong to the same engagement
LULL = 15.0           # s: a gap in the fire this long is a lull


def font(size, bold=False):
    for name in (("arialbd.ttf" if bold else "arial.ttf"), "DejaVuSans.ttf"):
        for d in ("C:/Windows/Fonts", "/usr/share/fonts/truetype/dejavu", ""):
            try:
                return ImageFont.truetype(os.path.join(d, name) if d else name, size)
            except OSError:
                pass
    return ImageFont.load_default()


# ---------------------------------------------------------------------------- loading

class Match:
    def __init__(self, path):
        self.map = None
        self.samples = []            # [(t, {name: row}, vehicles, drones)]
        self.ev = collections.defaultdict(list)
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    o = json.loads(line)
                except json.JSONDecodeError:
                    continue  # the last line of a run that was cut off
                k = o["k"]
                if k == "map":
                    self.map = o
                elif k == "s":
                    rows = {}
                    for r in o["b"]:
                        rows[r[0]] = {"team": r[1], "squad": r[2], "role": r[3], "x": r[4], "z": r[5], "y": r[6],
                                      "state": r[7], "stance": r[8], "down": r[9], "ride": r[10], "target": r[11],
                                      "seen": r[12], "supp": r[13], "hp": r[14], "ammo": r[15], "note": r[16] if len(r) > 16 else ""}
                    self.samples.append((o["t"], rows, o.get("v", []), o.get("d", [])))
                else:
                    self.ev[k].append(o)
        self.times = [s[0] for s in self.samples]
        self.end = self.times[-1] if self.times else 0.0
        self.team_of = {}
        for _, rows, _, _ in self.samples:
            for n, r in rows.items():
                self.team_of[n] = r["team"]
        self._ground()

    def sample_at(self, t):
        i = bisect.bisect_left(self.times, t)
        return self.samples[min(i, len(self.samples) - 1)] if self.samples else None

    def samples_between(self, t0, t1):
        i0 = bisect.bisect_left(self.times, t0)
        i1 = bisect.bisect_right(self.times, t1)
        return self.samples[i0:i1]

    def _ground(self):
        m = self.map
        hm = m["hm"]
        n = hm["n"]
        q = np.frombuffer(base64.b64decode(hm["data"]), dtype="<u2").astype(np.float32).reshape(n, n)
        self.h = hm["lo"] + q / 65535.0 * (hm["hi"] - hm["lo"])
        self.h_step = hm["step"]
        self.h_origin = hm["origin"]
        # Hill shading: light from the north-west, a little exaggerated so gentle rises show.
        gz, gx = np.gradient(self.h * 1.6, self.h_step)
        nx, ny, nz = -gx, np.ones_like(gx), -gz
        norm = np.sqrt(nx * nx + ny * ny + nz * nz)
        light = np.array([-0.5, 0.75, -0.45])
        light /= np.linalg.norm(light)
        self.shade = np.clip((nx * light[0] + ny * light[1] + nz * light[2]) / norm, 0, 1)
        b = np.frombuffer(base64.b64decode(m["buildings"]["data"]), dtype="<i2").reshape(-1, 2)
        self.bcell = m["buildings"]["cell"]
        self.buildings = b.astype(np.int32)
        tr = np.frombuffer(base64.b64decode(m["trees"]), dtype="<i2").reshape(-1, 2)
        self.trees = tr.astype(np.float32)
        self.sites = m["sites"]
        self.bases = m["bases"]


# ---------------------------------------------------------------------------- engagements

class Engagement:
    def __init__(self, idx, shots):
        self.id = idx
        self.shots = shots
        self.t0 = shots[0]["t"]
        self.t1 = shots[-1]["t"]
        pts = [(s["p"][0], s["p"][2]) for s in shots if not indirect(s)] + [(s["a"][0], s["a"][2]) for s in shots]
        xs = [p[0] for p in pts]
        zs = [p[1] for p in pts]
        self.box = (min(xs), min(zs), max(xs), max(zs))
        self.footprint = pts

    @property
    def duration(self):
        return self.t1 - self.t0


def indirect(s):
    return "mortar" in s.get("m", "") or s.get("w", "").startswith("81mm")


def find_engagements(match, min_shots):
    shots = sorted((s for s in match.ev["shot"] if s["team"] >= 0), key=lambda s: s["t"])
    parent = list(range(len(shots)))

    def root(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    cell = LINK_DIST
    grid = collections.defaultdict(list)   # cell -> [(shot index, x, z)] for shots still in the time window
    window = collections.deque()
    for i, s in enumerate(shots):
        while window and s["t"] - shots[window[0][0]]["t"] > LINK_TIME:
            j, key = window.popleft()
            grid[key] = [e for e in grid[key] if e[0] != j]
        # Where this shot links: where it went; and where it came from, unless it was a long way off
        # (a mortar at its base, a tank 1.5 km back) or else every mission would chain into one.
        mine = [(s["a"][0], s["a"][2])]
        rng = math.dist((s["p"][0], s["p"][2]), mine[0])
        if not indirect(s) and rng < 500:
            mine.append((s["p"][0], s["p"][2]))
        for (x, z) in mine:
            cx, cz = int(math.floor(x / cell)), int(math.floor(z / cell))
            for dx in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    for (j, jx, jz) in grid.get((cx + dx, cz + dz), ()):
                        if (jx - x) ** 2 + (jz - z) ** 2 < LINK_DIST ** 2:
                            parent[root(i)] = root(j)
        for (x, z) in mine:
            key = (int(math.floor(x / cell)), int(math.floor(z / cell)))
            grid[key].append((i, x, z))
            window.append((i, key))
    groups = collections.defaultdict(list)
    for i, s in enumerate(shots):
        groups[root(i)].append(s)
    engs = [g for g in groups.values() if len(g) >= min_shots]
    engs.sort(key=lambda g: g[0]["t"])
    return [Engagement(k + 1, g) for k, g in enumerate(engs)]


def squad_episodes(match, min_rounds, quiet=30.0):
    """
    One squad's fight, start to finish: from the first round it fires or has fired at it (or a man
    hit) until 30 s pass with neither. Crews and aircraft are left out (their fights are the vehicle's).
    """
    squad_of = {}
    kind = {sq[0]: sq[1] for sq in match.map.get("squads", [])}
    for _, rows, _, _ in match.samples:
        for n, r in rows.items():
            if r["squad"]:
                squad_of[n] = r["squad"]
    touch = collections.defaultdict(list)   # squad -> [(t, shot)]
    for sh in match.ev["shot"]:
        a = squad_of.get(sh["s"])
        b = squad_of.get(sh["at"]) if sh["at"] else None
        if a:
            touch[a].append((sh["t"], sh))
        if b and b != a:
            touch[b].append((sh["t"], sh))
    for h in match.ev["hit"]:
        v = squad_of.get(h["v"])
        if v:
            touch[v].append((h["t"], None))
    eps = []
    for sq, evs in touch.items():
        if kind.get(sq) in ("Armor", "Transport", "Air", "Mortar"):
            continue
        evs.sort(key=lambda x: x[0])
        cur = []
        for t, sh in evs + [(1e18, None)]:
            if cur and t - cur[-1][0] > quiet:
                shots = [x[1] for x in cur if x[1] is not None]
                if len(shots) >= min_rounds:
                    e = Engagement(0, shots)
                    e.squad = sq
                    e.kind = kind.get(sq, "?")
                    e.t0, e.t1 = cur[0][0], cur[-1][0]
                    eps.append(e)
                cur = []
            cur.append((t, sh))
    eps.sort(key=lambda e: e.t0)
    for k, e in enumerate(eps):
        e.id = k + 1
    return eps, squad_of


def analyse(match, e):
    """Everything measured about one engagement, as a dict (and the participants' tracks)."""
    x0, z0, x1, z1 = e.box
    pad = 60.0
    in_box = lambda x, z: x0 - pad <= x <= x1 + pad and z0 - pad <= z <= z1 + pad
    shooters = collections.Counter(s["s"] for s in e.shots)
    names = set(shooters) | {s["at"] for s in e.shots if s["at"]}
    if getattr(e, "squad", None):
        for t, rows, _, _ in match.samples_between(e.t0, e.t1):
            for n, r in rows.items():
                if r["squad"] == e.squad:
                    names.add(n)
    for k in ("hit", "down", "kill"):
        for h in match.ev[k]:
            if e.t0 - 1 <= h["t"] <= e.t1 + 5 and in_box(h["p"][0], h["p"][2]):
                names.add(h["v"])
    # Anyone who spent time in the thick of it, even without firing (pinned, or holding their fire).
    fp = np.array(e.footprint[:: max(1, len(e.footprint) // 400)], dtype=np.float32)
    for t, rows, _, _ in match.samples_between(e.t0, e.t1):
        for n, r in rows.items():
            if n in names or r["ride"]:
                continue
            if in_box(r["x"], r["z"]) and np.min((fp[:, 0] - r["x"]) ** 2 + (fp[:, 1] - r["z"]) ** 2) < 40 ** 2:
                names.add(n)
    names.discard("")
    per = {}
    tracks = {}
    for n in names:
        track = [(t, rows[n]) for t, rows, _, _ in match.samples_between(e.t0 - 30, e.t1 + 10) if n in rows]
        tracks[n] = track
        inside = [(t, r) for t, r in track if e.t0 <= t <= e.t1]
        path = static = longest = run = 0.0
        moving_time = 0.0
        states = collections.Counter()
        stances = collections.Counter()
        supp = []
        for (ta, ra), (tb, rb) in zip(inside, inside[1:]):
            dt = tb - ta
            if dt <= 0 or dt > 2:
                continue
            d = math.hypot(rb["x"] - ra["x"], rb["z"] - ra["z"])
            if ra["ride"] or ra["down"]:
                continue
            path += d
            if d / dt < MOVE_SPEED:
                static += dt
                run += dt
                longest = max(longest, run)
            else:
                moving_time += dt
                run = 0.0
            states[ra["state"]] += dt
            stances[ra["stance"]] += dt
            supp.append(ra["supp"])
        first = inside[0][1] if inside else None
        last = inside[-1][1] if inside else None
        stance_changes = sum(1 for (_, a), (_, b) in zip(inside, inside[1:]) if a["stance"] != b["stance"] and not a["down"] and not b["down"])
        present = (inside[-1][0] - inside[0][0]) if len(inside) > 1 else 0.0
        per[n] = {
            "team": match.team_of.get(n, -1),
            "role": first["role"] if first else "?",
            "squad": first["squad"] if first else "",
            "shots": shooters.get(n, 0),
            "path": path,
            "net": math.hypot(last["x"] - first["x"], last["z"] - first["z"]) if first else 0.0,
            "static": static,
            "moving": moving_time,
            "longest_static": longest,
            "states": states,
            "stances": stances,
            "supp": sum(supp) / len(supp) if supp else 0.0,
            "present": present,
            "stance_changes": stance_changes,
            "state_changes": 0,
        }
    for sc in match.ev["st"]:
        if e.t0 <= sc["t"] <= e.t1 and sc["s"] in per:
            per[sc["s"]]["state_changes"] += 1
    hits = [h for h in match.ev["hit"] if e.t0 - 1 <= h["t"] <= e.t1 + 2 and h["v"] in names]
    downs = [h for h in match.ev["down"] if e.t0 - 1 <= h["t"] <= e.t1 + 5 and h["v"] in names]
    kills = [h for h in match.ev["kill"] if e.t0 - 1 <= h["t"] <= e.t1 + 5 and h["v"] in names]
    booms = [b for b in match.ev["boom"] if e.t0 - 1 <= b["t"] <= e.t1 + 1 and in_box(b["p"][0], b["p"][2])]
    drills = [d for d in match.ev["drill"] if e.t0 - 10 <= d["t"] <= e.t1 and in_box(d["p"][0], d["p"][2])]
    times = [s["t"] for s in e.shots]
    gaps = [b - a for a, b in zip(times, times[1:]) if b - a >= LULL]
    ranges = sorted(math.dist(s["p"], s["a"]) for s in e.shots if not indirect(s))
    teams = sorted({per[n]["team"] for n in per if per[n]["team"] >= 0})
    # Standoffs: stretches of 30 s or more with fire going on, but hardly anyone moving and nobody hit.
    standoffs = []
    bins = np.arange(e.t0, e.t1 + 5, 5.0)
    if len(bins) > 1:
        fire = np.histogram(times, bins)[0]
        moving = np.zeros(len(bins) - 1)
        present = np.zeros(len(bins) - 1)
        for n, track in tracks.items():
            for (ta, ra), (tb, rb) in zip(track, track[1:]):
                if not (e.t0 <= ta < e.t1) or ra["ride"] or ra["down"]:
                    continue
                k = min(int((ta - e.t0) // 5), len(moving) - 1)
                present[k] += 1
                if math.hypot(rb["x"] - ra["x"], rb["z"] - ra["z"]) / max(tb - ta, 1e-3) >= MOVE_SPEED:
                    moving[k] += 1
        hit_t = np.histogram([h["t"] for h in hits], bins)[0]
        frac = np.divide(moving, present, out=np.zeros_like(moving), where=present > 0)
        quiet = (fire > 0) & (frac < 0.15) & (hit_t == 0)
        start = None
        for k, q in enumerate(list(quiet) + [False]):
            if q and start is None:
                start = k
            elif not q and start is not None:
                if (k - start) * 5 >= 30:
                    standoffs.append((bins[start], bins[k] if k < len(bins) else e.t1))
                start = None
        e.fire_bins = (bins, fire, frac, present)
    else:
        e.fire_bins = None
    # How far apart the sides are: for everyone in it, the nearest enemy who's also in it; the median, every 5 s.
    gap = []
    for t, rows, _, _ in match.samples_between(e.t0, e.t1)[::10]:
        pos = [(r["team"], r["x"], r["z"]) for n, r in rows.items() if n in names and not r["ride"] and not r["down"]]
        near = []
        for team, x, z in pos:
            d = [math.hypot(x - x2, z - z2) for t2, x2, z2 in pos if t2 != team]
            if d:
                near.append(min(d))
        if near:
            near.sort()
            gap.append((t, near[len(near) // 2]))
    e.gap = gap
    out = {
        "teams": teams,
        "participants": len(per),
        "shots_by_team": {t: sum(1 for s in e.shots if s["team"] == t) for t in teams},
        "hits_on_team": collections.Counter(h["vt"] for h in hits),
        "downs_on_team": collections.Counter(h["vt"] for h in downs),
        "kills_on_team": collections.Counter(h["vt"] for h in kills),
        "range_med": ranges[len(ranges) // 2] if ranges else 0.0,
        "lulls": gaps,
        "first_casualty": min([h["t"] for h in downs + kills], default=None),
        "booms": len(booms),
        "drills": drills,
        "standoffs": standoffs,
        "gap_start": gap[0][1] if gap else None,
        "gap_end": gap[-1][1] if gap else None,
        "gap_min": min((g for _, g in gap), default=None),
        "per": per,
    }
    e.stats = out
    e.tracks = tracks
    e.hits, e.downs, e.kills, e.booms = hits, downs, kills, booms
    return out


# ---------------------------------------------------------------------------- drawing

def bilinear(grid, fx, fz):
    """Sample a grid at fractional (column, row) positions: fx along columns (x), fz along rows (z)."""
    n = grid.shape[0]
    fx = np.clip(fx, 0, n - 1.001)
    fz = np.clip(fz, 0, n - 1.001)
    i = fx.astype(int)
    j = fz.astype(int)
    tx = (fx - i)[None, :]
    tz = (fz - j)[:, None]
    a = grid[np.ix_(j, i)]
    b = grid[np.ix_(j, i + 1)]
    c = grid[np.ix_(j + 1, i)]
    d = grid[np.ix_(j + 1, i + 1)]
    return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz


class View:
    """A square of the map drawn into a square of pixels (north, -Z, up)."""

    def __init__(self, match, cx, cz, size, px):
        self.m = match
        self.x0, self.z0 = cx - size / 2, cz - size / 2
        self.size, self.px = size, px
        self.k = px / size

    def p(self, x, z):
        return ((x - self.x0) * self.k, (z - self.z0) * self.k)

    def ground(self):
        m, px = self.m, self.px
        xs = self.x0 + (np.arange(px) + 0.5) / self.k
        zs = self.z0 + (np.arange(px) + 0.5) / self.k
        shade = bilinear(m.shade, (xs - m.h_origin) / m.h_step, (zs - m.h_origin) / m.h_step)
        hgt = bilinear(m.h, (xs - m.h_origin) / m.h_step, (zs - m.h_origin) / m.h_step)
        base = np.array([150, 160, 120], dtype=np.float32) if m.map["id"] != "alhamra" else np.array([200, 180, 140], dtype=np.float32)
        img = (base[None, None, :] * (0.55 + 0.6 * shade[:, :, None])).clip(0, 255)
        # Contours every 5 m.
        c = np.floor(hgt / 5.0)
        edge = (np.abs(np.diff(c, axis=0, prepend=c[:1])) + np.abs(np.diff(c, axis=1, prepend=c[:, :1]))) > 0
        img[edge] *= 0.82
        im = Image.fromarray(img.astype(np.uint8), "RGB")
        d = ImageDraw.Draw(im, "RGBA")
        cell = m.bcell * self.k
        b = m.buildings
        bx = (b[:, 0] * m.bcell - self.x0) * self.k
        bz = (b[:, 1] * m.bcell - self.z0) * self.k
        keep = (bx > -cell) & (bx < px) & (bz > -cell) & (bz < px)
        for x, z in zip(bx[keep], bz[keep]):
            d.rectangle([x, z, x + max(cell, 1), z + max(cell, 1)], fill=(70, 66, 60, 255))
        tx = (m.trees[:, 0] - self.x0) * self.k
        tz = (m.trees[:, 1] - self.z0) * self.k
        keep = (tx >= 0) & (tx < px) & (tz >= 0) & (tz < px)
        r = max(1.0, 2.2 * self.k)
        for x, z in zip(tx[keep], tz[keep]):
            d.ellipse([x - r, z - r, x + r, z + r], fill=(40, 90, 40, 150))
        f = font(max(11, int(px / 55)), True)
        for s in m.sites:
            x, z = self.p(s[1], s[2])
            rr = s[3] * self.k
            d.ellipse([x - rr, z - rr, x + rr, z + rr], outline=(255, 255, 255, 170), width=2)
            d.text((x - rr, z - rr - f.size - 2), s[0], font=f, fill=(255, 255, 255, 230), stroke_width=2, stroke_fill=(0, 0, 0, 200))
        for t, (bx_, bz_) in enumerate(m.bases):
            x, z = self.p(bx_, bz_)
            d.rectangle([x - 8, z - 8, x + 8, z + 8], outline=TEAM_COL[t] + (255,), width=3)
        return im

    def scale_bar(self, d):
        f = font(12)
        metres = 50 if self.size < 400 else 100 if self.size < 900 else 250
        w = metres * self.k
        y = self.px - 18
        d.line([12, y, 12 + w, y], fill=(0, 0, 0, 255), width=4)
        d.line([12, y, 12 + w, y], fill=(255, 255, 255, 255), width=2)
        d.text((16 + w, y - 8), f"{metres} m", font=f, fill=(255, 255, 255, 255), stroke_width=2, stroke_fill=(0, 0, 0, 255))


def frame(match, e, view, bg, ta, tb, label):
    """One storyboard frame: where everyone is at tb, their movement and fire since ta."""
    im = bg.copy()
    d = ImageDraw.Draw(im, "RGBA")
    # Fire in the window: faint lines from shooter to where it went.
    for s in e.shots:
        if ta <= s["t"] <= tb:
            a = view.p(s["p"][0], s["p"][2])
            b = view.p(s["a"][0], s["a"][2])
            col = TEAM_COL[s["team"]] if 0 <= s["team"] < 3 else (255, 255, 255)
            d.line([a, b], fill=col + (45 if not indirect(s) else 90,), width=1)
    for bm in e.booms:
        if ta <= bm["t"] <= tb:
            x, z = view.p(bm["p"][0], bm["p"][2])
            r = max(4.0, bm["fr"] * view.k)
            d.ellipse([x - r, z - r, x + r, z + r], outline=(255, 140, 0, 200), width=2)
    # Everyone's trail over the window, then where they are.
    f = font(11)
    for n, track in e.tracks.items():
        pts = [(t, r) for t, r in track if ta <= t <= tb]
        if not pts:
            continue
        team = match.team_of.get(n, 0)
        col = TEAM_COL[team] if 0 <= team < 3 else (255, 255, 255)
        xy = [view.p(r["x"], r["z"]) for _, r in pts]
        if len(xy) > 1:
            d.line(xy, fill=col + (150,), width=2)
        t, r = pts[-1]
        x, z = xy[-1]
        if tb - t > 1.5:
            continue  # gone (dead) before the end of the window
        if r["down"]:
            d.polygon([(x, z - 6), (x - 5, z + 4), (x + 5, z + 4)], fill=(120, 120, 120, 255), outline=(0, 0, 0, 255))
        else:
            rad = 4 if r["stance"] == "P" else 5
            fill = col + (255,)
            d.ellipse([x - rad, z - rad, x + rad, z + rad], fill=fill, outline=(0, 0, 0, 255))
            if r["stance"] == "P":
                d.line([x - 8, z, x + 8, z], fill=(0, 0, 0, 255), width=2)  # lying down
        if r["supp"] > 0.5 and not r["down"]:
            d.ellipse([x - 9, z - 9, x + 9, z + 9], outline=(255, 255, 255, 160), width=1)
    for h in e.downs + e.kills:
        if ta <= h["t"] <= tb:
            x, z = view.p(h["p"][0], h["p"][2])
            col = TEAM_COL[h["vt"]]
            if h in e.kills:
                d.line([x - 7, z - 7, x + 7, z + 7], fill=(0, 0, 0, 255), width=5)
                d.line([x - 7, z + 7, x + 7, z - 7], fill=(0, 0, 0, 255), width=5)
                d.line([x - 7, z - 7, x + 7, z + 7], fill=col + (255,), width=3)
                d.line([x - 7, z + 7, x + 7, z - 7], fill=col + (255,), width=3)
            else:
                d.ellipse([x - 7, z - 7, x + 7, z + 7], outline=col + (255,), width=3)
    for dr in e.stats["drills"]:
        if ta - 10 <= dr["t"] <= tb:
            x, z = view.p(dr["p"][0], dr["p"][2])
            d.text((x + 6, z - 6), dr["d"], font=f, fill=TEAM_COL[dr["team"]] + (255,), stroke_width=2, stroke_fill=(0, 0, 0, 255))
    fb = font(15, True)
    d.rectangle([0, 0, view.px, 22], fill=(0, 0, 0, 150))
    d.text((6, 3), label, font=fb, fill=(255, 255, 255, 255))
    view.scale_bar(d)
    return im


def timeline(match, e, width, height):
    im = Image.new("RGB", (width, height), (24, 24, 28))
    d = ImageDraw.Draw(im, "RGBA")
    f = font(12)
    left, right, top, bottom = 60, width - 20, 28, height - 26
    span = max(e.t1 - e.t0, 1.0)
    tx = lambda t: left + (t - e.t0) / span * (right - left)
    d.text((8, 6), "fire (rounds per 5 s, by side) · casualties (circle down, X dead) · share of the people in it moving (line)", font=f, fill=(210, 210, 210))
    bins = np.arange(e.t0, e.t1 + 5, 5.0)
    if len(bins) > 1:
        counts = {t: np.histogram([s["t"] for s in e.shots if s["team"] == t], bins)[0] for t in range(3)}
        peak = max(1, max(c.max() for c in counts.values()))
        for k in range(len(bins) - 1):
            base = bottom
            for t in range(3):
                h = counts[t][k] / peak * (bottom - top) * 0.9
                if h > 0:
                    d.rectangle([tx(bins[k]) + 1, base - h, tx(bins[k + 1]) - 1, base], fill=TEAM_COL[t] + (200,))
                    base -= h
        if e.fire_bins is not None:
            _, _, frac, present = e.fire_bins
            pts = [(tx(bins[k] + 2.5), bottom - frac[k] * (bottom - top)) for k in range(len(frac)) if present[k] > 0]
            if len(pts) > 1:
                d.line(pts, fill=(255, 255, 255, 220), width=2)
        d.text((left - 52, top - 2), f"{peak}", font=f, fill=(180, 180, 180))
    gap = getattr(e, "gap", [])
    if len(gap) > 1:
        gmax = max(g for _, g in gap) * 1.1
        pts = [(tx(t), bottom - g / gmax * (bottom - top)) for t, g in gap]
        d.line(pts, fill=(120, 255, 140, 230), width=2)
        d.text((right - 150, top - 2), f"sides apart (green) 0-{gmax:.0f} m", font=f, fill=(120, 255, 140))
    for a, b in e.stats["standoffs"]:
        d.rectangle([tx(a), top, tx(b), bottom], outline=(255, 80, 200, 255), width=2)
        d.text((tx(a) + 3, top + 2), "standoff", font=f, fill=(255, 120, 220))
    for h in e.downs:
        x = tx(h["t"])
        d.ellipse([x - 5, bottom - 5, x + 5, bottom + 5], outline=TEAM_COL[h["vt"]] + (255,), width=2)
    for h in e.kills:
        x = tx(h["t"])
        d.line([x - 5, bottom - 5, x + 5, bottom + 5], fill=TEAM_COL[h["vt"]] + (255,), width=3)
        d.line([x - 5, bottom + 5, x + 5, bottom - 5], fill=TEAM_COL[h["vt"]] + (255,), width=3)
    step = 10 if span < 120 else 30 if span < 400 else 60
    t = math.ceil(e.t0 / step) * step
    while t <= e.t1:
        d.line([tx(t), bottom, tx(t), bottom + 4], fill=(160, 160, 160))
        d.text((tx(t) - 12, bottom + 6), f"{t:.0f}s", font=f, fill=(160, 160, 160))
        t += step
    return im


def storyboard(match, e, out_dir, frames=6, panel=620):
    x0, z0, x1, z1 = e.box
    cx, cz = (x0 + x1) / 2, (z0 + z1) / 2
    size = max(x1 - x0, z1 - z0) + 160
    size = min(max(size, 220), 1600)
    view = View(match, cx, cz, size, panel)
    bg = view.ground()
    cols = 3
    rows = math.ceil(frames / cols)
    head = 64
    tl_h = 170
    sheet = Image.new("RGB", (cols * panel, head + rows * panel + tl_h), (16, 16, 18))
    span = max(e.t1 - e.t0, 1.0)
    window = max(span / frames, 12.0)
    for k in range(frames):
        tb = e.t0 + span * (k + 1) / frames
        ta = max(e.t0 - 5, tb - window)
        im = frame(match, e, view, bg, ta, tb, f"{ta:.0f}-{tb:.0f} s")
        sheet.paste(im, ((k % cols) * panel, head + (k // cols) * panel))
    sheet.paste(timeline(match, e, cols * panel, tl_h), (0, head + rows * panel))
    d = ImageDraw.Draw(sheet)
    st = e.stats
    teams = " vs ".join(TEAM_NAME[t] for t in st["teams"])
    cas = ", ".join(f"{TEAM_NAME[t]} {st['downs_on_team'].get(t, 0)} down/{st['kills_on_team'].get(t, 0)} dead" for t in st["teams"])
    who = f"{e.squad} ({e.kind}) in contact" if getattr(e, "squad", None) else teams
    d.text((10, 6), f"#{e.id}: {who} · {teams} · {e.t0:.0f}-{e.t1:.0f} s ({e.duration:.0f} s) · {st['participants']} people · median range {st['range_med']:.0f} m",
           font=font(17, True), fill=(255, 255, 255))
    shots = ", ".join(f"{TEAM_NAME[t]} {n}" for t, n in st["shots_by_team"].items())
    d.text((10, 32), f"rounds: {shots} · hits taken: {dict((TEAM_NAME[t], n) for t, n in st['hits_on_team'].items())} · {cas} · lulls {len(st['lulls'])} ({sum(st['lulls']):.0f} s)"
           f"{' · STANDOFF ' + str(len(st['standoffs'])) if st['standoffs'] else ''}", font=font(13), fill=(210, 210, 210))
    path = os.path.join(out_dir, f"engagement_{e.id:03d}.png")
    sheet.save(path)
    return path


def overview(match, engs, out_dir, px=1400):
    size = match.map["size"]
    view = View(match, 0.0, 0.0, size, px)
    im = view.ground()
    d = ImageDraw.Draw(im, "RGBA")
    f = font(13, True)
    for e in engs:
        x0, z0, x1, z1 = e.box
        a = view.p(x0, z0)
        b = view.p(x1, z1)
        teams = e.stats["teams"]
        col = TEAM_COL[teams[0]] if teams else (255, 255, 255)
        d.rectangle([a, b], outline=(255, 255, 255, 200), width=2)
        d.text((a[0] + 3, a[1] + 2), f"{e.id} ({e.duration:.0f}s)", font=f, fill=(255, 255, 255, 255), stroke_width=2, stroke_fill=(0, 0, 0, 255))
    for h in match.ev["kill"]:
        x, z = view.p(h["p"][0], h["p"][2])
        col = TEAM_COL[h["vt"]] if 0 <= h["vt"] < 3 else (255, 255, 255)
        d.line([x - 4, z - 4, x + 4, z + 4], fill=col + (255,), width=2)
        d.line([x - 4, z + 4, x + 4, z - 4], fill=col + (255,), width=2)
    view.scale_bar(d)
    path = os.path.join(out_dir, "overview.png")
    im.save(path)
    return path


# ---------------------------------------------------------------------------- the report

def report(match, engs, out_dir):
    lines = []
    w = lines.append
    w(f"{match.map['name']} · {match.end:.0f} s of match · {len(match.ev['shot'])} rounds fired · {len(match.ev['kill'])} dead · {len(match.ev['down'])} downed")
    w(f"{len(engs)} fights (>= {ARGS.min_shots} rounds; " + ("one squad's contact until 30 s of quiet)" if ARGS.by == "squad" else f"shots linked within {LINK_TIME:.0f} s and {LINK_DIST:.0f} m)"))
    if engs:
        durs = sorted(e.duration for e in engs)
        w(f"durations: median {durs[len(durs) // 2]:.0f} s, shortest {durs[0]:.0f} s, longest {durs[-1]:.0f} s")
    w("")
    w(f"{'id':>3} {'squad':>11} {'time':>11} {'dur':>5} {'ppl':>4} {'range':>6} {'rounds':>18} {'down/dead by side':>26} {'1st cas':>8} {'lulls':>10} {'moving':>7} {'static':>7} {'longest still':>13} {'prone':>6} {'supp':>5} {'standoff':>9}")
    for e in engs:
        st = e.stats
        per = [p for p in st["per"].values()]
        mv = sum(p["moving"] for p in per)
        sta = sum(p["static"] for p in per)
        tot = max(mv + sta, 1e-6)
        longest = max((p["longest_static"] for p in per), default=0.0)
        prone = sum(p["stances"].get("P", 0) for p in per) / max(sum(sum(p["stances"].values()) for p in per), 1e-6)
        supp = sum(p["supp"] for p in per) / max(len(per), 1)
        rounds = "/".join(f"{st['shots_by_team'].get(t, 0)}" for t in st["teams"])
        cas = "/".join(f"{st['downs_on_team'].get(t, 0)}+{st['kills_on_team'].get(t, 0)}" for t in st["teams"])
        first = f"{st['first_casualty'] - e.t0:.0f}s" if st["first_casualty"] is not None else "-"
        w(f"{e.id:>3} {getattr(e, 'squad', '-'):>11} {e.t0:>5.0f}-{e.t1:<5.0f} {e.duration:>5.0f} {st['participants']:>4} {st['range_med']:>5.0f}m {rounds:>18} {cas:>26} {first:>8} "
          f"{len(st['lulls']):>3} ({sum(st['lulls']):>3.0f}s) {mv / tot:>6.0%} {sta / tot:>6.0%} {longest:>11.0f}s {prone:>6.0%} {supp:>5.2f} {len(st['standoffs']):>9}")
    w("")
    w("closing, firing and dithering (per engagement):")
    w(f"{'id':>3} {'apart start->end (closest)':>28} {'fired':>6} {'rds/person-min':>15} {'state chg/min':>14} {'stance chg/min':>15}")
    for e in engs:
        st = e.stats
        per = list(st["per"].values())
        mins = sum(p["present"] for p in per) / 60.0
        fired = sum(1 for p in per if p["shots"] > 0) / max(len(per), 1)
        apart = f"{st['gap_start']:.0f}->{st['gap_end']:.0f} ({st['gap_min']:.0f}) m" if st["gap_start"] is not None else "-"
        w(f"{e.id:>3} {apart:>28} {fired:>6.0%} {sum(p['shots'] for p in per) / max(mins, 1e-6):>15.1f} "
          f"{sum(p['state_changes'] for p in per) / max(mins, 1e-6):>14.1f} {sum(p['stance_changes'] for p in per) / max(mins, 1e-6):>15.1f}")
    w("")
    w("how the longest engagements went, 30 s at a time (rounds by side · casualties · moving · sides apart · drills):")
    for e in sorted(engs, key=lambda e: -e.duration)[:6]:
        w(f"  #{e.id} ({e.duration:.0f} s, {' vs '.join(TEAM_NAME[t] for t in e.stats['teams'])})")
        t = e.t0
        while t < e.t1:
            u = t + 30
            rds = collections.Counter(s["team"] for s in e.shots if t <= s["t"] < u)
            cas = sum(1 for h in e.downs + e.kills if t <= h["t"] < u)
            dr = [d["d"] for d in e.stats["drills"] if t <= d["t"] < u]
            mv = []
            if e.fire_bins is not None:
                bins, _, frac, present = e.fire_bins
                mv = [frac[k] for k in range(len(frac)) if t <= bins[k] < u and present[k] > 0]
            gp = [g for tt, g in getattr(e, "gap", []) if t <= tt < u]
            w(f"    {t:>5.0f}s  " + " ".join(f"{TEAM_NAME[k][0]}{rds.get(k, 0):>3}" for k in e.stats["teams"])
              + f"  cas {cas}  moving {sum(mv) / len(mv) if mv else 0:>4.0%}  apart {sum(gp) / len(gp) if gp else 0:>4.0f} m  {' '.join(dr)}")
            t = u
    w("")
    # What people in fights spend their time doing, across all of them.
    states = collections.Counter()
    for e in engs:
        for p in e.stats["per"].values():
            states.update(p["states"])
    total = sum(states.values()) or 1
    w("time in fights by state: " + ", ".join(f"{s} {states[s] / total:.0%}" for s, _ in states.most_common()))
    # Who sat still longest while a fight went on around them. Recon (an observation post) and anti-tank teams
    # lying in wait are meant to: they're listed apart, so what's left is people who should have been doing something.
    kind = {sq[0]: sq[1] for sq in match.map["squads"]}
    still, posts = [], []
    for e in engs:
        for n, p in e.stats["per"].items():
            row = (p["longest_static"], n, e.id, p["role"], p["squad"], p["shots"], max(p["states"], key=p["states"].get) if p["states"] else "-")
            (posts if kind.get(p["squad"]) in ("Recon", "AntiTank") else still).append(row)
    still.sort(reverse=True)
    posts.sort(reverse=True)
    w("")
    w("longest stretches without moving during a fight (s, who, engagement, role, squad, rounds they fired, their main state):")
    seen = set()
    for row in still:
        if row[1] in seen:
            continue  # once each: the same man sitting through several overlapping fights is one stretch
        seen.add(row[1])
        w(f"  {row[0]:>5.0f}  {row[1]:<8} #{row[2]:<3} {row[3]:<4} {row[4]:<10} {row[5]:>4} rds  {row[6]}")
        if len(seen) == 15:
            break
    if posts:
        best = {}
        for r in posts:
            best.setdefault(r[1], r)
        w("  (recon posts and anti-tank teams lying in wait, still as they should be: " + ", ".join(
            f"{r[1]} {r[0]:.0f}s" for r in list(best.values())[:6]) + ")")
    # State flapping: how often people changed state per minute of fight (indecision shows here).
    changes = collections.Counter()
    for s in match.ev["st"]:
        changes[(s["from"], s["to"])] += 1
    w("")
    w("most common state changes over the whole match: " + ", ".join(f"{a}->{b} {n}" for (a, b), n in changes.most_common(12)))
    path = os.path.join(out_dir, "summary.txt")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    return path, lines


def main():
    global ARGS
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--out", default=None)
    ap.add_argument("--min-shots", type=int, default=12)
    ap.add_argument("--frames", type=int, default=6)
    ap.add_argument("--max-boards", type=int, default=40)
    ap.add_argument("--by", choices=["squad", "area"], default="squad", help="a fight is one squad's contact (default), or shots chained by place and time")
    ARGS = ap.parse_args()
    out = ARGS.out or os.path.splitext(ARGS.file)[0] + "_fights"
    os.makedirs(out, exist_ok=True)
    match = Match(ARGS.file)
    if ARGS.by == "squad":
        engs, _ = squad_episodes(match, ARGS.min_shots)
    else:
        engs = find_engagements(match, ARGS.min_shots)
    for e in engs:
        analyse(match, e)
    path, lines = report(match, engs, out)
    print("\n".join(lines))
    overview(match, engs, out)
    for e in sorted(engs, key=lambda e: -len(e.shots))[: ARGS.max_boards]:
        storyboard(match, e, out, frames=ARGS.frames)
    print(f"\nwrote {out}")


if __name__ == "__main__":
    main()
