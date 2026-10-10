# Ridgeline — design

A singleplayer take on Squad (with Wardogs' economy later): three mercenary
factions fight for control of a 2x2 km mountain map, settlement by settlement. Every other
combatant is a bot. First person, Squad-level realism, 30-60 minute matches.

The goal is **fun to play**, not the deepest possible simulation. Realism goes in
where it adds tension or readability (ballistics, sound, suppression) and stays
out where it only adds chores.

## Pillars

1. **Gunfeel first.** If shooting steel in an empty field isn't satisfying, nothing else matters.
2. **Bots that feel like players.** They hesitate, peek, panic, get greedy, and hold grudges. They don't have aimbot accuracy or brain-dead strafing.
3. **Money is at risk.** Buying gear is a bet, and losing it hurts.
4. **The world goes on without you.** You fight your own fight, while you can hear everyone else's.

## One world, two levels of detail

Every bot, vehicle, corpse, wreck and crater lives in a single **world
model**. A bot is *embodied* (full physics, animation, ballistics) when it's
near the player, and *abstract* (cheap simulation on a coarse grid) when it isn't.
It's the same bot with the same state either way; only the fidelity changes.

- **Abstract combat** between squads is resolved statistically. The inputs are cover,
  numbers, skill, suppression and range. The outputs are exactly the things an embodied
  fight would leave behind: deaths at positions, dropped gear, wrecked
  vehicles, craters, spent ammo.
- **Aftermath persists.** A body is a world object with a position and a loadout.
  When you walk into an area where an abstract fight happened, you find the
  bodies, wrecks and craters where that fight put them. Explosions already
  leave permanent craters today (see `Effects.Explosion`).
- **Promotion and demotion.** When the player comes within about 300 m, abstract
  entities become real nodes at their abstract positions and states: mid-reload,
  suppressed, wounded. When the player leaves, they go back to abstract. The
  handover is the thing to get right. A fight should never visibly "restart"
  when you arrive.
- **Sound is never level-of-detail'd away.** Abstract fights emit the same
  world-space sound events (gunshots, explosions) as embodied ones, at the real
  positions. `DistantWar` is the first stand-in for this.

## Acoustics (built)

- **Distance is simulated, not hand-made.** Loud sounds are synthesised "dry" (what you'd hear
  a metre or two away) and propagated offline by `Acoustics` to a ladder of twelve distances
  (1.5 m to 2.4 km, at most about twice apart past 5 m):
  - air absorption per ISO 9613-1 for the map's temperature and humidity (at 1 km, 8 kHz is ~100 dB down, 1 kHz ~5 dB), which is what turns a crack into a pop;
  - the ground reflection, modelled physically: a spherical wave off porous ground (Miki impedance, ground wave via the Faddeeva function), losing coherence to turbulence at high frequencies and long range, averaged over uneven ground. Up close it's a faint colouring; far off it's the classic dip at a few hundred Hz with the lowest frequencies doubled;
  - both as one minimum-phase (causal) filter per distance, so a distant shot keeps a clean onset.
  At play time the band is picked by chance weighted by log-distance; neighbouring bands are close, so that's a small step in timbre. Far bands are stored at 22 or 11 kHz (the air has left nothing above). Built at startup in ~3 s.
- **Sources.**
  - Blast waves are Friedlander pulses with a negative phase of equal area: no net impulse, so no DC hump or inaudible sub-bass eating headroom.
  - A rifle's low body thump is its near part: full strength by the gun, fading beyond ~6 m. Far off a rifle is a "pop", not a boom.
  - Steel plates ring with a free plate's modes (~500 Hz up), split pairs beating, a random strike point, long below coincidence and radiating weakly there, and a chain jingle.
  - Mortar, rocket, launcher, steel and armour hits go through the same distance physics as guns.
- **Climate per map.** Temperature, humidity, ground softness, turbulence and typical wind come from the biome (desert hot and dry over sand, forest damp and still on a soft floor, highlands cool and windy). The speed of sound follows (340 m/s at 15 °C).
- Every sound is an event at a point, and its wavefront travels at the speed of sound, carried a little by the wind (`SoundWorld`). A plate at 600 m rings about 1.8 s after it swings. A shot from 1 km lands about 3 s after the muzzle flash.
- **Supersonic crack** (`Ballistics.CheckCrack`). The shock leaves the bullet all along its path as a Mach cone. The part that reaches you left a point upstream of the closest approach, when the bullet was there. So it arrives b·√(M²−1)/(M·c) after the bullet passes, from a direction tilted toward the shooter. Beside or behind the gun, outside the cone, there's no crack. The N-wave is Whitham's: its duration grows with miss distance and calibre (~120 µs at 1 m for rifle rounds, longer for .50 cal, autocannon and tank rounds and supersonic rockets), its peak falls as miss distance^-3/4, and it's a little louder for a faster bullet. Heard out to ~150 m (250 m for heavy rounds).
- **Muzzle directivity.** A gun is loudest ahead of its muzzle (Fansler's fit to measured rifles up to a 105 mm gun: 0 / −9 / −18 dB at 0° / 90° / 180°, offset to +3 / −6 / −15 re the old all-round level) and thinner from behind; right behind a shooter, their body dulls it. The reverb is fed the all-round output, so a shot fired away from you is more reverb and less shot. Not applied within a few metres (your own gun) or to the guns of a vehicle you're aboard.
- **Occlusion.** A hill or building between you and a sound makes it play as if from ~3x further (darker) and 5 dB quieter.
- **Weather.** The wind wanders (direction drifting, strength easing every minute or three, gusts). Downwind, sound carries a few dB further; upwind, beyond a shadow boundary tens of metres off in a strong wind, it drops by up to ~16 dB and dulls. Turbulence makes a distant sound's level wander physically (about 2 dB at 100 m, 4 dB at 1 km, more in wind), over about half a second and differently by direction, so a burst swells or fades together.
- **Forest.** Woodland between you and a sound scatters its highs (about 2 dB per 100 m at 1 kHz, more above).
- **Loudness.** Falls off at 12 dB per tenfold distance, softer than the physical 20, and far bands keep only part of the loss of highs and of the ground dip, so a firefight 1–2 km away stays present in the mix.
- **Voice budget.** 96 voices. When they're all busy, the quietest one is replaced, so a huge battle degrades gracefully.
- **Blasts.** The shockwave also travels at the speed of sound. Close blasts cause ear ringing and temporarily muffle the whole world.
- **Impacts and footsteps by surface** (`Surfaces`). A round sounds different in earth, sand, masonry, rock, timber, sheet metal and flesh; grazing hits on something hard often ricochet with a falling whine. Footsteps on turf, sand, gravel, concrete, boards and metal differ, and hard floors carry further. The surface comes from the collider and the material each map box is tagged with.
- **Moving sources** (engines, rotors, drones) are heard from where they were when the sound left them (a helicopter 1 km off is heard ~3 s behind where you see it) and Doppler-shifted by how fast they were closing.
- **Later.** Aggregating many distant shots into a "battle bed"; rooms within a building (a building is one acoustic space now); HRTF.

### Acoustics, part 2: the space you're in (built)

- **Bands are dry.** A gun's or explosion's distance bands hold only what the air and the ground do to it, with no reverb baked in.
- **Live reverb of the listener's space** (Tail bus: a reverb whose room size, damping and pre-delay follow the listener, eased over ~0.5 s):
  - a room: its decay from its volume and surfaces (Sabine: bare masonry, rubble, and every opening absorbing what reaches it), bright, first reflection after a mean free path;
  - a street: a quick slap and flutter;
  - forest: short and soft;
  - open ground: sparse, late and dark.
  Outdoors, the reverberant share grows with distance. A delay line in front of the reverb gives the gap before the first reflections (Godot's reverb "pre-delay" only times a feedback echo, used here for the flutter), and its damping runs backwards: 0 is the darkest tail.
- **Real first reflections outdoors.** Rays from the source find walls and cliffs within 260 m that the listener can see. Each is an echo played from that surface's direction, delayed by the extra path, with spreading and absorption losses (walls reflect more than broken ground), and the muzzle's directivity toward it. The strongest three are kept, within a budget of about 30 a second.
- **Buildings as acoustic spaces** (`Rooms`). Every enterable building registers its interior box and its openings. Between inside and outside (or two buildings), sound takes the shortest clear way through openings: the listener hears it from the window or doorway, later and a bit quieter. A closed door leaks round its edges: 16 dB down, nearly flat, a little dull. With no way through, only what the walls pass (mass law: the loss climbs above a few hundred Hz) gets in. The reverb of the room you're in hears it through the same filter.
- **Doors** (`Door`). Real hinged doors in outside doorways, about half left open. Use key to open or close. Bots open doors in their way and leave them open. A closed door blocks movement, sight and bullets. Doors have their own physics layer, so the navmesh treats doorways as open. An open door swings back flat against the inside wall.
- **Behind you.** Sounds from behind go through a high-shelf cut (about 9 dB of head and ear shadow above 3.5 kHz), the main front/back cue on headphones, whatever other filtering they get.
- F3 shows which acoustic space you're in and the wind.
- `sounddump=<dir>` (dev arg) writes every synthesised sound to WAV files and a table of their levels.

## Ideas recorded for later (the user's)

- **Drones** (next run, built on the squad foundation, the way drone tactics were added to existing infantry tactics):
  - Civilian quadcopters (Mavic-type): recon (their sightings feed the whole side's intel picture, like recon teams) and dropping grenades or small bombs.
  - FPV strike drones: a one-way drone with a charge and an impact fuse, flown into a target (vehicles, positions, people).
  - A drone team (or platoon element) using both types, possibly with some division of labour.
  - Limited stocks of FPVs and bomblets, resupplied by a logistics truck.
  - FPVs working with recon sources and Mavic spotting.
  - Counter-drone: spotting them, shooting them down, taking cover.
- **Trench map** (its own run): a front of trench lines with dugouts and trench rooms to fight through, where FPVs and drone-dropped grenades would come into their own.
- **Campaign war mode**: now planned in full as Conquest mode (below). The fixed 5 × 5 km battle maps first sketched here became a window built round the player.

## Conquest mode (planned)

A whole war on a generated island of about 100 × 100 km. Each side fields an army of about 50,000, and the player is one grunt in it. The aim is a soldier's life that plays out like a story nobody wrote, different every time because all of it is simulated. Planned with the user in October 2026; nothing is built yet. It comes next, ahead of new weapons and maps, and the economy is part of it.

**The sides**
- ALPHA, BRAVO and CHARLIE are organised like the armies whose kit they use (US, Russian, British):
  - squad (US 9; British section 8; Russian about 8, with its BMP's crew);
  - then platoon, company, battalion, brigade or regiment, division and army.
- 50,000 is about 3–4 divisions, 10 brigades, 40 battalions, 150 companies and 500 platoons.
- About half of each army is support (drivers, gun crews, medics, staff), simulated mostly as the crews of trucks, guns and aid posts.
- Doctrine differs as well as names: Russian practice is more centralised and leans on artillery; Western armies leave more to junior leaders.
- 100 km suits three armies: about 10 divisions on 10,000 km² is 30 × 30 km each, a realistic division sector.
- Each side starts with a small area round its port. The aim is the whole island.

**The soldiers**
- A species that exists to fight, and enjoys it. They'd fight every day if they could.
  - There's no morale: no panic, rout, surrender or combat stress.
  - They want to stay alive to go on fighting, so they take cover and their leaders withdraw on purpose. Dying doesn't bother them, so they put their lives on the line far longer than human soldiers: a company fights on to half its men, and an offensive to half its strength.
- They sleep, eat and tire. The brake on their wars is the body, not the mind: fighting is hard physical work, but leaves no weight on them.
  - In the line they eat rations where they are. When it's quiet, a field kitchen's cooks send hot food up. At the rear there's a mess.
  - Lost sleep costs judgement and shooting, about 25% per day awake (Belenky et al. 1994).
  - So their wars run in surges: days of all-out fighting, then as long as it takes to sleep, eat, refill and plan the next.
- No civilians: the towns are empty.
- Every soldier has a record: an identity computed from the world seed and their number (so it costs nothing to keep), and what has happened to them (wounds, kills, kit, where they are).
- Skill grows with combat (`Personality`), so veterans and new replacements fight differently.
- Death is final, and the dead keep a one-line record (name, unit, date, place, cause). The wounded go back through aid posts and hospitals and return days later.
- Tickets are the side's headcount: 50,000 at the start, less the dead, plus replacements who arrive by ship.

**Command**
- Commanders are soldiers at real headquarters. When one is killed the deputy takes over. Their personality shows in how they fight.
- They know only what their units have reported, late by the time it takes to come up the chain.
- Orders take time to plan and pass down: a commander uses a third of the time available and leaves two thirds to those below (the 1/3–2/3 rule, ADP 5-0).
- The player's unit gets no special treatment.

**Three levels of simulation**

| Level | Who | Cost |
|---|---|---|
| Record | all ~150,000, always | ~100 bytes each |
| Abstract: moving and fighting on coarse terrain | everyone not embodied | grows with how many are fighting, not how many there are |
| Embodied: today's bots | the ~100–150 nearest the player | as now: Valley at 3×33 took 255 s of computing for 360 s of play, headless |

- **Abstract fights are individual.**
  - Each soldier spots, takes cover, suppresses, is wounded (`Body`) and runs low on ammo.
  - Fights are resolved burst by burst, once or twice a second, on a 5–10 m terrain grid (Combat Mission uses 8 m).
  - The numbers are fitted to embodied matches from telemetry, so a fight comes out the same at either level.
- **One squad brain, two bodies.** The drill and attack decisions are the same code at both levels; only carrying them out differs (navmesh and physics, or the grid). A squad halfway round a flank when the player arrives is still halfway round it.
- The abstract war runs on its own threads.
- Saving writes the records only, after demoting the area round the player.
- **Time skip.** Everything goes abstract, the player too, who sees a map and a log. The skip stops before the action: new orders, contact near the platoon, fire landing nearby.

**The playable window**
- A window a few km across is built round the player from the world data and re-centred as they move: behind a load at first, streamed later. With no fixed squares, there's no border to split a fight.
- Each window has its own origin. Godot here is single precision: 50 km out, positions come in ~4 mm steps, about what a crawling soldier moves in a physics tick.
- Beyond the window: low-detail land and sea to the horizon, and stand-ins for what the abstract war has out there (vehicles, wrecks, fires and smoke, flares, tracers, flashes). The sound is already there.
- Cities are real sizes: a city of 100,000 covers 20–30 km². So interiors exist only within about 1 km of the player, with shells beyond. The abstract level needs only footprints.

**The island**
- One climate per island, picked at the start: Mediterranean first, desert, arctic and others later. Latitude comes with it: the sun model takes it, so an arctic island gets its polar night.
- **Terrain is made the way real terrain is: uplift against erosion.**
  - Rivers cut down faster the more water they carry and the steeper they run (the stream power law).
  - Cordonnier et al. (2016) generate large terrain this way, solved with Braun and Willett's (2013) implicit method, O(n) a step.
- **Then, in order:**
  1. slopes, rivers, lakes and the coast (cliffs, beaches, harbours);
  2. vegetation and farmland, by climate, height and water;
  3. towns sized like real ones (rank-size: the biggest about twice the second and three times the third);
  4. ports, in sheltered bays and river mouths;
  5. roads, by least-cost paths along the valleys, with bridges and fords;
  6. farms and hamlets;
  7. resource nodes.
- **Checked against real measures:** river lengths against Hack's law, branching against Horton's ratios, town sizes against rank-size.

**Weather**
- Weather moves across the island:
  - a front crossing at 30–50 km/h;
  - rain in the west while the east is clear;
  - fog in the valleys at dawn after a clear night.
- Daily weather comes from a stochastic weather generator (Richardson 1981) fitted to the climate, through the seasons.
- It matters by what it does:
  - mud slows vehicles off the roads;
  - rivers rise over fords;
  - low cloud and wind ground helicopters and drones.

**Economy and supply**
- **Money** is one pot per side. It comes from the extractors and from free *miners* that quarry stone anywhere on the side's ground.
  - Each miner has a crew of 2–4.
  - Quick to set up, slow to pack up, and can't move while working.
  - Yields less when crowded.
- **Resource nodes** give money, ammo and fuel through an *extractor*, which an expensive vehicle puts up. The extractor stores what it makes for trucks to collect.
- **Ammo, fuel and food are three separate cargoes.**
  - Ammo and fuel come from extractors, or are bought and shipped in. Food comes by ship.
  - Food can't burn or explode. It's a lesser target than ammo or fuel, but a unit cut off from it goes hungry.
  - Trucks carry them to depots and on to units.
  - Each company gets a daily resupply run, usually after dark (the US calls it a LOGPAC), bringing ammo, fuel, hot food, mail and replacements.
  - So there are supply lines and convoys to ambush, units cut off run dry, and offensives stall when they outrun their supply.
- **Depots** keep their stacks apart by quantity-distance rules, so one hit doesn't set off the lot.
- **Trucks** come by cargo: tankers, cargo trucks and the logistics truck, each side's real ones.
- **What burns or explodes.** Cargo and on-board ammunition burn or explode by what they are (UN hazard divisions):
  - 1.1, mass explosion: shells, mortar bombs, warheads, FPV charges. A truck of a hundred 155 mm shells holds about a tonne of TNT, some 4,500 grenades' worth. By the cube root, as in `Effects`, its blast reaches about 16 times as far as a grenade's.
  - 1.3, fire with a minor blast: propellant and rocket motors.
  - 1.4: small arms, which burn and pop.
  - Diesel and jet fuel seldom catch from a bullet. An HE or incendiary hit sets them burning: a long fire and a column of smoke, not a big blast.
  - A shaped charge or HE straight into the load can set it off at once. Otherwise it burns and cooks off over minutes, sometimes ending in a mass detonation, which gives the crew time to run.
- **Vehicles** are bought and arrive by sea.
  - Tiers run light vehicle, armoured car, APC, IFV and tank.
  - Within a class they run old to new, from each side's real line. For tanks: light tank (the mobile guns already in the game), an older main battle tank, then the current one.
- **Armies start with vehicles:** the trucks and light vehicles an army this size needs, and older armour. Money buys the newer tiers.
- **Ports** are where replacements and everything bought arrive. A port under fire or taken stops them, which makes the coast what everyone fights over.
- **Replacements** are free. They come as fast as the side's ports can land them, and only refill units to full strength; an army doesn't grow past it. (Otherwise whoever holds the most ports snowballs.)
- **Fire support.** Artillery is added: 120 mm mortars, 155 mm guns and rockets. So is naval gunfire from ships offshore: a 4.5–5 inch gun reaches about 20–25 km inland. Ships are abstract at first. Jets come later.

**Behaviour**
- The new behaviour is mostly between fights:
  - road marches, convoys and assembly areas;
  - digging in: lookouts, sectors, patrols, stand-to at dawn and dusk, and sleeping in shifts;
  - handing over positions, resupply, casualty evacuation, and replacements joining.
- Attacks move up a level: a company attacks with one platoon supporting by fire and the others assaulting.
- Every test run checks that behaviour fits the place:
  - soldiers at rest are under cover;
  - positions face the threat and have a field of fire;
  - lookouts see the approaches;
  - vehicles near the front are hidden;
  - marches keep their intervals;
  - digging happens only where the ground allows.

**The player**
- One grunt, with no promotions.
- After dying, the player carries on as a squadmate (never the leader). If the squad is wiped out, they arrive by ship as a replacement.
- They start in the lead battalion of the main effort. Replacements go where units are short of soldiers, which is usually where the fighting is.
- They know what their chain of command tells them, plus a map of what their side knows.
- Orders say why, and come from named people.
- Hills are named by their height ("Hill 302"), and towns have names.
- There's a journal, casualty lists, and a war map of how the front moved.

**Build order**
1. **The island generator** (built: see below). It produces map images and is checked against real measures.
2. **The abstract war, headless** (in progress: see below). Campaign telemetry and a replay of the war.
3. **Calibration.** Abstract fights fitted to embodied matches.
4. **The playable window** (in progress: see below). Promotion and demotion, and the distant layer.
5. **The soldier's life.** The player in the war: time skip, briefings, dying and carrying on, the journal.
6. **Economy and construction**, then weather and seasons.

**Defaults still to confirm**
- bought vehicles arrive by sea;
- ships are abstract at first;
- the player never takes over as leader;
- the starting motor pool;
- food comes free by ship, like the replacements.

### The island generator (built)

Phase 1. `-- mode=worldgen [seed=1] [seeds=N] [size=1280] [out=worldgen]` makes islands headless. For each seed it writes four files to `worldgen/`:
- `island-N.png`: the map;
- `-layers.png`: relief, uplift, rain and drainage;
- `-report.txt`;
- `.html`: the map with names on it (wheel to zoom, drag to pan, hover for details) and the report.

Defaults: 1280 × 1280 cells of 100 m over 128 km. An island takes 16–21 s, of which the erosion is 12–14 s. The code is in `src/Conquest/`. `IslandGen` runs the stages in order, each from its own random stream, so changing one doesn't reshuffle the rest.

- **Ground** (`Landform`, `Drainage`).
  - Uplift against stream-power erosion (n = 1, m = 0.45), solved implicitly, with hillslope creep. It runs on 320, 640 and 1280 grids: 240, 70 and 25 steps of 25,000 years.
  - Uplift is an island-shaped field with a fractal edge. It's highest along one to three ranges, middling over hill country, and low between (plains). Rock erodibility varies, in folded bands in places.
  - Heights are scaled into the climate's summit range. With n = 1 that's the same as uplifting at another rate.
  - Ground steeper than 40° slides down. The sea then rises 20–50 m and drowns the valley mouths into inlets, and islets under 0.6 km² go.
- **Climate** (`IslandClimate`).
  - The Mediterranean preset: 38° N, 18 °C at sea level, 560 mm on the windward lowlands, the wet wind from 300°.
  - Temperature falls 6.5 °C per km.
  - Rain follows the moist air across the island: more where it's forced up, less in the lee.
- **Rivers** (`Hydrology`).
  - Pits are filled with Priority-Flood, then each cell drains to its steepest neighbour (D8).
  - Each cell gets its drainage area and mean flow (rain × 0.3 runoff).
  - Lakes form where filling stands a metre of water. Floodplains are laid flat along rivers of 20 km² and up (HAND).
  - Streams get Strahler orders, and the 14 biggest rivers have their main stems traced.
- **Cover** (`LandCover`).
  - Vegetation by rain, height, aspect and slope: garrigue, maquis, Aleppo pine, oak, montane pine, grass, rock, beach and marsh.
  - Farmland is 0.4 ha a head, taken from the best land nearest the towns: fields on the flat, orchards on terraces.
- **Towns** (`TownPlanner`).
  - Sizes by rank-size from 110 people per km². That only sizes what was built.
  - Placed by flatness, water, farmland in reach, height and harbour. Villages favour hilltops and avoid the low shore; the ports and market towns are on the plains and the coast.
  - Spaced by size, and only on land masses of 30 km² and up.
  - Ports are towns of 3,000+ on sheltered water, none within 10 km of a bigger one.
- **Roads** (`RoadBuilder`, `RoadNet`, `PathFinder`).
  - Relative neighbourhood graphs: main roads between towns, secondary roads between villages, tracks to hamlets and nodes.
  - Routed by A*, priced on grade, ground and stream crossings, joining earlier roads. Bridges and fords are recorded.
  - Farmsteads lie among the fields, more of them near roads.
- **Sides and nodes** (`Holdings`).
  - Three starting areas of 6% of the land each, round ports kept 20 km from the cities. The trio is chosen for spacing and for evenness of what's inside, measured by travel time.
  - 16–22 resource nodes, at least 7 km apart: fuel in soft lowland rock, ore in hard mountain rock. One sits in each starting area, and each has a track to a road.
- **Names** (`PlaceNames`, `Peaks`): Mediterranean-sounding names; hills named by their height ("Hill 302").

Measured on seeds 1–6:
- land 5,100–6,900 km², summits 1,300–2,300 m;
- 390–640 places, 9–16 ports, 19–23 nodes;
- village median heights 130–330 m;
- the poorest starting area holds 0.62–0.90 of what the richest does.

| Check | Seeds 1–6 | Real |
|---|---|---|
| Hack exponent (river length ∝ area^h) | 0.53–0.57 | 0.5–0.65 (Hack 1957) |
| Bifurcation ratio, basins of order 4+ | 3.7–4.1 | 3–5 (Horton 1945) |
| Length ratio | 1.8–2.3 | 1.5–3.5 (Horton 1945) |
| Rank-size exponent | 0.96–1.08 | 0.8–1.2 (Zipf; Gabaix 1999) |
| Coastline fractal dimension | 1.17–1.28 | 1.1–1.3 (Mandelbrot 1967) |

(Before the checks were taken within basins, the bifurcation ratio read 5.2–5.5. Hundreds of tiny coastal basins of one or two streams were swamping it.)

Not done yet:
- **Weak rain shadow.** The lee is wetter than real: about 600 mm on the lee third, where Palma gets 430.
- **No deposition besides floodplains.** There are no deltas, and no coastal plains built out by rivers.
- **One climate.**
- **Towns are points with a radius.** Their streets and buildings come with the playable window (phase 4).
- **Nothing saves the island yet.** It's made fresh from the seed each time, so it's the same island every time.

### The abstract war (phase 2, in progress)

`-- mode=war [seed=1] [days=7] [trace=N]` raises the three armies on island N and runs the war headless, about 5,000–13,000 times faster than real time. It writes to `worldgen/`:
- `war-N-report.txt`: the orders of battle, then day by day the ground and objectives held, the dead and evacuated, and the fights (see below);
- `war-N.html`: the replay. Every company moves hour by hour over the island map, with territory shading, fights as red rings and the events list. Hover over a unit for its name.
- `war-N-start.png` and `war-N-progress.txt`.

The code is in `src/Conquest/War/`.

**The armies** (`Orbat`, `People`): about 47,000 each, built from templates of the real organisations.
- Every soldier has a name, rank, job and skill computed from the seed and their number, plus a record of what's happened to them: state, blood, ammunition (a rifleman's 210 rounds, a machine gunner's 600...).
- Vehicles are records too, with main-gun and machine-gun rounds. Armour starts at three-quarters of establishment.
- Tank counts differ the way the real armies do: BRAVO about 600, ALPHA 190, CHARLIE 90.

**Deployment and movement** (`Deployment`, `MoveGrid`):
- Army troops start by the port, divisions in sectors inland, fighting battalions at the edge of the starting area.
- A 400 m grid of speeds by mobility:
  - on foot, 4 km/h on roads and Tobler's slope function across country;
  - wheeled, 50/35/20 km/h by road class;
  - tracked, 35 on roads and 8–20 off them.
- Rivers are crossed only at bridges and fords, except on foot.
- Units march by day, at most 8 hours on foot and 10 at the wheel. Pulling back from a fight isn't a march: it goes on by night at two thirds of the pace. A unit rides if its vehicles seat three in four of its people.
- Routes keep off enemy-held and contested ground.

**Command** (`Command`): the army plans every 3 h, divisions every hour, brigades every half hour. Orders take an hour to reach battalions and half an hour to reach companies.
- The army shares out objectives (towns, ports, nodes, main-road bridges, big hills), nearest and most valuable first. Each goes to the nearest division with battalions free for it, two tasks a free battalion, so idle divisions are committed rather than left waiting behind a busy one.
- Once the open ground is mostly taken, or from day 3, it goes over to the offensive. It picks the enemy whose nearby ground is worth most against the least known strength, and keeps to that choice unless the other enemy becomes twice as good a target. Its planned offensives (below) take that enemy's places; the rest of the army holds. (Brigades used to attack enemy places on their own all along the front as well, and over a month that caused more casualties than anything else.)
- Brigades send one battalion to an empty objective and two to a held one. A battalion rings the objective with its line companies.
- A brigade with nothing to take holds its own objectives facing the enemy the army isn't attacking (economy of force). Objectives are 4–5 km apart, so these make the front a line of strongpoints.
- A battalion that takes ground in the front line (enemy ground within 5 km) stays on it as its garrison while it's in the front line.
- A brigade keeps one battalion back in reserve, from those free to manoeuvre, when it has two or more free.
- After an operation a battalion consolidates: 3 h after taking an objective, 6 h after being beaten off or held up. One below half strength isn't sent on another.

**Planned offensives** (`Offensive`): an army masses for an attack on one axis rather than every battalion going for whatever is nearest.
- **The axis.** An army on the offensive picks the cluster of enemy places (within 6 km of each other, within 10 km of its own ground) worth most for the least strength known there.
- **The force.**
  - Battalions for three to one against what's there: what has been seen, but at least a strong company assumed on each enemy place. That comes to 4 to 12 battalions.
  - First those not tied down, then garrisons pulled out of quiet stretches of the front against the same enemy, nearest first. Those holding against the other enemy stay (economy of force).
  - Each must be at 80% strength or more, supplied, and rested (owing less than 8 hours' sleep).
- **Assembly.** They gather 6 km short of the axis and go in at dawn, at least 12 hours after the plan. In the last hour the guns fire a preparation on the enemy units known to be there.
- **The attack.** The battalions go in together, two on each held place. They don't stop to hold what they take: other battalions take newly won ground over, and the attackers go on to the next place, up to three bounds deeper.
  - They attack by night as well as by day, at night pace. Everyone else marches by day.
  - A battalion pauses after a place falls, and for the night's resupply when it's out of food or fuel or below half its ammunition. (Battalions with nothing left used to be sent on regardless.)
- **The end.** It's called off when its battalions are worn to half their strength on average, when its companies are exhausted (16 hours of sleep owed on average), or after a day without taking new ground (a place lost and retaken doesn't count). There is no limit of days: it goes on as long as the troops can. US doctrine reckons a unit below 70% unfit for offensive operations; this species goes on. The army then plans the next for at least a day, and launches it once enough battalions are rested, fed, supplied and back at 80%.

**Territory and intel** (`Territory`, `WarIntel`): 1 km squares, held by whoever alone has troops within 1.5 km. Commanders plan on the enemy their side has seen, for six hours after.
- A place a battalion takes brings its square with it. With the enemy still close by, the square is contested, and contested ground keeps its holder. (The square used to stay the old holder's, so the next ground update gave the place straight back and the battalion took it again: one oilfield changed hands 47 times in a month, every 20 minutes for a day at a time, and one bridge 127 times.)

**Contact** (`Combat.Detect`): enemy units within 3 km, by sight line, range, light and how much they show.
- A fight starts only within 1.5 km (2.5 with gun vehicles), and only if:
  - one side is coming on;
  - or it's a first sighting inside 600 m;
  - or they're within 200 m.
- Two units that know of each other and hold still don't fight: that's a front.
- Contact is looked for once a minute. Units found within 400 m are put back where they were when they came within 400 m during that minute.
- A headquarters or support unit that sees an enemy ahead halts and waits for orders.

**Fights** (`Combat`): soldier by soldier, in 4-second steps.
- A company deploys in line facing the enemy: squads 50 m apart, headquarters 150 m back, soldiers about 10 m apart.
- Cover and concealment come from the ground (buildings, rock, woods, scrub), with trenches after 2 hours dug in.
- Seeing a man gets harder with range and in the dark: half as likely at 300 m by eye, at 800 m through a vehicle's thermal sight, and at a third of the range at night through goggles. It also needs a clear line of sight, and the chance of one halves every kilometre.
- Fire discipline: aimed fire at someone seen. Fire at a place only to cover an attack, or to answer fire.
- A near miss pins a man (0.3 a rifle round). Pinned, he lies flat, shows less, fires slower and worse. A squad pinned flat doesn't move until the fire lifts.
- Tanks and IFVs fire their machine guns at troops. They keep shells for men behind hard cover, anti-tank teams and, for autocannon, troops out of machine-gun range. Shells and grenades land off the aim by the weapon's error.
- Wounds: a fifth killed outright, a third down (a third of those bleed to death within the hour unless a medic or squadmate stops it), the rest lightly wounded and fighting on.
- Companies decide every 30 s:
  - attack if advancing, at 3:1 against a dug-in enemy and 2.5:1 against a hasty one (FM 3-90's planning ratios), bounding and assaulting from 60 m;
  - pull back by bounds, half the squads covering, when outnumbered 2:1 (6:1 dug in, what a prepared position is worth by the 3:1 an attack needs) or after losing half its men (human soldiers were reckoned spent at three in ten);
  - otherwise hold.
- A fight ends when it's quiet, when one side is gone, or after half an hour with nobody coming on.
- A company held up by enemies still standing in its way halts and its battalion decides what next. One that was beaten off falls back 2 km, away from the enemy and toward its own rear.
- A squad with nobody left who can fight is nothing to see or attack.
- A unit holding ground engages whatever moves through its field of fire.
- The holding side's down are evacuated. A side pulling back carries out most of its own.

**Supply** (`Supply`): food, fuel and ammunition used up, and brought forward from the port by the armies' own trucks.
- **Using it.**
  - A soldier eats 1.8 kg of rations a day.
  - Vehicles burn fuel by the km and, engines running, by the hour in a fight, at their makers' figures (M1A2 1,900 L at about 4 L/km, T-90 2.5 L/km, Bradley 660 L, Stryker 200 L...). Every day they also run a couple of hours for power and heat, and generators burn half a litre a soldier.
  - Ammunition goes as it's fired, weighed packed: 25 g a rifle round, 30 kg a tank round.
- **Depots.** Ships land 4,000 t a day at the side's port while it holds it; a port lost takes its depot with it. Each division has a support area 15 km behind its brigades, each brigade one 8 km behind its battalions (US doctrine puts them 10–20 km and further behind the front; the island is small). They aim to hold three and two days of what their units use, and stand where trucks can reach them from the port.
- **Convoys.**
  - The armies' logistics companies (US distribution and forward support companies, Russian material support battalions, British logistic regiments) drive the depot runs by night and day. Cargo trucks carry 6 t, tankers 8 t.
  - A brigade's companies fetch from their division's support area, a division's from the port, and the army's take from the port to whichever division is shortest.
  - Convoys are units on the map. They halt for an enemy close ahead, can be ambushed, and lose the load of every truck destroyed. Their crews eat and fuel at the depots.
- **The nightly resupply (LOGPAC).** At 22:00 every unit within 30 km of its depot by land, round ground the enemy holds alone, is topped up: food to three days, fuel to full, ammunition to its basic load. The forward support companies' trucks that carry it aren't moved on the map yet. A depot short of something shares out what it has.
  - **Area support.** A unit its own depot can't reach, or whose depot stands empty, is served by the nearest of its side's depots that can reach it, as US sustainment doctrine supports whoever is in a support unit's area.
  - **Convoys.** A convoy halted on the road is served like any other unit, and a convoy carrying rations eats from its load rather than go hungry.
- **Falling back.** A support area doesn't stand on ground the enemy has taken or is fighting over: it falls back toward the port, stock and all. (Support areas used to follow their brigades' middle 8 km back, so one whose brigade had pushed into enemy country, or had no fighting battalions left, stood on the enemy's ground and fed nobody.)
- **Breaking out.** A unit no depot can reach, hungry for half a day, breaks out toward the nearest ground a depot can reach, by night as well as by day. A fighting battalion goes as a whole and gives up the ground it held (FM 3-90, breakout from encirclement). These soldiers don't surrender. (Units cut off used to sit and wait: at the end of a month, 50–120 per army had gone a median of one to two and a half weeks without food.)
- **Going without.**
  - Out of fuel, vehicles don't move. Out of ammunition, soldiers don't fire.
  - Hungry, they shoot 15% worse for each day without food.
  - A battalion short of fuel or food, or below half its ammunition, isn't sent on an operation until it has been resupplied.
- **In fights,** unarmed vehicles are targets now; guns take armour first and leave ambulances alone. A fighting company's trucks stay back with its trains, but a convoy's or a headquarters' are the unit itself.

**Artillery** (`Artillery`): battalions' mortars, brigades' and divisions' guns, and the armies' rockets.
- **Close support.** A company in contact calls fire on the strongest enemy squad it can see, outside the distance the weapon needs from its own men: mortars 200 m, guns 300, unguided rockets 600, guided 150.
  - The nearest mortars answer first, then the guns; rockets only for big targets.
  - Rounds land 4 minutes after the call for mortars and 6 for guns (10 for BRAVO, whose fire control is more centralised), adjusted and fired for effect.
  - A battery fires 3 rounds a gun (BRAVO 5, by Russian norms).
- **Where they land.** The observer knows where the target is to within 10 m plus 3% of his range from it, halved by adjusting. Each round then spreads by its weapon's dispersion: 0.3% of range for guns, 0.4% for mortars, 1.5% for BRAVO's unguided BM-27 rockets, and 5 m for GMLRS.
- **What they do.**
  - A round's casualty radius is 25 m for 155/152 mm, 18 m for 120 mm, 12 m for 81 mm (British mortars) and 30–35 m for rockets.
  - Everyone within four radii is pinned, and it falls on whoever is there, own troops included.
  - Outside a fight, troops are in their carriers on the move (armour stops nearly all fragments, a truck's canvas few). Halted, they have shell scrapes within half an hour and trenches within two.
  - A group's ground grows with its size at the same 10 m spacing, so a 150-strong headquarters isn't one 60 m square.
- **On what's been seen.** Every 10 minutes, idle guns and rockets with half their ammunition left fire on enemy units their side saw in the last 10 minutes, biggest first, outside fights and clear of their own troops. This takes at most a quarter of a gun's daily allowance; priority of fires goes to troops in contact and to the main effort. They wait an hour before firing on the same unit again. A headquarters or support unit shelled moves 1.5 km back.
- **Counter-battery.**
  - The enemy's radars find a battery that fires seven times in ten if his guns are within 30 km. His nearest free guns fire back on where its tubes fired from, 4 minutes after its rounds land (8 for BRAVO).
  - Batteries shoot and move: self-propelled ones 2 minutes after their last round, towed ones 10, so counter-battery fire mostly catches towed guns.
  - A battalion's mortar platoon sets up on its own, within 1.5 km of its company, so fire on its mortars doesn't fall on the battalion staff.
- **Ammunition.**
  - Each gun may fire a daily allowance (the controlled supply rate): about a unit of fire for BRAVO's guns (60 rounds of 152 mm), two thirds of that for the others', 60 bombs a mortar, and a launcher's load.
  - A gun and its ammunition carrier hold 90 rounds, a mortar 80, a launcher 12. They're brought up by the supply system at 45 kg a shell.

**The wounded and replacements** (`Medical`):
- **Light wounds.** A soldier lightly wounded fights on, and is fit again a week later.
- **Serious wounds.** A soldier down and carried out (from a fight his side held, or most of them when it pulled back; anyone seriously hurt by shelling) goes back through the battalion's aid post to hospital.
  - One in twenty dies of his wounds, as in recent wars with quick evacuation; one in four if his unit is cut off from its depot.
  - Of the rest, two in five return to duty after one to four weeks; the others are invalided home, out of the war. In the Second World War about half of those hospitalised came back.
- **Replacements.** Each port a side holds lands 500 soldiers a day (a troopship every two days).
  - They fill vacancies only, the units shortest of men first, each in the job of the soldier he replaces (a lost leader's place goes to a private: promotion within the unit isn't modelled). An army is made up to strength but never grows past it.
  - Each replacement is a new soldier, with his own name and skill.
- **Joining.** Soldiers back from hospital and replacements reach their unit with its nightly resupply, if it gets through to them, and are issued their basic load.
- **Tickets.** A side's headcount: 50,000 at the start, less the dead, plus the replacements landed.

**Sleep and tiredness** (`Rest`): what stops these soldiers is the body. Tiredness is physical only, with no combat stress.
- **Sleep owed.** Awake, a company runs up half an hour of sleep owed every hour, so a normal 16-hour day is paid off by 8 hours asleep. Marching on foot runs it up at three quarters, riding at a half, and fighting at one and a half (hard physical work). Hunger adds a quarter.
- **Sleeping.** A company halted and out of a fight sleeps by night (20:30–05:30), and by day too once it owes 8 hours. In the rear everyone sleeps and pays off an hour an hour. In the line (enemy ground within 2 km) they sleep in shifts, half at a time, and pay off half as fast.
- **What it costs.** Lost sleep costs judgement and shooting, about 25% per day awake (Belenky et al. 1994): aim falls by a quarter for every 16 hours owed, to no worse than 30%.
- **Readiness.** A battalion with a company owing 8 hours or more isn't sent on an operation ("tired" in the report's "why idle" line). An offensive whose companies owe 16 hours on average is called off: exhausted.
- **Measured** (30 days, seed 2). Offensives go in owing 1–5 hours and end owing 5–9. None was called off exhausted, even attacking by night. An offensive's companies spend the night 91% asleep, 7% moving and 2% fighting, and the day 49% halted, 23% moving, 23% asleep and 5% fighting. Between bounds they wait: for a place to be consolidated, orders to come down, the night's resupply, or six hours to regroup after being beaten off. A halted company sleeps. So sleep shows in the shooting (up to an eighth worse late in an offensive) and in battalions not ready for the next, and it's attrition and stalling that end an offensive.
- Night attacks put a quarter of the offensives' fights and a sixth of their casualties in the dark. Before, it was a seventh of the fights and a twentieth of the casualties, and their companies slept 99% of the night.
- A column halted for the night or at the end of its day's march beds down like any other. (Those still had a route and stayed awake all night, so offensives that had marched to their assembly areas went in owing 10 hours and were called off exhausted 8 hours in.)

**The assault test** (`AssaultTest`): `-- mode=assault [seed=2] [runs=20] [attack=9] [defend=3] [arty=0|1]`.
- BRAVO's first rifle companies dig in round the town nearest the island's middle. ALPHA's first rifle companies set off at it from 3 km out and press on, renewing the attack every half hour until the place falls. With `arty=1`, both sides' battalion mortars set up behind their lines. Everyone else stands aside.
- The same attack is fought with fresh dice each run. `assault-N.txt` gives what it cost each side, how often it carried the town (counting only defending companies that were attacked), how long it took, rounds per hit, and the share of hits inside 100 m.
- Measured: 9 companies (1,374 men) against 3 dug in (255 men and 30 BMPs), with mortars, 12 runs.
  - It carried the town 7 times in 12, in a median 2¾ hours.
  - The attackers lost 25% (6% killed); the defenders lost 57% (13% killed). Before companies fought on to half their men, it was 23% and 52%, carried 8 times.
  - The mark for a battalion attack at three to one on a prepared company position is 5–15% for the attackers, more for the defenders, over hours, carrying it more often than not. The attackers' losses run high.
- Before the 6:1 rule for troops dug in, a defending company left its trenches at its first sight of the attack 1.4 km off, and no attack ever had to carry a position.

**Reading a run.** The report lists:
- casualties against Dupuy's rates;
- hits by range;
- fights grouped by casualties;
- the biggest fights, with their causes;
- the units that fought most;
- the share of those who fought who were hit;
- artillery: rounds and tonnes fired, shells a gun a day, missions by kind, guns lost to counter-battery, soldiers hit by shellfire, and how those outside fights were caught;
- the wounded and replacements: carried into the medical chain, died of wounds, invalided, back to duty, replacements landed and joined, tickets;
- supply: tonnes landed, hauled and issued, convoy runs, cargo lost, unit-nights cut off and fed by another depot, break-outs, and units out of fuel, hungry or low on ammunition at the end. It also lists the depots' fill, any depot standing empty with its convoys' state, and why the hungry are cut off;
- places changing hands, the places that changed hands most, and what the new holder had there when ground changed hands without a battalion taking it;
- fights by night, and how an offensive's companies spend the day and the night.

`trace=N` adds fight N's state every 30 s to the report: fit, hit, suppressed, flat, in cover, what each side can see and how far. If the war sits in one place for 15 s of real time, the progress file says where, and an exception ends the run with an error instead of leaving Godot idling.

**Measured** (7 days, seeds 2 and 1). Each army's casualties of all kinds, as a share of the army a day:

| | Seed 2 | Seed 1 |
|---|---|---|
| ALPHA | 2.9% | 1.9% |
| BRAVO | 1.2% | 0.8% |
| CHARLIE | 1.0% | 2.2% |

Dupuy's figures: divisions in battle 1–3% a day, whole armies well under 1%.
- **Over 30 days on seed 2** the armies averaged 1.8%, 0.9% and 1.0% a day: 19,600 dead in the month. Before the species was allowed to fight on to half strength it was 1.4%, 0.7% and 0.5% (13,800 dead); feeding the units that used to sit hungry added about 2,000 more. Seed 1's first week ran at 2.6%, 0.9% and 2.8%.
  - The war comes in surges: 600–1,700 dead a day in the first ten days, then 190–790.
  - 22 offensives in the month, each army's next going in two or three days after its last ended. Each lasted half a day to 3½ days (median under two), took 0–14 places, and ended when it stopped gaining new ground, its battalions at 50–93% strength.
  - After a month nobody is near defeat: land shares 27%, 39% and 30% (from 24%, 27% and 22%), armies at 81–92% strength. A war on this island will run for months.
  - Hungry at the end of the month: 11, 8 and 7 units, all of them surrounded, against 69, 137 and 116 before area support, falling back and break-outs. Each army had 650–2,200 unit-nights fed by a depot not its own, and 200–310 break-outs.
  - Places change hands 25 times a day; the most fought over changed hands 30 times in the month.
- **Where the hits come from:** planned offensives 55%; shelling outside fights 20% (half on units seen, half counter-battery); brigades taking open ground 16%; units meeting on the march 7%; skirmishes along static fronts 1%.
- **The offensives.** They made 14 offensives of 4–12 battalions. Some took 7–14 places and went a bound or two deeper; others stalled and were called off after a day, or were fought down to 70% within hours.
- **The war's course.** BRAVO and CHARLIE both turned on ALPHA, which still grew from 24% of the island to 36%. ALPHA took the heaviest losses: 7,300 dead, 15% of its starting strength.
- Fighting peaks on days 1–3, at 560–830 dead a day for the hardest-hit army. It goes on all week, at 40–380 a day on days 5–7.
- About a third of fights are brushes where nobody is hurt.
- Shell and mortar fire causes 47–71% of the hits, against about 60–75% in the World Wars. Guns fire 17–37 shells a day each. Each army fires 2,400–5,900 t of shells, bombs and rockets in the week, and loses 27–76 guns to counter-battery.
- Direct-fire hits come mostly at 50–400 m, mostly from rifles, then vehicle machine guns, autocannon and grenades.
- Each side lost 290–1,460 vehicles in the week, most of them light vehicles, APCs and trucks, many of them to shellfire.
- Supply in a week: each army made 78–123 convoy runs and issued its units about 500 t of food, 740–2,100 t of fuel and 2,200–5,900 t of ammunition. Shells are now most of what the convoys carry.
- At the end of the week, 40–80% of each army's fighting battalions hold ground. Another 5–25% are on an operation, and 4–13 battalions per army are reserves or idle.
- 66–97 places change hands a day.
- On seed 2, ALPHA grew from 24% of the land to 33% and BRAVO from 27% to 32% while they fought along a north–south front, and CHARLIE held at about 25%. On seed 1, CHARLIE was crushed between the other two, from 18% of the land to 13%, while BRAVO grew from 36% to 52%.

How the fights were brought down to this, all in the code's comments. In the first runs, armies lost 10% a day. The causes were:
- squads standing in a 40 m square;
- spotting that didn't fall off with range;
- shells landing exactly on the man aimed at;
- tanks firing HE at anyone they saw;
- suppression that wore off in 6 s and was barely raised by a near miss;
- companies withdrawing all at once, upright;
- fights opening with companies mixed together after driving into each other between two looks;
- soldiers of companies that had left a fight going on shooting in it, out of reach;
- companies marching straight back into the enemy that had stopped them;
- battalions sent on the next objective the moment they finished one;
- battalions leaving everything they took empty for the enemy to walk back into;
- every target going to the nearest division, so one had forty tasks while another sat idle;
- units beaten off at night sitting where they were until morning, fighting every five minutes;
- ground between garrisons lying open, garrisons watching enemy columns drive past, and brigades keeping a reserve back when every other battalion was holding ground.

The busiest unit now fights 70–85 times a week. Before the last three fixes, pairs of units caught each other 200–350 times.

Not done yet:
- **The pace of a war.** Three ways of tying battalions to ground were tried:
  - holding everything in the front line froze the war by day 5 with the armies at 90% strength;
  - capping garrisons at a third of a brigade ran it at 2–4% a day, with 170 places changing hands daily;
  - what's here sits between the two.

  Real offensives pause for supply and replacements, which aren't modelled yet. Until they are, how hard the war runs comes from these command rules rather than from logistics.

  Over 30 days (seed 2), without replacements, the war went quiet after about two weeks, the armies at 60–77% strength.
  - With replacements and the wounded returning, it goes on to about day 25: 60–170 dead a day for the hardest-hit army in the third week. The armies end at 84–90% strength.
  - But the fronts still freeze after the first week. Most battalions are tied down holding ground (21 of ALPHA's 36, 44 of BRAVO's 59 at day 14), and a few wait on a hungry company. Only 0–4 per army are on an operation.
  - Planned offensives (above) now keep the war moving: 22 in the month, about 25 places changing hands a day, and the land shares shift by up to half.
  - The other lever is the 500 soldiers a day each port lands. It sets how long an army can be fed into a losing fight: CHARLIE on seed 1 had 8,775 replacements in a week while it was crushed.
- **Supply binds through shells, not yet through want.** The convoys keep up: no unit ran out of fuel and few of ammunition, since the guns fire within their daily allowance. Ports being taken, and cargo that can blow up (the hazard divisions under "Economy and supply"), would make it bite.
- **The last leg isn't driven.** The nightly resupply from a brigade's depot to its companies is reckoned, not driven, so it can't be ambushed yet. Support areas move with their brigades at once, stock and all.
- **Attacks cost the attacker too much, and the war never lets up.**
  - On the assault test, attackers lose 23% where history says 5–15%. There's no smoke to cover the approach yet, and the defenders' dug-in IFVs see through everything with thermal sights.
  - Over a month, an army attacked from two sides has 17% of its starting strength killed. Germany's eastern army lost about 2% a month in 1941–45, but that average includes long quiet spells. This species fights every day it can, so the war's lulls last only as long as planning and readiness take: a day or two.
  - Phase 3 fits the assault to the battle simulation.
- **Ground changes hands as units pass.** Ground is held by whoever alone has troops within 1.5 km, as of a look every half hour. About a third of the changes without a battalion taking anything are troops passing by, half of them support troops (convoys, headquarters). A unit driving fast through empty ground holds only where it was at each look.
- **Some units stay surrounded.** A few (7–11 per army at a month's end) go weeks without food: their break-out finds no way, or they're cut off again. ALPHA, attacked from two sides, also has brigade depots standing empty while their convoys are held up by the enemy near their routes; area support feeds most of their units from the division's.
- **Strongpoints, not sectors.** Ground is held where troops are, and the front is a line of garrisoned objectives with no unit boundaries between them.
- **Artillery without drones or air observation.** Fire on what's been seen uses the side's ground sightings within 3 km. Drones, which find most targets for artillery in Ukraine, would add their own. There's no smoke or illumination, and no fire planned ahead of an attack.
- **Calibration against the battle maps (phase 3).** The battle maps' own hit rates are still high (see "Not done" under the quality pass), so real casualty rates stay the yardstick for the war as a whole.

### The playable window (phase 4, in progress)

The player is one soldier in the abstract war. Round them, a window a few kilometres across is built from the island and
runs as the real battle simulation; everywhere else stays abstract. Calibration (phase 3) is folded in: the window is
what turns abstract units into embodied squads and back, which is what calibration needs.

**Slices**
1. **The ground** (built): the window's terrain, water, roads, woods, fields and towns, read off the island.
2. **The armies, frozen** (built): the war run headless to a chosen day, then the units nearest the window's middle embodied where they are, as squads of bots with their soldiers' names, their vehicles and their orders.
3. **Live**: the abstract war runs on outside in real time. Units crossing the window's edge are promoted or demoted. What happens inside (casualties, ammunition, ground taken) is written back to the war's records.
4. **The distant layer**: low-detail land and sea to the horizon, and what the war is doing out there (flashes, fires, smoke, flares, sound).
5. **Moving the window**: rebuilt round the player as they near its edge, behind a load at first.

**The ground** (`WindowGround`, `Valley.BuildWindow`, `WindowMode`). `-- mode=window [seed=2] [town=Name | at=x,z] [size=4096]` makes the island and builds a 4 km window round a town or a point (km east and south of the island's centre). The player stands at its middle; nobody else is there yet.
- **Heights.** The island's 100 m grid, interpolated smoothly (Catmull-Rom), on a 5 m mesh. Finer relief is added: a few metres of outcrop and gully on steep, hard ground, half a metre on the flat, none under water.
- **Rivers.** Every stream the island maps (2 km² of catchment or more), traced from its head down its cells and smoothed. One shared field bends them into meanders, up to about 30 m off the cell line (meanders run 10–14 channel widths, Leopold and Wolman 1960); sharing it keeps tributaries joined. Bankfull width comes from the island (Leopold and Maddock 1953), depth is a twelfth of it, and the banks slope. The water surface follows the island's cells downstream and never rises.
- **Water.** The sea at 0 m, the lakes at their levels, the rivers as ribbons on their surfaces. Water too deep to wade (1.2 m for people with kit) or ford (0.9 m for vehicles: trucks ford about 0.75 m, tanks about 1.2 m unprepared) is struck out of the navmeshes.
- **Roads.** The island's roads, smoothed into curves: main 7 m wide, secondary 5.5 m, tracks 3.5 m. The ground is levelled across them, eased along them over about 60 m, with a 6 m shoulder. A river 3 m wide or more keeps its channel, and the road crosses on a bridge with a deck 1.5 m above the water and parapets, rising to it over 40 m either side. A brook goes under the road through a culvert.
- **Land cover.** The island's cover cell by cell, the edges wandered by up to 60 m of noise. Fields are a patchwork of crops about 170 m across. The island's town cover beyond the streets built is outskirts.
- **Trees by cover**, per hectare: pinewood 45 pines, oakwood 38 broadleaves, maquis 55 bushes, garrigue 25 low scrub, orchards 20 trees, a few in grass and along fields. That's fewer than real woods (300–1,000 stems a hectare), as on the battle maps, to keep the count playable; the proportions between covers hold.
- **Settlements.** The island's towns stand where they are:
  - those of a thousand people or more as towns of streets and blocks (the battle maps' town builder), up to 700 m across for now, each district a place to fight over;
  - smaller villages and hamlets as villages;
  - the island's farms as farmsteads, clear of roads and streams.
- **The origin is the window's middle**, so positions stay small. Godot here is single precision: 50 km from an origin, positions come in 4 mm steps.
- **Measured** (island 2):
  - the village of Froltosa (1,830 people) builds in 4 s, with 548 buildings, 14 farms, 5 streams, 4 roads, 3 bridges and 47,000 trees. Its navmesh is 63,000 polygons for people and 170,000 for vehicles;
  - the port of Porto Tuca (9,300) builds in 8 s, with 1,286 buildings. Its navmesh bakes in 22 s the first time (tiles are cached after), with 686 stretches of sea and inlet barred;
  - making the island itself takes 18 s;
  - both run at 150–160 FPS.

**The armies, frozen** (`ConquestWindow`). `-- mode=window seed=2 day=3 [hour=H] [bots=160] [join=0] [verbose]` runs the war headless to dawn on day 3 (17 s on island 2), opens the window on the biggest fight going on (or midway between the two nearest enemy companies), and brings the armies near its middle in once the navmesh is up. The war is stopped while you play; nothing goes back to it yet (slice 3).
- **Who comes in.** The battle maps run at about 100–150 bots, and a 4 km stretch of front holds 1,500 or more soldiers, so only the squads, crews and sections nearest the middle are embodied, each whole, up to the cap (160 by default). The cap is shared between the sides by their strength within a kilometre of the middle, so the bubble keeps the war's odds. (Taken nearest first regardless of side, a fight came in at 131 BRAVO to 37 CHARLIE, and CHARLIE's were all down within a minute.)
- **Who they are.** Each soldier fit to fight (or lightly wounded) becomes a bot under their rank and surname, with their skill from the war. Their role comes from their job: squad leaders lead, machine gunners carry the light machine gun, grenadiers the launcher, anti-tank gunners the light anti-tank weapon, marksmen the marksman's rifle, medics the bag, and the rest are riflemen. Every army still carries the same rifles (there are no per-army infantry kits yet).
- **Vehicles.** Each vehicle the war unit still has comes in beside it, crewed from its drivers, gunners and crewmen, in its army's model (Bradley, BMP-2, Warrior...). Guns, rocket launchers and engineer vehicles have no embodied model and stay out. Crewmen beyond what a vehicle seats fight on foot.
- **Where.** A soldier in a fight comes in exactly where the war's fight has him (his squad's place, and his own within it), so a fight carries on rather than restarting. Units not fighting come in at their squad's place in their company.
- **Orders.**
  - In a fight, the attackers go for the enemy in it, and the rest hold where they are, facing it.
  - A unit on the move goes where it was going, clipped to the window.
  - A unit holding or halted defends where it is: the town district, when it's on one.
- **The player** takes a rifleman's place in the embodied rifle squad nearest the middle (ALPHA's if it has one there), as that soldier.
- **The vehicle system** (`MotorPool`) now serves any match through `IMotorHost`. In the window, vehicles lost stay lost: the war decides replacements.
- **War.Tick** is one step of the whole war, shared by `mode=war` and the window. A 3-day war gives the same results to the last soldier as before it was split out.
- **Measured** (island 2, day 3, the biggest fight: 11 companies of BRAVO's 75th Motor Rifle Regiment against CHARLIE's 42nd Armoured Infantry Brigade).
  - 164 soldiers came in (78 BRAVO, 87 CHARLIE) in 42 squads from 6 companies, with 17 vehicles. 1,368 more in the window were left abstract.
  - The rendered run played at 82 FPS; headless, 179 s of game took 121 s.
  - **The embodied fight is far bloodier than the abstract one.** 68 of the 164 were killed in 90 seconds, most by BRAVO's BMP gunners at about 220 m, and some crews died with their vehicles. The abstract fight it continues was calibrated to historical casualty rates, a few per cent an hour. Fitting the two together is the calibration this phase folds in. Bots in the open at 200 m in front of autocannons are the first suspects.

**Calibration: the same fight both ways** (in progress). `calib=1` lets the war run on, abstractly, minute by minute beside the embodied fight from the same moment. Each minute the log sets the bubble's soldiers' fates side by side: how many the war's fight has killed, downed and wounded, against the embodied fight. `telemetry=` records the embodied fight as for the battle maps (`tools/telemetry.py`); `ITelemetryMatch` lets any match be recorded.
- **The test fight** (island 2, day 3): BRAVO's 75th Motor Rifle Regiment against CHARLIE's 42nd Armoured Infantry Brigade.
  - In the war it was a standoff at about 400 m: 30 minutes old, 557 soldiers in it, nobody attacking. It had fired 12,907 rounds for 63 casualties (about 205 rounds a hit), and the last hit was 2 minutes before.
  - In the next 5 minutes, the war's fight downed 3 of the bubble's 164 soldiers and killed none.
- **Embodied, as first built**, it killed 102 of the 164 in 5 minutes, at about 13 rounds a hit. The gap is two orders of magnitude, and real firefights at 200–400 m run at hundreds of rounds a hit or more.
  - BRAVO's 12.7 mm vehicle guns did most of it: 58–62 kills at 210–240 m, one round in 13 a hit. The gun (a cone of about 8 mils) and the gunner (laying to within 1.5 mils once settled) look realistic for a pintle mount; men in their sights at 240 m are what die.
  - **Exposure.** Of 131 men hit by machine guns and rifles, 121 were kneeling or standing, and four in five of those were fully suppressed. Bots spent 75% of the time on their feet and 4% prone.
- **Fixed so far:**
  - **Pinned.** Under heavy fire in the open, a bot goes flat whatever he's doing, and stays flat a few seconds after it eases (`BotBrain.WantStance`). BRAVO's dead in 5 minutes fell from 30 to 13. On a 33-a-side Valley battle, rounds per bullet hit went from 20 to 44, with kills about the same; single runs vary a lot.
  - **Dug in.** Squads the war has dug in come in with sandbagged fighting positions, a man behind each half. CHARLIE's dead fell from 83 to 67 of 86.
- **Round 2** (`count hit in cover` and `hit through` in the log say what the men hit in cover were doing and what rounds came through):
  - **Suppression lasts.** It wears off over 10 s instead of 4: a man can't tell the last burst from a pause. Driven down by fire, a man in cover stays down 3–8 s before he looks again, instead of popping straight back up into a gun still laid on the spot.
  - **Parapets.** A dug-in squad's positions have a parapet a metre thick (FM 3-21.8), not a hasty half-metre sandbag wall. A 12.7 mm round gets through about half a metre of sand here, and got through the hasty wall at its joins and corners: 108 rounds went through sand in one run.
  - **Hunched.** On a knee behind cover and not looking out, a man hunches with his head down, about a metre up instead of 1.25 (`Bot.Hunched`; the figure is drawn the same way). Kneeling upright, his head and shoulders stood above a waist-high wall: of 57 men hit while down in cover, 39 had the cover between them and the gun at chest height and were hit above it.
- **Where it stands** (the same fight, 5 minutes):

| | CHARLIE dead | BRAVO dead | Rounds a hit |
|---|---|---|---|
| As first built | 72 of 86 | 30 of 78 | 13 |
| Round 1 (pinned, dug in) | 67 | 25 | 20 |
| Round 2 | 59 (15 in the first minute, from 39–48) | 25 | 30 |
| The war's fight | 3 down | 0 | 205 |

  On 33-a-side Valley battles nothing changed beyond run-to-run noise (56–63 kills in 6 minutes, 16–44 rounds a hit).
- **Still to do.**
  - **Cover from one side only.** Of the men hit in cover, the biggest group (34) now had nothing between them and the gun that hit them: their cover faced one enemy while another, off to the flank, shot them. BRAVO's 11 vehicles stand in a line, and a straight wall shields one direction.
  - **A fair test.** The war's fight was a standoff 30 minutes old in which nobody had been hit for 2 minutes; the embodied one starts at full intensity. A fairer yardstick is the assault bench embodied (an attack on a dug-in position) against history.
- **Run to run, a 5-minute embodied fight varies a lot.** The round-2 setup run twice gave CHARLIE 59 and 72 dead, BRAVO 25 and 16. A change has to beat that to be judged on one run.
- **Tried and set aside** (round 3):
  - **Flanked in cover:** fire from a side the cover doesn't shut off blows it, and the man takes cover from the new shooter. It fired 49 times in a run, and the result (CHARLIE 76, BRAVO 31) was inside the noise.
  - **Horseshoe parapets:** the ends turned back 50° round the flanks. Together with the flank rule, more rounds came through the sand (25 against 9–16).
  - Neither is in.

**The embodied assault test** (`-- mode=window seed=2 assault=1 [attack=2] [defend=1] [minutes=45] [join=1] [verbose]`, `ConquestWindow.EmbodyAssault`). The abstract assault test fought with bots, at platoon scale so it fits the bots the battle maps run.
- **The setup.** ALPHA's rifle platoons attack BRAVO's dug in round the near (west) edge of the town nearest the island's middle (Froltosa).
  - The defending squads stand 50 m apart across the line, in fighting positions with metre-thick parapets, their vehicles 60 m behind.
  - The attackers start 800 m out: each squad goes for the stretch of the line opposite it, and each platoon's vehicles support by fire from 450 m.
  - Two platoons on one is about three to one, the odds FM 3-90 plans an attack on a prepared position at.
- **How it ends.** Carried, when no defender is on his feet on the line and an attacker is. Held, when the attack is fought down to half. Or out of time. It reports what each side lost and how long it took, against the abstract test and the historical marks, and quits.
- **First runs** (16:00, clear):

| | Attackers lost | Defenders lost | Took |
|---|---|---|---|
| 3 platoons (126, 9 Bradleys) on BRAVO's anti-tank platoon (22, 4 vehicles) | 9% | 100% | 4 min, carried |
| 2 platoons (84, 6 Bradleys) on a motor rifle platoon (26, 3 BMPs) | 50% (44% killed) | 54% | 5 min, held |
| The abstract test (9 companies on 3) | 25% | 57% | 2¾ h, carried 7 in 12 |
| History, 3:1 on a prepared position | 5–15% | more | hours |

- **What it shows.** The embodied attack is a brawl of minutes, not an attack of hours.
  - The Bradleys carried their infantry in mounted, 800 m to 170 m in two minutes, and dismounted about 150 m short of the position. Doctrine dismounts 300–500 m out and fights forward under covering fire.
  - Both sides spent the first two and a half minutes out of contact while the attack closed across open ground in daylight. The defenders' platoon never opened fire at range.
  - Then most of the killing was close: the defenders' dead fell within 5 m of their killers.
  - Calibrating the embodied fights to history means the battle engine fighting at company level by doctrine, not more single tweaks:
    - seeing and engaging at long range;
    - attackers dismounting short of the position;
    - suppression and bounds across the last 300–500 m;
    - preparatory fire.

  Each needs several runs per setting, at 5–10 minutes each.

**The doctrine pass** (in progress). The battle engine was built for 30–60 minute battle-map matches. Calibrating it against the war is also a check that its soldiers sell as real soldiers to someone watching in real time. `tools/coherence.py run.jsonl` reads a recorded fight for what a watching player would notice:
- when the first shot and kill come;
- the range each side fires at;
- how fast men move in and out of contact;
- how much of the time they're up, on a knee or flat, overall and under heavy fire.

Each change is judged on three runs of the embodied assault test.
- **Round D1.**
  - **Dismounting** 350–500 m short of the objective (APCs 50 m further), outside the defenders' small-arms reach, as doctrine has a mechanised platoon fight forward on foot (FM 3-21.71). It was 180–300 m. In the assault test the infantry walked anyway, so it changed nothing there: three runs held at half in 5–6 minutes, attackers 50–54%, defenders 65–81%.
  - What it showed: attackers covered the ground at a median 3.5 m/s out of contact (sprinting to far waypoints, jogging otherwise), and running never tired them.
- **Round D1b.**
  - **Pace.** Out of contact, soldiers walk (FM 3-21.8: rushes are for crossing fire). The walk under a fighting load is 1.6 m/s, not 2.0 (a road march is 4 km/h with halts, FM 3-21.18). Men keep to the leader's pace, and hurry only to catch up.
  - **Wind.** A run under the load takes a man's wind in about two and a half minutes, a sprint in 14 s; winded, he walks.
  - **The test's attack** goes at the defended line as an enemy-held place, so the squads run the deliberate attack: an ORP out of sight 290–340 m out, support by fire, the assault.
  - Three runs: carried in 8, 9 and 8 minutes; attackers lost 13%, 17% and 7% (history: 5–15%); defenders lost everyone, most of them to the Bradleys' 30 mm HE from their support positions. Out of contact men moved at a median 2.0–3.1 m/s.
  - On a 33-a-side Valley battle: kills about the same (64 in 6 minutes against 56), moving out of contact 2.9 m/s against 3.4.
- **Round D2: sited for fields of fire.** A dug-in squad's positions go where they see the most of the ground toward the enemy (FM 3-21.8): of the places within 30 m either side and 20 m back (or 10 forward) of where the squad was, the one whose positions, from 1.4 m up, see the most of a kneeling man at 100–450 m across a 60° front. At Froltosa's edge, among woods and folds, they see 28% of it.
  - Four runs: carried in about 9, 9, 8 and 6 minutes.
  - Attackers lost about a third, 19%, 7% and 6%, mostly to the defenders' grenade launchers and rifles; the defenders lost everyone.
  - The defence now opens fire as the attack comes up: in one run at a median 442 m.
- **Still to do.**
  - **The defence fires little,** 60–400 rounds a fight. The attack comes up mostly out of its sight: its ORPs see none of the line, and its support positions see the line from 110–170 m.
  - **The fight is minutes long.** The deliberate attack's timings were set for battle-map matches: 8–30 s at the ORP; then the assault goes when support is set (at most 75–90 s), or once support has been firing for 20 s in contact.
  - **No mortars or artillery** on either side in the embodied test yet.

**Fire support in the window** (`WindowFires`, mortars as embodied tubes).
- **The war's guns and rockets fire into the window from where they stand**, kilometres back and off the map, by the war's own rules (Artillery):
  - **Range and timing.** A battery answers if it reaches the target and has rounds left in its day's allowance. The rounds land 6 minutes after the call (10 for BRAVO's guns, 15 for its rockets).
  - **Accuracy.** The observer's error is 10 m plus 3% of his range, halved by adjusting; dispersion is 0.3% of range for guns. The tubes fire in volleys about 8 s apart.
  - **Rounds** come out of the war's tubes and allowance (`Artillery.Take`, the same code the war uses: a 3-day war is identical to the last soldier).
  - **The shells are real:** flown in for their last 3 s so men hear them and get down, bursting with real fragments. A 155/152 mm shell has an even chance of a fragment hit on a standing man at about 50 m (the war's casualty radius scaled from the 81 mm bomb's), and about 38 hand grenades' charge.
- **Calls for fire.** A squad leader who sees the enemy beyond the guns' safe distance from his own people (300 m for guns) calls fire on him; a squad in contact calls it on the enemy its side knows of near it. Only squads in contact used to call, and contact came inside the safe distance, so the guns never fired.
- **Planned fire.**
  - A preparation lands at a set time, without a call.
  - A defence has targets planned on its approaches, and fire called onto one comes in 90 s.
  - Lift and shift: any mission stops when the side's own people come within the gun's safe distance of its target.
- **Mortars** come into the window as tubes with their crews, and lay on the clusters of enemy their side has seen. From a target being reported to the first bombs leaving the tube is now 4 minutes, as the war has it (`CrewBrain.MortarResponse`; this applies on the battle maps too). They used to fire four seconds after a sighting: in the assault test BRAVO's mortars had killed 22 attackers before ALPHA's guns could fire.
- **Fighting positions walled all round** (`Fortifications.Position`), as a dug position is earth all round: the metre-thick parapet in front, half-metre walls down the flanks and across a lower rear, with a gap to get in and out. With a front only, a shell landing behind the line sprayed men standing at ground level, and 18 rounds of a preparation killed 12–14 men in their positions.
- **The embodied assault test with fire support.** Each side's battalion mortars are embodied (ALPHA's 500 m behind its start line, BRAVO's 700 m behind its line), and a battery of each side's guns stands 8 km back off the map. ALPHA's guns fire a preparation on the line at 1.5 and 3.5 minutes, while the attack is still 450–650 m out. BRAVO has its approaches planned at 350 and 550 m. The result judges the attack on the line's own defenders, with the fire support's losses given apart.

| | Attackers lost | Defenders lost | Took |
|---|---|---|---|
| Mortars firing on sight | 50% | 12% | 4 min (fought down to half) |
| Mortars at 4 minutes; preparation too late, always lifted | 45%, 7%, 21% | 92–100% | 7–9 min |
| Preparation in time, observers calling fire | 18%, 18%, 1% | 96–100% | 5–8 min |
| Positions walled all round | 5%, 46%, 8% | 85–100% | 7–9 min |
| The abstract test (with mortars) | 25% | 57% | 2¾ h |
| History | 5–15% | more | hours |

- With all-round positions, ALPHA's preparation killed 2 and 6 men in its runs against 12–14 before; ALPHA's mortars killed 11 in one run (bombs fall steeply into an open position). One BRAVO call on a planned target brought 30 rounds of 152 mm down 90 s later and killed 9 attackers.
- Run to run, the fight swings on the duel between BRAVO's BMPs and ALPHA's Bradleys. When the BMPs live, their 30 mm HE kills 19–24 attackers.
- **Still to do.**
  - Mortar crews snag on their own tube walking to it, and in two runs in three the mortars fired nothing.
  - The embodied mortar is the 81 mm whatever the army's (the war's US and Russian battalion mortars are 120 mm).
  - Nothing in the window yet goes back to the war but the rounds fired (slice 3).

**Doctrine's pace** (`Squad.Deliberate`, on in the window; the battle maps keep their compressed attack). The user wants a fight to take half an hour to an hour: shorter than history's hours, for a species that doesn't hang back, but not minutes. A deliberate attack on a prepared position takes its time on things soldiers do (FM 3-21.8 ch. 7, FM 3-21.71):
- **Bounding overwatch** ends each bound with a halt to look and listen, 20–40 s, before the next.
- **The leader's recon from the ORP.** Formed up, the leader plans the attack (support position, line of departure). He then goes forward with his buddy, crouched once inside 320 m, holding fire unless they're onto him. His recon post is somewhere 170–320 m out that sees some of the objective from a knee with something in front, failing that the support position. He watches three minutes, comes back and gives a minute of orders. A preparation planned on the objective is called then (`WindowFires.OnOrder`: a planned target, landing in 90 s), and nobody moves forward under it. A leader who falls on the recon leaves the squad to go in on the plan.
- **Fire superiority.** Deployed, the support opens fire once it's set and the assault team is at the line of departure (or in contact, or after six minutes). The assault goes when the support has fired two minutes and nobody in the squad has been under fire for 20 s, or after six minutes of support fire. The vehicles fire on the objective from then too.
- **Overhead cover.** A position dug in six hours or more (an estimate), or prepared on purpose as the assault test's is, has a roof: 45 cm of earth on logs (FM 3-21.8), raised about 1.4 m on posts. The men fire from a knee through the slot under it, and nobody stands up under it. It's on its own layer (`Layers.Overhead`): rounds and fragments hit it, men walk under it. A heavy shell's direct hit would bring a real one in; here it holds.
- **Tried and fixed on the way.**
  - Leaders on recon saw the defenders, called "contact, engage", and the squad deployed off it: every recon in the first run.
  - Sent to the support position itself (100–170 m out, chosen to shoot from), they were seen there and fired on. Six leaders in eight found no post 190–300 m out with a quarter of the line in view, at Froltosa's wooded edge.
- **The embodied assault test at doctrine's pace** (2 platoons on 1, each run about real time):

| | Attackers lost | Defenders lost | Took |
|---|---|---|---|
| Before (fire support, walled positions) | 5%, 46%, 8% | 85–100% | 7–9 min |
| Doctrine's pace | 17%; 56% (held) | 100%; 92% | 11 min each |
| With recon posts, overhead cover, IFV missiles (the missiles never fired, see Combined arms) | 50%, 51% (both held) | 38%, 65% | 10–11 min |
| Missiles fixed | 17%, 10%, 14% | 100%, 100%, 96% | 12, 12, 11 min, carried |
| The abstract test (9 companies on 3) | 25% | 57% | 2¾ h |
| History, 3:1 on a prepared position | 5–15% | more | hours |

- **Where the time goes now.** The ORPs are reached at 6–8 minutes, recons take 2–6, and squads deploy at 8–11 minutes, most of them because the recon was seen. The defence then dies within a minute or two of the support opening up at 100–170 m. The killers are the Bradleys' 30 mm HE bursting at the firing slot of a kneeling man, and coax at 100–130 m. No squad got to its assault: the line was empty first.
- **Still to do for fight length.**
  - **One go and it's over.** The test ends at the first decision. A real fight of an hour is several goes: a held attack pulls back, reorganises, brings fire onto what stopped it and goes again; a lost position is counterattacked by the defence's reserve. The war's fights already do this. In the window it comes with the units round the fight being live (slice 3).
  - **The cannon at the slot.** A 30 mm gunner puts HE onto a kneeling man in his firing slot at 400–600 m within a few bursts. This wants checking against gunnery data.
  - **Recon parties are seen** at 120–180 m in most squads, and their squads deploy off it.
  - The vehicle duel still swings runs: dug-in BMPs beat the Bradleys 4–0 and 4–1 in two runs before the missile fix.

**Playing in the window** (`WindowHud`). The window had only the bare player HUD: no squad, no map, and nothing after dying.
- **On screen:**
  - the day and hour, and how many each side has on their feet in the window;
  - who you are (rank and name from the war), your job and your unit up to the battalion;
  - your squad's roster (hurt, down, low on ammunition, the leader starred), its order and the way to it, and the briefing (what the squad is doing and your part in it), shared with the battle maps (`SquadPanel`);
  - the radio (your squad wherever they are, your side within 250 m) and the kill feed.
- **Your squad on screen.** The green marks over your squadmates asked the battle map for your squad, so in a window nobody was marked. They now ask whichever is being played (`HudOverlay.MySquad`).
- **The map** (M).
  - **The window:** the ground and its towns; your side's squads and where they've been sent; your squad's plan (ORP, support position, line of departure); vehicles; the enemy your side has seen in the last minute; your side's fire missions and when they land. The wheel zooms in on you (1–8×, 2× to start): the fighting is a few hundred metres of a 4 km window.
  - **The island** (Tab): the war's ground as each side holds it, a kilometre square at a time, with contested squares ringed; each side's share of the island; your side's battalions; and the window on it. The island's picture is drawn off the main thread when the window opens, and the holdings as one small picture made again only when the war moves on (drawn square by square, 10,000 a frame, the map ran at 12 FPS).
  - Watching (`join=0`) shows every side.
- **Killed, you carry on as a squadmate** (the design: one grunt, no respawn). For 10 s the camera is on the one you'll be: the nearest of your squad on their feet, not riding, and not the leader while anyone else is left. 1–9 pick another.
  - You take his place as he is: his job and kit, the rounds in his weapon and pouches, and his wounds.
  - The man you become leaves the world marked gone, hidden and still, and is freed a few seconds later, once every brain has dropped him. (Freed at once, he stayed among other bots' threats as a freed object; and the spectator kept him as its man, so the radio read where he was every time you were dead after that: thousands of exceptions a run. The camera's man is now checked before he's read.)
  - Taking a squad leader's place, you're a rifleman: no promotions, and the squad is led by whoever is next.
  - With your squad gone, you carry on in the nearest squad of your side that has anyone left, rifle squads first. Replacements by sea need the war running live (slice 3).

**Not done in the ground yet**
- Towns are capped at 700 m across; a town of 9,300 is really about 1.5 km. Big towns will need building interiors only near the player, with shells beyond.
- The town builder doesn't know about rivers, so a stream through a town can run under its blocks. There are no quays at ports.
- Cover edges still show the 100 m grid from the air; one climate's colours only.
- Nothing beyond the window's edge yet (slice 4).

## Milestones

1. **Feel prototype (done).** Firing range: carbine and marksman rifle,
   grenades, stances, lean, sway, breath hold, recoil, suppression, speed-of-sound
   audio, distant battle ambience, sniper drill.
2. **Bot duels (current).** The arena map plus 1v1, 2v2, 5v5, 8v8 and 12v12 rounds, played or
   spectated. Tune the human-like aim (reaction time, flick and correct, recoil
   control skill) and perception (sight and hearing) until bots feel fair.
3. **The battlefield.** Split into steps:
   - **3a (built):** a 2 km map with 9 settlements (villages, walled compounds, hilltop outposts) and 3 faction bases. King of the Hill: every 5 s the side with the most people alive in the zone scores a point, the zone moves every 8 minutes, and the first side to 300 wins. The dead respawn after 10 s at their side's rally point, 280 m from the zone. Everyone is a fully simulated bot (3×8 or 3×12), plus a map screen (M) and a compass marker for the zone.
   - **3b (built):** Territory mode, squads and command.
     - Every settlement is a capture point. Standing on it with more people than anyone else neutralises the owner's hold, then captures it; a bigger advantage is faster.
     - Tickets: each death costs 1, and every 15 s a side loses 1 per point it's behind the leader. At 0 it can't reinforce; the last side with tickets wins.
     - Spawns: your base, or any owned point with no enemy near. Bots reinforce whichever is nearest their squad's objective; the player picks on the map.
     - Squads of up to 6 with a leader. On the move the members keep a wedge on the leader; the leader jogs if they're strung out.
     - A commander per side (every 20 s, and right after a capture or loss) values points (neutral, enemy, or ours under threat) and assigns squads, spreading them out.
     - The player is in ALPHA-1: click a point on the map to order the squad there (for 5 minutes), or press B to make the squad follow you.
     - Bounding: bots fight from cover, then move up to the next covered spot 6-25 m closer to the enemy (or to the objective) while a squadmate covers. Only a third of a squad moves at once.
   - **3b+ (built): roles and squad types.**
     - Roles: squad leader, rifleman, automatic rifleman (M249, suppresses in long bursts and moves last), grenadier (M320 40 mm, lobs HE at people behind cover at 35-300 m), medic (treats the worst-hurt nearby, 55 hp per kit), marksman, combat engineer (sandbag walls), ammo bearer (tops up anyone within 6 m).
     - Squad kinds (Roles.Compose): rifle squads take ground; weapons teams (2 AR) set up 110-230 m out on high ground with a line of sight to the point a rifle squad is attacking and pin it; recon teams (2 marksmen) observe enemy points from 170-320 m, see further (binoculars), and radio contacts that show on the map and reach nearby friendlies; engineers fortify the owned point nearest the front; logistics (ammo + medic) follow the neediest squad.
     - The player picks a role in the menu or with 1-7 while dead. H is the role's tool. A roster panel shows your squad, roles, health and low ammo.
   - **Wounds (built, `Body`).** There are no hit points. A body has:
     - Blood volume, bleeding, trauma and pain.
     - Specific injuries: head (usually fatal; a graze concusses), chest (internal bleed, punctured lung 35%, heart 7%), abdomen, arm (arterial 12%), leg (femoral 14%). The arm/torso split uses how far the round's path passes from the body's centre line.
     - Down: below 55% blood, from a hit that drops you, or from trauma. Dead: below 35% blood, catastrophic trauma, hit again while down, or 150 s down.
     - Effects:
       - leg: slower, no sprint;
       - arm: sway and slow reloads;
       - lung: no breath-hold, faster stamina loss;
       - blood loss: grey tunnel vision, a heartbeat, slow turning;
       - concussion: flashes, double vision, swimming view, muffled ears.
     - Self-aid (X, and bots do it themselves) stops limb bleeds and halves internal ones. Medics treat everything and revive the downed (6 s). Tickets are only spent on death.
     - Tinnitus is two stages: a masking ring, then a longer muffled deafness. It scales with distance, and a wall between you and the blast counts.
     - Next: medevac (needs vehicles), smoke, engineers breaching and demolishing, AT/AA teams once vehicles exist, and rebaking the navmesh around fortifications.
   - **Vehicles, part 1: ground (built).**
     - Seven classes, each with its own variant for all three factions and a distinct silhouette: LTV (HMG on a pintle), transport truck (12 aboard), logistics truck (a FOB's worth of supplies), APC (HMG turret, 8 aboard), IFV (30 mm AP/HE + coax, tracked), MBT (120/125 mm APFSDS/HE + coax), MGS (105 mm, wheeled).
     - Movement is kinematic and follows the ground. Heavy vehicles push trees over. There's run-over damage.
     - Damage uses armour facing (front, side, rear, top), slope, and penetration against armour; small arms do nothing much. A penetration can damage the engine, throw a track, jam the turret, set off the ammunition, or send spall through the crew (who take ordinary wounds). A destroyed vehicle becomes a burning wreck that stays.
     - The player gets in and out with F and changes seat with the number keys. The driver uses a chase camera. The gunner uses a stabilised sight: the turret slews at its own speed, a ring shows where the barrel points, and the sight has a zoom. V changes ammo (and the coax).
     - Bot crews:
       - drivers follow a separate vehicle navmesh (2 m clearance, trees included), look ahead for obstacles, back out when stuck, and stop for friendly troops;
       - gunners have all-round awareness, see hull-down turrets, only shoot armour they can penetrate from the angle they have, and choose AP, HE or the coax.
     - Infantry anti-tank: a light AT rocket (1 per rifle squad) and heavy AT teams. Rockets are visible and smoke. Bots lay the rocket and lead the target; other infantry take cover from armour.
     - Motor pool: the fleet grows with team size. Vehicles park at base, respawn after a delay, and cost tickets when lost. Useless vehicles are abandoned, then scuttled.
     - Transports fetch a far-off rifle squad and drop it short of its objective. Logistics trucks carry supplies for a logistics team to build FOBs (a spawn point and ammo cache, which can be overrun or destroyed).
     - The radio carries armour sightings (shown on the map, and AT teams are sent to hunt the armour) and heavy-contact calls (the commander weights reinforcements toward them).
   - **Vehicles, part 2: air and fire support (built).**
     - Helicopters are transport (pilot, 2 door guns, 8 aboard) and attack (chin 30 mm for the gunner, rocket pods for the pilot), 3 faction variants each.
     - The flight model is collective, cyclic (asks for an attitude) and pedals, with drag-limited speed, capped climb and sink rates, and a banked turn. A hard, fast or tilted touchdown is a crash; a tail-rotor hit spins it; a helicopter destroyed in the air falls and explodes on impact.
     - Player controls: mouse is the cyclic (it centres itself), W/S collective, A/D pedals, Space hover assist, X flares, LMB rockets, Alt to look around, with a chase camera and a flight HUD.
     - Bot pilot: an autopilot converts wanted acceleration into rotor tilt and collective, follows the terrain (looking up to 450 m ahead, slowing when it needs to climb), and stays inside the map. Modes are transit, landing (slow first, then down) and attack runs (in from about 1.4 km, a rocket salvo at about 1 km, break off at 450 m, come round from a new bearing).
     - Transport-helicopter jobs: medevac (it lands by a cluster of wounded down 15 s or more with no known enemy near, stabilises and loads them, flies them home) and air assault (it lifts a rifle squad that has a long way to go and lands it short of the objective).
     - Gunships fly close air support over the main attack, or hunt reported armour.
     - Anti-air: the SPAA carries twin 35 mm with proximity-fused rounds (they burst next to an aircraft) and leads airborne targets. MANPADS: bots track the target for about 2 s, then fire; players aim until the lock tone and a LOCK box appear. Missiles home with lead pursuit and can be decoyed by flares (about 60% while flares are out); bot pilots pop flares on a launch warning.
     - Mortars: an emplaced 81 mm tube (at base, moved up to the newest FOB). The crew fires 5-7 round missions at clusters of recent enemy sightings, with no fire within 70 m of friendlies. Incoming bombs whistle about 2 s before landing. The player lays it by looking at the spot.
     - Fleets per faction: BRAVO fields an APC where the others field an IFV, and CHARLIE fields an MGS instead of a tank. Helicopters appear at 24+ per side, and gunships and SPAA at 30+.
   - **Maps (built).** Five procedural battlefields, picked in the menu (`MapSpec`):
     - *Valley* (2 km, temperate): the original rolling hills and villages.
     - *Kessel Forest* (3 km): dense mixed forest (pine, broadleaf, birch, undergrowth), steep hills, one big summit with an outpost.
     - *Novigrad* (3 km, urban): a city of 3-6 storey apartment blocks, parks and a cathedral, six districts as capture points, wooded hills round it (one big one with an outpost), a few villages out in the hills.
     - *Al Hamra* (5 km, desert): a flat-roofed desert city in the middle (1-3 storeys, rooftops you can fight on, a mosque with dome and minaret, a covered market), six districts; out in the sand, mud-brick villages, walled compounds, rocky mesas with outposts on two, an oasis with palms and a village.
     - *Highlands* (5 km): big open hills and ridges, patchy pine, many hamlets, outposts on the three biggest hills.
     - Terrain grid spacing grows with the map (4 m, 5 m, 8 m) so the mesh stays the same size. Tree species, density and patchiness come from the biome; trees and rocks are planted after the buildings so nothing grows through a house.
     - Buildings are merged into one mesh per material per 64 m chunk and one static body per chunk (a city is about 100 000 boxes). Walls are cut into as few boxes as possible around their doors and windows.
     - Multi-storey buildings: 3.2 m storeys, windows with 1 m sills (low cover), rooms split by partitions with doorways, and a stair shaft of stacked 30° ramps. Flat roofs have parapets and an open stairhead. Buildings near a district can all be entered; the rest of the city is mostly solid shells with window panels.
     - Floors, stairs and roofs are "ground" to the cover finder, so bots take cover and fight on any floor. The navmesh covers every storey.
   - **Terrain-aware tactics (built, `Surroundings`).** Every few seconds a bot works out whether it's in the open, in forest (trees within 30 m), in a built-up area, or indoors (a floor overhead).
     - Formation: a wide wedge in the open (7 m), a tighter wedge in the woods (3.5 m), a staggered file in streets and buildings (2.8 m).
     - Bounds: up to 36 m in the open, 22 m in forest, 16 m in town, 10 m indoors.
     - Pace: no sprinting down a street once there's a fight about; walking with the gun up indoors.
     - Grenades: someone in a room gets fragged sooner and from closer (5 m); going into a building after someone starts with a frag through the door.
     - Defenders of a district take windows and rooftops facing out and watch out of them. Weapons and recon teams prefer an upper window or rooftop overlooking their target to a hillside.
   - **Front line (built, on by default; a checkbox in the menu).** Hell Let Loose-style: the points and the three bases form a network (a Gabriel graph, which links each point to its natural neighbours). A side can only capture a point linked to one it holds or to its base, and each side starts holding the point nearest its base. The commander ignores points behind enemy lines and garrisons its own front-line points. The map draws the links, coloured where one side holds both ends, and marks each point ATTACK, DEFEND or locked for your side.
   - **Vehicle traffic (built).** Drivers keep a gap behind vehicles going their way, steer round parked or oncoming ones (both keep right), give way at crossings (to the right, and to whoever gets there first), and if two are still nose to nose after 5 s the one with the lower id backs off and replans.
   - **Engine sound aboard (built).** Inside, you hear a muffled cabin version of the engine (steep low-pass plus a hull boom) instead of the outside loop at 0 m. Getting in or out crossfades over about 2 s, and the outside engine's near field is capped so it isn't a blast when you're standing next to it.
   - **Mortar rounds and aircraft wrecks (built).**
     - A falling bomb's whistle is a sound source that travels with it, pitch-shifted by how fast it's closing on you. A round landing off to the side falls in pitch as it drops past; one coming at you holds a high, steady note and gets louder.
     - Explosions take a charge size (grenade = 1, 81 mm bomb = 4). Fireball, smoke, debris, flash and overpressure reach scale with its cube root, and shells get their own deeper, longer blast sound.
     - Aircraft destroyed in the air keep their momentum: the wreck arcs down, tumbles and noses over, with its fire and smoke trailing behind it, and burns where it hits. `mode=vtest air shootdown` tests it.
   - **Bot movement in towns (fixed).**
     - "Can I walk straight there?" is tested with a body-sized sphere, not a thin ray, so bots don't clip corners or aim through windows.
     - A waypoint only counts as reached on its own level (on a stair the next one can be straight overhead).
     - In towns and indoors a bot that's been pushed off its route re-plans instead of grinding into a wall.
     - A squadmate only takes over the leader's route from a point it can walk straight to. In towns and indoors the squad files along the leader's actual trail instead of taking formation positions that can land inside walls or under a staircase.
     - Fixed an old bug: a new order arriving within 0.6 s of the last path query kept the finished old path, so the bot "arrived" before the new path was ever planned.
     - On Novigrad at 20 per side, stuck events went from 713 to 266 over 7 minutes.
   - **Situational awareness (built).**
     - Squad engagement: a squad is engaged once anyone in it is shot at, hurt or suppressed, or an enemy is within 60 m. The leader also engages further contacts that are in the way (near the route or the objective) and not too many. An engaged squad fights back instead of walking on to the objective. Contacts that aren't in the way are bypassed.
     - Defenders: with the front line, about a third of each side's rifle squads hold the most threatened front-line points. When their point is attacked, they take windows, rooftops and cover on the side the attack is coming from.
     - Assaults: a squad attacking an enemy-held point forms up 60-190 m short (most of the squad within 25 m, or 25 s at most), then the leader calls the assault and they go in together.
     - Armour memory: infantry remember armour they saw in the last 20 s, and hear it moving close by (heavy vehicles within 220 m, light within 140 m). AT soldiers who can hurt it go hunting from a flank. Everyone else stays in cover from where it was instead of walking back into its sights.
     - Vehicle crews see all round, so gunners engage infantry beside and behind them, not just ahead.
     - Stairs have a handrail along each flight's open side and a rail at each landing: bots get on at the foot, not over the side, and don't step off into the stairwell.
     - Helicopters idle quietly on the ground, and a real crash (over 9 m/s) wrecks them.
   - **Squad as a unit (built, from a review of the bot code).**
     - Fire teams: Alpha and Bravo, dealt out by role so each gets a support weapon and a specialist; the squad leader stands apart. If he falls, the most experienced man takes over and says so.
     - Team bounding overwatch: in a fight only the bounding team moves, all of it together, while the other team covers. They swap once the movers are set (or after 12 s).
     - Sectors of fire: holding or consolidating on a point, the squad shares out the full circle. At a halt, men alternate sides of the direction of travel.
     - Fire discipline: soldiers always shoot within 150 m or when returning fire. Otherwise the squad engaged in a fight fires out to about 400 m (long arms about 700 m), an unengaged squad out to about 300 m. A squad holding a point holds fire until the attackers are within about 200 m.
     - Drills (`Squad.Drill`):
       - React to contact (50-300 m): the team nearer the enemy suppresses, the other flanks wide.
       - Break contact: when badly outnumbered and under real pressure, teams bound back by turns while the others cover.
       - React to indirect fire: shell or mortar impacts within 60 m send the squad 45-70 m off the impact area.
       - Consolidate: after taking a point the squad holds it for 40 s with 360° security, and the leader reports casualties and ammo state (green, amber or red).
     - March orders (`Squad.MarchOrder`), picked by the squad leader from the situation:
       - Travelling: a wedge in the open, a file along the leader's trail in town and forest.
       - Travelling overwatch: a hostile objective within 1 km, and the second team trails about 50 m back.
       - Bounding overwatch: inside 600 m, the leader's team advances about 50 m and stops, calls the other team up, then goes again.
       - Herringbone at halts (alternate sides, facing out; a ring at the ORP).
       - Assault line: abreast of the leader in the assault.
     - Deliberate attack on an enemy-held point (`Squad.Phase`):
       - ORP: about 150-340 m short, at least 8 s for security and orders.
       - Deploy: the team with the automatic rifle goes to a support-by-fire position 110-180 m out, off to a flank, with a line of sight to the objective. The leader takes the other team to the line of departure (about 120 m out).
       - Assault: support opens fire on the objective (area fire at its buildings if nobody is in sight) and the assault team goes in on line.
       - Limit of advance: nobody chases more than 50 m past the objective during the assault or consolidation.
       - The commander doesn't re-task a squad mid-attack. Time limits (ORP 60 s, deploy 120 s, assault 100 s) keep a stuck attack from hanging.
     - Buddy pairs: each team is two pairs. The last 40 m are closed by buddy rushes: one dashes about 8 m to cover while the other covers, then they swap. In close combat the pair takes turns moving and firing.
     - Danger-area crossings (`SquadCrossing`). This applies only near the enemy: a hostile objective within 1.2 km, or a fight in the last 90 s. Only infantry on foot do it.
       - Every 2 s on the move, the leader samples the next 150 m of his route every 5 m.
         - A sample counts as covered if there are trees around it, a wall within a few metres, or a roof overhead.
         - Otherwise it counts as exposed if at least 3 of 6 horizontal sightlines run 60 m clear.
       - A crossing is an exposed stretch that starts within ~65 m and has cover on both sides: a street, a square, a gap, a clearing or open ground. If he's already out in the open, it's bounding overwatch's job instead.
       - The drill:
         1. Halt. The leader goes to the near edge and looks for 3 s. Bravo spreads along the near edge watching left and right; Alpha closes up behind the leader.
         2. Alpha sprints across and spreads along the far side.
         3. Bravo crosses under Alpha's cover.
         4. The march resumes, with a 25 s cooldown.
       - Without fire teams, the men alternate.
       - Contact or new orders call it off.
       - Slots are pulled in off walls and snapped to the navmesh.
       - Pass `nocross` to turn it off for A/B tests.
   - **Graphics settings (menu):** display mode (windowed, borderless or exclusive fullscreen), v-sync, frame cap, render scale (FSR below 100%), MSAA and shadows. They're saved, and F11 toggles fullscreen anywhere. Screenshot and test runs stay windowed.
   - **3c:** scale. The abstract far-away simulation and the promotion/demotion handover described above, reaching 3×33 with persistent aftermath.
4. **Economy.** Cash, buy screen, gear loss, and bots buying loadouts.
5. **Roster.** About 150 persistent named mercs with skills, playstyles, bank
   balances and grudges. Post-match scoreboards.
6. **Vehicles.** Trucks, then armor, then helicopters.
7. **Conquest mode** (next; see "Conquest mode (planned)"). It takes in 3c, the economy and the roster.

## Bots (built)

- **Senses** (`BotSenses`)
  - Sight builds up awareness over time. Distance, being in central vs peripheral vision, stance, movement, and whether the target just fired all change the rate. It's checked against a target's head, chest and hips.
  - Hearing uses sound events whose wavefront has actually reached the bot, and gives only a rough position.
  - Bots also learn about enemies from teammate callouts within earshot or on their squad's radio (after a short delay), from being shot at, and from being hit by aimed fire (see Quality pass).
- **Aim** (`BotAim`)
  - A human-like flick that over- or undershoots, then settles.
  - Turn speed is capped, so big swings take time.
  - Moving targets are tracked through a smoothed estimate, so poorer bots lag behind and under-lead.
  - Hand tremor is worse when moving, suppressed or hurt.
  - The share of recoil a bot fails to pull down stays as aim error, so weaker bots' sprays climb.
- **Brain** (`BotBrain`): states are Advance, Investigate, Engage, TakeCover, InCover (peek / hide cycle), Hold, Flank and Search.
  - Reaction time applies before the first shot.
  - Fire discipline scales with range: long bursts up close, controlled pairs mid-range, single aimed shots far out.
  - Bots won't shoot through a teammate.
  - They won't fire when their muzzle would put the round into cover.
  - With no contact for a while they sweep the map, including the enemy spawn.
  - Standoffs don't last. After a short hold someone commits: a frag followed by a push, a "cover me" flank while a teammate suppresses, or a straight push.
  - Suppressive fire and pre-fire go at the enemy's last known position.
  - Panic bursts: longer full-auto strings when suppressed or hit.
  - Bots lean around corners (and the hitbox leans with them), throw frags along solved arcs, and get behind something (or flat, or away) when a grenade lands nearby.
  - They go prone in the open at range, when pinned, at observation posts and for incoming mortar bombs (see Quality pass).
  - They sprint between positions.
- **Craters** (`CraterField`): one shared field of dig depth, spoil and scorch on a 0.25 m grid, so blasts on top of each other merge into one bigger, deeper pit and blow earlier rims away. On terrain, the ground is cut away (a hole mask the terrain shader discards) so the bowl has real depth. Characters don't sink into craters yet.
- **Cover** (`CoverFinder`): fully dynamic.
  - Candidate spots on the navmesh are tested with rays from the threat's eye.
  - A spot counts only if it hides you crouched. It scores higher if you can shoot over it standing, or step out to shoot around it.
- **Headless testing:**
  - Command: `-- mode=spec5 verbose shot=x.png frames=N` with `--headless --fixed-fps 60`.
  - It logs every kill, per-bot shots, hits, blocked shots and wide misses, and a status dump every 30 s.

## Combined arms (built)

At 33 a side each faction has one tank, one IFV/APC and one SPAA, so the working unit is **a vehicle with a rifle squad**, not a vehicle platoon (`MotorPoolCombat`).

- **Pairing** (commander): each IFV/APC is mechanised with the rifle squad that has the longest way to go. The tank and the SPAA go with the main attack. They stay with that squad while it lasts.
- **Mechanised infantry**
  - **Pickup:** the carrier fetches its squad when fetching and driving beats walking (up to 1.6 km away, and the squad more than 450 m from its objective).
  - **Waiting:** the squad halts once the ride is within 400 m. A pickup that can't get through gives up after 40 s plus 1 s per 5 m of distance, then waits 3 minutes before trying again.
  - **Drop:** the carrier drives to a dismount point out of the objective's sight, 180–300 m short (IFV) or 260–380 m short (APC).
  - **Contact on the way** (hit, or enemy within 350 m) means dismounting there and then, with the squad exiting on the side away from the threat.
  - **Afterwards** it supports the squad from behind, never out in front, and fetches them again for the next long move.
- **Tanks**
  - They overwatch from standoff in the open.
  - During a deliberate attack they take the support-by-fire position alongside the support team.
  - In town or forest they go in behind the infantry along the squad's own trail. Alone in close terrain, a tank pulls back out.
  - Enemy armour reported within 1.1 km comes first. An IFV only takes on lighter armour; an APC keeps out of armour's way.
- **Firing positions**
  - They must be reachable on the vehicle navmesh and have gun line of sight. Hull-down spots are preferred: the turret sees over a crest the hull sits behind.
  - After ~8 engagements or 150 s the vehicle moves on (shoot and scoot), backing out rather than turning round.
- **Fire for the infantry**
  - A squad in contact calls its vehicle ("Bradley, enemy north, 300 meters — put fire on it!") and the gun suppresses the contact.
  - In the assault the vehicle fires on the objective, avoiding any spot our men are within 35 m of, and lifts fire when they're on it.
- **Gunnery discipline**
  - Engagement ranges: a tank gun 2.5 km against armour, an autocannon 1.6 km. Against infantry, HE 1.5 km and MGs 900 m.
  - **IFVs' anti-tank missiles** (2026-10-10): the Bradley's TOW-2 (to 3,750 m, about 190 m/s, about 900 mm of armour; six of its seven here, two in the launcher) and the BMP-2's 9M113M Konkurs (to 4,000 m, about 210 m/s, about 750 mm; four, one on the launcher). Wire-guided down the gunner's sight: they fly the line of sight (not climbing for the roof as a helicopter's do), the gunner holds his sight on the target until it strikes and fires nothing else meanwhile, and the vehicle halts to fire and stays halted while it flies (`Vehicle.HaltUntil`). Lose sight of the target and the missile flies on unguided. The gunner takes the missile to armour the cannon can't get through, or beyond 1.5 km; nearer lighter armour gets the 30 mm. A player in the gunner's seat locks it on the enemy vehicle under the crosshair. The IFVs had only cannon and coax: they couldn't hurt a tank, and fought each other with 30 mm at close range.
  - Priority goes to a gun laid on us that can kill us, then to men with rockets, then to anyone within 80 m.
  - No firing with a friendly near the line of fire or within 14 m of where HE lands.
  - New armour targets are called out. Idle guns scan their sector.
- **Smoke**
  - Clouds block bot and crew sight lines. Armour carries 2 salvos of smoke launchers; hit by an AT weapon, it smokes towards the shooter and reverses out.
  - Leaders carry smoke grenades and screen a danger-area crossing when the enemy's close.
- **Protecting armour:** infantry target enemy AT soldiers near our vehicles first.
- **Rearming:** a vehicle below 20% main-gun ammunition goes to the nearest FOB, logistics truck or base to rearm (20 s).
- **Trucks:** drop-offs are now out of the objective's sight too, 240–390 m short.
- **Not done:**
  - vehicle platoon formations and bounding (pointless with one vehicle of each kind);
  - recovery vehicles;
  - squad-called CAS runs;
  - LZ selection that avoids known AA;
  - convoy escort.
- **A/B testing:** pass `noca` to turn off the pairing.

## Playing inside the squad (built)

- **Squad briefing** (`Squad.Brief`, shown under the order line). It reads the same state the bots act on, so following it means doing what the squad expects.
  - It shows what the squad is doing right now: marching (and how), a drill, a step of the attack, a danger-area crossing, riding, holding.
  - It shows **your part in it** by fire team, for example "Bravo (you): cover left and right along the near edge", "FLANK LEFT while Alpha suppresses", or "SUPPORT BY FIRE — hold fire until the assault".
  - It also shows your team and buddy, and what the squad's vehicle is doing.
- **Your spot:** a green "your spot" marker where the formation or plan wants you, hidden in drills, fights and vehicles. Your sector of fire shows as a green bar on the compass.
- **Leader commands** (leader role; N opens the menu, then a number):
  1. move here (aim point)
  2. hold here
  3. on me / work the objective
  4. suppress where I'm aiming (15 s of area fire)
  5. smoke there (the nearest man with smoke, within 40 m)
  6. march order: auto / file / on line / halt
  7. vehicle fire mission on the aim point (25 s)
  8. carrier: pick us up / dismount
- **Riding:** you're dismounted with your squad, getting out on the side away from the fire, with a prompt.
- **Map (M):** shows your squad's ORP / SBF / LD, danger-area crossings, contact, the suppress point, and your vehicles with their fire.
- **Testing hooks:** `role=leader`, and `squadcmds=0,3,4,...` fires commands every 5 s from 10 s in.

## Drones (built)

- **The drone team** (`SquadKind.Drone`: two drone operators and a rifleman for security). The commander posts it 350-700 m back from the attack it supports (or from the most threatened front point), out of sight, watching the objective.
  - The first operator flies the quad (a Mavic-class camera drone with two grenades under it); the second flies FPVs. Alone, one man does both.
- **Quad** (`Drone`, `DroneOps`)
  - Orbits ~80 m around the objective at ~105 m above whatever is below it. At that height it is a speck: bots hear it, but only shoot once it comes down.
  - Its camera sees down and out to ~130 m, but not through roofs or canopy. Everything it sees goes to the side's intel picture and to nearby squads' spotting (it feeds the mortars, the map and the FPVs), and vehicles get called in on the radio.
  - Anyone who has gone to ground (stopped, and no friendlies within 30 m) gets a bomb run: it comes down to ~55 m, settles over the target, and drops.
  - It waits 10 s between drops to see where the last one landed, and comes home to swap batteries and re-arm: the battery lasts 8 minutes, and re-arming takes 25 s.
- **FPVs**, two kinds:
  - **Frag FPV:** for people. Against a hull it only hurts thin armour.
  - **AT FPV:** a bigger, slower airframe carrying an RPG warhead. Against armour it climbs over the target and dives steeply onto the roof.
  - **Targets:** an FPV waits for one worth it. Armour called in on the radio (by anyone, the quads included) comes first; tanks and IFVs get the AT drone, while trucks and light vehicles can take a frag one. Otherwise it goes for a group of enemies the side has eyes on.
  - **Flight:** it climbs straight up clear of cover, and its fuse arms 2 s out. It flies out low (35 m), then dives in over the last ~260 m, leading its target.
  - **Shot down or crashed** after arming, 85% go off where they land; some are duds. Shot in the air, 40% go off there and then.
  - **Sound:** FPVs are loud and carry, so you hear one coming and hear it pass.
- **Stocks** (per operator: 2 quads, 8 grenades, 3 FPVs, 2 AT FPVs). Only a logistics truck or a FOB refills them; an ammo bearer can't.
  - Drone shortage counts as low ammo, so logistics runs go to the drone team.
  - An operator short of drones walks to a parked logistics truck within 150 m.
- **Counter-drone**
  - Bots hear an FPV from ~150 m and a quad from ~140 m and call it out. They shoot it with lead (a quad only inside ~100 m, and after ~15 s of missing most give up on it for a while) (one hit brings any drone down; an FPV may go off).
  - About a third of bots get under a roof or canopy away from a quad's camera instead.
  - An FPV diving at a bot from under 45 m makes it dive out of the line.
  - Vehicle anti-air guns engage drones out to 2 km (FPVs within 300 m first); MGs engage them within 350 m.
- **Player** (drone operator kit)
  - H flies the quad (it lands at your feet when you let go), and J flies an FPV.
  - You watch a video feed (snow grows with range past ~900 m) while hearing with your own ears.
  - Getting hit takes you off the sticks.
  - The map shows friendly drones and each quad's camera footprint.

## Quality pass (built)

An audit of the bots, the combat model and the frame cost, measured on 6-minute 33-a-side Valley runs (`mode=tspec33 level=valley verbose`).

- **What bots know**
  - A blast is heard where it went off. It no longer tells the listener where whoever fired it is. (Before, every explosion "placed" its shooter at the crater, about 150 000 false fixes in 6 minutes.)
  - Callouts reach whoever is within shouting distance (60 m) and the caller's own squad on its radio (600 m), with a few metres of error per hundred. The rest of the side learns from the map, recon reports and fire support. (Before, every sighting went to every bot on the side, 40% of them over 300 m away.)
  - Being hit by aimed fire gives a rough idea where it came from. A fragment, a shell or a drone's bomb doesn't. Friendly fire never makes a teammate a threat.
  - Sight isn't cut off at 400 m anymore (it's 1 km). How fast someone is picked out depends on the observer's eyes and optics (naked eye, binoculars at a recon post, a marksman's scope, a set-up weapons team, a gunner's thermal sight or a crew's vision blocks) and on the target: stance, movement, firing, and concealment (woods, or inside a building seen from outside). The long-range fire discipline of marksmen, recon, weapons teams and vehicle guns now actually comes into play.
- **Stance.** Bots go prone:
  - fighting in the open beyond 40 m with no cover (if they can still see the enemy from the ground);
  - when pinned in the open;
  - at observation posts;
  - when a mortar bomb whistles in within ~45 m (heard in its last 3 s);
  - for a grenade with nothing to hide behind.
  Getting down takes 0.8 s and getting up 1 s. They crawl at 0.6 m/s. Aim is steadier (a bipod for machine gunners and marksmen), but the rifle only comes up or down so far, and they kneel instead if the ground in front blocks the barrel. They don't go prone in streets or indoors.
- **Hits.**
  - At head height a round has to pass within a head's width (0.13 m) of the head to hit it. Beside the head and below the chin it's the shoulder; beside it and higher up it's a near miss that flies on. (The capsule is shoulder-wide to the crown, and 59% of "head" hits were really beside the head.) Head hits went from 28% to about 5% of hits.
  - Downed soldiers can be hit (stray rounds and fragments used to stop dead on them). Nobody shoots or throws where a downed teammate is.
- **Explosions** (`Grenade.Detonate`)
  - Each explosive has a fragment reach: the distance at which a standing man in the open has an even chance of at least one fragment hit (hand grenade 10 m, 40 mm 7 m, 81 mm 25 m, 120/125 mm HE 30 m, 70 mm rocket 18 m, FPV 9 m).
  - Each explosive has a charge relative to a hand grenade: 40 mm 0.25, 81 mm 4, 105 mm 9, 120/125 mm 12, 70 mm rocket 4.5. The charge scales the blast, flash, noise and reach against light vehicles and FOBs. (Tank HE used to go off like a hand grenade.)
  - The fragments are real projectiles, but only those that matter are flown. Each person nearby gets their share of the spray: a number drawn for the density at their distance, their stance and exposure. Each is a real fragment aimed across their silhouette, so walls, sandbags and people in between still stop them. A few more fly off for the dust.
  - The result is realistic lethality (a grenade at 5 m used to have about a 25% chance of hitting a standing man) with 80% fewer projectiles.
  - Vehicle gunners keep HE past its fragment reach from our own people. The mortar checks fire before every round, not only when the mission is called.
- **Squads**
  - Soldiers who are down stay in the squad, and in their fire team when revived. (Anyone joining used to purge the wounded, so the revived were orphaned and a revived player leader stopped leading.)
  - Fire teams and buddy pairs stay together through casualties: newcomers go to the smaller team, and a team two short takes a man across. (They used to be dealt again from scratch, reshuffling about every 5 s in a fight.)
  - The flanking team of a react-to-contact drill keeps going when it's seen. It only fights where it is if hit, pinned or within 30 m, and it has 45 s to get round. A squad in a contact drill counts as engaged for the whole drill.
  - Bots take cover from a grenade behind anything within a few strides, and otherwise run or get flat. Their blast effects are shielded by walls, as the player's already were.
- **Vehicles.**
  - The gunship rearms at its pad once its rockets are gone and its gun is low, and the mortar gets bombs carried up once it's dry. (Both used to be permanently out of action.)
  - A gunner killed at an exposed mount falls off the vehicle instead of hanging in the air.
  - Blast kills are credited to whoever fired.
- **Rates of fire** carry the remainder of each cycle into the next, so they're exact whatever the step: bots far from the camera, which think every 2nd or 3rd tick, the player at any frame rate, and vehicle guns.
- **Frame cost** (the same simulation, done with less waste).
  - Navmesh closest-point queries (people's and vehicles') and people's path queries look only at the tiles around the ends. Godot's own queries go through every polygon on the map. Results are identical, and a path falls back to the whole map if it can't get through the nearby tiles.
    - Vehicle routes still ask the whole map. On the vehicles' navmesh, broken up by woods and streets, a route that can't be finished in the nearby tiles mostly can't be finished at all, and searching the tiles first cost more than it saved: Kessel 20 → 26 ms a route, Novigrad 10 → 11–13 ms.
    - Closest point: 6 → 0.06 ms (Novigrad), 15 → 0.08 ms (Al Hamra).
    - Path: 8 → 0.6 ms (Novigrad), 22 → 1.2 ms (Al Hamra).
    - Choosing a vehicle firing position: 45–70 ms → ~2 ms.
  - Other savings:
    - The bullet loop reads everyone's position once a tick.
    - The crowding grid no longer grows for the whole match.
    - Bots standing still on firm ground skip the physics move.
    - Bots in cover no longer re-ask for their spot every tick.
    - Sight lines check the height grid for an intervening hill before casting a ray.
    - The hot paths make no garbage.
  - Valley 33×3 went from 367 s to 255 s of wall time for 360 s of game (headless). Movement is −48%, ballistics −60%, the brain −53%. People's routes are 0.7 ms each there. The whole run logs no engine errors (it used to log one at every start, from a navmesh query before the first sync).
- **Diagnostics.** `Prof.Count` counters (hit zones, flank outcomes, prone, cover searches that failed, projectiles, path fallbacks, suppression, close-ups, LZs) are printed by DevShot. `navbench` times the navmesh queries on a map and checks that the tile-local ones agree with Godot's.

### Match telemetry

`telemetry=<file.jsonl>` records a match. The first line is the map: heightmap, buildings, trees, sites, bases, squads. Then:

- every half-second, everyone's position, state and why, stance, target, suppression, health and ammo;
- every vehicle's position, crew, health and damage (engine, tail rotor, doomed), and a helicopter's collective;
- every drone;
- events: every shot (who, from, toward, weapon, aimed/suppressive/prefire), hit, down, kill, explosion, state change, squad drill.

Two tools read it (Python 3 with numpy and Pillow):

- `python tools/telemetry.py file.jsonl`
  - Splits the match into fights: one squad's contact, until 30 s of quiet.
  - For each fight it draws a storyboard, six map frames with trails, fire, casualties and drills, plus a timeline (fire by side, casualties, share moving, how far apart the sides are).
  - `summary.txt` gives each fight's length, rounds, casualties, lulls, standoffs, how far the sides closed, who fired, and the longest anyone sat still. It also has 30 s phase logs of the longest fights and the most common state changes.
- `python tools/replay.py file.jsonl` writes a self-contained HTML replay:
  - the whole map, with everyone moving (stance, down, aboard), every shot as a tracer (aimed, suppressive, a vehicle's gun), explosions out to their fragment reach, casualties and squad drills;
  - a fire timeline by side to scrub along;
  - click a soldier to follow him and see his state and why, his health, suppression and target.
- `python tools/behavior.py a.jsonl b.jsonl …` puts runs side by side:
  - dithering: cover re-seeks that went nowhere, flip-flops, state and stance changes per minute;
  - cohesion;
  - fire volume and mix;
  - bounds and rushes;
  - fight length and closing.

### What the telemetry turned up (fixed)

- **Dithering.**
  - Cover was re-judged against whoever the target was that instant, so men shuffled between spots 640 times in 6 minutes. It is now judged against whoever it was taken from, after 3 s.
  - The covering team of a withdrawal was released by the rest of the brain a moment after being told to hold.
  - Men flipped between "push on" and "take the fight". Two tests of whether an enemy mattered disagreed, depending on whether he was in sight. There is now one.
  - A crewman heading for his vehicle, with an enemy in sight 150 m off, went "to the vehicle" and "take the fight" twice a second. He now keeps going unless they're close or he's hit.
  - Result: cover re-seeks that went nowhere 107 → 3 a minute; flip-flops 127 → about 7.
- **Rushes.** In the open, with no cover to bound to, a man dashes 12–22 m (6–10 m in a buddy rush), drops, and fires. Before, an attack across open ground had no way forward.
- **Cohesion.**
  - A contact drill's flanking team goes to one objective worked out from the team, each man a few metres apart in a line. Each man used to work out his own from where he stood.
  - On the move, the leader waits ("close it up") while anyone in a fight, or half the squad, is 60 m or more away (40 s at most). Not in an assault, a crossing or at the objective.
  - While the squad is in a fight, nobody walks off to the objective.
  - The flank call and briefing now name the side the team actually goes, not one worked out from the map's orientation.
  - Result: men 60 m and more from every squadmate, 14–18% → 6–11% of the time.
- **Fire.**
  - Aimed fire has a target location error: how sure the shooter is where exactly the man is. About 1 mrad in full view once watched a while. It is larger:
    - the less of him shows;
    - three times as large at first sight;
    - among trees or in a dark room;
    - when the shooter is being shot at.
    It is smaller through magnification. Up close it's nothing; at 250 m it's decimetres to metres.
  - Suppression lasts as long as the position is fresh: 15 s after he was last seen (25 s for the machine gunner), not 5.
  - Suppressive fire is at sustained rates: a rifleman's single shots every second or two, the machine gun's 4–8 round bursts every few seconds. The M249 carries 600 rounds.
  - A man in cover who opens up comes up for the whole string.
  - How long a man watches where the enemy went to ground before going after him himself scales with range: a few seconds close in, about 5 times that at 200 m. At range it's the fire team that moves.
- **Aircraft and vehicles.**
  - Nobody leaves a helicopter in flight. A tail rotor shot out used to have the crew stepping out at 80 m, and a man downed aboard tumbled out, unhurt by the fall, while the empty aircraft flew on into a hillside. A casualty stays in the seat until it lands. If the pilot is hit, the front-seater takes the controls.
  - With the tail rotor gone the pilot puts it down at once, briskly. On the ground the crew gets out of a crippled aircraft.
  - A doomed aircraft that settles on the ground is wrecked. One sat intact for a whole match at −11 079% health, crewed and firing.
  - Crews don't climb back into a vehicle the motor pool has written off. A truck with its wheels shot out had its driver bailing out and climbing back in every second, and it was never replaced because it was never empty.
  - LZs are clear all round past the rotor disc (trees and walls included), further out if need be. It used to be four points 9 m out, then the spot asked for.
  - A crash after being shot up is credited to whoever shot it up.
- **Navmesh.**
  - Nothing asks the navigation map before its first sync. Godot logs an error and answers (0, 0, 0).
  - The tile-local path search runs uncapped (the tiles are the cap). Godot 4.7 doesn't reset its polygon count when it retries toward the nearest reachable point, so a capped search of a few tiles could run out on the retry and return nothing. That happened 20 times in 3 minutes on Novigrad.
  - Most whole-map fallbacks were for places that can't be reached at all: 85% on Novigrad came back with the nearby tiles' answer. The nearest reachable point is remembered per 2 m cell. A later route there heads straight for that point, and the tiles' path is taken when it's shorter than twice the margin, since no route that leaves the box can be shorter. Otherwise the whole map is asked for the way to that same point, which is quick because it can be reached. The answer is the same, without the exhaustive search.
  - People's routes on Novigrad: 7.7 ms → 3.1 ms.

### Second round (built)

- **Recording.** `record.cmd` builds the game and runs it with every Territory match recorded to `recordings\` (one file per match, named for the map and the time; spectated matches too). When the game closes it makes a replay page and storyboards for each new recording and opens the newest. Extra arguments pass through: `record.cmd mode=tspec33 level=valley`. The replay marks you, if you played.
- **Bullets through things** (`Penetration`).
  - A round's penetration (its `Pen`, the same mm-of-steel scale as armour, falling with its speed) is spent getting through what it hits. A solid material costs in proportion to how much of it is in the round's path, measured exactly from the box or trunk struck. A hollow thing (a car, a shipping container, sheet metal) costs a fixed amount to cross.
  - Calibrated on 5.56 mm at the muzzle:
    - through an inside wall (15–20 cm), a door, a car, a shed or a thin tree;
    - not through an outside wall (25 cm of masonry or mud brick) or sandbags.
  - A heavy machine gun or an autocannon goes through outside walls.
  - Grenade and shell fragments get through a door or a sheet of tin close to the burst.
  - What comes out is slower, so it hits for less, and a little off line.
  - Inside walls are told from outside walls by thickness, as the buildings are built.
  - Cover-finding knows the difference too: an inside wall, a door, a car or a shed hides a man but isn't cover, and the line of fire is followed on through them.
- **Magazines** (`Magazines`).
  - Everyone carries real magazines, each with what's left in it. A reload takes the fullest, and the part-used one goes back in a pouch (0.6 s slower) unless it's dropped for speed.
  - The player taps Reload to keep it, or double-taps it (or binds "Quick reload") to drop it.
  - Bots keep it unless they're under fire. A resupply hands out full magazines.
  - Part-used magazines used to be thrown away at every reload, with every round in them.
- **Keeping heads down.** When the man a bot is firing at ducks out of sight, it keeps putting rounds on the spot for 2–4 s (a machine gunner 3–6 s), staying up from cover to do it. That's the point of the fire: he can't come back up, or move, while it lands. It used to happen only some of the time, and only from the open.
  - Result: 76% of suppressive rounds land within 4 m of the man they're meant for (was 53%).
  - The target's suppression a second later is typically 0.64, above half 64% of the time (was 0.19 and 27%).
- **Vehicles in the way** (`VehicleDetour`). The navmesh doesn't know about vehicles, so routes ran straight through parked trucks and wrecks. A route now bends round any stopped, landed or wrecked vehicle: the shorter way round its footprint, with room for a man, by corners on standable ground. That happens when the route is planned, and every half-second on the move for vehicles that stop across it later. Stuck-by-a-vehicle events fell to about a tenth.
- **Gunships** (`HeliPilot.AttackRun`).
  - Near the fighting they fly nap of the earth: about 11 m over the tops of whatever is under and ahead (buildings by ray, woods as a 15 m canopy), looking ahead as far as they need to climb in time. It used to be 25 m over the highest ground in the next 450 m, with no idea of trees or buildings.
  - They attack by pop-up from battle positions 1.2–1.8 km out:
    - low to a position the ground hides from the target;
    - up just high enough to see it (known from the position check);
    - a rocket salvo, with the gunner's cannon joining in;
    - back down; then on to another position, never the same twice running.
  - Shot at while up, they drop at once and move. Positions are chosen out of sight of every enemy anti-aircraft gun the side knows about. Only if there's nowhere safe for half a minute do they hold off.
  - A gunship waits on the pad for its front-seater before lifting off: without him there's no gun, and nobody to take the controls if the pilot is hit.
  - Transports approach landing zones low too.
  - Result on Valley: gunships airborne 80–136 s (was 18–58), typically 24–28 m over the ground (was 54–70), firing several salvos each.
- **Key bindings** (`Controls`).
  - Every action the game listens for, with its default key and where it's used, in one list.
  - The F-keys, the map and squad keys, the spectator keys and free look used to be hard-wired. Spectating's C both switched the view and was "down" in the free camera, so going down left it. The view toggle is V now.
  - Two actions share a key only if they're never wanted in the same place (on foot, in a vehicle, flying a drone, spectating).
  - The main menu has a **Controls** section: click a key and press the new one, conflicts are flagged, reset one or all. Bindings are kept in `user://keybinds.cfg`.
  - Help text and on-screen hints show the current keys.
  - `KEYBINDS.md` is written from the list (`-- bindsdoc=KEYBINDS.md`).
- **Relevance, again.** A man at the objective who has just decided a far-off enemy isn't his business doesn't turn to fight him every time he shows: for 8 s he carries on, shooting at him if he's in sight, unless that enemy comes close or starts hitting him.

### Third round (built)

- **Drone bombs go off.** Not one grenade a quad let go had ever exploded: the rule that deletes a spent bullet (slower than 40 m/s) deleted each one on its first step, falling at 2 m/s. The player's drops were lost the same way. Dropped rounds are exempt now.
  - The quad lets go when the grenade will land on the man: it allows for its own drift during the ~3 s fall and for where he's moving. It used to wait until it was within 1.8 m of dead overhead and all but still, which a drone hovering in the wind seldom is.
  - A drop lands a metre or so off, more from higher up: it's aimed off a picture on a screen, from a drone that's never quite still.
- **Guided missiles** (`VWeapon.Guided`, `Ballistics.GuideGround`), a second load on the gunships' pods:
  - the Apache's AGM-114L Hellfire (its own radar seeker: fire and forget), the Viper's AGM-114K (rides the aircraft's laser), and the Hind's 9M120 Ataka (radio command). 6–8 each, 6–7 km.
  - They loft and come down on the roof. A laser or radio missile needs the aircraft to keep the target in sight until it hits: if the aircraft ducks, is hit, or the target goes behind something, the missile flies on unguided.
  - Bots use them on air defence first, anywhere near where they're working, then on armour near the objective (not on trucks). Rockets and the gun do the rest.
  - The player's pilot seat: Fire Mode switches pods between rockets and missiles. The sight boxes the vehicle a missile would go for (the one nearest the nose within 12°, in sight and in range), and Fire launches.
- **Air defence radar and warning receivers.**
  - An anti-aircraft gun's radar takes 4 s to lock on and its fire control to work out a solution before the first burst. It sees any aircraft in the open out to its guns' reach, 3 km. The gun used to open fire the moment its crew saw one, and a gunship over a ridge for a second was gone.
  - The aircraft's radar warning receiver hears the lock. A bot crew calls the gun in on the radio (so every aircraft on the side plans round it), and the pilot gets out of its sight: to the nearest spot low over the tops, with the ground or a building between, favouring the way it's already going. Two radars at once: the warning is for the nearer.
  - If the gun that locks on is the one the gunship has come for, and it's in sight and in reach, it doesn't run. It takes the shot from where it is, launching at once, and stays up to steer the missile: whoever shoots first wins. Chased off again and again on the way to a firing position, one gunship never got a missile away.
  - The player gets the warning on the HUD, with a bearing. After launching a laser or radio missile the HUD counts down while it's steered ("GUIDING — keep it in sight").
- **Aircraft are called in as aircraft.** A helicopter someone saw went out on the radio as "enemy armour". Tanks were sent to overwatch where it had been, AT teams to ambush it, and gunships to fly air support over it, and Kessel logged scores of navmesh errors routing vehicles toward points in the sky.
- **Gunship crews' eyes.** From the air, vehicles are picked out to 1.5 km all round, and to 3 km ahead in a gunship's sight (it was 900 m, as for a tank's crew).
- **Where gunships fight from** (`HeliPilot.BattlePosition`).
  - Missile positions are as far out as the missile reaches and the map allows, best about 4 km, never under 800 m. They used to be 3.2–5.2 km out always: on Valley (2 km across) every one was pulled in to the edge and failed, and the gunship hung about there for the rest of the match.
  - Not within 350–500 m of an enemy base. The way there counts: a route in sight of known air defence scores badly.
  - Rockets need the top of what's in the target area in sight (roofs, canopy, ground), not a point at head height among the houses. That was why most positions failed.
  - With nowhere to fire from, it waits somewhere hidden from the air defence it knows of, not at the map's edge in view of the gun that just chased it off. It only holds off altogether once its missiles are gone.
- **Flying.**
  - See and avoid: an aircraft closing on another gives way by the rules of the air. The one with the other on its right slows, turns right and passes above. Landing and hovering aircraft are kept clear of, and bots give way to the player. Two took off side by side at the start of a match, crossed 12 m up and came down together.
  - Two aircraft that collide both take the hit and come apart. Wrecks fall to the ground, sliding off walls and past other aircraft. The two above hung 13 m up on each other for the rest of the match, and a helicopter flew into them later.
  - Take-off goes straight up clear of what's round the pad before moving off. One tipped forward into the base's flagpole. The way in to the pads allows for the pole too: it's too thin for the rays that find what's ahead, and two clipped it coming home.
  - The last 45 m of a landing keeps over what's between it and the LZ (one flew into a roof). With a damaged engine it can still land: the test was under 55% collective, and a damaged hover takes 72%.
  - Speed is held to what the aircraft can climb over. Flat out up a hillside at 35 m/s, three flew into it in one Highlands match.
  - A base's pads may stand nearer the map's edge than aircraft otherwise go. On Valley they stood outside it, and a gunship sent home to rearm was stopped 140 m short and never rearmed.
- **Coming home.**
  - A crippled aircraft (a damaged engine and under 30% of its hit points, or under 25%) flies home and is written off there.
  - If one of the crew in the cockpit is hit, it goes home, and the wounded are lifted out. A gunship whose front-seater was down pressed on for half a minute until its pilot went down too.
  - Pods jammed: home for the armourers to clear them.
- **Result** (6-minute 33-a-side runs on Valley, Kessel, Highlands and Novigrad):
  - Gunships now destroy 2–4 enemy air defence vehicles a match with missiles, from 0.6–2.9 km. Before, none.
  - They launch 4–13 missiles a match, up from 1.
  - Valley is 2 km across, all of it within the guns' 3 km, and a gunship or two is still lost there most matches. They last longer than before: in the last Valley run two of the three flew for 300 s and more.
  - On Kessel and Highlands they mostly last the match.
  - No more mid-air collisions or flying into hillsides, and gunships rearm.
  - No exceptions, and no engine errors except Kessel's usual handful from the navmesh.
- **A vehicle's gun doesn't fire into what's in front of it.** Before each shot the gunner checks the barrel's line for the first 60 m: a wall, a tree, the crest the hull is down behind. The sight sits higher than the gun and sees over what the barrel can't. A Centauro put two 105 mm HE rounds into a wall ten metres in front of it and killed its own infantry beside it. If it stays masked, the vehicle moves.
  - Firing positions are judged from the barrel's height, not the turret roof. Hull-down with the line clear at the roof and not at the gun used to put the round into the crest.
- **Telemetry** adds the radio net (who called in what, where) and what aircrews say. A helicopter's row says what it's doing (to a firing position, popped up, launching, guiding the missile, evading a radar, going home...). `Prof` counts radar locks, evasions, give-ways, missiles launched and lost, and why firing positions were turned down.

### Long matches (built)

Four matches the user recorded (8–51 minutes), plus two one-hour runs, showed what 6-minute tests couldn't:
- **No side could lose.** A match ended only on tickets or at the 1-hour limit, and at 33 a side the tickets outlast the hour: about 6–7 a minute were spent, from 990. Bases couldn't be attacked, the base spawn couldn't be denied, and a side's home points could always be retaken through the link to its base.
- **The front froze.** In the 51-minute Novigrad match 14 of the 20 changes of ownership came in the first 22 minutes, with 12 minutes at a time with none. In a one-hour run the points stood at 4/2/3 from minute 11 to the end. Fire per 5 minutes fell from about 2,500 rounds to 420.
- **Why:**
  - **Spawns.** Men respawned inside the capture circle of the owned point nearest their squad's objective, so that point became a fortress (13 ALPHA men sat in one district for half an hour) while the points around it were left empty.
  - **Too little force.** Each side has two rifle squads, always sent to different places, so every front was a six-against-six duel.
  - **Distraction.** A single enemy stepping onto an owned point recalled squads from their attacks.
  - **Nobody pressed an advantage.** Both trailing sides always went for the leader.
  - **Uneven starts.** The starting sectors depended on the map: Highlands gave 3/5/6 points, and the short side bled tickets from the first 15 s.

What changed (`TerritoryMode`):
- **Headquarters.** Each base is a headquarters (the ring round the flag, 50 m). Once an enemy holds a point linked to it, that enemy can go for it. More of them than defenders on it wears its hold down: 160 s with a one-man edge, 40 s with four or more. With no enemy on it, the hold comes back. At nothing, it's overrun and that side is out: no reinforcements, no vehicles, and its points go neutral. The last side left wins.
- **Last stand.** Tickets are a side's reserves. At zero it gets no more reinforcements or vehicles, its squads fall back on its headquarters, and anyone can go for it, linked or not. It's out when the headquarters falls or its last man dies. It used to be out on the spot, its men stopping where they stood. In one-hour runs sides ran out of tickets at 45–70 minutes, and no headquarters was ever taken.
- **Match length.** The time limit is now a setting (1, 2 or 3 hours, or none; 3 by default; `minutes=` for test runs). At the limit the side holding the most ground wins, then the most tickets. It used to be tickets alone, with a tie going to ALPHA.
- **Reinforcements need a line back to base.**
  - The base only takes reinforcements while no enemy is within 90 m of it.
  - A point or FOB only takes them while it's joined to the headquarters through your own points along the links (FOBs within 500 m of one). A point that's been cut off gets nobody, and when there's nowhere to come back, men wait.
  - Men respawn just outside the point on the side away from the enemy, and walk up to it.
- **The commander.**
  - It presses the weakest side, not the leader.
  - For 90 s after a capture it pushes on to the points linked to the one it took.
  - It sends more than one squad at a defended point or a headquarters.
  - It answers a real attack on its own ground, not one man stepping onto it. The points in front of its headquarters matter most.
  - Its own headquarters under attack is worth every squad it has.
- **Fair starts:** every side starts with as many points as the side with the fewest.
- **Fixes:**
  - People flying over a point no longer count as on it.
  - An owned point's hold comes back when nobody's on it, and half-done progress on a neutral point fades.
  - A point linked to two bases (Novigrad's Signal Hill) now belongs to the nearer one.
  - A squad can make a proper attack, with an ORP and support-by-fire, on a point it has attacked before, 90 s after the last one. Before, every later attempt was a walk-in.
  - Radio reports of vehicles that have since been removed no longer throw. That happened about 50 times an hour, the one error the long runs found that the short ones never did.
- **Telemetry** records the score every 5 s: tickets, who's out, point owners and headquarters hold. `tickets=N` starts every side with N for test runs.

Fixes the long runs turned up:
- **Mortars hit what they aim at.** The tube was laid in the frame of the slope its pit sits on (8–45°), and a tube near vertical is swung toward the downhill side by any tilt. Bombs came down a median 790 m from their aim point, none of several hundred within 50 m, all over the map.
  - The baseplate is now bedded in and levelled.
  - It uses charges 0–4 for the range, where before a single charge couldn't reach anything inside about 600 m.
  - It's laid to a tenth of a degree, on each bomb's own point.
  - The second crewman kneels at the tube and loads; alone, the gunner fires at half the rate.
  - Result: a median miss of 19–43 m at 1.1–2.5 km.
- **Men stuck for the whole match.**
  - The navmesh's voxel bake closed doorways that didn't line up with its grid, so rooms behind them were islands: a third of the Highlands' building points and half of Novigrad's. Every doorway is now a navmesh link: 189 on Highlands, 4,173 on Novigrad.
  - Spawn spots are checked for being inside something or cut off.
  - A squad leader waits for a ride only while it's getting closer.
- **Vehicles and support.**
  - Crews try each side of the hull when they can't get round to the back, and go to their own vehicle however far off it is. Replacement aircraft used to sit on their pads uncrewed.
  - The logistics truck goes home to load (it used to count as reloaded the moment it stopped), forward for a FOB, and out to drone teams that are running short. Drones restock at FOBs too; drone flying used to stop after 15–25 minutes.
  - Shot-down aircrew go back to base, and replacements park clear of the wreck.
- **Things removed from the world.** An explosion credited to a man since cleared away threw, about once every ten minutes. So did asking a mortar, which has no driver's seat, for its driver.

From an 84-minute Al Hamra match played as a rifleman in ALPHA-1:
- **Orders stay put.** Hour-long runs re-tasked each squad every minute or two, and a quarter of the changes came less than a minute after the one before, often straight back to the order the squad had just left: an enemy or two turned up near one of our points and dropped out of mind 15 s later. The orders at the top of the screen kept changing, and the squad walked back and forth. A fresh order now carries a commitment bonus that fades over two minutes, and a front point with a garrison keeps it unless another is clearly worse off.
- **Smoke only with someone to screen from.** A squad used to pop smoke at a danger area whenever a hostile objective was within 450 m, so empty streets were screened. It now smokes only when someone in the squad has seen or heard an enemy within 400 m of the crossing in the last minute, or its last fight was that close. It still halts and crosses in bounds near the enemy, with or without smoke.
- **Replacements join their squad.** A man came back at the spawn nearest his squad's objective, often 300–900 m from the squad itself. The player was separated from theirs for about 20 minutes of the match. He now comes back nearest the squad leader (on foot), and nearest the objective only when the squad has nobody left.
- **Medics go further for their own.** Medics looked 60 m out at most when there was no fight on, so a downed man 65–300 m away bled out with nobody coming. The player was downed 8 times and picked up none of them. For a downed man in the medic's own squad, the reach is now 150 m when it's quiet; it's still 25 m in a firefight.
- **Telemetry.** New `order` events (squad, new order) and `cross` events (start, smoke with the threat's position, crossed or abandoned).
- **Replacements come up with the squad, not one at a time.** Every man used to come back on his own 10 s after he was killed. A squad in a fight was topped up the whole time, so none was ever wiped out, and men came back scattered and far from their squads. (The player killed themself with grenades to get a better respawn.) An infantryman's replacement now waits for his squad, and they come up together:
  - once the squad is at a spawn of ours (its leader in or by a point we hold, at a FOB or at base) and out of contact;
  - all at once, at the spawn nearest where it was sent, when nobody in the squad is left on his feet (once the last man killed, you included, is due);
  - or, when it's got nowhere near a spawn in three minutes, on their own to the spawn nearest it, once it's out of contact.

  A squad in a fight gets nobody new. A rifle squad worn down to a third of its men or fewer (with two or more waiting) is **shattered**: it breaks off what it's doing and falls back ("Regroup at …") to the nearest spawn to take them on, then gets new orders. Once it's falling back it keeps the same spawn while that stays open, and keeps going until it has its men or is back over half strength. Vehicle crews still come back at base.

  In a 20-minute Valley run, 21 of 22 regroups got their men 4–130 s after the order. Wiped-out squads came back whole, and about 84 of 99 men were on the field on average, where before nearly everyone was. Smoke went on 28 of 67 crossings in that fighting, against about 60% of crossings before. On Al Hamra it was 2 of 36.
- **You come back with your squad.** You wait with its other men, watching one of them. The dead screen says why you're waiting and how long before you're sent up anyway. A spawn you click on the map sends you there on your own, for that one time only: the pick used to stick, and every later death put you back there.
- **Split squads.** When most of a rifle squad is down (two or more waiting, at least as many as are still up), and the rest haven't got anywhere they can take them on within 90 s, the dead come back anyway, fighting or not, as a detachment of their own (ALPHA-1B, under the senior man among them) at the spawn nearest the squad. The smaller of the two goes to link up with the larger; the larger keeps the job, or takes it over if it's the detachment. They're one squad again once their leaders are within 40 m, or as soon as either has nobody left on his feet. You come up with the detachment if you're waiting too.
- **Back near the fight.** A squad wiped out comes back near where it last was, not at the spawn nearest its objective. A FOB counts as 150 m nearer than the points when a spawn is picked: it's what one's built for.
- **Pause** (P). The game stops, its clock with it. F10 still goes to the main menu. There was no pause before.

### Fourth round: looking and playing right (built)

Six auditors looked at the player's own systems, the wound model, the data from three long runs, and how bots look to someone watching them. What they found, fixed:

- **Bots that look like soldiers**
  - **Legs follow the feet.** The legs turn toward where he's going at a human rate (240°/s, slower crouched), while the torso and rifle stay on the aim, up to 80° of twist. Moving against his facing he's physically slower: 1.8 m/s sideways and 1.2 m/s backwards (before, 3.4 and 2.4 m/s with a forward stride, so men crab-ran and moonwalked). Crouched, he walks bent-legged instead of sliding along on a knee.
  - **Stance changes move the body, eyes and hitbox together.** Getting down or up takes the time it takes, for being seen and hit too. Prone and back goes by way of the knee.
  - **No more standing up to die.** A prone man who's hit stays face down. A standing man falls the way he was running, or away from the shot, instead of a coin toss (half of prone men used to swing up through standing and fall over backwards).
  - **No walking through each other.** A man can't move closer to another than their bodies allow. On a shared route he slows to the man ahead and follows 0.9 m behind, and he steps round a man who's standing or coming the other way. Overlapping pairs fell from about 70 to about 5 per 10-minute run.
  - **Looking about.** A man keeping watch looks one way for a few seconds, then another, on his own time. Every man on a side used to sweep to the same sine wave, in step.
  - **Holding.** He comes up for a look every 6–12 s, and only when there's someone to look for. Men holding with no enemy anywhere bobbed up and down every 2–4 s, drone operators included: 22 stand/kneel flickers a minute before, 0.9 after.
  - **The mortar's assistant gunner** stays kneeling at the tube, and shuffles round it on a knee. He got up and knelt again every 2 s: 25 flickers a minute before, none after.
  - **Close quarters.** A man moves deliberately to one side for 1.5–3 s, and only turns back when he's stuck. He used to switch sides every 0.3–0.9 s, like a video-game dodge.
- **Decisions**
  - **Staying on his man.** The man he's fighting counts as seen through a moment behind cover, and the longer he's been on him, the more it takes to switch. Turning onto someone else costs, more the further round. Switches straight back (A to B to A within 3 s) fell from 15 to 0.9 a minute.
  - **A moment out of sight** (0.7 s) no longer ends the fight: he stays on the spot where the man was, and doesn't stand up.
  - **Bandaging.** Unless a wound is spurting, a man gets to cover before he dresses it, instead of stopping dead mid-sprint in the open.
  - **Cover from armour** is kept while the armour is about, even with infantry known too. Before, he walked back out and in again every 6 s.
  - **Off a mortar's impact area** with nothing to get behind, he goes flat for a few seconds. He used to be "in cover" behind nothing, then walk about in the open.
  - **Buddy aid.** When no medic is coming for a downed squadmate (the medic is hit, dead or far off) and it's quiet, the nearest man goes and stops the bleeding. Only a medic gets him up again.
  - **Medics.** A medic treats what he can actually help: getting a man up, dressing open wounds, lost blood. Tissue damage and a dressed wound still bleeding inside counted too, so a medic re-treated the same man until his kits were gone. Revives went from 10 in 20 minutes to 22 in 15.
- **Squads**
  - **Boarding.** A man heading for his vehicle is steered only by boarding. The march and the wait for the squad kept stopping and re-routing him, so a squad leader crawled beside his helicopter at 0.4 m/s and it left without him.
  - **No danger-area drills** while a ride is on its way or boarding.
  - **The formation turns with the leader** at a walking pace, with a dead band. It used to snap to his heading at every corner, and the flank men ran across behind him.
  - **Followers keep their own place.** On the leader's route a follower cuts it at the point nearest his own place in the formation. He used to take the leader's destination too, run past him, and turn back.
  - **No more freezing after a ride.** A route marked "plan the rest at the end" stayed marked when the rest turned out to be a straight walk. So at its end he planned again every frame, which reset the stuck timer and the clock the brain waits on for a new route. In a playtest, men who'd just got out of a vehicle stood where the shared stretch ended for minutes while their squad walked off. Frozen followers in 6 minutes of Al Hamra went from 11 to 0.
- **The player**
  - **The squad goes back to its own leader's judgement** when you go down or are killed. Before, your last order held it for 5 minutes, through regrouping too.
  - **Bandaging (X) works in vehicles**, except at an aircraft's controls where X is flares.
  - **Squad commands from a vehicle** aim where you're looking. Their number keys no longer also move you to another seat.
  - **Visual fixes.** Suppression's darkening fades in vehicles, on a drone and while down. There's no floating rifle after going down at a drone's controls, and no scope zoom stuck on the sky while downed.
  - **Roles past 9** can be picked when dead (0 steps through them). Prompts show your own key bindings.
- **Combat**
  - **Bleed-outs go to the enemy who drew the blood**, not to a teammate's stray fragment or your own grenade that grazed you later. No kill is counted for killing yourself or a teammate.
  - **Fragments come from the burst:** 40 mm, rockets and shells no longer spare the man who fired them (hand grenades never did).
  - **Hits built by hand take the zone into account:** through a car door and spall inside a vehicle. A head hit there could never kill, and leg hits were too strong.
  - **After going through something,** a round keeps only what it had left, instead of getting through two cars, or a car and then a wall.
  - **The M249** fires from an open bolt: its belt holds 100, not 101.

### Not done

- **Crews stuck on the way to their vehicle.** Now and then a crew walking to its vehicle (a mortar that's moved, a logistics truck) hits a snag on its route 90–220 m out. It gets stuck there, re-plans the same route, and gets stuck again for minutes. That's up to 100 stuck events in a 10-minute run, and anywhere from 4 to 96 depending on the match. Squadmates keeping formation get stuck about 12 times a minute too. Neither has been traced yet.
- **No thermal sight for the player.** The player only has goggles, even in a vehicle's gunner seat or in the roles whose bots carry thermal sights. That would need a white-hot thermal view rendered from body heat.
- A prone man's hitbox is still an upright capsule 0.62 m tall: no head or leg hits on him, and seen from the side he's about a third of his real length. Laying it along the body is its own piece of work.
- Hit rates at 100–300 m are still high: about 15% of aimed rounds hit, mostly men standing or crouched in cover or running to it. Real combat runs far lower, and much of the gap is how much bots expose themselves.
- Suppressive fire is still a modest share, about 15% of rifle and machine-gun rounds. When a bot means to suppress it is often moving, has its own cover in the way, or isn't yet aimed at the spot. The `supp:*` counters count the windows and why none opened.
- Transport helicopters have one pilot seat. When he's hit, nobody can take over and the aircraft comes down (a real UH-60 has two pilots). Gunships' front-seaters do take over.
- A downed man in a ground vehicle is still pulled out on the spot.
- Every anti-aircraft vehicle is a radar-laid gun. None carries missiles (the real Stormer carries Starstreak), so beyond about 3 km a helicopter is safe from them.
- Vehicles still run over their own infantry now and then, 1–3 times in a 6-minute match. Drivers stop for anyone in a corridor straight ahead. How the rest happen (people stepping in from the side, a turn, reversing) hasn't been traced yet.

## Night, weather and tracers (built)

The lighting, the soldiers' eyes and ears, and the sound all read one model of the time and the weather (`src/World/Conditions.cs`).

- **The clock.** A match starts at the hour picked in the menu: dawn, morning, noon, afternoon, dusk, night or random. The clock then runs at the chosen rate (stopped, real time, 4× or 12×). At 4× a three-hour match goes through twelve hours: into the night and out again.
- **Sun and moon.** Both are placed by the hour at 38° N, with the moon's age random for each match (`moon=` pins it for tests). Light on open ground is modelled in lux:
  - about 50 000 at midday;
  - 400 at sunset;
  - 3 at the end of civil twilight;
  - 0.25 under a full moon;
  - 0.001 by starlight.

  Cloud, rain and fog cut it.
- **Weather** is picked per match (clear, overcast, rain, fog or random) and fixed for the match.
  - Fog brings visibility down to 180–500 m, rain to 1.5–4 km.
  - Contrast fades with distance, reaching the 2% threshold at the visibility range (Koschmieder).
  - Rain raises the background noise by about 14 dB.

**What you see** (`SkyView.cs`)
- **Brightness follows the eye's adaptation.** Noon is 1, a full moon about 0.11, starlight 0.04. Night is dark, readable by moonlight, and near-black under cloud. Tracers, flashes and flares keep their own brightness, so they stand out the way they do to dark-adapted eyes.
- **Sun and sky.** The sun's colour comes from air mass: gold, then red, as it sets.
- **Moon and stars.** Moonlight is blue-tinted, and the moon casts shadows. About 6 000 stars turn about the pole and fade as the sky brightens.
- **Overcast** skies are grey, with flat, shadowless light.
- **Fog** density is set from the visibility.
- **Rain** is streaks around the camera, drifting with the wind, with a synthesised rain sound that is muffled indoors.
- **HUD and telemetry.** The HUD shows the time and the weather. Telemetry records the conditions, with the hour and the light level on each score line.

**Tracers** (`Tracers.cs`, `TracerDraw.cs`)
- **Which rounds are tracers.** Belt-fed guns (M249, coax, door guns, HMG) fire one in five. Cannon (30 mm, 35 mm, tank rounds) are all tracers, except the Apache's 30 mm, which has none. Rifles fire ball only.
- **Colour:** red-orange for ALPHA and CHARLIE, green for BRAVO.
- **Burn-out** is taken from each round's published trace range: 5.56 about 800 m, 7.62 about 900 m, .50 about 1 450 m, 35 mm 3 500 m. After that the round flies on unseen.
- **Drawing.** All tracers are drawn as camera-facing streaks in one batch: 0.015 ms a frame.
- **Ricochets** now fly on, with the tracer if it has one: flatter, slower, and off line. Before, the round stopped where it glanced. How shallow the strike has to be depends on the surface:
  - steel and rock throw a round off up to about 20–25°;
  - masonry, concrete and paving only below about 12°.

**Light at night** (`Illumination.cs`, `NightLight.cs`, `Effects.cs`)
- **Muzzle-flash lights** (up to 8, nearest first) are drawn only when it's dark.
- **Illumination rounds.** Mortars fire an 81 mm round (600 000 cd, 60 s under a parachute, falling at about 5 m/s from about 600 m) over a target area that's still dark, before the HE.
- **Fires.** Burning wrecks light their surroundings (20 000–80 000 cd, flickering).
- **Registration and scale.** Each source registers with `Conditions`, so the soldiers see by it, and it's drawn on the same brightness scale as the sky.

**Seeing and hearing** (`NightGear.cs`, `BotSenses.cs`)
- **The naked eye.** How fast a man is picked out follows the light on him, including flares and fires. By starlight the eye alone finds almost nothing past about 100 m. Everything also fades with the weather's contrast loss. That loss is measured against clear air, so a clear day sees exactly as before. A player indoors at night counts as lit like any other indoor spot, not as if standing in the open.
- **Night vision kit** (by side and role):
  - ALPHA and CHARLIE issue goggles to all infantry, and thermal sights to marksmen, recon, heavy anti-tank and weapons-team gunners.
  - BRAVO issues goggles to leaders, every fire team's machine gunner, recon, marksmen and crewmen; image-intensifier sights on some weapons; and thermal only to recon. Every team has someone who can see at night, but BRAVO is still the weakest side in the dark.
  - Armoured vehicles' gunners have thermal sights.
- **Goggles** amplify about 1 300× over a 40° field of view, and a flare nearby washes them out.
- **Thermal** doesn't care about the dark, and it's also used by day whenever the air is thick. How much further than the eye it sees depends on the weather:
  - about 3 times through haze;
  - about 1.5 times through fog;
  - no further through rain, since the drops stop both alike.
- **Muzzle flashes.** At night a flash gives the firer away at daylight ranges. Machine guns' tracers show them for longer. A man seen only by his flash is lost when he stops firing.
- **Rain** masks footsteps and quiet sounds.
- **The player's goggles** (L) show a green phosphor image with grain, bloom and the tube's real 40° circle (about 43% of the screen's height with the naked eye's field of view, more through a scope). They lift the exposure of whichever camera you're looking through, on foot, in a vehicle or on a drone.
- **Test runs are repeatable.** A run started from the command line gets the same moon, fog, rain and season for its seed. A game from the menu draws them fresh.

Measured, 33 a side on the Valley, median range of the first shot at each target:

| Conditions | Range |
|---|---|
| Noon | 259 m |
| Starlight | 144 m |
| Overcast night | 124 m |
| Fog at night | 84 m |

At night, fights are started by thermals and goggles, then fought at the flashes.

### From playing at night (built)

- **Shadows from local light.** Illumination flares and burning wrecks cast shadows from buildings and terrain. Cube-map shadows are expensive, so only the 2 or 4 lights putting the most light on the camera's ground get them (by the Shadows setting), and none by day. A burning wreck's light sits inside the flame, 2.5 m up.
- **Your own muzzle flash.** A flash hider's job is to keep the shooter's night vision. So at night your own first-person flash shrinks to about a third of its size and opacity, with a much smaller light. Other people's flashes are unchanged.
- **Losing the tail rotor.** The main rotor's torque spins the helicopter, up to about 110°/s at hover power, less with forward speed. The pedals do nothing. It touches down skidding sideways, and above 3–7 m/s it rolls over. A landed helicopter with nobody at the controls no longer spins.

### Getting out of the way (built)

- **Reading where it will land.** A man who hears a shell or bomb whistling in (its last 2–3 s), sees a quad overhead (its grenade takes 2–4 s to fall) or sees an FPV diving judges where it will go off. Depending on the time he has, he:
  - gets behind something solid, if there's cover close;
  - sprints clear of the spot, if there's time;
  - otherwise drops flat where he is.

  He stays down 1.5–3 s after a round, or 5–9 s under repeated fire.
- **Shelter.** Under repeated shelling, or with a quad about, men go to a ground-floor room away from windows and doors, or under a roof or trees. The spot has to be reachable and actually indoors. They stay 12–22 s after the last round, at most 90 s for shelling or 30 s for a drone. They don't walk off to shelter with an enemy seen close in the last few seconds.
- **Shooting at drones.** Riflemen now actually fire at drones that threaten them: FPVs first, quads near enough, not a high recon drone. At range only the machine gunner and half the others engage. (They used to "engage" without firing, because their ordinary aim overwrote the drone aim a frame later.)
- **Air defence and drones.** Real SPAA does shoot drones (the Gepard over Ukraine). So it takes aircraft, and drones that are attacking, closing, large or close, but leaves small quads beyond 150 m to small arms.
- **The drone's camera.** Military quads carry thermal (Mavic 3T class), so darkness costs them little, but fog and rain cost them range, as for a thermal sight.

### Assaults (built)

- **The rally point (ORP)** is picked out of sight of the objective, checked by raycast at eye height.
- **The support-by-fire position** is chosen from sampled spots on the near flank, plus windows and rooftops. They're scored by the share of the objective's fighting positions actually in sight from them, standing and kneeling, then range (best at 100–170 m), angle to the assault, cover and height. The chosen spot must be a real walk from the rally point.
- **The line of departure** is reachable, and hidden if possible.
- **Area fire** only goes at points the firer has a line to, and lifts off points with our men within 15 m.
- **Contact.**
  - At the rally point, contact means no forming up: straight to the deployment.
  - Only an enemy right on the rally point (within 60 m), or a man lost, compromises it. The squad then fights a hasty attack, and may form up again after a lull.
  - Deploying under contact, the assault goes in once most of the support is firing on the objective. Only a different enemy on the flank within 80 m, or a third of the squad lost, calls the deployment off.
  - In the assault, only heavy losses stop it.

  (Contact anywhere within 120 m of the rally point used to end the plan. With three sides moving about, nearly every attack was broken up before it deployed.)

### Your own squad (built)

- **"Your squad" in the menu:** join ALPHA-1 as before, or lead your own squad of 0, 1, 2, 3 or 5 bots (ALPHA-0).
  - It follows you, and the commander never gives it orders.
  - It uses the side's spawns and the usual reinforcement rules.
  - With 0, you're a lone soldier who comes back at the spawn nearest where you fell.
  - When you die it falls back to the nearest spawn, so you rejoin it quickly: about 20 s instead of 3 minutes.
  - The other two sides each get an extra bot squad of the same size, so the headcounts stay level.
- **Not waiting on you.** In a bot-led squad, its leader no longer holds everyone up while you wander. Between 60 and 150 m behind, he calls "close up", waits 12 s, then carries on (not again for 90 s). Further off, or ahead of him, you're on your own.

## Big-map notes (3a)

- The navmesh is baked in 240 m tiles in parallel and cached in `user://navcache` (about 5 s on first launch, 0.3 s after that).
  - Cells are 35 cm; the arena uses 17.5 cm.
  - Trees are on their own physics layer and left out of the navmesh, because thousands of tiny holes would make it huge.
- Navmesh queries are about 3–5 ms each on this map, so hot paths avoid them:
  - Cover search uses physics checks (is there ground here, and room to stand?).
  - Short moves with a clear line skip pathfinding.
  - Long repaths are limited to one per bot every 0.6 s.
- Objective pressure: bots don't get pulled into long-range duels or noise-chasing away from the zone.
- Headless profiling: `Prof.Time(...)` scopes, printed by DevShot.
- Navmesh on the big maps (Godot 4.7 specifics):
  - Tiles are 252 m, and 63 m over towns. A big tile of multi-storey interiors goes over Recast's per-tile vertex limit and bakes silently empty.
  - Tile corners and sizes sit on a 3.5 m grid (a multiple of both the 0.35 m and 0.5 m cells), so neighbouring tiles' edges coincide and join by key. Margin-based edge connections are on only for the regions along the seam between big and small tiles. With them on everywhere, syncing a map of ~280k polygons takes about a minute, and nobody can path until it's done.
  - `MapGetPath` stops searching after 4096 polygons and returns a partial path. Bots and vehicles use `NavBaker.Path` (QueryPath, up to 16 000 polygons).
  - Query cost scales with the whole map's polygon count, not the path's length. Godot makes the navmesh's polygons from Recast's height-detail triangles, and by default they follow the ground to within 20 cm, which puts 100k polygons on the 2 km valley and 1M on a 5 km map. Detail sampling of 16 cells and 0.8 m error cuts that to about a quarter. `Valley.Ground` clamps results to the terrain surface, because the mesh can now sit up to 0.8 m below it.
  - Desert mesas have a scree ramp on one side (about 25°), so the outposts on top can be reached on foot and by vehicle.

## Maps: testing

- `level=<id>` picks the battlefield for a headless run, e.g. `-- mode=tspec20 level=alhamra verbose shot=x.png frames=N`.
- `cam=x,z,h look=x,z,h` puts a fixed camera for a screenshot (heights over the ground).
- The verbose dump includes where bots are (open / forest / urban / interior) and how many are upstairs.

## Running

- `play.cmd` builds and launches.
- `dotnet build` alone only compiles.
- Dev screenshots: `Godot ... --path . -- shot=out.png [frames=N] [weapon=2] [ads] [grenade] [drill]`.
  Add `--headless --fixed-fps 60` for a smoke test with no window.
