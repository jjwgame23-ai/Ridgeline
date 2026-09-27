"""
A replay of a match you can watch in a browser: the map, everyone moving (stance, down, riding), every shot as
a tracer, explosions, casualties, squad drills, and a timeline of the fire by side to scrub along. Click a
soldier to follow him and see what he was doing and why. One self-contained HTML file, no server.

    python tools/replay.py match.jsonl [--out replay.html] [--px 2048] [--from 0] [--to 999]

The match is recorded with telemetry=<file.jsonl> (see DESIGN.md, "Match telemetry").
"""
import argparse
import base64
import bisect
import collections
import io
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import telemetry as T  # noqa: E402

STATES = ["Advance", "Investigate", "Engage", "TakeCover", "InCover", "Hold", "Flank", "Search", "Evade", "Aid", "Player"]
ABSENT = -32768


def b64(a):
    return base64.b64encode(np.ascontiguousarray(a).tobytes()).decode("ascii")


def build(m, t0, t1, px):
    samples = [s for s in m.samples if t0 <= s[0] <= t1]
    if not samples:
        raise SystemExit("no samples in that time range")
    times = np.array([s[0] for s in samples], dtype=np.float32)
    n = len(samples)

    # People: everyone who appears, in a stable order (team, squad, name).
    info, human = {}, set()
    for _, rows, _, _ in samples:
        for name, r in rows.items():
            info.setdefault(name, (r["team"], r["squad"] or "", r["role"]))
            if r["state"] == "Player":
                human.add(name)
    people = sorted(info, key=lambda k: (info[k][0], info[k][1], k))
    idx = {k: i for i, k in enumerate(people)}
    P = len(people)
    pos = np.full((n, P, 2), ABSENT, dtype=np.int16)
    st = np.zeros((n, P), dtype=np.uint8)    # state (low 4 bits), stance (2 bits), down, riding
    hp = np.zeros((n, P), dtype=np.uint8)
    tgt = np.full((n, P), -1, dtype=np.int16)
    fl = np.zeros((n, P), dtype=np.uint8)    # target in sight, suppression (0..127 in the high 7 bits)
    state_ix = {s: i for i, s in enumerate(STATES)}
    for i, (_, rows, _, _) in enumerate(samples):
        for name, r in rows.items():
            j = idx[name]
            pos[i, j] = (int(round(r["x"] * 10)), int(round(r["z"] * 10)))
            stance = {"S": 0, "C": 1, "P": 2}.get(r["stance"], 0)
            st[i, j] = (state_ix.get(r["state"], 0) & 15) | (stance << 4) | ((1 if r["down"] else 0) << 6) | ((1 if r["ride"] else 0) << 7)
            hp[i, j] = max(0, min(100, int(round(r["hp"] or 0))))
            if r["target"] and r["target"] in idx:
                tgt[i, j] = idx[r["target"]]
            supp = r["supp"] or 0.0
            fl[i, j] = (1 if r["seen"] else 0) | (min(127, int(round(supp * 127))) << 1)

    # Vehicles by instance (a replacement is a new one).
    vinfo = {}
    for _, _, veh, _ in samples:
        for v in veh:
            vinfo.setdefault(v[0], (v[1], v[2], v[3]))
    vids = list(vinfo)
    vix = {k: i for i, k in enumerate(vids)}
    V = len(vids)
    vpos = np.full((n, V, 2), ABSENT, dtype=np.int16)
    vfl = np.zeros((n, V), dtype=np.uint8)   # destroyed, crewed, air, landed
    for i, (_, _, veh, _) in enumerate(samples):
        for v in veh:
            j = vix[v[0]]
            vpos[i, j] = (int(round(v[4] * 10)), int(round(v[5] * 10)))
            flags = v[11] if len(v) > 11 else ""
            vfl[i, j] = (1 if v[7] else 0) | ((1 if v[8] else 0) << 1) | ((1 if v[2] in ("AH", "UH") else 0) << 2) | ((1 if "L" in flags else 0) << 3)

    # Drones: just dots, by side.
    dr = []
    for i, (_, _, _, drones) in enumerate(samples):
        for d in drones:
            dr.append((i, d[1], int(round(d[2] * 10)), int(round(d[3] * 10))))
    drones = np.array(dr, dtype=np.int32).reshape(-1, 4) if dr else np.zeros((0, 4), dtype=np.int32)

    # Shots: from the muzzle toward where it was aimed. Kind: 0 aimed, 1 suppressive or area fire, 2 anything else
    # (prefire, launchers, at drones), 3 a vehicle's gun.
    shots = [s for s in m.ev["shot"] if t0 <= s["t"] <= t1]
    sht = np.array([s["t"] for s in shots], dtype=np.float32)
    shp = np.array([[s["p"][0] * 10, s["p"][2] * 10, s["a"][0] * 10, s["a"][2] * 10] for s in shots], dtype=np.float32).clip(-32767, 32767).astype(np.int16).reshape(-1, 4)

    def kind(s):
        mode = s["m"]
        if mode.startswith("from "):
            return 3
        return 0 if mode == "aimed" else 1 if mode in ("suppress", "support by fire") else 2
    shk = np.array([(max(0, s["team"]) & 3) | (kind(s) << 2) for s in shots], dtype=np.uint8)

    def ev(k):
        return [e for e in m.ev[k] if t0 <= e["t"] <= t1]

    cas = []
    for k in ("down", "kill"):
        for e in ev(k):
            w = e.get("w", "") or ""
            by = e.get("s", "") or "?"
            cas.append([round(e["t"], 2), 1 if k == "kill" else 0, e["vt"], round(e["p"][0], 1), round(e["p"][2], 1),
                        e["v"], by, w, round(e.get("d", 0) or 0)])
    cas.sort()
    booms = [[round(e["t"], 2), round(e["p"][0], 1), round(e["p"][2], 1), e.get("pow", 1), e.get("fr", 7), e.get("w", "")] for e in ev("boom")]
    drills = [[round(e["t"], 2), e["sq"], e["team"], e["d"], round(e["p"][0], 1), round(e["p"][2], 1)] for e in ev("drill")]

    # What each man was doing and why, as he changed state.
    notes, note_ix = [], {}
    changes = collections.defaultdict(list)
    for e in ev("st"):
        if e["s"] not in idx:
            continue
        nt = e["n"] or ""
        if nt not in note_ix:
            note_ix[nt] = len(notes)
            notes.append(nt)
        changes[idx[e["s"]]].append([round(e["t"], 2), state_ix.get(e["to"], 0), note_ix[nt]])

    # Fire by side, per 5 s, for the timeline.
    bins = int(np.ceil((times[-1] - times[0]) / 5.0)) + 1
    fire = np.zeros((3, bins), dtype=np.int32)
    for s, k in zip(shots, shk):
        if (k >> 2) == 3:
            continue
        b = int((s["t"] - times[0]) // 5)
        if 0 <= b < bins and 0 <= s["team"] < 3:
            fire[s["team"], b] += 1

    # The map, rendered as in the storyboards, the whole of it.
    size = m.map["size"]
    view = T.View(m, 0.0, 0.0, size, px)
    img = view.ground()
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=84, optimize=True)
    squads = [[sq[0], sq[1]] for sq in m.map["squads"]]
    return {
        "map": {"id": m.map["id"], "name": m.map["name"], "size": size, "img": "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode("ascii"),
                "sites": m.map["sites"], "bases": m.map["bases"]},
        "teams": ["ALPHA", "BRAVO", "CHARLIE"][: max(2, len(set(i[0] for i in info.values())))],
        "states": STATES,
        "times": b64(times), "n": n,
        "people": [[k, info[k][0], info[k][1], info[k][2], 1 if k in human else 0] for k in people],
        "pos": b64(pos), "st": b64(st), "hp": b64(hp), "tgt": b64(tgt), "fl": b64(fl),
        "veh": [[vinfo[k][0], vinfo[k][1], vinfo[k][2]] for k in vids],
        "vpos": b64(vpos), "vfl": b64(vfl),
        "drones": b64(drones), "ndrones": int(len(drones)),
        "sht": b64(sht), "shp": b64(shp), "shk": b64(shk), "nshots": int(len(shots)),
        "cas": cas, "booms": booms, "drills": drills,
        "notes": notes, "changes": {str(k): v for k, v in changes.items()},
        "fire": fire.tolist(),
        "squads": squads,
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("file")
    ap.add_argument("--out")
    ap.add_argument("--px", type=int, default=2048, help="map image size in pixels (the whole map)")
    ap.add_argument("--from", dest="t0", type=float, default=0.0)
    ap.add_argument("--to", dest="t1", type=float, default=1e9)
    a = ap.parse_args()
    m = T.Match(a.file)
    data = build(m, a.t0, a.t1, a.px)
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "replay.html"), encoding="utf-8") as f:
        page = f.read()
    blob = json.dumps(data, ensure_ascii=True, separators=(",", ":"))
    page = (page.replace("/*REPLAY_DATA*/null", blob)
                .replace("{{TITLE}}", f"{m.map['name']} After-Action Replay")
                .replace("{{MATCH}}", os.path.basename(a.file)))
    out = a.out or os.path.splitext(a.file)[0] + "_replay.html"
    with open(out, "w", encoding="utf-8") as f:
        f.write(page)
    print(f"wrote {out} ({os.path.getsize(out) / 1e6:.1f} MB): {data['n']} frames, {len(data['people'])} people, {len(data['veh'])} vehicles, {data['nshots']} shots")


if __name__ == "__main__":
    main()
