"""
Check a recorded match against the rulebook (tools/rules.toml): every bot, every half-second sample, and every event.

    python tools/doctrine_check.py run.jsonl [more.jsonl ...] [--top 5] [--rules tools/rules.toml] [--json out.json]

Record a match with the dev arg telemetry=<file> (see CLAUDE.md). For each rule it prints:
- a "never" rule: PASS, or FAIL with every breach;
- a "norm": how often it was broken per 10 bot-minutes in contact, OUT OF BAND if the rule has a band and the rate is
  over it, and the worst episodes, each with the time to look at in the replay (tools/replay.py) and the bot's own
  reason at that moment;
- a rule the recording can't support (made before the fields it needs existed): "not checkable".
Then the patterns, which aren't judged: what the run looks like, to set beside real data (BOT-VERIFICATION.md).

The exit code is 1 if any "never" rule failed. tools/test_doctrine_check.py tests every rule on recordings made up for
it, one that should be flagged and one that shouldn't.
"""
import argparse
import bisect
import collections
import json
import math
import os
import sys
import tomllib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import telemetry as T  # noqa: E402

RULES_PATH = os.path.join(HERE, "rules.toml")


def load_rules(path=RULES_PATH):
    with open(path, "rb") as f:
        rules = tomllib.load(f)["rule"]
    for r in rules:
        r.setdefault("params", {})
        r.setdefault("needs", [])
        r.setdefault("min_seconds", 0.0)
        if r["check"] not in CHECKS:
            raise SystemExit(f"rules: {r['id']} asks for check '{r['check']}', which doctrine_check.py doesn't have")
    return rules


# ---------------------------------------------------------------------------- episodes

class Episode:
    def __init__(self, who, t0, info, t1=None):
        self.who, self.t0, self.t1, self.info = who, t0, t0 if t1 is None else t1, info

    @property
    def dur(self):
        return self.t1 - self.t0

    def as_dict(self):
        return {"who": self.who, "t0": round(self.t0, 2), "dur": round(self.dur, 2), "info": self.info}


class Tracker:
    """Turns a condition tested at every sample into episodes lasting at least `least` seconds."""

    def __init__(self, least):
        self.least = least
        self.open, self.done = {}, []

    def step(self, t, who, cond, info=""):
        if cond:
            if who not in self.open:
                self.open[who] = Episode(who, t, info)
            self.open[who].t1 = t
        elif who in self.open:
            self._end(who)

    def end_absent(self, present):
        """Close the episodes of anyone not looked at this sample (down, dead, gone). (The first version left them open,
        so a man downed while standing under fire and revived a minute later read as 58 s upright under fire.)"""
        for who in [w for w in self.open if w not in present]:
            self._end(who)

    def _end(self, who):
        e = self.open.pop(who)
        if e.dur >= self.least:
            self.done.append(e)

    def close(self):
        for who in list(self.open):
            self._end(who)
        return self.done


# ---------------------------------------------------------------------------- the recording, read once

class Run:
    """A recording with what every rule needs worked out once: speeds, who's indoors, who's in contact, events by kind."""

    def __init__(self, path):
        self.path = path
        self.m = m = T.Match(path)
        built = {(int(a), int(b)) for a, b in m.buildings}
        cell = m.bcell
        self.samples = []
        prev = {}
        self.contact_s = 0.0
        self.fields = set()
        for t, rows, vehs, _ in m.samples:
            derived = {}
            for n, r in rows.items():
                if not self.fields and r.get("gait") is not None:
                    self.fields = {"gait", "ft", "leads", "doing", "seenby", "seenby_armour", "cover", "sid"}
                speed = 0.0
                if n in prev and 0.3 < t - prev[n][0] < 1.5 and not r["ride"]:
                    speed = math.hypot(r["x"] - prev[n][1], r["z"] - prev[n][2]) / (t - prev[n][0])
                prev[n] = (t, r["x"], r["z"])
                contact = not r["down"] and not r["ride"] and (r["supp"] > 0.3 or r["seen"] == 1 or (r.get("seenby") or 0) > 0)
                if contact:
                    self.contact_s += 0.5
                inside = (int(math.floor(r["x"] / cell)), int(math.floor(r["z"] / cell))) in built
                derived[n] = (speed, contact, inside)
            self.samples.append((t, rows, vehs, derived))
        self.times = [s[0] for s in self.samples]
        self.ev = m.ev
        for k in ("crack", "spot", "spawn", "release"):
            if m.ev.get(k):
                self.fields.add(k)
        if self.fields:  # a recording from after the new fields: these kinds exist even if none happened
            self.fields |= {"crack", "spot", "spawn", "release"}
        self.fields |= {"supp", "seen", "stance"}
        self.shots_by = collections.defaultdict(list)
        for s in m.ev["shot"]:
            self.shots_by[s["s"]].append(s)

    def sample_at(self, t):
        i = bisect.bisect_left(self.times, t)
        return self.samples[min(i, len(self.samples) - 1)] if self.samples else None

    @property
    def contact_minutes(self):
        return self.contact_s / 60.0


