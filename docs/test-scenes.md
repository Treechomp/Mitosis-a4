# Test scenes — small, exactly-specified worlds for system testing

**Branch:** `claude/game-test-scene-gen-2jkwur` — a standalone workstream, separate from the
main balance chain. It adds a harness for quickly generating **limited-size scenes** that
exercise specific game systems (hunting, fleeing, terraforming, grazing/nutrition, factions)
under controlled, repeatable conditions, with much deeper logging than the main game.

## Quick start

1. Open `godot/Scenes/TestScene.tscn` in the editor and run it (**F6**).
2. It loads the scenario file set in the `TestSceneManager` inspector
   (`Scenario Path`, default `res://TestScenarios/predator_prey.scenario.txt`).
3. Logs land in `godot/logs/` (path printed at startup): the usual events/population/stats
   CSVs plus the new `latest_decisions.csv`, `latest_terrain.csv`, `latest_nutrition.csv`.

The simulation stack is the **real one** — the same systems in the same order as
`GameManager` — only worldgen and initial spawning are replaced. `TestSceneManager` inherits
`GameManager` and overrides two hooks (`CreateWorld`, `PopulateWorld`), so mainline system
changes flow into test scenes automatically.

### Observation tools (available in BOTH Main and TestScene)

| Key | Action |
| --- | --- |
| `LMB` | inspect the creature under the cursor — live panel, top right |
| `Tab` / `Shift+Tab` | cycle the highlighted species (highlighted creatures render near-white) |
| `H` | highlight the selected creature's own species |
| `G` | jump the camera to the next member of the highlighted species |
| `F` | toggle free camera (detached from the player creature and from terrain speed) |
| `Esc` | clear selection and highlight |

The inspector reports **drives**, not just vitals: hunt target and distance, pack phase/role,
flee state and stamina, fear ratio and response, terrain discomfort, roam target, colony
carrying/dormancy, growth and elder-enrage state. That's what tells you *why* a species is
behaving the way the CSVs say it is — e.g. a predator sitting at `hunt no target` while starving
next to prey is a target-acquisition failure, not a balance problem.

In the test scene, terrain paint mode (`P`) takes over the left mouse button; every other
observation control keeps working while painting.

### Runtime controls (beyond the normal WASD/zoom/F3)

| Key | Action |
| --- | --- |
| `Space` | pause / resume |
| `.` | single-step one tick (pauses first) |
| `,` | cycle simulation speed: 20 → 60 → 180 → 5 TPS |
| `P` | toggle terrain **paint mode** |
| `[` / `]` | (paint mode) previous / next brush tile |
| `1`–`5` | (paint mode) brush radius 0 / 1 / 2 / 4 / 8 |
| `LMB` drag | (paint mode) paint tiles |
| `O` | export current terrain as a scenario-ready ASCII map into `logs/` |

Painting sets the tile **and** its moisture/temperature parameters to the canonical values
for that type (`ScenarioTileParams`), so terraforming and the colour palette stay consistent.
Elevation is not editable live (chunk meshes are built once), so painted water/mountains
render flat with their discrete colour — gameplay (drowning, movement, spawning) is driven by
tile type and behaves correctly. For properly sunken water/raised mountains, put them in the
scenario file and relaunch (small worlds load in about a second).

## Scenario files

Plain text, INI-style sections, `#` comments (whole-line or inline). Samples live in
`godot/TestScenarios/`. Alternatively paste scenario text into the `Inline Scenario` field in
the inspector (takes precedence over the file when non-empty).

```ini
[world]
name = my_test
size_chunks = 4        # world edge in 32-tile chunks (4 = 128x128 tiles)
seed = 42              # rng seed for stat variation; 0 = random each run
base_tile = Grass      # fill for the whole map
elevation = 0.5        # flat land height, clamped to 0.42..0.70
tps = 20
max_population = 4000
disabled_species = Shroomer, Hawk   # optional; also honoured by systems
no_factions = false                 # disable Shroomer+Sectid+Faeling in one flag

[terrain]              # shape ops painted over the base fill, in order
rect   ShallowWater 0 0 128 20     # tile, x, y, w, h
circle Forest 64 64 10             # tile, cx, cy, r
band   Arid x 100 128              # tile, axis (x|y), from, to — full strip

[map]                  # optional ASCII painting, stamped after shape ops
origin = 0,0           # where row 0, col 0 lands in the world
~~~~....ffff
~~......ffff
........ffff

[legend]               # extends/overrides the default legend (see below)
~ = DeepWater
. = Grass
f = Forest

[spawn]                # exact spawns, one per line
Deer x24 @ 48,64 r10 groups=3      # 24 deer in 3 groups, scattered r=10 around (48,64)
Wolf x5 @ 100,40 r4 group          # one pack of 5 (first member is alpha)
Rabbit x16 @ 60,80 r14             # scattered individuals (species default social type)
polar_bear x2 @ 20,20              # names are case-insensitive; '_' = space
nest @ 90,30 colony=1 sectids=8    # Sectid nest + 8 starter sectids around it
crystal @ 30,30                    # Faeling crystal
spore @ 25,25 x6 r4                # 6 Shroomer spores (parent=<Species> to override)

[logging]
decisions = true                   # behavioral decision log (see below)
decision_species = Wolf, Deer      # restrict decision log; empty = all species
terrain_interval = 100             # tile histogram + terraform activity; 0 = off
nutrition_interval = 100           # nutrition economy; 0 = off
snapshot_interval = 50             # population/stats CSV cadence (main game: 100)
track = Wolf                       # existing per-entity TRACKED logging
```

