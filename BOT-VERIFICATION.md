# Verifying bot behaviour: research, and a design for Ridgeline

2026-10-11. This answers: is there a solid way to verify that bots act properly, in real time? And how do you tell "proper" from "wrong" when you're looking for faults and can't trust your own eye?

## The short answer

**Nobody can verify behaviour 100%.** Human behaviour models are the least validated part of military simulation:

- A Naval Postgraduate School study found that the most highly validated models are physiological ones and a few weapon models. Few soldier-level or unit-level behaviour models come close [Goerger].
- A 2002 review called the field "relatively immature, with no theory, few tools and techniques" [Harmon et al. 2002].

What *is* achievable, and what serious simulation work does, is:

1. **Every bot, every half second, checked against rules written down beforehand.** Each rule comes from a source, not from your impression. This can run headless and in real time.
2. **Many patterns at once, compared with real data.** This means individual, squad and unit numbers measured over many runs, not one.
3. **Human judgement that can't be biased toward finding faults:**
   - you watch only the flagged moments;
   - you compare clips blind;
   - you score against a fixed rubric.

That turns "watch everything and trust my eye" into "watch the 15 moments the checker flagged, and decide each one against a written standard".

**Your worry is a documented problem, not a personal failing.**
- Goerger's study found that an expert's bias has a statistically significant effect on how he rates simulated soldiers' urban combat skills. He also found that the *rating scale* used changes how much bias gets in [Goerger].
- An Army review lists "reliance on Subject Matter Experts who cannot be expected to be unbiased" among the field's main weaknesses [DTIC reviews ADA495197, ADA616306].

The cure is the same everywhere: decide what "right" looks like before you look, in writing, from a source, and measure.

---

## What the field does: seven ideas worth taking

**1. Verification is one thing, validation another** [Sargent].
- *Verification* asks: does the code do what the rule says?
- *Validation* asks: is the rule how soldiers actually behave, for this purpose?

Most of what feels wrong in a match is one of three things:
- a verification failure: the code doesn't do what was intended (the soldier-cloning bug in REVIEW.md);
- a validation failure: the intent itself is unrealistic;
- an expectation failure: the bot is right, and our picture of soldiers is wrong.

Separating them is half the battle.

Sargent's standard list of validation techniques maps straight onto Ridgeline. "Traces" (following one entity through the run) and "event validity" are what the telemetry already supports. "Extreme condition" and "degenerate" tests are scripted micro-scenarios. "Internal validity" is run-to-run spread, and "Turing tests" are blind comparisons.

**2. Write the standard down the way the Army does: task, conditions, standards** [T&EO].
- The Army judges units in training with *training and evaluation outlines*. Each one gives:
  - the task;
  - the conditions;
  - the standard;
  - a list of performance steps that an observer-controller marks GO or NO-GO.
- That's exactly the job: someone watches a fight and decides, step by step, whether it was done right, against a published list rather than his own feel.
- The battle drills (react to contact, break contact, the squad attack) each have one.
- One caution from the Army's own research: plain GO/NO-GO "may not be sufficiently discriminative". So record the measured value too (how many seconds, how many metres), not just pass or fail [AD1075577].

**3. Match many patterns at once, at several scales** [Grimm et al. 2005, pattern-oriented modelling].
- Many wrong models can reproduce any one pattern; far fewer can reproduce several independent patterns at different scales together.
- For Ridgeline, keep at least three scales in view every time:
  - **individual:** posture under fire, how long a rush lasts, time to the first shot;
  - **squad:** dispersion, share moving at once, time to return fire;
  - **unit:** casualties per hour, rounds per hit, fight length.
- Then a fix that improves one number but breaks another shows up at once. That's the "whack-a-mole" problem in DESIGN.md's calibration rounds.