CHECKS = {}


def check(fn):
    CHECKS[fn.__name__] = fn
    return fn


def is_bot(r):
    """The rules judge bots. The player still counts as someone's squadmate, but isn't flagged himself. (Flagged, a
    test run's idle player read as "idle in contact" and "cut off from his squad".)"""
    return r["state"] != "Player"


def per_person(run, rule, cond):
    """A per-sample condition over every bot on foot (not down, not riding), as episodes."""
    tr = Tracker(rule["min_seconds"])
    for t, rows, _, d in run.samples:
        present = set()
        for n, r in rows.items():
            if r["down"] or r["ride"] or not is_bot(r):
                continue
            present.add(n)
            speed, contact, inside = d[n]
            hit = cond(r, speed, contact, inside)
            tr.step(t, n, bool(hit), f'{r["state"]}: {r["note"]}' if hit is True else (hit or ""))
        tr.end_absent(present)
    return tr.close(), None


# ---------------------------------------------------------------------------- the individual soldier

@check
def upright_under_fire(run, rule):
    p = rule["params"]
    return per_person(run, rule, lambda r, sp, c, inside:
                      r["stance"] == "S" and r["supp"] >= p["supp"] and not inside and r.get("cover") != 1)


@check
def upright_run_in_view(run, rule):
    p = rule["params"]
    return per_person(run, rule, lambda r, sp, c, inside:
                      r["stance"] == "S" and sp > p["speed"] and (r.get("seenby") or 0) >= 1 and not inside)


@check
def time_to_first_round(run, rule):
    p = rule["params"]
    eps, lat = [], []
    for s in run.ev["spot"]:
        for sh in run.shots_by.get(s["s"], []):
            if sh["at"] == s["at"] and sh["t"] >= s["t"]:
                dt = sh["t"] - s["t"]
                if dt <= p["within"]:
                    lat.append(dt)
                    if dt < p["floor"]:
                        eps.append(Episode(s["s"], s["t"], f'{dt:.2f} s after picking out {s["at"]} at {s["d"]:.0f} m ({s.get("by", "")})', s["t"] + dt))
                break
    return eps, quantiles("picked out to first round at him", lat, "s")


# ---------------------------------------------------------------------------- the squad

def squads(rows):
    by = collections.defaultdict(list)
    for n, r in rows.items():
        if r["squad"] and not r["ride"] and not r["down"]:
            by[r["squad"]].append((n, r))
    return by


@check
def bunched(run, rule):
    metres = rule["params"]["metres"]
    tr = Tracker(rule["min_seconds"])
    for t, rows, _, d in run.samples:
        present = set()
        for sq, men in squads(rows).items():
            for n, r in men:
                if not is_bot(r):
                    continue
                present.add(n)
                _, contact, inside = d[n]
                close = any(o != n and math.hypot(ro["x"] - r["x"], ro["z"] - r["z"]) < metres for o, ro in men)
                tr.step(t, n, contact and close and not inside, f'{r["state"]}: {r["note"]}')
        tr.end_absent(present)
    return tr.close(), None


