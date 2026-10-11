# Ridgeline and Conquest: a strict review

2026-10-11, at commit `e4d98e4`. Nothing in the code was changed for this.

**How it was done.** I read `DESIGN.md` end to end, and all of the Conquest code (`src/Conquest/**`). I also read the parts of the battle engine that set lethality and realism: `Body`, `WeaponDef`, `VehicleDef`, `BotSenses`, `BotAim`, `Personality`, `Player`, `Ballistics`, `Squad` and `DistantWar`. Then I built the game and ran the project's own tests headless:

- worldgen for seeds 1–3;
- `mode=war` on seed 2 for 7 and 30 days, and on seed 1 for 7 days;
- `mode=assault`, with and without `arty=1`;
- a 33-a-side Valley match, with telemetry;
- a Conquest window on day 3 (`mode=window seed=2 day=3`);
- a night rain match on Al Hamra.

Numbers quoted below come from those runs unless a line says otherwise. The appendix lists them.

**Severity tags.**
- **[CRITICAL]**: breaks Conquest's core promise, or makes results wrong.
- **[MAJOR]**: clearly unrealistic, or a real bug with a visible effect.
- **[MINOR]**: polish, consistency, or a small inaccuracy.

---

## 0. What to fix first

1. **[CRITICAL] Bringing units into the window clones soldiers** (§2.4).
   - When a vehicle needs more crew than its unit's crewmen, it borrows from the infantry but never removes them from the infantry list.
   - The same soldier crews several vehicles *and* fights on foot.
   - In the day-3 window, one BRAVO lieutenant colonel was on the ground **nine times at once** (eight vehicle crews and an infantry squad). 20 of 193 bodies were clones, which explains why 179 came in against a cap of 160.
2. **[CRITICAL] An embodied fight is 10–100× deadlier than the abstract fight it continues** (§2.1).
   - Whatever is near the player bleeds far faster than the rest of the war.
   - In the day-3 window, a "stand-off" that had killed nobody in 20 minutes became one BMP killing 11 of 43 Americans in 70 s. That BMP was **450 m ahead of its own infantry**, the very thing the last commit set out to fix.
   - Your own company is the one that gets wiped out. The war's course then depends on where you stand.
3. **[CRITICAL] Units near you can be invisible.** Squads over the bot cap stay abstract inside the window. They're invisible and silent, and they can't shoot or be shot. Abstract units and the window's units never fight each other (§2.2).
4. **[CRITICAL] The world isn't the same place twice.** When the window moves, the micro-terrain, river meanders, wood edges, town street plans and the season (sun declination) are generated again. All bodies, wrecks, craters and dug positions vanish (§2.3).
5. **[CRITICAL] Nothing can be saved.** Every launch makes the island again and re-runs the war on the main thread: up to about 5 minutes frozen, with no progress shown, for day 30. The "soldier's life" can't outlast one sitting (§2.5).
6. **[CRITICAL] The war's "fight on until half are gone" rule doesn't work for mechanised and armoured companies.** Strength counts each vehicle as 4 men, but the baseline counts men only, so the half-strength test can't trip while the vehicles live. In seed 2's biggest fights, 85–93% of the mechanised side's men were hit (§1.1-B2).
7. **[MAJOR] The "armour starts at 75%" rule silently skips BRAVO.** It rounds per template line, so BRAVO gets 600 tanks instead of about 450, and every army keeps all its gunships (§1.1-B1).
8. **[MAJOR] A company that loses its vehicles keeps driving at vehicle speed for ever.** `Mob` is never recomputed. The war's deaths in the window aren't counted. Tickets go *up* as armies take losses (§1.1).
9. **[MAJOR] The nightly resupply never reaches embodied soldiers.** The refill is overwritten when the unit goes back to the war (§2.4).
10. **[MAJOR] The wound model kills nearly every serious casualty within about 2½ minutes.** Every downed man dies at 150 s untreated. Bleed rates run 10–100× faster than real. Meanwhile a medic "revives" a lung-shot man into the fight in 6 s (§3.1).
11. **[MAJOR] Bots shoot too accurately, and the player moves like a Squad player.**
    - The rifle averages 7.4–9.4 rounds a hit at about 250 m.
    - The reflexes look esports-fast in `Personality`, but measured end to end they aren't. From picking a man out to the first round at him takes a median 5.3 s; the fastest tenth take 0.8 s.
    - The player jogs at 3.4 m/s with no fatigue, while bots tire at that pace in 2½ minutes (§3.2, §3.4).
12. **[MAJOR] Command and intelligence know everything at once.** Every sighting gives the exact position and exact headcount, at once, to the whole side. Commanders, deputies, personalities and doctrine differences don't exist in the decision code. Veteran experience isn't stored (§1.3).
13. **[MAJOR] Anti-tank weapons are wrong in the war.** Javelin, Kornet and Konkurs teams act as 500 m RPGs. IFV missiles exist embodied but not in the war. Tank fire control hits 7.6% at 2 km (§1.2).
14. **[MAJOR] The war has no arc.** It has no end condition, no economy and no replacement vehicles. In-game it never rains and seasons never change. Three-quarters of BRAVO's battalions sit idle with "no tasks" (§1.4).
15. **[MAJOR] Abstract fights and artillery make no sound in the window.** Nothing exists beyond 4 km. That breaks pillar 4 and the "sound is never level-of-detailed away" rule (§2.2).
16. **[MINOR but corrosive] The docs and tooling have drifted.**
    - `DESIGN.md`'s measured tables are stale.
    - The assault test is called "3:1" but runs at 5.4:1 in men.
    - `coherence.py` mislabels who is "in contact".
    - 115 `.uid` files appear untracked on every import.
    - There's no automated regression test (§6).

---

## 1. Conquest: the abstract war (`src/Conquest/War`)

### 1.1 Bugs (confirmed in code, most also in the run output)

**B1. [MAJOR] "Armour at 75%" rounds per template line, so it does nothing for single-vehicle entries.** [Orbat.cs:441](src/Conquest/War/Orbat.cs:441)
- The code is `Math.Round(n*0.75 + U(-0.25, 0.25))`. With n = 1 that's `Round(0.5..1.0)`, which is almost always 1.
- Every BRAVO tank, BMP/BTR (one per squad) and recon IFV, and every gunship in all three armies, comes at 100%.
- The report shows BRAVO with **600 tanks** (full establishment), against ALPHA's 190 and CHARLIE's 89–93. The design doc reads 600 as intended. It isn't: the rule was meant to cut it to about 450.
- *Fix:* apply the 75% per company or battalion across all its vehicles of a class, or draw per vehicle with probability 0.75.

