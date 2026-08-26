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
lod_override = none                 # none (default) | Full | High | Medium | Low | Minimal

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
heart @ 48,48                      # Shroomer mycelium heart (parent=<Species> to override)
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

### `lod_override` — testing the tiers a small world can't reach

`lod_override` pins **every** entity to one LOD tier for the whole run, ignoring distance from
the player. Values are `Full`, `High`, `Medium`, `Low`, `Minimal`, or `none` (the default,
meaning normal distance-based tiering). An unrecognised value warns and falls back to `none`.

It exists because a test scene otherwise cannot observe most of the simulation. Tier boundaries
are multiples of the camera's visible radius — `fullRange = visibleRadius × 1.15`, then ×2, ×3,
×5, so 69 / 138 / 207 / 345 tiles at the default radius of 60. The largest scenario world is 128
tiles across, whose greatest possible distance from a centred player is about 181 tiles: **`Low`
and `Minimal` are unreachable in every scenario in this directory**, while a profiled 36-chunk
game world runs 69% of its entities at `Minimal` and 16% at `Low`. Without the override the
harness is structurally blind to the tiers most of the world actually lives in.

The override changes only *which* tier is chosen. Tick interval, the phase stagger and the
`EffectiveInterval` bookkeeping are the tier's own and are untouched, so a forced tier behaves
exactly like an earned one — both paths run the same code in `LODSystem.ApplyTier`. (Dropping the
stagger would have made cost-spike bugs invisible while hunting rate bugs.)

```ini
[world]
lod_override = Minimal   # every creature decides once per 20 ticks, wherever the player stands
```

## The LOD differential test

`Scripts/Testing/LodDifferentialRunner.cs` runs a scenario twice from one seed — once pinned to
`Full`, once to `Minimal` — and compares end-state metrics. It is the executable form of the rule
in `FEATURES_AND_DESIGN.md` §6.1: **LOD may coarsen the timing of a decision, never the amount of
anything that happens.** A gated system that advances a per-tick quantity has to multiply it by
`SimulationLOD.EffectiveInterval`; when it doesn't, the quantity quietly runs up to 20× slow for
most of the world, and which part of the world depends on where the player is standing.

```
godot --headless --path godot res://Scenes/LodDifferential.tscn -- --ticks=3000
    --scenario=predator_prey,shroomer_bloom   # default: every scenario in TestScenarios/
    --tolerance=0.15 --min-count=5 --control-runs=2
```

Exit code 0 when every scenario passes. Per-scenario CSVs land in
`logs/lod_differential_<scenario>.csv`, with `logs/lod_differential_summary.csv` across all of
them, and each individual simulation keeps its full log set under
`logs/lod_differential/<scenario>_<full|minimal|controlN>/`.

Metrics compared: per-species population, births and deaths by cause, kills, terraform nudges and
class shifts, nutrition consumed / regenerated / corpse-enriched, mean Shroomer growth scale, and
mean nearest-same-species-neighbour distance (which is what Separation and Herding actually
produce).

**Read the noise floor before believing a number.** These scenarios are small and chaotic: running
`predator_prey` at Full tier on seed 42 and again on seed 43 moves the kill count from 17 to 40. A
bare 15% test would therefore fail nearly every ecological metric however correct the LOD code is.
So each scenario also runs `--control-runs` extra **Full-tier** simulations on shifted seeds, and
the worst Full-vs-Full divergence a metric shows there becomes its noise floor. A metric is only
reported as `FAIL` when LOD moved it further than the dice did — over the tolerance *and* over the
floor. The raw tolerance verdict is kept in the CSV (`exceeds_tolerance`, and the `within_noise`
verdict) so nothing is hidden.

The floor is a sample, not a proof. A metric can pass because the scenario is too noisy to measure
it, which is a statement about the scenario rather than a clean bill of health — mechanical rates
(terraform nudges, mean growth scale) have floors of a few percent and are where this test has
teeth. Findings and the standing exceptions are recorded in
[lod-differential-baseline.md](lod-differential-baseline.md).

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