@check
def idle_in_contact(run, rule):
    tr = Tracker(rule["min_seconds"])
    idx = collections.defaultdict(int)
    last = {}
    for t, rows, _, d in run.samples:
        present = set()
        for n, r in rows.items():
            lst = run.shots_by.get(n, [])
            while idx[n] < len(lst) and lst[idx[n]]["t"] <= t:
                last[n] = lst[idx[n]]["t"]
                idx[n] += 1
            if r["down"] or r["ride"] or not is_bot(r):
                continue
            present.add(n)
            sp, contact, inside = d[n]
            silent = t - last.get(n, -1e9) > rule["min_seconds"]
            tr.step(t, n, contact and r["stance"] == "S" and sp < 0.3 and silent and not inside, f'{r["state"]}: {r["note"]}')
        tr.end_absent(present)
    return tr.close(), None


@check
def cut_off(run, rule):
    metres = rule["params"]["metres"]
    tr = Tracker(rule["min_seconds"])
    for t, rows, _, d in run.samples:
        present = set()
        for sq, men in squads(rows).items():
            if len(men) < 2:
                continue
            for n, r in men:
                if r["role"] == "CRW" or not is_bot(r):
                    continue
                present.add(n)
                far = min(math.hypot(ro["x"] - r["x"], ro["z"] - r["z"]) for o, ro in men if o != n)
                tr.step(t, n, far > metres, f'{r["state"]}: {r["note"]} ({far:.0f} m from the nearest squadmate)')
        tr.end_absent(present)
    return tr.close(), None


@check
def bounding(run, rule):
    p = rule["params"]
    tr = Tracker(rule["min_seconds"])
    for t, rows, _, d in run.samples:
        present = set()
        for sq, men in squads(rows).items():
            if len(men) < 3:
                continue
            present.add(sq)
            doing = men[0][1].get("doing") or ""
            engaged = any(d[n][1] for n, _ in men)
            moving = sum(1 for n, _ in men if d[n][0] > p["speed"])
            share = moving / len(men)
            tr.step(t, sq, engaged and doing not in p["exempt"] and share > p["share"], f"{moving} of {len(men)} moving ({doing})")
        tr.end_absent(present)
    return tr.close(), None


@check
def seen_by_armour(run, rule):
    hurt = set(rule["params"]["can_hurt"])
    return per_person(run, rule, lambda r, sp, c, inside:
                      r["role"] not in hurt and (r.get("seenby_armour") or 0) >= 1)


@check
def return_fire(run, rule):
    """A squad under effective fire (a round within crack_m of one of its men, or a hit): how long until any of its men
    fires back. A breach is no round back within `within` seconds while one of them had his target in sight."""
    p = rule["params"]
    incoming = []
    for c in run.ev["crack"]:
        if not c.get("frag") and c["d"] <= p["crack_m"] and c.get("st", -1) >= 0 and c["st"] != c["vt"]:
            incoming.append((c["t"], c["v"]))
    for h in run.ev["hit"]:
        if not h.get("frag") and h["s"] and run.m.team_of.get(h["s"], h["vt"]) != h["vt"]:
            incoming.append((h["t"], h["v"]))
    incoming.sort()
    started = {}
    eps, lat = [], []
    shot_times = {n: [s["t"] for s in lst] for n, lst in run.shots_by.items()}
    for t, victim in incoming:
        smp = run.sample_at(t)
        if smp is None or victim not in smp[1]:
            continue
        sq = smp[1][victim]["squad"]
        if not sq or t - started.get(sq, -1e9) < 30.0:
            continue
        started[sq] = t
        men = {n for n, r in smp[1].items() if r["squad"] == sq}
        # The squad's first round from a second before the fire came in.
        first = None
        for n in men:
            ts = shot_times.get(n, [])
            i = bisect.bisect_left(ts, t - 1.0)
            if i < len(ts) and (first is None or ts[i] < first):
                first = ts[i]
        # Already firing when it came in: not a reaction to measure.
        if first is not None and first < t:
            continue
        dt = (first - t) if first is not None else None
        if dt is not None and dt <= 30.0:
            lat.append(dt)
        if dt is None or dt > p["within"]:
            could = any(r["seen"] == 1 for tt, rows, _, _ in run.samples[bisect.bisect_left(run.times, t):bisect.bisect_right(run.times, t + p["within"])]
                        for n, r in rows.items() if n in men)
            if could:
                eps.append(Episode(sq, t, f"under fire ({victim}); first round back {'never' if dt is None else f'after {dt:.1f} s'}", t + (dt or p["within"])))
    return eps, quantiles("under fire to the squad's first round back", lat, "s")