**B2. [CRITICAL] The break rule compares unlike quantities.** [Combat.cs:1053](src/Conquest/War/Combat.cs:1053), [1080](src/Conquest/War/Combat.cs:1080), [1088](src/Conquest/War/Combat.cs:1088)
- `own = fit + 4 × armed vehicles`, but `came = Strength[mid]`, which is the soldiers who joined the fight. Crews of armed vehicles are left out ([Combat.cs:359](src/Conquest/War/Combat.cs:359)).
- So `own*2 < came` ("lost half its men") can't trip while the vehicles live. A tank company never breaks on losses.
- What seed 2's biggest fights show:
  - #601: ALPHA, 474 men with 75 armed vehicles, 439 hit (93%);
  - #1969: 298 men with 62 armed vehicles, 253 hit (85%);
  - #222: 623 men with 52 armed vehicles, 408 hit.
- "Hit" includes light wounds and men hit twice, so these numbers don't prove the bug alone. The code does: with 75 armed vehicles, `own` can't fall below `came/2` until nearly every vehicle is gone.
- The species is meant to stop at half.
- *Fix:* compare like with like: men with men, and vehicles with vehicles, or both weighted the same way on both sides.

**B3. [MAJOR] A mover's mobility is set once, at the start, and never again.** [War.cs:216](src/Conquest/War/War.cs:216)
- When a mechanised company loses its carriers, `Supply.Measure` sets `FuelCap = 0`. The out-of-fuel check `u.FuelCap > 0f && u.Fuel <= 0f` ([War.cs:310](src/Conquest/War/War.cs:310)) is then skipped.
- So the company walks at tracked or wheeled speed (35–50 km/h) for the rest of the war, with no fuel.
- `Armed` also stays true, which keeps the 2.5 km contact range.
- Over 30 days ALPHA loses 2,985 vehicles, so this grows steadily.
- *Fix:* recompute `Mob`, `Armed` and seats whenever vehicles are lost (Knock, Strike, window release).

**B4. [MAJOR] Deaths in the window aren't counted by the war.** [WindowLive.cs:129-133](src/Conquest/Window/WindowLive.cs:129), [WindowLive.cs:259](src/Conquest/Window/WindowLive.cs:259)
- `State = Dead` is set without `War.Dead[side]++` or `Kills[...]`.
- The report, the tickets and the day-by-day totals all leave out the bloodiest fighting in the game: the fighting next to you.

**B5. [MAJOR] Tickets go up as armies lose.** [Medical.cs:20](src/Conquest/War/Medical.cs:20), [WarMode.cs:234](src/Conquest/War/WarMode.cs:234)
- Tickets = 50,000 − dead + replacements. Replacements also fill places left by the *invalided*.
- ALPHA ends the 30-day run with **59,857 tickets after 11,371 dead**.
- The constant 50,000 isn't the headcount either: the armies are raised at 50,162–54,683.
- As a "headcount" or a loss condition, it's meaningless.

**B6. [MAJOR] Cover is rolled once, when a soldier joins a fight, and kept wherever he goes.** [Combat.cs:365](src/Conquest/War/Combat.cs:365), [Combat.cs:836](src/Conquest/War/Combat.cs:836), [859](src/Conquest/War/Combat.cs:859)
- Bounds, assaults and 200 m withdrawals all keep the first roll.
- A dug-in company that pulls back keeps its trench (0.15) in open fields.
- Shell and grenade bursts use `o.Cover` even while the squad is moving.
- *Fix:* call `Ground(...)` again after every move step, using the squad's new position.

**B7. [MAJOR] A full fight makes its units immune to anyone new.** [Combat.cs:232-235](src/Conquest/War/Combat.cs:232)
- Once a fight has 12 movers, `Contact` returns without making a new fight. So no fresh company can engage any unit in that fight.
- The comment says contacts beyond that become fights of their own; the code drops them.
- Twelve-battalion offensives (36+ companies) hit this all the time.

**B8. [MAJOR] Supply comes through the starting port only.** [Supply.cs:393-419](src/Conquest/War/Supply.cs:393)
- `Land` uses `side.Port`. Ports captured later land replacements ([Medical.cs:76](src/Conquest/War/Medical.cs:76)) but no supplies.
- Lose the home port and the side gets no supply, permanently, even while it holds five other ports.
- Replacements meanwhile land at every port the side owns as an *objective*, including one taken minutes ago that's still contested.

**B9. [MAJOR] A depot can stay empty for the whole war.** In both the 7- and 30-day runs on seed 2, CHARLIE's `26/23 DIV support area` stands empty at the end. Its logistics company reads "ToLoad waiting (no way)" every time. Nothing re-plans its route or reassigns another company.

**B10. [MINOR] Small rule slips.**
- `Aim(war)` reads `Light(war, 0)` for every side ([Combat.cs:222](src/Conquest/War/Combat.cs:222)).
- The bleed rule only lets a `Job.Medic` *in the same fight squad* treat ([Combat.cs:1033](src/Conquest/War/Combat.cs:1033)). US and British medics sit in platoon or company HQ, so in practice only BRAVO's squad medics ever treat anyone.

**B11. [MINOR] Commanders are just names.**
- `Unit.Commander` is used only in the report ([WarMode.cs:375](src/Conquest/War/WarMode.cs:375)).
- A dead officer's replacement is capped at E4 ([Medical.cs:124](src/Conquest/War/Medical.cs:124)) and inherits `Job.Commander`, so specialists end up commanding companies.
- No deputy takes over. Losing a headquarters doesn't change what its formation does.

**B12. [MINOR] `Soldier.Experience` is declared and never read or written** ([Forces.cs:26](src/Conquest/War/Forces.cs:26)). "Skill grows with combat; veterans and replacements fight differently" isn't implemented: a replacement is drawn from the same skill curve as a month's veteran.

### 1.2 Realism problems

**R1. [CRITICAL] Small arms are far too lethal per round.**
- The war report gives **100 rounds per casualty**, and prints beside it "real: WWII … tens of thousands of rounds per casualty".
- The assault test gives 385–428 rounds per hit.
- Even allowing for most fire being suppressive, this is one to two orders of magnitude off. It's also the yardstick the window is being tuned toward.

**R2. [MAJOR] Casualty rates are much higher than the history the design cites.**
- ALPHA runs at 3.5% of the army a day over 7 days, and 1.9% a day over 30. It has 21% of its army killed in a month.
- Dupuy's figure for whole armies is "well under 1%".
- KIA:WIA comes out at 1:3.1, which is the WWII ratio. Yet the medical chain assumes modern evacuation (5% died of wounds), where the ratio runs 1:5–8. Pick one era and stick to it.

