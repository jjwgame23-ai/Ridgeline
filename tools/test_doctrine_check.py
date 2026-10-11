"""
Tests for tools/doctrine_check.py: every rule in tools/rules.toml on recordings made up for it, one it must flag and
one it mustn't, so a rule that misfires is caught here and not in a match.

    python tools/test_doctrine_check.py
"""
import base64
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import doctrine_check as D  # noqa: E402

MAP = {"k": "map", "id": "test", "name": "test", "size": 400, "seed": 0, "teams": ["ALPHA", "BRAVO", "CHARLIE"],
       "cond": {"hour": 12, "weather": "Clear", "timescale": 1, "moon": {"phase": 0, "lit": 0}, "vis": 20000, "rain": 0},
       "hm": {"n": 2, "step": 400, "origin": -200, "lo": 0, "hi": 1, "data": base64.b64encode(bytes(8)).decode()},
       "buildings": {"cell": 4, "data": ""}, "trees": "", "sites": [], "bases": [[0, 0], [0, 0], [0, 0]], "squads": []}


def man(name, team=0, x=0.0, z=0.0, **kw):
    """A row of a sample, as Telemetry.cs writes it, with quiet defaults."""
    f = dict(squad="ALPHA-1", role="RIF", y=0.0, state="Engage", stance="S", down=0, ride=0, target="", seen=0, supp=0.0,
             hp=100, ammo=100, note="", gait="still", ft="A", leads=0, doing="Travelling", seenby=0, seenby_armour=0,
             cover=-1, sid=-1)
    f.update(kw)
    return [name, team, f["squad"], f["role"], x, z, f["y"], f["state"], f["stance"], f["down"], f["ride"], f["target"],
            f["seen"], f["supp"], f["hp"], f["ammo"], f["note"], f["gait"], f["ft"], f["leads"], f["doing"], f["seenby"],
            f["seenby_armour"], f["cover"], f["sid"]]


def vehicle(vid, kind, team, x, z, task="", crew="", supports=""):
    return [vid, kind, kind, team, x, z, 0.0, 0, 1, task, 100, "", 0, crew, supports]


class Rec:
    """A made-up recording."""

    def __init__(self):
        self.lines = [MAP]

    def sample(self, t, men, vehs=()):
        self.lines.append({"k": "s", "t": round(t, 2), "b": list(men), "v": list(vehs), "d": []})

    def over(self, t0, t1, rows, vehs=lambda t: ()):
        t = t0
        while t <= t1 + 1e-9:
            self.sample(t, rows(t), vehs(t))
            t += 0.5
        return self

    def event(self, k, **kw):
        self.lines.append({"k": k, **kw})
        return self

    def check(self, rule_id):
        import json
        fd, path = tempfile.mkstemp(suffix=".jsonl")
        os.close(fd)
        try:
            with open(path, "w", encoding="utf-8") as f:
                for line in self.lines:
                    f.write(json.dumps(line) + "\n")
            rules = [r for r in D.load_rules() if r["id"] == rule_id]
            _, results = D.evaluate(path, rules)
        finally:
            os.remove(path)
        res = results[0]
        assert res["skipped"] is None, f"{rule_id} skipped: needs {res['skipped']}"
        return res


def flagged(res):
    return len(res["episodes"]) > 0


def shot(t, s, at="", w="M4 carbine · red dot", p=(0, 0, 0), a=(0, 0, 100), team=0, m="aimed"):
    return dict(t=t, s=s, team=team, p=list(p), a=list(a), w=w, m=m, at=at)


class Individual(unittest.TestCase):
    def test_R01_upright_under_fire(self):
        self.assertTrue(flagged(Rec().over(0, 8, lambda t: [man("A", supp=0.8, cover=0)]).check("R01")))
        self.assertFalse(flagged(Rec().over(0, 8, lambda t: [man("A", supp=0.8, cover=0, stance="P")]).check("R01")))
        self.assertFalse(flagged(Rec().over(0, 8, lambda t: [man("A", supp=0.8, cover=1)]).check("R01")))

    def test_R01_a_man_down_ends_the_episode(self):
        # Up under fire for 1 s, down for a minute, up again for 1 s: never 5 s on end. (The first checker read 58 s.)
        rows = lambda t: [man("A", supp=0.8, cover=0, down=1 if 1 < t < 61 else 0)]
        self.assertFalse(flagged(Rec().over(0, 62, rows).check("R01")))

    def test_R02_upright_run_in_view(self):
        run = lambda seen: (lambda t: [man("A", x=3.0 * t, gait="run", seenby=seen)])
        self.assertTrue(flagged(Rec().over(0, 8, run(1)).check("R02")))
        self.assertFalse(flagged(Rec().over(0, 8, run(0)).check("R02")))
        self.assertFalse(flagged(Rec().over(0, 3, run(1)).check("R02")))  # a 3 s rush

    def test_R04_first_round_faster_than_a_person(self):
        base = lambda: Rec().over(0, 5, lambda t: [man("A"), man("E", team=1, z=200)]).event("spot", t=1.0, s="A", team=0, at="E", d=200, by="eye")
        self.assertTrue(flagged(base().event("shot", **shot(1.3, "A", "E")).check("R04")))
        res = base().event("shot", **shot(3.0, "A", "E")).check("R04")
        self.assertFalse(flagged(res))
        self.assertIn("median 2.0", res["stats"])


