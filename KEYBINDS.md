# Ridgeline key bindings

The defaults, as `src/Game/Controls.cs` defines them (this file is written from it: run the game with `-- bindsdoc=KEYBINDS.md`). Change any of them in the main menu, under **Controls**; your own bindings are kept in `user://keybinds.cfg`.

## Movement

| Action | Default | Notes |
|---|---|---|
| Move forward | W | Helicopter: collective up |
| Move back | S | Helicopter: collective down |
| Move left | A | Helicopter: pedal left |
| Move right | D | Helicopter: pedal right |
| Jump / stand up | Space | Vehicle: brake · helicopter: hover · drone and free camera: up |
| Sprint | Shift | While aiming: hold your breath · drone and free camera: faster |
| Crouch | C / Ctrl | Drone and free camera: down |
| Go prone | Z |  |
| Lean left | Q |  |
| Lean right | E |  |

## Weapons

| Action | Default | Notes |
|---|---|---|
| Fire | LMB | Drone: drop / detonate |
| Aim (hold) | RMB | Vehicle: zoom |
| Reload, keeping the magazine | R | Tap again straight away to drop the magazine instead |
| Quick reload, dropping the magazine | unbound | Unbound: double-tap Reload does the same |
| Fire mode | V | Vehicle: ammunition |
| Check magazine | T |  |
| Primary weapon | 1 |  |
| Secondary weapon | 2 |  |
| Throw a frag | G |  |

## Actions

| Action | Default | Notes |
|---|---|---|
| Use / get in or out | F | Doors, vehicles |
| Bandage yourself | X | Helicopter: flares |
| Role tool | H | Medic kit, sandbags, quadcopter · drone: bring it home / ditch it |
| Fly an FPV drone | J |  |
| Fly an anti-tank FPV | K |  |
| Look around (hold) | Alt |  |

## Squad

| Action | Default | Notes |
|---|---|---|
| Map | M | Pick a spawn there when dead; as squad leader, click to send the squad |
| Squad: on me | B | Squad leader |
| Squad commands | N | Squad leader; then a number |

## Spectating

| Action | Default | Notes |
|---|---|---|
| Next soldier | LMB |  |
| Previous soldier | RMB |  |
| Chase / eyes view | V |  |
| Free camera | F |  |

## Game

| Action | Default | Notes |
|---|---|---|
| Help | F1 |  |
| Debug overlay | F3 |  |
| Sniper drill (firing range) | F4 |  |
| Distant battle on/off (firing range) | F5 |  |
| Bot debug | F6 |  |
| Volume down | F7 |  |
| Volume up | F8 |  |
| Main menu | F10 |  |
| Fullscreen | F11 |  |

## Fixed keys

- **Esc**: let go of the mouse; close the squad command menu.
- **1–8**: in the squad command menu, pick a command; while waiting to respawn, pick your role.
- **1–9**: in a vehicle, change seats.

## Shared keys

Some keys do two jobs where the two can never be wanted at once (a key for a weapon on foot and a seat in a vehicle, say). Two actions on one key where both could be wanted in the same place are a conflict, and the Controls menu flags them.

The defaults have no conflicts.