# ---------------------------------------------------------------------------- vehicles

@check
def armour_ahead(run, rule):
    p = rule["params"]
    kinds = set(p["kinds"])
    tr = Tracker(rule["min_seconds"])
    for t, rows, vehs, _ in run.samples:
        people = [r for r in rows.values() if not r["down"]]
        present = set()
        for v in vehs:
            vid, name, kind, team, vx, vz = v[0], v[1], v[2], v[3], v[4], v[5]
            key = f"{name}#{vid}"
            enemies = [(r["x"], r["z"]) for r in people if r["team"] != team]
            if kind not in kinds or v[7] or not v[8] or not enemies:
                continue
            present.add(key)
            de = min(math.hypot(ex - vx, ez - vz) for ex, ez in enemies)
            inf = [(r["x"], r["z"]) for r in people if r["team"] == team and not r["ride"] and math.hypot(r["x"] - vx, r["z"] - vz) < 800]
            df = min((min(math.hypot(ex - ix, ez - iz) for ex, ez in enemies) for ix, iz in inf), default=1e9)
            crew = f", crew {v[13]} working with {v[14] or 'nobody'}" if len(v) > 14 else ""
            tr.step(t, key, de < p["enemy_m"] and df - de > p["ahead_m"],
                    f"{kind} task '{v[9]}'{crew}; enemy {de:.0f} m off, its own infantry {'none within 800 m' if df > 1e8 else f'{df:.0f} m from the enemy'}")
        tr.end_absent(present)
    return tr.close(), None


# ---------------------------------------------------------------------------- fire

@check
def beyond_range(run, rule):
    ranges = rule["params"]["ranges"]
    eps = []
    n = 0
    for s in run.ev["shot"]:
        w = s["w"]
        reach = next((r for k, r in ranges.items() if w.startswith(k)), None)
        if reach is None:
            continue
        n += 1
        dist = math.dist(s["p"], s["a"])
        if dist > reach:
            eps.append(Episode(s["s"], s["t"], f"{w} aimed at {dist:.0f} m ({s['m']}{', at ' + s['at'] if s['at'] else ''})"))
    return eps, f"{len(eps)} of {n} rounds from these weapons ({100.0 * len(eps) / max(n, 1):.1f}%)"


@check
def friendly_fire(run, rule):
    eps = []
    for h in run.ev["hit"]:
        if h.get("frag") or not h["s"] or h["s"] == h["v"]:
            continue
        if run.m.team_of.get(h["s"]) == h["vt"]:
            eps.append(Episode(h["s"], h["t"], f"hit {h['v']} of his own side with {h['w']} at {h['d']:.0f} m"))
    return eps, None


# ---------------------------------------------------------------------------- the world and the handover

@check
def one_body(run, rule):
    tr = Tracker(0.0)
    for t, rows, _, _ in run.samples:
        by = collections.defaultdict(list)
        for n, r in rows.items():
            if (r.get("sid") or -1) >= 0:
                by[r["sid"]].append(n)
        present = set()
        for sid, names in by.items():
            if len(names) > 1:
                present.add(sid)
                tr.step(t, sid, True, f"war soldier {sid} is {len(names)} people: {', '.join(sorted(names))}")
        tr.end_absent(present)
    eps = tr.close()
    for e in eps:
        e.who = f"soldier {e.who}"
    return eps, None