class SquadRules(unittest.TestCase):
    def test_R03_slow_to_return_fire(self):
        rows = lambda t: [man("A"), man("B", x=10, seen=1), man("E", team=1, z=200)]
        base = lambda: Rec().over(0, 20, rows).event("crack", t=1.0, v="A", vt=0, s="E", st=1, d=1.0, w="AK", how="past", frag=0)
        self.assertTrue(flagged(base().check("R03")))
        res = base().event("shot", **shot(3.0, "B", "E")).check("R03")
        self.assertFalse(flagged(res))
        self.assertIn("median 2.0", res["stats"])

    def test_R03_not_if_nobody_could_see_him(self):
        rows = lambda t: [man("A"), man("B", x=10), man("E", team=1, z=200)]
        rec = Rec().over(0, 20, rows).event("crack", t=1.0, v="A", vt=0, s="E", st=1, d=1.0, w="AK", how="past", frag=0)
        self.assertFalse(flagged(rec.check("R03")))

    def test_R05_bunched(self):
        self.assertTrue(flagged(Rec().over(0, 8, lambda t: [man("A", supp=0.5), man("B", x=1.0, supp=0.5)]).check("R05")))
        self.assertFalse(flagged(Rec().over(0, 8, lambda t: [man("A", supp=0.5), man("B", x=5.0, supp=0.5)]).check("R05")))

    def test_R06_idle_in_contact(self):
        rows = lambda t: [man("A", seen=1, target="E"), man("E", team=1, z=200)]
        self.assertTrue(flagged(Rec().over(0, 40, rows).check("R06")))
        self.assertFalse(flagged(Rec().over(0, 40, rows).event("shot", **shot(20.0, "A", "E")).check("R06")))

    def test_R06_not_the_player(self):
        # The rules judge bots: an idle player in a test run isn't flagged.
        rows = lambda t: [man("You", seen=1, state="Player"), man("E", team=1, z=200)]
        self.assertFalse(flagged(Rec().over(0, 40, rows).check("R06")))

    def test_R07_cut_off(self):
        self.assertTrue(flagged(Rec().over(0, 70, lambda t: [man("A"), man("B", x=200)]).check("R07")))
        self.assertFalse(flagged(Rec().over(0, 70, lambda t: [man("A"), man("B", x=20)]).check("R07")))
        # The player far from his squad isn't flagged, but each bot then is: the player is someone's only squadmate.
        res = Rec().over(0, 70, lambda t: [man("You", state="Player"), man("B", x=200)]).check("R07")
        self.assertEqual({e.who for e in res["episodes"]}, {"B"})

    def test_R09_too_many_moving_at_once(self):
        def rows(doing, movers):
            return lambda t: [man(n, x=(2.0 * t if i < movers else 0.0), z=10 * i, supp=0.5, doing=doing) for i, n in enumerate("ABCD")]
        self.assertTrue(flagged(Rec().over(0, 8, rows("BoundingOverwatch", 4)).check("R09")))
        self.assertFalse(flagged(Rec().over(0, 8, rows("Assault", 4)).check("R09")))
        self.assertFalse(flagged(Rec().over(0, 8, rows("BoundingOverwatch", 2)).check("R09")))

    def test_R10_in_sight_of_armour(self):
        self.assertTrue(flagged(Rec().over(0, 12, lambda t: [man("A", seenby=1, seenby_armour=1)]).check("R10")))
        self.assertFalse(flagged(Rec().over(0, 12, lambda t: [man("A", role="LAT", seenby=1, seenby_armour=1)]).check("R10")))


class Vehicles(unittest.TestCase):
    def test_R08_armour_ahead_of_its_infantry(self):
        def rec(own_z):
            rows = lambda t: [man("A", z=own_z), man("E", team=1, z=300)]
            vehs = lambda t: [vehicle(1, "IFV", 0, 0, 0, "overwatching", "ALPHA-9", "ALPHA-1")]
            return Rec().over(0, 12, rows, vehs)
        res = rec(-400).check("R08")
        self.assertTrue(flagged(res))
        self.assertIn("ALPHA-9", res["episodes"][0].info)
        self.assertFalse(flagged(rec(250).check("R08")))