**R3. [MAJOR] Anti-tank guided missiles are modelled as RPGs.** [Combat.cs:663](src/Conquest/War/Combat.cs:663)
- `Job.AntiTank` covers all of these:
  - BRAVO's ATGM teams;
  - CHARLIE's Javelin detachments;
  - ALPHA's weapons-squad Javelin gunners.
- They fire to **500 m** with hit chance `0.4·exp(−d/300)`, which is 16% at 300 m.
- Javelin reaches 2.5–4 km, Konkurs 4 km and Kornet 5.5 km, all with a high hit probability. Infantry anti-armour defence is grossly understated.

**R4. [MAJOR] IFVs have no missiles in the war, though they do embodied.** [Combat.cs:768-771](src/Conquest/War/Combat.cs:768)
- In the war, an IFV against a tank kills 0.02 a hit (cannon only).
- Embodied IFVs carry TOW-2 and Konkurs ([VehicleDef.cs:195-207](src/Vehicles/VehicleDef.cs:195)).
- So the same platoon has a different anti-armour capability at each level of detail: a calibration mismatch built in.

**R5. [MAJOR] Tank fire control is pre-WWII.**
- Hit chance is `0.4·exp(−d/1200)`: 17% at 1 km, 7.6% at 2 km ([Combat.cs:771](src/Conquest/War/Combat.cs:771)).
- A modern stabilised fire control system gives 70–90% first-round hits on a visible target at those ranges.
- The "seeing" term is already separate, so this is double-counting.
- The model ignores target size, motion and fire-control generation.

**R6. [MAJOR] Crossing open ground costs almost nothing.** `Exposure` returns 0.6 for a squad on the move, against 0.55 for a crouched man in the open ([Combat.cs:918](src/Conquest/War/Combat.cs:918)). The extra "shows" factor helps a little, but the model doesn't make the crossing the dangerous part, though crossing the killing zone is the central problem of the deliberate attack.

**R7. [MAJOR] A battalion "occupies" an objective by ringing it.** [Command.cs:334-338](src/Conquest/War/Command.cs:334)
- The line companies go to points 120° apart round the objective, one of them on the *far (enemy) side*. Every attack becomes an encirclement, marched round or through the enemy.
- A battalion attack converges on one axis: a support-by-fire position, then the assault on a narrow frontage.

**R8. [MAJOR] Intelligence is perfect and shared at once.** [WarIntel.cs:11](src/Conquest/War/WarIntel.cs:11)
- A sighting stores the unit's exact position, exact `People` and armour flag, and every commander on that side has it the same minute.
- The design promised reports "late by the time it takes to come up the chain".
- There's no misidentification (platoon versus battalion), no double counting, and no drop in the quality of reports at night.

**R9. [MAJOR] Every army thinks the same.**
- The decision rules (`Command`, `Offensive`, `Combat.Decide`) are identical for all three sides.
- The only differences are BRAVO's artillery delay and rounds per tube, and fewer night sights.
- The design's "Russian practice is more centralised and leans on artillery; Western armies leave more to junior leaders" isn't modelled in decisions.

**R10. [MAJOR] Command delays are fixed.** [Command.cs:26-29](src/Conquest/War/Command.cs:26) They're 2 h, 1 h, 30 min and 15 min, whatever the time available. The header cites the 1/3–2/3 rule, but the code doesn't implement it.

**R11. [MAJOR] Whole branches of the armies don't take part.**
- **Army troops stay at the port** ([Command.cs:146-148](src/Conquest/War/Command.cs:146)): the army's GMLRS and BM-27 battalions, its cannon battalion, the engineer brigade, aviation and air defence all stay within 4 km of the port.
- **Aircraft do nothing in the war**: 24 gunships and 30 transport helicopters an army.
- **Engineers do nothing**: no mines, obstacles, bridge demolition or bridging. Bridges are never blown, though they're objectives.
- **Air defence does nothing.**
- **No drones** in the war's reconnaissance or fires.
- Mines alone are one of the biggest casualty producers in modern war.

**R12. [MAJOR] Infantry wade any river; the window disagrees.**
- On foot, infantry ford any river in 30 min (catchment ≥ 50 km² is just a delay; [Mobility.cs:103-108](src/Conquest/War/Mobility.cs:103)).
- The window bars water deeper than 1.2 m from the people navmesh.
- A war route can therefore cross where an embodied squad can't, and the two levels disagree on what ground is passable.

**R13. [MINOR] A mixed company moves with its best vehicle.** A company with any tracked vehicle uses the tracked grid for all its trucks too. Tracked units are logged at up to 254 km in a day, wheeled at 342 km.

**R14. [MINOR] Hunger has a floor and no end.** Aim falls to no worse than 40% ([Supply.cs:366](src/Conquest/War/Supply.cs:366)), and starvation never ends a unit's usefulness. At day 30:
- CHARLIE's hungry units have gone a median **330 hours** without food and are still fighting;
- BRAVO has 42 units hungry, at a median of 127 h.

