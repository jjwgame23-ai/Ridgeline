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
- **Campaign war mode** (a big one; to scope separately): a whole war instead of one skirmish.
  - A large theatre (about 100 × 100 km, e.g. an island) split into 5 × 5 km battle maps, like X4's sectors or PlanetSide 2's continents.
  - A realistic military chain of command above the squad (platoon, company, battalion and up), so moves across the campaign map make narrative sense and taking a sector serves a real war aim beyond one round.
  - The economy would fit here.
  - Viability: the existing 5 km map generator can make each sector. The operational layer (units, supply, front lines across sectors) can run as the abstract simulation described above, with the sector you're in fully simulated. Needs: a campaign map generator (terrain regions, towns, roads), persistent unit rosters and supply, a strategic AI per side, and the hand-off between the abstract and full simulations.

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
- **Pause** (P). The game stops, its clock with it. F10 still goes to the main menu. There was no pause before.

### Not done

- Hit rates at 100–300 m are still high: about 15% of aimed rounds hit, mostly men standing or crouched in cover or running to it. Real combat runs far lower, and much of the gap is how much bots expose themselves.
- Suppressive fire is still a modest share, about 15% of rifle and machine-gun rounds. When a bot means to suppress it is often moving, has its own cover in the way, or isn't yet aimed at the spot. The `supp:*` counters count the windows and why none opened.
- Transport helicopters have one pilot seat. When he's hit, nobody can take over and the aircraft comes down (a real UH-60 has two pilots). Gunships' front-seaters do take over.
- A downed man in a ground vehicle is still pulled out on the spot.
- Every anti-aircraft vehicle is a radar-laid gun. None carries missiles (the real Stormer carries Starstreak), so beyond about 3 km a helicopter is safe from them.
- Vehicles still run over their own infantry now and then, 1–3 times in a 6-minute match. Drivers stop for anyone in a corridor straight ahead. How the rest happen (people stepping in from the side, a turn, reversing) hasn't been traced yet.

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
