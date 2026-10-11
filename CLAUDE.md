# Ridgeline: notes for Claude sessions

Ridgeline is a singleplayer squad shooter built for realism in Godot 4.7.2 (.NET, C#, net10.0). Three factions of 33 bots each fight over a large map, and the player is one soldier among them. `DESIGN.md` records what's built and why. Read its sections for whatever you're about to touch, and add to it when you build something.

## Build

`dotnet build` needs the .NET 10 SDK. Godot's packages (Godot.NET.Sdk, GodotSharp) come from nuget.org, so compiling needs no Godot install.

## Test by running matches

The main test is a match nobody plays, run headless and logged.

**Godot binary**
- Windows: `play.cmd` builds and launches the game.
- Linux, e.g. a cloud session: download Godot 4.7.2 .NET from
  https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_linux_x86_64.zip
  then import the project once: `<godot> --path . --headless --import`.

**A 33-a-side match with no player, logged**

```
<godot> --path . --headless --fixed-fps 60 -- mode=tspec33 level=valley verbose telemetry=t.jsonl shot=z.png frames=21600 > run.log 2> run.err
```

- `frames=` counts 60 per second of game, and ends the run only together with `shot=`. Headless runs save no image.
- Levels: `valley`, `alhamra`, `novigrad`, `highlands`, `kessel`.
- Conditions: `hour=22 moon=0.5 weather=clear|overcast|rain|fog timescale=4`. Also `minutes=` (match length) and `tickets=`.
- `run.err` must contain no "Exception".
- `verbose` writes squad orders, assault phases, drills, "still" and "stuck" reports, and counters (`count ...` lines) to the log.

**Conquest islands.** `<godot> --path . --headless -- mode=worldgen seed=1 [seeds=3] [size=1280]` writes four files per island to `worldgen/` (gitignored):
- `island-N.png`, the map;
- `-layers.png`;
- `-report.txt`;
- `.html`, the map with names.

The report checks the island against laws measured on real landscapes, and each line says "ok" or "OUTSIDE". About 20 s an island.

**Conquest window.** `<godot> --path . -- mode=window seed=2 [town=Name | at=x,z]` builds a 4 km window of island N round a town and lets you walk it (nobody else is there yet). Add `shot=x.png frames=300 cam=x,z,h look=x,z,h` for a screenshot; that needs a rendered run (no `--headless`), which opens a window on screen. Headless it prints the build and navmesh stats. About 30 s, plus the first navmesh bake. Add `day=3 verbose` (and `join=0` to watch) to run the war to day 3 and bring the armies near the window's middle in as bots; the log says who came in and every kill. Playing it: M is the map (Tab: the whole island); killed, you carry on as a squadmate after 10 s (1–9 pick who). The war runs on round the window by default, handing units in and out (`live=0` freezes it); `verbose` logs each unit that comes in or goes back and the war's orders. Near the window's edge it's built again round you (a scene reload); with nobody of your side left you come in as a replacement by sea. `side=0|1|2` puts you on a side. Test hooks: `edgetest` (put near the edge), `replacetest` (nobody to carry on as; add `hurt=dead hurtat=900`). The menu's Conquest tab starts the same window (`ConquestPlan`).

**Embodied assault test.** `<godot> --path . --headless -- mode=window seed=2 assault=1 verbose [attack=2] [defend=1] [minutes=120] telemetry=t.jsonl` fights ALPHA platoons against a BRAVO platoon dug in round a town, with bots, at doctrine's pace (`Squad.Deliberate`), and prints what each side lost and how long it took against the abstract test and history, then quits. It runs at about real time: as long as the fight, 30–60 min. Run batches in the background, built once (edits made meanwhile mustn't get in).

**Conquest war.** `<godot> --path . --headless -- mode=war seed=2 days=7 [trace=N]` makes island N, raises the three armies and runs the war. It writes `war-N-report.txt` (casualties against real rates, the biggest fights, the units that fought most) and `war-N.html` (the replay) to `worldgen/`. `trace=N` adds fight N's state every 30 s to the report. About 1–2 min for 7 days.

**Assault test.** `<godot> --path . --headless -- mode=assault seed=2 runs=12 [attack=9] [defend=3] [arty=1]` fights the same attack on a dug-in position again and again. It writes `assault-N.txt`: losses on each side, how often the place is carried, and how long it takes, against historical marks. About 2 min for 12 runs.

**Coherence.** `python tools/coherence.py run.jsonl [...]` reads a recorded fight for what a watching player would notice: when the shooting starts, the range each side fires at, how fast men move in and out of contact, and how much they stand, kneel or lie flat (under heavy fire too).

**Rulebook.** `python tools/doctrine_check.py run.jsonl [...]` checks a recording against `tools/rules.toml`: every bot, every sample, against rules written down with their sources. A "never" rule fails the run (exit code 1); a norm gives a rate per 10 bot-minutes in contact and the worst moments, with the time to look at in the replay and the bot's reason. Change thresholds in `rules.toml`, not in the code; a new rule needs a source and a test in `tools/test_doctrine_check.py` (run it after any change to either). Method and sources: `BOT-VERIFICATION.md`.

**Reading a run.** `tools/telemetry.py` (class `Match`) reads the `.jsonl` file:
- a sample of everyone every 0.5 s: position, state and why, stance, target, suppression, health, ammo, pace, fire team, leader, what the squad is doing, how many enemies see him (and from armour), cover from his threat, war soldier;
- events: shot, crack (a near miss), hit, kill, down, boom, st (state change), spot (a sighting), drill, order, cross, reinf, radio, score, spawn, and in a Conquest window promote and release.
- People are named by callsign, with "#2" while someone else in the world has the same one. A Conquest window that moves writes `-2`, `-3`... beside the first file.

## How the work is done

- **Simulation fidelity first.** Use real behaviour, doctrine and numbers, and cite the source briefly in a comment. Nothing is faked or scripted for effect. Performance work must not change results; if a trade-off is unavoidable, say so.
- **Judge bots from real runs.** Compare telemetry from before and after a change, with numbers.
- **Comments explain why.** When fixing a bug, record what used to happen in a parenthetical: "(It used to ..., so ...)".
- **Recordings stay local.** `recordings/` (the player's recorded matches) is gitignored.