## Reproducibility

Runs are deterministic. Every simulation random stream comes from `Utils/SimRandom`, which
`GameManager` fixes to the run's seed before the system stack is built, so the same world seed and
scenario file replay identically. Previously each system time-seeded its own `Random`, which made
two runs of one scenario incomparable — any difference between them might be the change under test
or might be the dice. Leave `WorldSeed` at 0 for a fresh nondeterministic run.

## Shipped scenarios

| File | Exercises |
| --- | --- |
| `predator_prey.scenario.txt` | hunting, pack coordination, fleeing/fear, cover |
| `grazing_depletion.scenario.txt` | nutrition depletion/regen, food-seek roams, discomfort escape |
| `shroomer_bloom.scenario.txt` | terraforming (wetter), fertility feeding, spore spread, fungivory |
| `faction_skirmish.scenario.txt` | three-way faction terraform tug-of-war, nests/crystals/spores |
| `aquatic_biome.scenario.txt` | element-aware discomfort, shark/fish habitat preference, penguin haul-out breeding, world-edge steering |
| `freshwater_pond.scenario.txt` | inland fish regulation with no marine predators: plankton depletion + fertility-coupled breeding, otters and crocodiles |
| `crystal_siege.scenario.txt` | Sectids besieging Faeling crystals — the objective path end to end (both crystals down by t≈800) |
| `nest_raid.scenario.txt` | Faeling keepers breaking a Sectid outpost — `nest_destroyed` + `colony_destroyed`, and the two-way counter-siege |
| `heart_drying.scenario.txt` | drying terraform collapsing a Shroomer mycelium heart with no blow ever struck at it (t≈5,280) |

## Running one scenario to an outcome (headless)

The test scene runs a scenario forever in front of a person, which answers *does this look right*
but not *does this finish, and by when* — a siege that completes at tick 900 and one that never
completes look identical for the first thirty seconds of watching. `ScenarioRunner`
(`Scenes/ScenarioRun.tscn`) is the second question:

```
godot --headless --path godot res://Scenes/ScenarioRun.tscn --
    --scenario=crystal_siege --ticks=4000 --expect-all=Crystal
```

It builds the scenario world from the same `SimulationStack` the game uses, ticks a fixed budget,
and reports for every `StructureKind` how many existed, how many survived, and the ticks of first
loss and total loss. `--expect` requires at least one of a kind to be destroyed within the budget;
`--expect-all` requires the kind to be wiped out. The distinction matters because factions
**rebuild** — a Shroomer bloom that loses its heart founds another once it is large enough — so
"the mechanism fired" and "the faction was erased" are different claims. Exit code is 0 when every
expectation held.

## Where the code lives

- `godot/Scripts/Testing/` — `TestSceneManager` (harness node), `TestScenario` (parser),
  `ScenarioTerrainGenerator` (authored terrain), `ScenarioSpawner` (exact spawns),
  `LodDifferentialRunner` (headless Full-vs-Minimal comparison, `Scenes/LodDifferential.tscn`).
- `godot/Scripts/Systems/SimulationStack.cs` — the one definition of which systems run and in
  what order. `GameManager` and the differential runner both build from it, so the harness
  cannot end up testing a different stack from the one the game ships.
- `godot/Scripts/World/IChunkGenerator.cs` — the seam that lets a scenario replace noise
  worldgen; `ScenarioTileParams.cs` — canonical params per tile type (keeps classification,
  terraform, and palette consistent on authored terrain).
- `godot/Scripts/Systems/EcosystemLogger.cs` — the extended logs and their static toggles.
- Decision instrumentation sits directly in `HuntingSystem` / `FleeingSystem` /
  `WanderSystem`, gated by `EcosystemLogger.DecisionLoggingFor(speciesId)` so detail strings
  are never built when logging is off.
