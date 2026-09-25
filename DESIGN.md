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

- **Distance is simulated, not hand-made.** Guns and explosions are synthesised
  "dry" (what you'd hear a metre away: a Friedlander blast wave, gas roar, body
  thump, the bolt cycling) and then propagated offline to six distances (5 m to
  1.2 km) by `Acoustics`:
  - air absorption per ISO 9613-1 (at 1 km, 8 kHz is ~100 dB down, 1 kHz ~5 dB), which is what turns a crack into a thump;
  - a ground-reflection dip in the low mids;
  - a synthetic valley impulse response (discrete hillside echoes plus a tail that darkens as it decays), whose share grows with distance, which is why far gunfire rolls.
  Everything runs in the frequency domain (FFT) at startup (~2 s). At play time the band is picked by chance weighted by log-distance, so moving away shifts the sound gradually.
- **Occlusion.** A hill or building between you and a sound makes it play as if from ~3x further (darker) and 5 dB quieter.
- **Turbulence.** Beyond ~150 m each shot's level wobbles by a few dB.

- Every sound is an event at a point, and its wavefront travels at 343 m/s
  (`SoundWorld`). A plate at 600 m rings about 1.7 s after it swings. A shot from
  1 km lands about 3 s after the muzzle flash.
- **Supersonic crack.** A bullet passing within 40 m emits a crack from its
  point of closest approach. The crack arrives before the muzzle report, and the
  gap between them tells you the distance (`Ballistics.CheckPass`).
- **Distance layers.** Near, mid and far versions of loud sounds. Far away you
  hear low-passed rumble with terrain echoes. Loudness falls off at 12 dB per
  tenfold distance, softer than the physical 20, so a firefight 1–2 km away
  stays present in the mix.
- **Voice budget.** 72 voices. When they're all busy, the quietest one is
  replaced, so a huge battle degrades gracefully.
- **Blasts.** The shockwave also travels at the speed of sound. Close blasts
  cause ear ringing and temporarily muffle the whole world.
- **Later.** Occlusion by terrain and buildings, crack direction from the Mach
  cone, and aggregating many distant shots into a "battle bed".

### Acoustics, part 2: the space you're in (built)

- **Bands are dry.** A gun's or explosion's distance bands hold only what the air and the ground do to it (absorption, the ground-reflection dip), with no reverb baked in. The rifle source has a shorter, brighter roar and a brief thump, so far off it stays a "pop" and doesn't turn into a boom.
- **Live reverb of the listener's space** (Tail bus: a reverb whose room size, damping and pre-delay follow the listener, eased over ~0.5 s):
  - a room: small, dense, bright, scaled to the building;
  - a street: a quick slap and flutter;
  - forest: short and soft;
  - open ground: sparse and late.
  Outdoors, the reverberant share grows with distance.
- **Real first reflections outdoors.** Rays from the source find walls and cliffs within 260 m that the listener can see. Each is an echo played from that surface's direction, delayed by the extra path, with spreading and absorption losses (walls reflect more than broken ground). The strongest three are kept, within a budget of about 30 a second.
- **Buildings as acoustic spaces** (`Rooms`). Every enterable building registers its interior box and its openings. Between inside and outside (or two buildings), sound takes the shortest clear way through openings: the listener hears it from the window or doorway, later and a bit quieter. A closed door costs 14 dB and the highs. With no way through, only the walls' low thump gets in.
- **Doors** (`Door`). Real hinged doors in outside doorways, about half left open. Use key to open or close. Bots open doors in their way and leave them open. A closed door blocks movement, sight and bullets. Doors have their own physics layer, so the navmesh treats doorways as open. An open door swings back flat against the inside wall.
- **Behind you.** Sounds from behind go through a gentle high-shelf cut (head and ear shadow), the main front/back cue on headphones.
- F3 shows which acoustic space you're in.

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
   - **Graphics settings (menu):** display mode (windowed, borderless or exclusive fullscreen), v-sync, frame cap, render scale (FSR below 100%), MSAA and shadows. They're saved, and F11 toggles fullscreen anywhere. Screenshot and test runs stay windowed.
   - **3c:** scale. The abstract far-away simulation and the promotion/demotion handover described above, reaching 3×33 with persistent aftermath.
4. **Economy.** Cash, buy screen, gear loss, and bots buying loadouts.
5. **Roster.** About 150 persistent named mercs with skills, playstyles, bank
   balances and grudges. Post-match scoreboards.
6. **Vehicles.** Trucks, then armor, then helicopters.

## Bots (built)

- **Senses** (`BotSenses`)
  - Sight builds up awareness over time. Distance, being in central vs peripheral vision, stance, movement, and whether the target just fired all change the rate. It's checked against a target's head, chest and hips.
  - Hearing uses sound events whose wavefront has actually reached the bot, and gives only a rough position.
  - Bots also learn about enemies from teammate callouts (after a short delay), from being shot at, and from being hit.
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
  - Bots lean around corners (and the hitbox leans with them), throw frags along solved arcs, and run from grenades that land nearby.
  - They sprint between positions.
- **Craters** (`CraterField`): one shared field of dig depth, spoil and scorch on a 0.25 m grid, so blasts on top of each other merge into one bigger, deeper pit and blow earlier rims away. On terrain, the ground is cut away (a hole mask the terrain shader discards) so the bowl has real depth. Characters don't sink into craters yet.
- **Cover** (`CoverFinder`): fully dynamic.
  - Candidate spots on the navmesh are tested with rays from the threat's eye.
  - A spot counts only if it hides you crouched. It scores higher if you can shoot over it standing, or step out to shoot around it.
- **Headless testing:**
  - Command: `-- mode=spec5 verbose shot=x.png frames=N` with `--headless --fixed-fps 60`.
  - It logs every kill, per-bot shots, hits, blocked shots and wide misses, and a status dump every 30 s.

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
