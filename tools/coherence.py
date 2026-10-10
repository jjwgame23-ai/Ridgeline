"""Coherence report for a recorded fight: what a watching player would notice. Usage: coherence.py run.jsonl [...]"""
import json, sys, collections, math

def lines(p):
    for l in open(p, encoding="utf-8"):
        try:
            yield json.loads(l)
        except Exception:
            pass

def pct(v, q):
    if not v: return float("nan")
    v = sorted(v); return v[min(len(v) - 1, int(len(v) * q))]

for path in sys.argv[1:]:
    prev = {}
    speed_quiet, speed_hot = [], []
    stance_fire = collections.Counter(); stance_all = collections.Counter()
    shots_by_team = collections.Counter(); shot_ranges = collections.defaultdict(list)
    first_shot = None; first_kill = None; last_kill = 0.0
    kills = collections.Counter(); people = {}
    last_shot_near = {}  # name -> time a shot was fired by or near them (contact)
    for e in lines(path):
        k = e.get("k")
        if k == "shot":
            t = e["t"]
            if first_shot is None: first_shot = t
            team = e.get("team", -1)
            shots_by_team[team] += 1
            p, a = e.get("p"), e.get("a")
            if p and a:
                shot_ranges[team].append(math.dist((p[0], p[2]), (a[0], a[2])))
            last_shot_near[e.get("s")] = t
        elif k == "kill":
            if first_kill is None: first_kill = e["t"]
            last_kill = e["t"]; kills[e.get("vt")] += 1
        elif k == "s":
            t = e["t"]
            for b in e["b"]:
                name, team = b[0], b[1]
                people[name] = team
                if b[9] or b[10]: continue  # down, or riding
                x, z = b[4], b[5]
                hot = t - last_shot_near.get(name, -99) < 20 or (first_shot is not None and t - first_shot < 600 and b[13] > 0.3)
                stance_all[b[8]] += 1
                if b[13] > 0.5: stance_fire[b[8]] += 1
                if name in prev:
                    t0, x0, z0 = prev[name]
                    dt = t - t0
                    if 0.3 < dt < 1.5:
                        v = math.dist((x, z), (x0, z0)) / dt
                        if v > 0.3: (speed_hot if hot else speed_quiet).append(v)
                prev[name] = (t, x, z)
    n_fire = max(1, sum(stance_fire.values())); n_all = max(1, sum(stance_all.values()))
    print(f"== {path.split('/')[-1]}")
    print(f"  first shot {first_shot if first_shot is not None else '-':>} s, first kill {first_kill if first_kill is not None else '-'} s, last kill {last_kill:.0f} s; killed per side {dict(kills)}")
    for team in sorted(shot_ranges):
        r = shot_ranges[team]
        print(f"  side {team}: {shots_by_team[team]} rounds; aimed at a median {pct(r, .5):.0f} m (quartiles {pct(r, .25):.0f}-{pct(r, .75):.0f}, longest tenth past {pct(r, .9):.0f})")
    print(f"  moving out of contact: median {pct(speed_quiet, .5):.1f} m/s (90th {pct(speed_quiet, .9):.1f}); in contact: median {pct(speed_hot, .5):.1f} m/s")
    print(f"  on their feet, kneeling, flat: all the time {stance_all['S']/n_all:.0%} {stance_all['C']/n_all:.0%} {stance_all['P']/n_all:.0%}; "
          f"under heavy fire (suppressed past half) {stance_fire['S']/n_fire:.0%} {stance_fire['C']/n_fire:.0%} {stance_fire['P']/n_fire:.0%}")
