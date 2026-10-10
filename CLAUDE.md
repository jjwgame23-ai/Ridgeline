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

**Conquest window.** `<godot> --path . -- mode=window seed=2 [town=Name | at=x,z]` builds a 4 km window of island N round a town and lets you walk it (nobody else is there yet). Add `shot=x.png frames=300 cam=x,z,h look=x,z,h` for a screenshot; that needs a rendered run (no `--headless`), which opens a window on screen. Headless it prints the build and navmesh stats. About 30 s, plus the first navmesh bake. Add `day=3 verbose` (and `join=0` to watch) to run the war to day 3 and bring the armies near the window's middle in as bots; the log says who came in and every kill.

**Embodied assault test.** `<godot> --path . --headless -- mode=window seed=2 assault=1 verbose [attack=2] [defend=1] [minutes=45] telemetry=t.jsonl` fights ALPHA platoons against a BRAVO platoon dug in round a town, with bots, and prints what each side lost and how long it took against the abstract test and history, then quits. About 5–10 min.

**Conquest war.** `<godot> --path . --headless -- mode=war seed=2 days=7 [trace=N]` makes island N, raises the three armies and runs the war. It writes `war-N-report.txt` (casualties against real rates, the biggest fights, the units that fought most) and `war-N.html` (the replay) to `worldgen/`. `trace=N` adds fight N's state every 30 s to the report. About 1–2 min for 7 days.

**Assault test.** `<godot> --path . --headless -- mode=assault seed=2 runs=12 [attack=9] [defend=3] [arty=1]` fights the same attack on a dug-in position again and again. It writes `assault-N.txt`: losses on each side, how often the place is carried, and how long it takes, against historical marks. About 2 min for 12 runs.

**Coherence.** `python tools/coherence.py run.jsonl [...]` reads a recorded fight for what a watching player would notice: when the shooting starts, the range each side fires at, how fast men move in and out of contact, and how much they stand, kneel or lie flat (under heavy fire too).

**Reading a run.** `tools/telemetry.py` (class `Match`) reads the `.jsonl` file:
- a sample of everyone every 0.5 s;
- events: shot, hit, kill, down, boom, st (state change), drill, order, cross, reinf, radio, score.

## How the work is done

- **Simulation fidelity first.** Use real behaviour, doctrine and numbers, and cite the source briefly in a comment. Nothing is faked or scripted for effect. Performance work must not change results; if a trade-off is unavoidable, say so.
- **Judge bots from real runs.** Compare telemetry from before and after a change, with numbers.
- **Comments explain why.** When fixing a bug, record what used to happen in a parenthetical: "(It used to ..., so ...)".
- **Recordings stay local.** `recordings/` (the player's recorded matches) is gitignored.
