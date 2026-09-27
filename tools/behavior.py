"""
Behaviour metrics from one or more telemetry files, side by side (for A/B runs).

    python tools/behavior.py before.jsonl after.jsonl [...]

For each file: dithering (cover re-seeks that went nowhere, flip-flops between states, state and
stance changes per minute), squad cohesion, how much of the fighting is firing, how the fights go
(per-squad contact episodes: length, how far the sides close, casualties), and what bounds and rushes
there were.
"""
import collections
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import telemetry as T  # noqa: E402


def metrics(path):
    m = T.Match(path)
    out = collections.OrderedDict()
    dur_min = m.end / 60.0
    by = collections.defaultdict(list)
    for s in m.ev["st"]:
        by[s["s"]].append(s)
    reseek = flips = 0
    for seq in by.values():
        for a, b in zip(seq, seq[1:]):
            if b["t"] - a["t"] >= 3.0:
                continue
            if a["to"] == "InCover" and b["to"] == "TakeCover" and math.dist(a["p"], b["p"]) < 3:
                reseek += 1
            if {a["to"], b["to"]} in ({"Hold", "Advance"}, {"Engage", "Advance"}) and a["from"] == b["to"]:
                flips += 1
    bot_min = 0.0
    stance_changes = 0
    last = {}
    for t, rows, _, _ in m.samples:
        for n, r in rows.items():
            if r["down"] or r["ride"]:
                last.pop(n, None)
                continue
            if n in last:
                bot_min += 0.5 / 60.0
                if last[n] != r["stance"]:
                    stance_changes += 1
            last[n] = r["stance"]
    out["minutes"] = f"{dur_min:.1f}"
    out["cover re-seeks going nowhere (/min)"] = f"{reseek / dur_min:.1f}"
    out["hold/engage<->advance flip-flops (/min)"] = f"{flips / dur_min:.1f}"
    out["state changes per bot-minute"] = f"{len(m.ev['st']) / max(bot_min, 1e-6):.1f}"
    out["stance changes per bot-minute"] = f"{stance_changes / max(bot_min, 1e-6):.1f}"
    # Cohesion: rifle-squad men more than 60 m from every squadmate.
    kind = {sq[0]: sq[1] for sq in m.map["squads"]}
    iso = tot = 0
    for t, rows, _, _ in m.samples[::4]:
        sq = collections.defaultdict(list)
        for n, r in rows.items():
            if r["squad"] and not r["ride"] and not r["down"] and kind.get(r["squad"]) == "Rifle":
                sq[r["squad"]].append(r)
        for mem in sq.values():
            if len(mem) < 3:
                continue
            for i, r in enumerate(mem):
                d = min(math.hypot(r["x"] - o["x"], r["z"] - o["z"]) for j, o in enumerate(mem) if j != i)
                tot += 1
                iso += d > 60
    out["rifle men isolated (>60 m from all squadmates)"] = f"{iso / max(tot, 1):.0%}"
    shots = [s for s in m.ev["shot"] if s["team"] >= 0]
    rifle = [s for s in shots if not s["m"].startswith("from ")]
    out["small-arms rounds per bot-minute"] = f"{len(rifle) / max(bot_min, 1e-6):.2f}"
    modes = collections.Counter(s["m"] for s in rifle)
    out["of which aimed / suppressive / other"] = f"{modes.get('aimed', 0)} / {modes.get('suppress', 0) + modes.get('support by fire', 0)} / {sum(modes.values()) - modes.get('aimed', 0) - modes.get('suppress', 0) - modes.get('support by fire', 0)}"
    out["kills / downs"] = f"{len(m.ev['kill'])} / {len(m.ev['down'])}"
    notes = collections.Counter(s["n"] for s in m.ev["st"] if s["to"] == "TakeCover")
    out["bounds to cover / rushes / buddy rushes"] = f"{notes.get('bounding forward', 0)} / {notes.get('rushing', 0)} / {notes.get('rushing (buddy covering)', 0)}"
    eps, _ = T.squad_episodes(m, 15)
    T.ARGS = type("A", (), {"min_shots": 15, "by": "squad"})
    long_eps = []
    for e in eps:
        T.analyse(m, e)
        if e.duration >= 60 and e.stats["gap_start"] is not None:
            long_eps.append(e)
    if eps:
        durs = sorted(e.duration for e in eps)
        out["squad fights (>=15 rds): count, median length"] = f"{len(eps)}, {durs[len(durs) // 2]:.0f} s"
    if long_eps:
        closed = [e.stats["gap_start"] - e.stats["gap_end"] for e in long_eps]
        closest = [e.stats["gap_min"] for e in long_eps]
        out["fights over 1 min: sides closed by (median)"] = f"{sorted(closed)[len(closed) // 2]:.0f} m (closest approach median {sorted(closest)[len(closest) // 2]:.0f} m)"
        fired = []
        for e in long_eps:
            per = list(e.stats["per"].values())
            fired.append(sum(1 for p in per if p["shots"] > 0) / max(len(per), 1))
        out["fights over 1 min: share of people in them who fired"] = f"{sum(fired) / len(fired):.0%}"
    return out


def main():
    cols = [(os.path.basename(p), metrics(p)) for p in sys.argv[1:]]
    keys = list(cols[0][1].keys())
    w = max(len(k) for k in keys) + 2
    print(" " * w + "".join(f"{name[:24]:>26}" for name, _ in cols))
    for k in keys:
        print(f"{k:<{w}}" + "".join(f"{c.get(k, '-'):>26}" for _, c in cols))


if __name__ == "__main__":
    main()