class Fire(unittest.TestCase):
    def test_R11_beyond_range(self):
        rows = lambda t: [man("A")]
        self.assertTrue(flagged(Rec().over(0, 2, rows).event("shot", **shot(1, "A", a=(0, 0, 700))).check("R11")))
        self.assertFalse(flagged(Rec().over(0, 2, rows).event("shot", **shot(1, "A", a=(0, 0, 300))).check("R11")))

    def test_R12_friendly_fire(self):
        rows = lambda t: [man("A"), man("B", x=50), man("E", team=1, z=200)]
        hit = lambda vt, v: dict(t=1.0, v=v, vt=vt, s="A", p=[0, 0, 0], z="Torso", w="M4", d=50, frag=0)
        self.assertTrue(flagged(Rec().over(0, 2, rows).event("hit", **hit(0, "B")).check("R12")))
        self.assertFalse(flagged(Rec().over(0, 2, rows).event("hit", **hit(1, "E")).check("R12")))


class World(unittest.TestCase):
    def test_R13_one_body_per_soldier(self):
        self.assertTrue(flagged(Rec().over(0, 2, lambda t: [man("A", sid=7), man("B", x=5, sid=7)]).check("R13")))
        self.assertFalse(flagged(Rec().over(0, 2, lambda t: [man("A", sid=7), man("B", x=5, sid=8)]).check("R13")))

    def spawn(self, **kw):
        e = dict(t=0.5, s="A", team=0, sq="ALPHA-1", role="RIF", p=[0, 10, 0], ground=10, water=0, solid=0, enemy=300, hp=100, ride=0, sid=-1)
        e.update(kw)
        return Rec().over(0, 2, lambda t: [man("A", y=10)]).event("spawn", **e)

    def test_R14_spawned_somewhere_impossible(self):
        self.assertTrue(flagged(self.spawn(solid=1).check("R14")))
        hull = self.spawn(solid=2, role="CRW").check("R14")  # beside his vehicle, about to board: said, not failed
        self.assertFalse(flagged(hull))
        self.assertIn("vehicle's hull", hull["stats"])
        self.assertTrue(flagged(self.spawn(water=1.5).check("R14")))
        self.assertTrue(flagged(self.spawn(p=[0, 9, 0]).check("R14")))
        self.assertFalse(flagged(self.spawn().check("R14")))
        self.assertFalse(flagged(self.spawn(water=1.5, ride=1).check("R14")))

    def test_R15_spawned_on_top_of_the_enemy(self):
        self.assertTrue(flagged(self.spawn(enemy=20).check("R15")))
        self.assertFalse(flagged(self.spawn(enemy=300).check("R15")))
        self.assertFalse(flagged(self.spawn(enemy=-1).check("R15")))

    def test_R16_handover_keeps_state(self):
        def rec(state, hp_back, hp_then=60):
            r = Rec().over(0, 6, lambda t: [man("A", sid=5)])
            r.event("release", t=1.0, s="A", team=0, sid=5, state=state, hp=hp_then, ammo=50, p=[0, 0, 0])
            r.event("spawn", t=5.0, s="A #2", team=0, sq="ALPHA-1", role="RIF", p=[0, 0, 0], ground=0, water=0, solid=0, enemy=300, hp=hp_back, ride=0, sid=5)
            return r
        self.assertTrue(flagged(rec("wounded", 100).check("R16")))
        self.assertTrue(flagged(rec("dead", 100).check("R16")))
        self.assertFalse(flagged(rec("wounded", 60).check("R16")))


class OldRecordings(unittest.TestCase):
    def test_rules_needing_new_fields_are_skipped(self):
        rec = Rec()
        rec.sample(0.0, [man("A")[:17]])
        rec.sample(0.5, [man("A")[:17]])
        fd, path = tempfile.mkstemp(suffix=".jsonl")
        os.close(fd)
        import json
        with open(path, "w", encoding="utf-8") as f:
            for line in rec.lines:
                f.write(json.dumps(line) + "\n")
        try:
            _, results = D.evaluate(path, D.load_rules())
        finally:
            os.remove(path)
        by = {r["rule"]["id"]: r for r in results}
        self.assertEqual(by["R01"]["skipped"], ["cover"])
        self.assertIsNone(by["R05"]["skipped"])


class Rulebook(unittest.TestCase):
    def test_every_rule_has_a_source_and_a_test(self):
        tested = {name.split("_")[1] for cls in (Individual, SquadRules, Vehicles, Fire, World) for name in dir(cls) if name.startswith("test_R")}
        for r in D.load_rules():
            self.assertTrue(r.get("source"), f"{r['id']} has no source")
            self.assertIn(r["kind"], ("never", "norm"))
            self.assertIn(r["id"], tested, f"{r['id']} has no test")


if __name__ == "__main__":
    unittest.main(verbosity=1)