Default map legend (a `[legend]` section can override any of it):
`~` DeepWater · `=` ShallowWater · `r` River · `.` Grass · `,` Shrubland · `f` Forest ·
`w` Wetland · `B` Bog · `m` Mountain · `s` Sand · `d` Dirt · `a` Arid · `t` Tundra ·
`p` Steppe · `g` Taiga · `i` Ice · `v` Savanna · `j` Jungle · `L` Lava · `R` Reef ·
space = keep underlying tile.

Spawns are placed **exactly where you say** — a warning is printed if the centre tile isn't
normally spawnable for the species, but the spawn still happens (stranding a creature on
hostile terrain may be the point of the test).

## The extended logs

All are opt-in per scenario and cost nothing in the main game (static toggles, off by default).

- **`decisions_*.csv`** — `tick,species,entity_id,x,y,system,decision,detail`. Logged on
  state *transitions*, with the parameters that shaped the choice:
  - `hunting`: `target_acquired` (prey, distance, hunger, urgency, pack),
    `hunt_stop_sated`; hunt failures/kills stay in the events CSV (`hunt_start`,
    `hunt_fail` with reason, `kill`).
  - `fleeing`: `flee_start` (response type, fear, threat distance, stamina), `flee_end`
    (reason=safe/discomfort_override), `abandon_hunt_fear`.
  - `wander`: `roam_start` (reason = seek_food / restore_terrain / keeper_siege / random,
    target, hunger urgency), `roam_end`, `escape_start`/`escape_end` (terrain discomfort).
- **`terrain_*.csv`** — per-interval row: count of every tile type, mean moisture,
  terraform nudges and class-crossing shifts that interval. Every terraform path is counted
  centrally in `WorldManager.Terraform` (roaming terraformers, nest hatches, crystal pulses).
  Individual class shifts also appear in the events CSV as `terraform_shift`
  (`old->new;dir=...;moisture=...`).
- **`nutrition_*.csv`** — per-interval row: standing nutrition total vs capacity (fill %),
  plus flows: `consumed` (grazing + faction fertility feeding), `regenerated`
  (TileRegenerationSystem), `corpse_enriched` (carrion decomposition).

`latest_*.csv` copies are always the most recent run, same as the existing logs.

## Shipped scenarios

| File | Exercises |
| --- | --- |
| `predator_prey.scenario.txt` | hunting, pack coordination, fleeing/fear, cover |
| `grazing_depletion.scenario.txt` | nutrition depletion/regen, food-seek roams, discomfort escape |
| `shroomer_bloom.scenario.txt` | terraforming (wetter), fertility feeding, spore spread, fungivory |
| `faction_skirmish.scenario.txt` | three-way faction terraform tug-of-war, nests/crystals/spores |
| `aquatic_biome.scenario.txt` | element-aware discomfort, shark/fish habitat preference, penguin haul-out breeding, world-edge steering |

## Where the code lives

- `godot/Scripts/Testing/` — `TestSceneManager` (harness node), `TestScenario` (parser),
  `ScenarioTerrainGenerator` (authored terrain), `ScenarioSpawner` (exact spawns).
- `godot/Scripts/World/IChunkGenerator.cs` — the seam that lets a scenario replace noise
  worldgen; `ScenarioTileParams.cs` — canonical params per tile type (keeps classification,
  terraform, and palette consistent on authored terrain).
- `godot/Scripts/Systems/EcosystemLogger.cs` — the extended logs and their static toggles.
- Decision instrumentation sits directly in `HuntingSystem` / `FleeingSystem` /
  `WanderSystem`, gated by `EcosystemLogger.DecisionLoggingFor(speciesId)` so detail strings
  are never built when logging is off.