@check
def spawn_sane(run, rule):
    p = rule["params"]
    eps = []
    in_hull = []
    for s in run.ev["spawn"]:
        why = []
        if s.get("solid") == 1:
            why.append("inside the ground or a building")
        elif s.get("solid") == 2:
            in_hull.append(s)
        if not s.get("ride") and s.get("water", 0) >= p["wade_m"]:
            why.append(f"in water {s['water']:.1f} m deep")
        if s.get("ground") is not None and s["ground"] - s["p"][1] > p["buried_m"]:
            why.append(f"{s['ground'] - s['p'][1]:.1f} m under the ground")
        if why:
            eps.append(Episode(s["s"], s["t"], f"came in {' and '.join(why)} ({s.get('sq', '')})"))
    # Inside a vehicle's hull isn't failed: crews are put beside their vehicle and board it (a placement to tidy, not a
    # man stuck in the ground), but it's said.
    note = f"{len(in_hull)} came in inside a vehicle's hull ({', '.join(sorted({s.get('role', '') for s in in_hull}))})" if in_hull else None
    return eps, note


@check
def spawn_near_enemy(run, rule):
    metres = rule["params"]["metres"]
    eps = [Episode(s["s"], s["t"], f"came in {s['enemy']} m from an enemy ({s.get('sq', '')})")
           for s in run.ev["spawn"] if 0 <= s.get("enemy", -1) < metres]
    return eps, f"{len(eps)} of {len(run.ev['spawn'])} people came in within {metres:.0f} m of an enemy"


@check
def handover_keeps_state(run, rule):
    eps = []
    released = {}
    events = sorted([("release", e) for e in run.ev["release"]] + [("spawn", e) for e in run.ev["spawn"]], key=lambda x: x[1]["t"])
    for kind, e in events:
        sid = e.get("sid", -1)
        if sid is None or sid < 0:
            continue
        if kind == "release":
            released[sid] = e
            continue
        r = released.get(sid)
        if r is None:
            continue
        if r["state"] == "dead":
            eps.append(Episode(e["s"], e["t"], f"war soldier {sid} came back in, though he went back to the war dead at {r['t']:.0f} s"))
        elif r["state"] in ("wounded", "down") and r["hp"] < 100 and e.get("hp", 0) >= 100:
            eps.append(Episode(e["s"], e["t"], f"war soldier {sid} went back {r['state']} at {r['hp']} health ({r['t']:.0f} s) and came back unhurt"))
    return eps, None


# ---------------------------------------------------------------------------- patterns (reported, not judged)

def quantiles(what, xs, unit):
    if not xs:
        return f"{what}: no cases"
    xs = sorted(xs)

    def q(p):
        return xs[min(len(xs) - 1, int(len(xs) * p))]
    return f"{what}: n={len(xs)}, 10th percentile {q(0.1):.1f} {unit}, median {q(0.5):.1f}, 90th {q(0.9):.1f}"