**4. Real data, corrected for combat.**
- **Exercises overstate lethality.** UK Ministry of Defence historical analysis compared real battles with laser-instrumented field trials of the same kinds of action. Small arms in real combat had about **a seventh to a tenth** of their exercise effectiveness [Rowland; Dupuy's summary]. Any yardstick taken from ranges or exercises has to be scaled down that much.
- **Suppression is the missing link.** A statistical test of NTC (the US Army's National Training Center) engagement data against Lanchester's laws found an exponential law fits better than the square law. It names suppression as the missing link, and concludes that simulations without it give distorted results [Johnson 1989, ADA225484].
- **What suppresses.**
  - Field experiments found suppression falls off linearly with how far a round passes from you, within the distances tested.
  - It rises with the number of rounds and with how loud they sound.
  - Rounds you see as well as hear (tracer, strikes) suppress more than sound alone [AD0519874].
  - The projectile's energy, as perceived loudness, is the main factor [ADA071116].
- **Engagement ranges.**
  - Returning NCOs and officers put about half of Afghan engagements beyond 300 m [Ehrhart].
  - Iraq firefights were mostly under 300 m [Ehrhart, via military.com].
  - These come from interviews: treat them as rough bands, not targets.
- **Movement under fire.** The 3–5 second rush is the doctrinal limit on exposure while moving under fire [IMT; ROTC handout of FM 3-21.8].
- **Engaging a target is short, intense work.** A Canadian study found that once a target was detected, the engagement took 6 s or less of intense focus [DRDC].
- **Time to first round** has been measured by the US Army: from target appearance to first round fired, including shouldering, sight picture and trigger [AD0713552]. DTIC blocked automated download, so I couldn't take numbers from it. It's the yardstick for the reaction-time question below and should be read in full.

**5. Compare against a person doing the same thing** [Wallace & Laird, "behaviour bounding"; the FLAIRS approach]. Give a human expert the same simulated situation the bot faces, record both, and compare the action sequences automatically. For Ridgeline that means:
- play the rifleman, or the squad leader through the squad commands, in a fixed scenario;
- record it with telemetry;
- compare against the bots in the same seat.
You don't need to be an expert soldier for this. A careful player following the field manual is a better yardstick than either your eye or the bot.

**6. Monitor play automatically** [runtime verification in game testing].
- Game-testing research writes expected behaviour as temporal-logic rules and checks every play trace against them.
- A thesis using the iv4XR framework found the monitors "caught invalid state transitions automatically" and recommended them as an aid to human testers rather than a replacement.
- That's the shape of the checker below.

**7. Doctrine is a norm, not a law.** NATO's human-factors workshop notes that computer-generated forces are mostly doctrine-based rules, while "doctrine is seldom observed in a pure form in practice" [NATO HFM-143]. So most rules should be *rates with bands* ("upright under fire for more than 5 s: rare"), and only a few are *never* rules (one soldier in two places, firing through a squadmate).

---

## A verification system for Ridgeline

### Layer 1: the rulebook (written before looking)

One file of rules. Each rule has:
- a plain statement;
- where it comes from;
- how it's measured from telemetry;
- whether it's a **never** (an invariant) or a **norm** (a rate with a band);
- whether today's telemetry can measure it.

**Built: [`tools/rules.toml`](tools/rules.toml)**, 16 rules. Each has a statement, a source, its thresholds, and the telemetry fields it needs. A rule without a source doesn't go in. Thresholds live in the file, not in the code.

| ID | Rule | Kind | Source |
|---|---|---|---|
| R01 | Not upright outdoors under heavy fire, with nothing hard between him and his threat, for more than ~5 s | norm | 3–5 s rush (ATP/FM 3-21.8) |
| R02 | No long upright runs in an enemy's view | norm | same |
| R03 | A squad under effective fire, able to see the shooter, returns fire within ~10 s | norm | react-to-contact drill T&EO |
| R04 | No first round faster than a person can fire (0.8 s floor, provisional) | norm | time-to-first-round studies [AD0713552] |
| R05 | Not bunched within 2 m outdoors in contact | norm | ATP 3-21.8 dispersion |
| R06 | Not standing still and silent in contact for 30 s | norm | battle drills |
| R07 | Not more than 80 m from every squadmate for a minute | norm | squad cohesion |
| R08 | Armour not 150 m+ ahead of its infantry near the enemy | norm | ATP 3-21.71 |
| R09 | Not most of a squad moving at once in contact, outside the assault | norm | bounding overwatch, fire and movement |
| R10 | A man who can't hurt armour doesn't stay in its sight | norm | the 2026-10-11 infantry-and-armour rule |
| R11 | No rounds aimed past the weapon's maximum effective range | norm | weapon manuals |
| R12 | No friendly hits from aimed fire | never | fire discipline |
| R13 | Every war soldier in the world at most once | never | data integrity |
| R14 | Nobody spawns inside the ground or a building, under the ground, or in water too deep to wade | never | data integrity |
| R15 | Nobody spawns within 50 m of an enemy | norm | handover |
| R16 | Nobody handed back wounded or dead comes back healed or alive | never | handover |

The *pattern* yardsticks (layer 3) sit beside these.

### Layer 2: the checker (built: [`tools/doctrine_check.py`](tools/doctrine_check.py))

`python tools/doctrine_check.py run.jsonl` reads the rulebook and tests every bot at every half-second sample, and every event, against it. It prints:
- each never rule's PASS or FAIL, with every breach;
- each norm's rate per 10 bot-minutes in contact (OUT OF BAND once a band is set);
- the worst episodes, each with the time to look at in the replay and **the bot's own reason at that moment**;
- the patterns.

Rules a recording can't support (made before the fields it needs) say "not checkable". The exit code is 1 if a never rule failed, so it can gate test runs. A 6-minute match takes a few seconds.

**Its tests: [`tools/test_doctrine_check.py`](tools/test_doctrine_check.py).** Every rule runs on recordings made up for it, one it must flag and one it mustn't. The tests also check that every rule in the file has a source and a test.

**What it found** (recordings with the new telemetry, 2026-10-11):
- **Valley, 33 a side, 6 minutes** (98 bot-minutes in contact). No never rule broke. Among the norms:
  - **R04:** from picking a man out to the first round at him took a median **5.3 s**, the fastest tenth **0.8 s**.
    - The first prototype, working from half-second samples, read "a fifth under 1 s". That was the sampling. The exact `spot` event shows the bots aren't superhuman here, whatever `Personality`'s raw numbers suggest.
  - **R03:** a squad under effective fire took a median 5.1 s to fire back. There were 12 cases of 20–32 s while someone in the squad could see the shooter.
  - **R02:** 88 long upright runs in the enemy's view, mostly flanking teams ("flanking with Bravo", 20–25 s). Now that "in view" is measured, these are real.
  - **R01:** 11 men upright under heavy fire for 5–13 s, mostly "breaking contact".
  - **R09:** 9 times most of a squad was on the move at once in contact (6 of 6, "Travelling").
  - **R10:** 3 men in sight of armour for up to 30 s, "pushing through to the objective".
- **Conquest window, day 3, 3 minutes:**
  - **R13 FAIL**, the soldier-cloning bug from REVIEW.md, found by the rule with nobody looking: "war soldier 50485 is 9 people".
  - **R14:** 11 crewmen came in inside their own vehicle's hull. They're put within 3 m of a 7 m vehicle's centre. That's reported, not failed.
  - **R04:** vehicle gunners fired 0.2–0.7 s after picking out a man 300–500 m away through the gun sight. That's the superhuman tail: a turret has to be laid.
  - **R08:** a BMP "overwatching the transport truck", working with no infantry, 151 m from the enemy.
  - **R10:** six Americans "in cover" in a BMP's sight for 14–39 s.
- **A pattern** (Valley): a standing man went to ground within 3 s of a round passing 49% of the time at 0–1 m and 37% at 4–6 m. Field experiments found suppression falls off linearly with miss distance [AD0519874]; here it barely changes, and only half go down even for a round within a metre.

**Two lessons from building it:**
- **The checker needs its own tests.** The first version reported a man "upright under heavy fire for 58 s". He'd been downed in the first second and revived a minute later, and the episode was never closed. That case is now a test.
- **Rules are only as good as what's logged.** The first version couldn't tell a flanking team running behind a hill from one running across the enemy's front. The `seenby` field now says how many enemies actually have him in sight.

### Layer 3: patterns at three scales, over many runs

DESIGN.md already notes that "a change has to beat [run-to-run noise] to be judged on one run". So:
- run each setting 10 or more times with different seeds;
- report the median and the spread;
- accept a change when its band overlaps the yardstick **and** it doesn't push another pattern out of its band.

| Scale | Pattern | Ridgeline now | Yardstick |
|---|---|---|---|
| individual | upright and exposed under heavy fire | 37–40% of samples under heavy fire are standing (Valley) | low; the rush rule says seconds |
| individual | picked out to first round | median 5.3 s, fastest tenth 0.8 s; vehicle gunners 0.2–0.7 s (window) | to take from the time-to-first-round studies |
| individual | engagement ranges | median aimed range 235–490 m by side | Afghanistan about half beyond 300 m; Iraq mostly under 300 [Ehrhart] |
| squad | going to ground against miss distance | 49% at 0–1 m, 37% at 4–6 m (Valley) | falls linearly with lateral miss distance; more with rounds and loudness [AD0519874] |
| squad | time to return fire | median 5.1 s (Valley) | react-to-contact T&EO: immediately |
| unit | rounds per hit, rifle | 7.4–9.4 | combat effectiveness a seventh to a tenth of exercises [Rowland]; historical rounds per casualty in the thousands |
| unit | casualties per side per hour, fight length | see REVIEW.md | Dupuy rates; NTC exponential law [Johnson 1989, ADA225484] |

The empty cells are honest. Where the source wasn't read, the number has to be pulled from the cited report before it's used as a target, not invented.

### Layer 4: people, without the bias

- **A "that looks wrong" key in game.**
  - One key writes a `flag` event into the telemetry, with the time, your position and the bots near you.
  - Afterwards the checker classifies every flag into one of three:
    - **a rule broken**: a bug, so fix it;
    - **every rule kept, but it looked wrong**: either the rule is wrong (look for a better source) or the expectation is (you've learned something);
    - **no rule covers it**: write one, with a source, or let it go.
  - Your hunches stop being lost or over-trusted. Over time the flag-to-rule-broken ratio tells you how good your eye is.
- **Blind pairs.**
  - `replay.py` shows two clips of the same scripted scenario from two builds (or bot against human), order random and labels hidden.
  - You rate each on a fixed scale with written anchors for each score, for example "1: no soldier would do this … 5: a squad leader would do exactly this".
  - Count how often the new build wins.
  - Goerger's point is that the scale's design changes how much bias gets in, so anchor it in behaviour, not adjectives.
- **The Turing test, done properly.** Mix clips of human-played squads (you and friends playing the bot roles, by the manual) with bot clips. Where you can't pick the bots better than chance on a rubric item, that item passes.
- **Scripted drills: unit tests for behaviour.** These are small fixed scenarios with a known right answer, each run 20 times and measured:
  - a rifleman in an open field under machine-gun fire at 300 m;
  - two squads meeting at 250 m;
  - a grenade landing 3 m away;
  - an IFV with its squad approaching a position;
  - an enemy behind a solid wall. This is a *degenerate* test: no hits should get through.
  They're far cheaper to judge than a 99-bot match, because there's one thing to look at.

### Real time

The same rules are cheap enough to run live every half second:
- **Overlay.** Show violations in the F6 bot-debug overlay (a red mark and the rule name over the bot) and as a line in a log. Test runs get an optional "pause on violation", so you can look at the moment it happens.
- **Watch only what's flagged.** In a 99-bot match you watch only the bots that are lit, with the brain's own reason already on its label (F6 shows state and why).

That's as close to "verify everyone in real time" as it gets: the machine watches everyone, and you watch what it can't decide.

### Telemetry (added 2026-10-11, `Telemetry.cs`)

- **Unique names.** A person whose callsign someone else in the world already has is "callsign #2". The day-3 window had 21 callsigns shared by several men among 193, so every tool merged them. A battle map's man coming back keeps his name, as before.
- **On every sample row:**
  - pace: walk, run, sprint, crawl;
  - fire team, and whether he leads;
  - what his squad is doing: a drill, a step of the attack, or its march order;
  - **how many enemies have him in sight by their own senses**, and how many of those from armour;
  - **cover between him and his threat** (one ray);
  - the war soldier he is.
- **On vehicle rows:** the crew's squad and the squad it works with.
- **Events:**
  - `crack`: a near miss, with the distance, which suppression is measured against;
  - `spot`: the moment a bot picks someone out, and with what;
  - `spawn`: where each person first appears, inside the ground or a building, inside a vehicle, under water, and the nearest enemy;
  - `promote` and `release` for a window's handover, with each man's state.
- **Files.** A window that moves writes `-2`, `-3` files instead of wiping its own recording.
- **No side effects.** None of it changes the game. Squad leader and fire teams are read without the side effects their getters have.
- **Old recordings** still load; the rules that need the new fields say "not checkable".

### What this can't do

- **Wisdom.** It can't prove the tactics are *wise*. It can show they keep to rules you trust and produce patterns that match real data. Judgement stays with you for situations no rule covers.
- **The species.** Real data comes from humans who flinch, break and surrender. Ridgeline's soldiers don't. Decide, rule by rule, which yardsticks still apply to them: the physics of exposure and suppression do, and morale-driven rates may not.
- **False alarms.** Thresholds will flag some correct behaviour, so the flag-classification step matters. A rule that mostly flags correct behaviour gets fixed or dropped.

### Order of work

1. ~~**Telemetry fields:** ids, exposure, cracks, spawn/promote/demote events.~~ Done 2026-10-11.
2. ~~**The rulebook as a data file**, read by the checker, with a test recording for each rule.~~ Done 2026-10-11 (16 rules).
3. **The scripted-drill mode, and a runner** for 10–20 seeds that prints medians and sets the norms' bands from them.
4. **The in-game flag key**, and flag classification in the checker.
5. **Blind pairs and the anchored rubric** in `replay.py`.
6. **The live overlay** in F6, and pause-on-violation for test runs.

---

## Sources

- Sargent, R. G., verification and validation of simulation models: [tutorial (PDF)](https://users.cs.northwestern.edu/~paritosh/papers/sketch-to-models/Sargent-tutorial-on-validation-and-verification-of-models.pdf), [WSC 2005](https://www.informs-sim.org/wsc05papers/014.pdf), [Journal of Simulation 2013](https://ideas.repec.org/a/taf/tjsmxx/v7y2013i1p12-24.html), [overview](https://en.wikipedia.org/wiki/Verification_and_validation_of_computer_simulation_models)
- Goerger, S., *Validating computational human behavior models: consistency and accuracy issues* (NPS): [record](https://calhoun.nps.edu/entities/publication/df5f6be6-e893-4c5f-86e7-94c51f037f0f), [DTIC ADA495197](https://apps.dtic.mil/sti/pdfs/ADA495197.pdf)
- [DTIC ADA616306](https://apps.dtic.mil/sti/pdfs/ADA616306.pdf) (criticisms of human behaviour representation validation: SME reliance, lack of objective methods)
- [NATO RTO-MP-HFM-143](https://www.sto.nato.int/publications/STO%20Meeting%20Proceedings/RTO-MP-HFM-143/MP-HFM-143-06.pdf) (CGF behaviour is doctrine-bound; doctrine seldom observed in pure form)
- Wallace & Laird, [Toward automatic knowledge validation](https://www.academia.edu/75134518/Toward_automatic_knowledge_validation); [Behavior Bounding (IJCAI 2003)](https://mlanthology.org/ijcai/2003/wallace2003ijcai-behavior); [Behavior Bounding (JAIR 2009)](https://jair.org/index.php/jair/article/download/10589/25334/19683)
- Grimm et al. 2005, Pattern-oriented modeling of agent-based complex systems, *Science* 310: [USGS record](https://pubs.usgs.gov/publication/70161783); critique and practice: [JASSS 15(3)5](https://www.jasss.org/15/3/5.html)
- Training and evaluation outlines and GO/NO-GO scoring: [DTIC AD1075577](https://apps.dtic.mil/sti/html/tr/AD1075577/index.html), [army.mil fact sheet](https://www.army.mil/article/272832/army_advances_air_launched_effects_for_future_vertical_lift)
- Rowland, *The Stress of Battle* (UK MOD): [book listing](https://ca.biblio.com/book/stress-battle-quantifying/d/1297190294); [Dupuy Institute summary](https://dupuyinstitute.org/2017/07/26/human-factors-in-combat-fatigue/); [combat degradation in the urban battle (JORS 1991)](https://iaorifors.com/paper/6423)
- NTC data and Lanchester / suppression: Johnson, R. L., *Lanchester's Square Law in Theory and Practice* (1989), [DTIC ADA225484](https://apps.dtic.mil/sti/html/tr/ADA225484/index.html); using NTC instrumentation data for hit probability, rate of fire, dispersion and speed: [RAND RGSD406](https://www.rand.org/pubs/rgs_dissertations/RGSD406.html)
- Suppression field experiments: [DTIC AD0519874](https://apps.dtic.mil/sti/pdfs/AD0519874.pdf), [ADA071116](https://apps.dtic.mil/sti/pdfs/ADA071116.pdf), [AD0723416](https://apps.dtic.mil/sti/pdfs/AD0723416.pdf), [ADA081134](https://apps.dtic.mil/sti/tr/pdf/ADA081134.pdf)
- Engagement ranges: Ehrhart, [*Increasing Small Arms Lethality in Afghanistan* (DTIC ADA512331)](https://apps.dtic.mil/sti/pdfs/ADA512331.pdf); [military.com summary](https://www.military.com/defensetech/2010/03/01/taking-back-the-infantry-half-kilometer)
- Rushes and individual movement: [individual movement techniques](https://en.wikipedia.org/wiki/Individual_movement_techniques), [ROTC handout](https://uakron.edu/armyrotc/MS1/33.pdf), [ATP 3-21.8 (2024)](https://tamucc.edu/education/departments/rotc/files/atp_3-21.8.pdf)
- Time to first round and engagement timing: [DTIC AD0713552](https://apps.dtic.mil/sti/pdfs/AD0713552.pdf), [DTIC ADA478986](https://apps.dtic.mil/sti/pdfs/ADA478986.pdf)
- Runtime monitoring in game testing: [LTL specifications for NPC behaviour (CEUR)](https://ceur-ws.org/Vol-3599/paper_2.pdf), [iv4XR NetHack thesis (Utrecht)](https://studenttheses.uu.nl/handle/20.500.12932/48467), [truncated LTL for regression-testing bots](https://docta.ucm.es/entities/publication/4d95b32e-0d5b-4d22-8d5a-6773db5e1122)