**R15. [MINOR] Counter-battery fire is very effective.**
- CHARLIE lost **106 of its 216 mortars in the first 7 days**.
- One ALPHA battery was hit 2.6 times its own strength over the month.
- Mortar platoons are among the units that fight most (73 fights a week). They blunder into contact while keeping up with their companies.
- Check whether 81 mm mortars should be counter-battery targets at that rate (they're also "wheeled", so they displace slowly).

**R16. [MINOR] Day and night are fixed.**
- Night is 20:30–05:30 every day, with no moon or season in the war ([War.cs:94](src/Conquest/War/War.cs:94), [Combat.cs:207-213](src/Conquest/War/Combat.cs:207)).
- The window's sun uses a random declination (§2.3). At 38° N its sunset falls between about 18:15 and 19:30, while the war's night starts at 20:30. For one to two hours the window is dark while the war still marches and spots by daylight.

**R17. [MINOR] Seasons and weather.**
- The war has no seasons and no weather at all.
- In the window, the weather is whatever the battle-map menu last had, and it stays that way through every window move and across days.

### 1.3 The design's promises that aren't in the code

- **Commanders as soldiers.** HQs, deputies taking over, personality in how they fight (B11).
- **Intelligence lag** up the chain (R8).
- **Experience and veterancy** (B12).
- **A one-line record for each of the dead:** name, unit, date, place, cause. `Soldier` stores only the time; cause is counted in aggregate; there's no place. So the dead of abstract fights can't be found where they fell, and the aftermath of abstract fights (bodies, wrecks, craters) can't exist.
- **The abstract war on its own threads.** It runs on the main thread (§2.5).
- **Time skip, journal, casualty lists, briefings from named people.** Not built.
- **Economy:** money, miners, extractors, buying vehicles. Resource nodes are just objectives worth 3, and lost vehicles are never replaced.
- **Weather generator, mud, rising fords, fronts.** Not built.
- **Ships, naval gunfire, amphibious landings.** Not built, on an island war.

### 1.4 Strategic and gameplay holes

- **[MAJOR] No end state.**
  - At day 30 the land shares are 31/35/30%. "A war on this island will run for months."
  - No side can be defeated: there's no exhaustion, no collapse of supply, no loss of the last port, no victory condition.
  - The player's war has no stakes and no ending.
- **[MAJOR] Idle battalions.**
  - Seed 1: BRAVO has **44 of 59 battalions** at "brigade has no tasks".
  - Seed 2, day 7: 22 "otherwise idle".
  - Offensives cap at 12 battalions, and the brigades' own attacks are off when the army is on the offensive. The strongest army sits still.
- **[MAJOR] Armies steadily lose their vehicles.** Nothing replaces them, so mechanised forces become foot forces over weeks. This is invisible because of B3.
- **[MINOR] Names repeat.** 50 first names × 100 surnames is 5,000 combinations for about 50,000 soldiers a side, so every name occurs about ten times per army. The day-3 window, besides its clones, had 14 more pairs and triples of identical rank-and-surname among 193 men: two "Ml. Serzhant Baranov" in one squad, and three "Efreitor Ilyin" in three squads. The radio and kill feed use rank and surname, so they become ambiguous.

---

## 2. Conquest: the playable window (`src/Conquest/Window`)

### 2.1 [CRITICAL] The level-of-detail gap (calibration)

The whole design rests on "a fight should never visibly restart when you arrive". Today the embodied fight is a different fight.

**The design's own measurements:**
- 68 of 164 killed in 90 s;
- 13–30 rounds a hit embodied, against 205 for the war's fight;
- the war's 5 minutes downed 3 and killed none.

**My runs:**
- **Valley, 33 a side:** M4 at **7.4 rounds a hit**, M249 at 26, at a median 250 m. Bots are on their feet **84%** of the time and prone 4% (37% standing even under heavy fire).
- **Day-3 window:** the abstract fight it continued was a stand-off. It was 20 minutes old, with 0 killed and the last hit 600 s earlier.
  - Embodied, one BMP-2 drove from about 1 km out to **about 200 m** from the ALPHA company, **450 m ahead of the nearest BRAVO infantry**.
  - In 70 s it killed 11 of the 43 Americans with 202 rounds of 30 mm HE.
  - ALPHA fired back 24 rifle and machine-gun rounds, 3 grenades and 2 LAWs.
  - BRAVO lost nobody in the first 3 minutes.

The causes, each a separate item to fix:

1. **The wound model** (§3.1). Every downed man dies at 150 s. Torso bleeds kill in about 70 s. The war, by contrast, has 70% of the downed surviving without a medic.
2. **Bot reflexes** (§3.2): mouse-like flicks at 0.15–0.36 s.
3. **Missing micro-terrain.** The war's sight model assumes hedges, walls, folds and terraces ("even odds of a clear line at 1 km"), and gives orchards and fields "the odd wall or ditch" ([Combat.cs:389-419](src/Conquest/War/Combat.cs:389)). The window builds **none** of them: no terraces, field walls, hedges or ditches anywhere in `src`.
4. **Sparse vegetation** ([WindowGround.cs:450-474](src/Conquest/Window/WindowGround.cs:450)):
   - pine 45 a hectare (real 300–1,000);
   - maquis 55 bushes a hectare (real maquis is a near-continuous 2–4 m thicket);
   - olive orchards 20 a hectare (real 100+).
   The war counts woods as 0.65 soft cover and 0.4 hide; embodied woods are a park.
5. **No concealment from low vegetation.** Bots have none from crops, grass or scrub ([BotSenses.cs:492-498](src/Bots/BotSenses.cs:492)). Only trees within 30 m ("Forest") or interiors count.
6. **Armour still drives ahead of its infantry.**
   - The telemetry logs the two BMPs' task, 156–165 m from the enemy, as **"overwatching the transport truck"**: they were supporting a truck crew, not infantry.
   - `PairVehicles` only pairs crews with `SquadKind.Rifle` squads ([WindowLive.cs:67](src/Conquest/Window/WindowLive.cs:67)). So recon, HQ and engineer vehicles never get infantry to support, and fall back on whatever the motor pool gives them.
   - Their victims were all logged "in cover", nearly all on a knee and most of them not seeing the gun. They died where they knelt for 70 s rather than getting out of the BMP's sight.

**Systemic consequence: an observer effect.** The fighting nearest the player always runs hottest. The player's battalion will reliably take the worst losses in its army, and the war's course becomes a function of where the camera is.

*Suggestion:* fix the causes in this order: wounds, then terrain cover and concealment, then reaction times. After each fix, compare the two levels on **the same fight**, as `calib=1` already does, and track "minutes to 10% casualties" rather than final losses.

### 2.2 [CRITICAL] Holes in the handover

- **Over the cap, units are absent.**
  - About 1,500 soldiers in a 4 km window against a cap of 160: the rest "left abstract" ([ConquestWindow.cs:283-291](src/Conquest/Window/ConquestWindow.cs:283)).
  - They have no body, sound or tracers, and can't be seen or shot.
  - An enemy company assaulting you while the cap is full simply isn't there until someone dies.
- **Abstract units and the window's units never interact.**
  - `Detect` skips any pair that includes a held unit ([Combat.cs:182](src/Conquest/War/Combat.cs:182)).
  - An abstract enemy 300 m from an embodied friendly company can't fight it. This happens in the gap between promotion at 1.2 km and demotion at 1.7 km, and anywhere over the cap.
- **The whole company is held, but only some of its squads may be embodied.**
  - `Hold` works on the mover, the company ([WindowLive.cs:87-92](src/Conquest/Window/WindowLive.cs:87)).
  - Its squads that aren't embodied are frozen: they aren't marched, can't be shot and can't be shelled ("ghost" squads). Their positions are dragged along with the embodied ones.
- **Promotion rescues a losing company.**
  - `Combat.Hand` settles the company's part "as if it held the field" ([Combat.cs:1215-1229](src/Conquest/War/Combat.cs:1215)), even if it was being beaten.
  - Its downed are evacuated rather than left. The enemy it was fighting loses its opponent, so the abstract fight may end and hand that enemy the field.
- **Who comes in is chosen by distance, not relevance.**
  - The day-3 run embodied BRAVO's regimental HQ, signal, engineer, medical and recon companies (about 300 men, 1 km back), and left the troops actually fighting abstract.
  - Embodied strength came out at 43 ALPHA against 150 BRAVO, against a cap of 160 (179 embodied, 20 of them clones: §2.4).
  - *Suggestion:* weight promotion by contact and threat (units in fights or moving toward the player), and keep rear services abstract unless they're the closest thing to the player.
- **Being wiped out still saves the downed.** A unit with nobody left standing still sends its downed into the medical chain ([WindowLive.cs:225-229](src/Conquest/Window/WindowLive.cs:225)). With nobody to carry them and the enemy holding the ground, most should die or be lost.
- **Wounded soldiers come in healed.** `Spawn` ignores the war's `Blood` and `Wounded` ([ConquestWindow.cs:582-602](src/Conquest/Window/ConquestWindow.cs:582)). Each promotion heals them again. Only the player's body is carried over.
- **[MAJOR] Silence and emptiness beyond the bubble.**
  - Nothing in `src/Conquest` emits a sound. Abstract fights, the war's artillery on units that aren't held, and convoys are all silent.
  - The distant layer (slice 4) doesn't exist: nothing past the 4 km edge.
  - For a design whose pillar is "you can hear everyone else's fight", this is the most noticeable gap in feel.

### 2.3 [CRITICAL] The world isn't persistent

- **Terrain detail depends on where the window is centred.**
  - `WindowGround` seeds its noise by window centre (`isl.Seed*7919 + cx*31 + cz`, [WindowGround.cs:49](src/Conquest/Window/WindowGround.cs:49)) and samples it in *local* coordinates.
  - So when the window moves, the same spot on the island gets:
    - different relief (metres of outcrop and gully);
    - river meanders shifted by up to 30 m;
    - wood edges moved by up to 60 m;
    - a different field patchwork.
  - *Fix:* seed once per island and sample in island coordinates.
- **Towns are laid out again each time.**
  - Towns are laid out with one sequential RNG (the constant `setup.Seed`), in order of population ([WindowMap.cs:33-58](src/Conquest/Window/WindowMap.cs:33)).
  - A town near the window's edge is **shrunk** to fit, and one with under 40 m of room is **skipped**.
  - So the same town has a different street plan, size and district names depending on where the window happens to be.
  - *Fix:* seed each town from its id, and build it whole (load past the window's edge, or shift the window).
- **Everything placed is lost when the window moves:**
  - bodies;
  - wrecks;
  - craters (the `CraterField`);
  - sandbag walls and fighting positions.
  Squads that were dug in get new positions dug in different places.
- **The season changes.**
  - `Conditions.Start` re-rolls the sun's declination (5–23°) on every launch ([Conditions.cs:67](src/World/Conditions.cs:67)), and a window move is a launch.
  - The moon phase and weather are carried over and frozen. The moon never advances across days.
  - Latitude is a constant 38° ([Conditions.cs:23](src/World/Conditions.cs:23)), not the island climate's.
- **The window only moves while you're on foot.** [WindowLive.cs:353](src/Conquest/Window/WindowLive.cs:353) In a vehicle, nothing stops you driving off the edge of the built ground.

### 2.4 Bugs in the window

- **[CRITICAL] Soldiers are cloned when units come in.** [ConquestWindow.cs:543](src/Conquest/Window/ConquestWindow.cs:543)

  ```csharp
  if (crew.Count < need) { crew.AddRange(rest.Take(need - crew.Count)); rest = rest.Skip(need - crew.Count).ToList(); }
  ```

  - `need - crew.Count` is evaluated *after* `AddRange` has grown `crew`, so `Skip(0)`: the borrowed soldiers stay in `rest`.
  - They're borrowed again by the next vehicle, then spawned once more as infantry.
  - In the day-3 window:
    - BRAVO's Podpolkovnik Zakharov crewed 8 vehicles (BRAVO-27 to -34) and fought on foot in BRAVO-35;
    - Podpolkovnik Gusev was on the ground four times;
    - ALPHA's company first sergeant, 1SG Ortiz, was both an LTV crewman and a rifleman, and the log has him **killed twice**, 16 s apart;
    - 20 of the 193 bodies were such clones. Every unit with more vehicles than crew jobs (HQs, signals, engineers, medical) is inflated.
  - All the clones map to one war soldier, so the last one written back decides his fate.
  - *Fix:* take `int k = need - crew.Count` before the `AddRange`, and skip `k`.
- **[MAJOR] The nightly resupply never reaches embodied soldiers.**
  - `Supply.Logpac` refills held units' `so.Ammo`, because held units aren't `InFight`.
  - On release, `WriteMan` overwrites `so.Ammo` with the bot's real magazines ([WindowLive.cs:277](src/Conquest/Window/WindowLive.cs:277)).
  - So the refill is thrown away, and a company in the window is never resupplied except by its own ammo bearers.
  - Vehicle ammunition and fuel used in the window are never written back either, so the war's vehicles keep their pre-window loads for free.
- **[MINOR] Grenades and 40 mm aren't written back**, as the design already notes.
- **[MINOR] Failure paths leave the window stuck.**
  - In `NearTheEdge` and `Replace`, `_moving = true` is set before checks that can return early ([WindowLive.cs:356-357](src/Conquest/Window/WindowLive.cs:356), [416-419](src/Conquest/Window/WindowLive.cs:416)).
  - In `Replace` the early return comes *after* `HandBack()` has cleared `War.Held`. If it fails, the window keeps its bots while the war marches and fights the same units again.
- **[MINOR] The kill log's distance misleads for explosive kills.** For an explosive kill, the log line "X killed Y, 0 m" prints the *burst*-to-victim distance. The day-3 log reads as though a man was killing others at arm's length; it was a BMP's cannon at about 200 m. Print the shooter's range, and give the burst distance separately.
- **[MINOR] Squads keep battle-map names in Conquest.** They're called `ALPHA-37` (`Squad.Name`) on the radio and in messages like "You carry on with ALPHA-37", not "1st Squad, 2nd Platoon, A Company".
- **[MINOR] The embodied mortar is 81 mm for every army**, while `WindowFires` names 120 mm bombs for ALPHA and BRAVO.
- **[MINOR] The war's battalion structures are lost in the window.** Company commanders (`Job.Commander`) are taken as vehicle crew first ([ConquestWindow.cs:531](src/Conquest/Window/ConquestWindow.cs:531)), and fire-team leaders become plain riflemen.

### 2.5 Performance and the experience of playing it

- **[CRITICAL] Saving and loading.**
  - There's no save. Each Enlist makes the island (about 20 s) and runs the war from day 1 on the main thread: about 10 s a day measured, 18 s to day 3, and around 5 minutes to day 30.
  - It all happens inside `_Ready` of the reloaded scene ([Main.cs:75](src/Game/Main.cs:75)): no loading screen, no progress.
  - The menu allows day 30, so the window can freeze for minutes, and Windows may report the game as "Not Responding".
  - Closing the game loses the soldier, the war and everything learned.
- **[MAJOR] `War.Tick` runs on the main thread every minute of play.**
  - From the progress file:
    - command about 10 ms a call;
    - ground update about 27 ms;
    - contact sweep heavier every 5 minutes.
  - They coincide every 30 minutes, which means a hitch while you're fighting.
  - The design's "the abstract war runs on its own threads" isn't done.
- **[MAJOR] Every window move reloads the scene** and bakes new navmesh tiles: 5–25 s each time you get 1.6 km from the window's centre. At the player's 3.4 m/s that's every 8 minutes of travel in a straight line. The vehicle case isn't handled at all (§2.3).
- **[MAJOR] The HUD knows too much for a grunt.**
  - It shows "how many each side has on their feet in the window", which is enemy strength from ground truth.
  - The window map shows every enemy your side saw in the last minute.
  - Pick a fidelity: a squad member would know his squad, his platoon's net, and what the platoon leader tells him.
- **[MAJOR] Artillery on call for everyone.**
  - Any squad leader of any squad (logistics included) who sees one enemy beyond 350 m can bring 155 mm battery fire onto him ([WindowFires.cs:165-191](src/Conquest/Window/WindowFires.cs:165)).
  - Minimum safe distances of 200 m (mortars) and 300 m (guns) ([Artillery.cs:65](src/Conquest/War/Artillery.cs:65)) are well inside US "danger close" (600 m for both). Fire that close is an exceptional, commander-approved call, not the norm.
  - Real fire requests go through forward observers or fire support teams, with priority targets and approval.
- **[MINOR] The Mediterranean island sounds temperate.** `Biome.Temperate` is hard-coded for the window ([WindowMap.cs:27](src/Conquest/Window/WindowMap.cs:27)), so the acoustics climate (temperature, humidity, ground softness) is the temperate one.

---

## 3. The battle engine (shared by the battle maps and the window)

### 3.1 [MAJOR] The wound model

- **Everyone who goes down dies at 150 s untreated, stable or not.**
  - `MaxDown = 150.0` ([Body.cs:45](src/Combat/Body.cs:45)). A broken leg with little bleeding is as fatal as a cut femoral artery.
  - Real casualties without major vascular injury survive for hours.
- **Bleed rates are 10–100× too fast.** (Rates are a share of blood volume a second, taking about 5 L; [Body.cs:106-129](src/Combat/Body.cs:106).)

  | Wound | Rate | Down after | Dead after | Real |
  |---|---|---|---|---|
  | "leg wound" | 0.005/s, about 1.5 L/min | 90 s | | minor wounds lose 10–50 mL/min |
  | "chest wound" (no major vessel) | 0.009/s | about 50 s | about 70 s | |
  | femoral | 0.035/s | 13 s | 19 s | about 2–5 min to exsanguinate |

  Self-aid only halves internal bleeds, so almost every torso hit kills in about 2–2½ minutes without a medic. That alone makes embodied fights far deadlier than the war's 20% killed outright plus 30% of the downed bleeding fatally.
- **Medics bring people back like a game.** "Revive": 6 s brings a man with a punctured lung back on his feet with trauma capped at 70 ([Body.cs:175-191](src/Combat/Body.cs:175)). Real casualties of that kind are stretcher cases. This is a Squad-style mechanic in a project that puts fidelity first.
- **Missing:**
  - no carrying or dragging of casualties by bots (buddy aid stops the bleeding but doesn't move the man);
  - no casualty collection point;
  - no stretcher teams;
  - no ground evacuation. Only the battle maps' helicopters do medevac, and in Conquest the helicopters stay with the army's aviation by the port.
- *Suggestion:* move to published survivability timelines (for example "time to unconsciousness" by injury class). Make "down" a state that can last hours. Make the medic stabilise, not resurrect.

### 3.2 [MAJOR] Bot reflexes and aim

- **Reflexes of an esports player.** [Personality.cs:48-53](src/Bots/Personality.cs:48)
  - `ReactionS` is 0.15–0.36 s from noticing to starting the flick.
  - `TurnSpeed` is 230–560°/s.
  - `FlickTau` is 0.13–0.45 s.
  - **Measured end to end, they aren't superhuman.** With the exact sighting event the telemetry now logs, the Valley match's time from a bot picking a man out to its first round at him was:
    - a median **5.3 s**;
    - **0.8 s** for the fastest tenth;
    - **0.7 s** at the quickest.
  - The fire-discipline and awareness logic around the reflexes keeps the result human. (A first estimate from half-second samples read "a fifth within 1 s"; that was the sampling.)
  - Compare it with the Army's time-to-first-round measurements (`BOT-VERIFICATION.md`) before tuning anything. The too-accurate fire (7.4–9.4 rounds a hit) is the problem to chase, not the reaction.
- **The aim model is a mouse model.** It's "modelled on how people aim with a mouse" ([BotAim.cs:6](src/Bots/BotAim.cs:6)). That fits a game opponent; it doesn't fit combat marksmanship under stress.
- **The pillar works against fidelity.** DESIGN Pillar 2 says "Bots that feel like players … greedy, hold grudges". That's the opposite of doctrine-driven soldiers, and the doctrine pass is fighting it.
- **Bots stand too much.** On their feet 84% of the time, and 37% standing even under heavy fire. Real infantry in contact spend most of their time prone or behind cover.
- **Callsigns on the battle maps** ("Viper", "Ghost", "Jinx") are a gamer touch in a realism game.

### 3.3 Weapons, ballistics and vehicles

- **[MAJOR] Every army fights with the same infantry kit.** Every side has the M4, M249, M320, the M72 LAW, the SR-25 and Stinger. There's no AK-74M/PKM/RPG-7/SVD, no L85/L7/NLAW, no GPMG at all (M240, PKM, L7), and no pistols.
- **[MAJOR] Tanks are crewed by two.** They have two seats, driver and gunner ([VehicleDef.cs:322-323](src/Vehicles/VehicleDef.cs:322)), and IFVs have no commander's seat. So the commander and loader of an Abrams, the commander of a T-72 or a Bradley, walk alongside as riflemen. A tank has no commander's independent sight and no hunter-killer drill.
- **[MAJOR] Every vehicle class has one armour value for all three factions.** A BMP-2 is as well protected as a Bradley or a Warrior, and a BTR-82A as a Boxer. Only the tanks' front plate differs. Each face (front, side, rear, top) is a single number: there's no turret-against-hull, no weak lower glacis, and no ERA.
- **[MINOR] Bullets ignore the wind.** The game has a wandering wind, but only sound uses it ([Ballistics.cs:212](src/Combat/Ballistics.cs:212)). Drag is one quadratic constant: no Mach-dependent Cd, no air density. At 600 m a marksman's round should drift tens of centimetres in a moderate crosswind.
- **[MINOR] Data slips.**
  - The LAW is described as "84 mm" though it's named 66 mm, and its muzzle velocity is 200 m/s (the M72's real figure is about 145).
  - The 2S25 Sprut is given a 105 mm gun (real 125 mm).
  - IFV and tank ammunition loads in [Orbat.cs:475-491](src/Conquest/War/Orbat.cs:475) are commented with per-model figures (Bradley 900, Warrior 230, M1A2 10,800 coax), but the code gives every model one value (400, 2,000 or 4,000).
- **[MINOR] Sight stops at 1 km.** Bots can't pick anyone out beyond 1,000 m ([BotSenses.cs:472](src/Bots/BotSenses.cs:472)), even through a vehicle's thermal sight against infantry.

### 3.4 [MAJOR] The player against the bots

- **The player moves on game rules.** [Player.cs:169-174](src/Player/Player.cs:169)
  - The normal pace is **3.4 m/s**, which is the bots' "run" ([Bot.cs:611](src/Bots/Bot.cs:611)).
  - It costs no stamina, though a bot is winded at that pace in about 2½ minutes.
  - The sprint is 5.8 m/s for 14 s, and recovers in about 7 s.
  - Jumping is 4.2 m/s up with a fighting load.
  - There's no penalty for moving sideways or backwards; bots are held to 1.8 and 1.2 m/s.
  - The player is a different species from his squadmates.
- **Death costs little.** You "carry on as a squadmate" after 10 s, choosing who (1–9). That's narratively justified, but in practice a respawn into the best-placed man, with no cost. Consider removing the choice, or limiting it to the next man in the chain.

### 3.5 Things that are faked

- **[MINOR] The firing range fakes distant fighting.** `DistantWar`, used only there ([Main.cs:101](src/Game/Main.cs:101)), stages it at 5 fixed sites, with random gunfire and real permanent craters ([DistantWar.cs:15](src/World/DistantWar.cs:15)). It's harmless on a range, but it's still the only "distant war" the game has. Its comment calls it "the seed of the abstract battle sim", yet Conquest, which has a real abstract war, plays nothing at all (§2.2).
- **Some checks use the truth instead of what the side knows.**
  - Medevac takes off only with "no enemy within 150 m", by ground truth ([MotorPool.cs:724](src/Vehicles/MotorPool.cs:724)).
  - FPV targets are picked by truth within 40 m ([DroneOps.cs:172](src/Drones/DroneOps.cs:172), [200](src/Drones/DroneOps.cs:200)).
  - In the window, a site is "hostile" if *any* enemy is in it, known or not ([ConquestWindow.cs:180](src/Conquest/Window/ConquestWindow.cs:180)).
  Each should ask the side's intel.

---

## 4. The island generator (`src/Conquest/*.cs`)

This is the strongest part of the project: the Hack, Horton, rank-size and coastline checks all pass on seeds 1–3. Weaknesses:

- **[MAJOR] The land cover isn't Mediterranean enough.**
  - Seed 1: orchards 28%, fields 6%, **grass 0%**, maquis 20%, oak 19%.
  - Grass exists only above the tree line or 1,250 m ([LandCover.cs:65-69](src/Conquest/LandCover.cs:65)).
  - Fields need slope under 4° *and* a noise value under 0.3 ([LandCover.cs:157](src/Conquest/LandCover.cs:157)), so most farmland becomes orchard.
  - Real Mediterranean islands have large shares of pasture and arable land, and abandoned terraces. Open ground drives sight lines and the whole infantry fight.
- **[MINOR] Known gaps.** The doc already lists the weak rain shadow, no deltas or coastal plains, one climate, and no quays at ports. Lakes come out at 0 km² on seed 1.
- **[MINOR] Uneven starts.** Starting areas are equal in area but not in what they hold: seed 1 has ALPHA's area at 29,100 people's worth against CHARLIE's 46,670. The armies are also raised unequal (50.2k–54.7k).
- **[MINOR] An island without three ports crashes.** If Holdings finds fewer than three starting ports, `Sides[s].Port` stays −1. `Supply.Setup` then indexes `Towns[-1]` and crashes. There's no graceful failure or re-roll.

---

## 5. Game design: Conquest as a game

- **The design pulls two ways.**
  - `DESIGN.md` opens: "The goal is fun to play, not the deepest possible simulation… realism … stays out where it only adds chores."
  - `CLAUDE.md` says: "Simulation fidelity first. Nothing is faked."
  - The code follows both by turns: there are revives, respawn-like carry-on and omniscient HUD counts beside doctrine-paced attacks. Decide which rule wins where they conflict, and write it down.
- **The species is judged against human history.** It has no morale and fights to 50%, but it's calibrated to Dupuy's *human* casualty rates, which include men breaking and surrendering. Either calibrate the fire model alone, with morale-free behaviour on top, or accept higher losses and say so.
- **Nothing gives the war a shape.** There are no goals for the player beyond surviving. There's no progression (deliberately no promotions), no journal and no time skip. With no save (§2.5), there's no long arc.
- **There's no onboarding.** The Conquest tab explains itself in two paragraphs. There's no tutorial for:
  - squad drills;
  - how to follow your leader's plan (ORP, SBF, LD);
  - what the briefing means.
- **There's no way out of a long session.** No pause-and-save, and no "skip to the next action".
- **Fights are compressed for time.** The deliberate attack's timings were cut to fit battle maps (a 3-minute leader's recon, assaults within 6 minutes). The user wants 30–60 minute fights, and doctrine's are hours. That's acknowledged in the design, but every timer in `Squad.Deliberate` is still a battle-map compromise.
- **Accessibility and comfort.** I found no hold-or-toggle choice for aim or crouch (only rebinding), and no colour-blind option for the team colours (blue, red, yellow). Sound cues (incoming shells, which way gunfire comes from) have no visual equivalent for players who can't hear well.

---

## 6. Documentation, process and code health

- **`DESIGN.md` has drifted.**
  - Its "Measured" numbers are stale. The abstract assault test now gives attackers 13% (19% with mortars), not the 25% the doc quotes.
  - Headings still say "Conquest mode (planned) … nothing is built yet".
  - The milestones say "Bot duels (current)".
  - `README.md` describes a "2x2 km map, 30-60 minute matches", with no Conquest.
- **The assault test is mislabelled.**
  - "9 companies against 3" is reported as 3:1 throughout. In men it's **1,374 against 255, 5.4:1** (plus 30 BMPs on the defence). It's scored against the 3:1 historical mark.
  - Either use 3:1 in men, or judge it against 5:1 marks.
- **`coherence.py` decides "in contact" wrongly.**
  - It marks a man in contact only if *he* fired in the last 20 s, or if he was suppressed during the first 10 minutes ([coherence.py:44](tools/coherence.py:44)).
  - The comment says "fired by or near them".
  - So men under fire who aren't shooting count as "moving out of contact" after minute 10. The doctrine pass's speed metric is contaminated.
  - It also swallows every JSON error silently.
- **Build warnings and the like.**
  - 6 build warnings, including:
    - `TerritoryMode.Owner` hiding `Node.Owner` (CS0108). That's a real hazard: Godot uses `Owner` for scene ownership.
    - a possible null dereference at [AssaultTest.cs:78](src/Conquest/War/AssaultTest.cs:78);
    - a null `who` at [MotorPoolCombat.cs:191](src/Vehicles/MotorPoolCombat.cs:191);
    - an unused field at [BotBrain.cs:900](src/Bots/BotBrain.cs:900).
  - Comment and code disagree: [MotorPoolCombat.cs:186](src/Vehicles/MotorPoolCombat.cs:186) says "40 s + 1 s per 5 m", and so does DESIGN, but the code is 60 s + 1 s per 3 m.
- **`.uid` files.** Only 18 of 133 `.cs.uid` files are tracked. Every fresh import (any new worktree, or a cloud session) leaves 115 untracked `.uid` files. Commit them all.
- **No automated tests or CI.** Every check is a multi-minute headless run judged by eye, and "run to run, a 5-minute embodied fight varies a lot". The project needs:
  - a fast deterministic regression suite for the abstract war, with fixed seeds and tolerance bands on dead per day, rounds per casualty, places changing hands, and hungry units;
  - unit tests for pure functions (`Body`, `Ballistics`, `Penetration`, `MoveGrid`, `Orbat` counts);
  - a CI job that builds and runs them.
  Bugs like B1 and B2 would be caught by asserting establishment counts and break points.
- **The big files carry history.** `BotBrain.cs` is 2,936 lines and `TerritoryMode.cs` 1,925. The "(It used to …)" comments are valuable history, but they now make up a large share of some files and bury the current rule. Consider moving older ones into DESIGN.

---

## 7. A suggested order of work

1. **Correctness in the war.** These are cheap and they change every number:
   - B1 armour rounding;
   - B2 break rule;
   - B3 recomputing mobility;
   - B4 window deaths;
   - B5 tickets;
   - B6 cover after moving;
   - B7 full-fight immunity;
   - B8 supply through captured ports;
   - B9 the depot deadlock.
   Then re-run the 7- and 30-day reports and update DESIGN's tables.
2. **The wound model** (§3.1). It's the biggest single source of the gap between the two levels.
3. **Cover and concealment in the window:**
   - field walls and terraces from the island's cover;
   - real stem densities, or bushes that block sight;
   - concealment from crops and grass.
   This also fixes the war's "even odds at 1 km" assumption not being true on the ground.
4. **Handover:**
   - promote by relevance;
   - let units over the cap fight the window's units abstractly (or raise the cap and LOD the brains);
   - hold squads, not whole companies;
   - write resupply into the bots;
   - carry wounds both ways.
5. **Persistence:**
   - seed window detail and towns in island coordinates;
   - save the war's records, the aftermath and the dead's one-line records;
   - put the war on a worker thread, with a loading screen and progress.
6. **Weapons and organisation realism:**
   - ATGMs at real ranges;
   - IFV missiles in the war;
   - modern tank fire control;
   - per-faction kit;
   - full crews;
   - per-faction armour.
7. **Command and intelligence:**
   - reporting delay and noise;
   - commanders who matter;
   - doctrine differences in decisions;
   - experience.
8. **Strategic arc:** the economy and vehicle replacement, an end condition, weather and seasons, the distant layer, and sound for the abstract war.
9. **Process:** CI, a regression suite, and refreshed docs.

---

## Appendix: what was run (headless, Godot 4.7.2 .NET, built from this commit)

| Run | Result |
|---|---|
| `dotnet build` | OK, 6 warnings (§6) |
| worldgen seeds 1–3 | all five landscape checks "ok" on each; seed 1: 5,094 km², 9 ports, cover as in §4 |
| war seed 2, 7 days (73 s) | ALPHA 3.5%/day, BRAVO 1.8%, CHARLIE 2.0%; 100 rounds per casualty; KIA:WIA 1:3.1; ALPHA tickets 53,212 after 3,999 dead |
| war seed 2, 30 days (295 s) | ALPHA 11,371 dead, tickets 59,857; land 31/35/30%; CHARLIE's depot empty all month; CHARLIE's hungry at a median 330 h |
| war seed 1, 7 days (62 s) | BRAVO 600 tanks; 44 of BRAVO's 59 battalions "brigade has no tasks"; CHARLIE 2.8%/day |
| assault seed 2, 12 runs | attackers 13% (3% killed), defenders 44%, carried 8 of 12, median 135 min, 385 rounds a hit |
| assault seed 2, `arty=1`, 12 runs | attackers 19%, defenders 50%, carried 9 of 12, median 146 min, 428 rounds a hit |
| Valley 33 a side, 6 min | no exceptions; 59 killed and 45 downed; M4 7.4 rounds a hit, M249 26; 84% standing, 4% prone; 216 "stuck" events (117 near vehicles) |
| window seed 2, day 3, `join=0`, 4 min | no exceptions; island 19 s, war to day 3 18 s, navmesh 4.4 s; embodied 179 against a cap of 160 (43 ALPHA, 150 BRAVO, 20 of them clones); BRAVO's embodied men were mostly regimental HQ, signal, engineer, medical and recon companies about 1 km back; one BMP killed 11 Americans in 70 s from about 200 m, 450 m ahead of its infantry |
| Al Hamra, 22:00, rain, moon 0.2, 4 min | no exceptions; 8 killed; bots on their feet 96% of the time |

All runs left `run.err` (the stderr files) empty: no exceptions anywhere.