def patterns(run):
    out = []
    shots = collections.Counter(s["w"] for s in run.ev["shot"])
    hits = collections.Counter(h["w"] for h in run.ev["hit"] if not h.get("frag"))
    rph = [f"{w.split(' ')[0]} {shots[w] / hits[w]:.1f}" for w in shots if hits.get(w) and shots[w] >= 20]
    out.append("rounds per hit (bullets, 20+ rounds fired): " + (", ".join(rph) or "none"))
    for team in (0, 1, 2):
        r = sorted(math.dist(s["p"], s["a"]) for s in run.ev["shot"] if s["team"] == team and s["m"] == "aimed")
        if r:
            out.append(f"side {team} aimed fire: median {r[len(r) // 2]:.0f} m, quartiles {r[len(r) // 4]:.0f}-{r[3 * len(r) // 4]:.0f} m ({len(r)} rounds)")
    allp, heavy = collections.Counter(), collections.Counter()
    for _, rows, _, _ in run.samples:
        for r in rows.values():
            if r["down"] or r["ride"]:
                continue
            allp[r["stance"]] += 1
            if r["supp"] > 0.5:
                heavy[r["stance"]] += 1

    def mix(c):
        n = max(1, sum(c.values()))
        return f"standing {c['S'] / n:.0%}, kneeling {c['C'] / n:.0%}, prone {c['P'] / n:.0%}"
    out.append(f"posture all the time: {mix(allp)}; under heavy fire: {mix(heavy)}")
    if run.ev["crack"]:
        bins = [(0, 1), (1, 2), (2, 4), (4, 6)]
        got = collections.defaultdict(lambda: [0, 0])
        for c in run.ev["crack"]:
            if c.get("frag"):
                continue
            before = run.sample_at(c["t"] - 0.25)
            if before is None or c["v"] not in before[1]:
                continue
            r0 = before[1][c["v"]]
            if r0["stance"] != "S" or r0["down"] or r0["ride"]:
                continue
            b = next((b for b in bins if b[0] <= c["d"] < b[1]), None)
            if b is None:
                continue
            got[b][0] += 1
            i = bisect.bisect_left(run.times, c["t"])
            for _, rows, _, _ in run.samples[i:i + 6]:  # the next 3 s
                if c["v"] in rows and rows[c["v"]]["stance"] in ("C", "P"):
                    got[b][1] += 1
                    break
        cells = [f"{a}-{b} m {got[(a, b)][1] / got[(a, b)][0]:.0%} of {got[(a, b)][0]}" for a, b in bins if got[(a, b)][0]]
        out.append("standing men going to ground within 3 s of a round passing, by miss distance: " + ", ".join(cells))
    return out


# ---------------------------------------------------------------------------- running it

def evaluate(path, rules):
    run = Run(path)
    results = []
    for rule in rules:
        missing = [f for f in rule["needs"] if f not in run.fields]
        if missing:
            results.append({"rule": rule, "skipped": missing, "episodes": [], "stats": None})
            continue
        eps, stats = CHECKS[rule["check"]](run, rule)
        results.append({"rule": rule, "skipped": None, "episodes": sorted(eps, key=lambda e: (-e.dur, e.t0)), "stats": stats})
    return run, results


def report(run, results, top):
    print(f"== {os.path.basename(run.path)}: {run.m.end / 60:.1f} min, {run.contact_minutes:.0f} bot-minutes in contact")
    failed = False
    per = max(run.contact_minutes, 1e-6) / 10.0
    for res in results:
        rule = res["rule"]
        head = f"  {rule['id']} {rule['name']:36s}"
        if res["skipped"]:
            print(f"{head} not checkable: the recording lacks {', '.join(res['skipped'])}")
            continue
        eps = res["episodes"]
        if rule["kind"] == "never":
            print(f"{head} {'PASS' if not eps else f'FAIL: {len(eps)}'}")
            failed |= bool(eps)
            shown = eps
        else:
            rate = len(eps) / per
            band = rule.get("band")
            flag = f"  OUT OF BAND (band {band})" if band is not None and rate > band else ""
            print(f"{head} {len(eps):4d} episodes, {rate:5.1f} per 10 bot-minutes in contact{flag}")
            shown = eps[:top]
        for e in shown:
            print(f"      t={e.t0:7.1f}s {e.dur:6.1f}s  {str(e.who)[:26]:26s} {e.info[:110]}")
        if res["stats"]:
            print(f"      {res['stats']}")
    print("  patterns:")
    for line in patterns(run):
        print(f"    {line}")
    return failed


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("files", nargs="+")
    ap.add_argument("--rules", default=RULES_PATH)
    ap.add_argument("--top", type=int, default=5)
    ap.add_argument("--json", help="also write every rule's episodes here")
    a = ap.parse_args()
    rules = load_rules(a.rules)
    failed = False
    dump = {}
    for path in a.files:
        run, results = evaluate(path, rules)
        failed |= report(run, results, a.top)
        dump[path] = {res["rule"]["id"]: {"skipped": res["skipped"], "stats": res["stats"],
                                          "episodes": [e.as_dict() for e in res["episodes"]]} for res in results}
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(dump, f, indent=1)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
