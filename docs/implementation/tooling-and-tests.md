# Tooling & tests

*Last updated: 2026-09-07 · verified against `7a7fb28`*

Four harnesses, three gates, one profiler, one preview tool. All of them build the world from
`SimulationStack.Build`, so no harness can end up testing a different stack from the one the game
ships.

## Scenes

| Scene | Purpose |
|---|---|
| `Scenes/Main.tscn` | the game (F5) |
| `Scenes/TestScene.tscn` | scenario-defined world, watched live (F6) |
| `Scenes/ScenarioRun.tscn` | one scenario to an outcome, headless, with expectations |
| `Scenes/LodDifferential.tscn` | Full-vs-forced-tier comparison, headless |
| `Scenes/PopulationSoak.tscn` | long standard-world run + the whole-game invariant gate |
| `Scenes/WorldgenPreview.tscn` | live worldgen parameter exploration (F6) |

## Test scenarios — `Scripts/Testing/`, `godot/TestScenarios/`

| File | Role |
|---|---|
| `TestSceneManager.cs` | harness node |
| `TestScenario.cs` | parser; `Shape`, `SpawnKind`, `SpawnOp` |
| `ScenarioTerrainGenerator.cs` | authored terrain, plugged in through `World/IChunkGenerator.cs` |
| `ScenarioSpawner.cs` | exact spawns |

A scenario is plain INI-style text with `#` comments. Sections: `[world]` (size, seed, base tile,
elevation, tps, population cap, disabled species, `no_factions`, `lod_override`), `[terrain]`
(rect / circle / band shape ops applied in order), `[map]` + `[legend]` (ASCII painting stamped
after shape ops), `[spawn]` (exact placements, including `nest`, `crystal`, `heart` and `spore`),
and `[logging]` (decision log, terrain and nutrition intervals, snapshot cadence, tracked species).
Scenario text can also be pasted into an `Inline Scenario` inspector field, which takes precedence
over the file.

Spawns are placed **exactly where stated**. A warning is printed when the centre tile is not
normally spawnable for the species, and the spawn happens anyway — stranding a creature on hostile
ground may be the point of the test.

`lod_override` pins every entity to one tier for the whole run regardless of distance. This is what
makes the coarse tiers testable at all: a small world's greatest possible distance from a centred
player does not reach them. An unrecognised value warns and falls back to `none`.

### Scenario grammar

```ini
[world]
name = my_test
size_chunks = 4        # world edge in 32-tile chunks (4 = 128x128 tiles)
seed = 42              # 0 = random each run
base_tile = Grass      # fill for the whole map
elevation = 0.5        # flat land height, clamped to a playable band
tps = 20
max_population = 4000
disabled_species = Shroomer, Hawk   # optional; also honoured by systems
no_factions = false                 # disable all three factions in one flag
lod_override = none                 # none | Full | High | Medium | Low | Minimal

[terrain]              # shape ops painted over the base fill, in order
rect   ShallowWater 0 0 128 20     # tile, x, y, w, h
circle Forest 64 64 10             # tile, cx, cy, r
band   Arid x 100 128              # tile, axis (x|y), from, to

[map]                  # optional ASCII painting, stamped after shape ops
origin = 0,0
~~~~....ffff
~~......ffff

[legend]               # extends or overrides the default legend
~ = DeepWater
. = Grass
f = Forest

[spawn]                # exact placements, one per line
Deer x24 @ 48,64 r10 groups=3      # 24 deer in 3 groups, scattered r=10 around (48,64)
Wolf x5 @ 100,40 r4 group          # one pack of 5 (first member is alpha)
Rabbit x16 @ 60,80 r14             # scattered individuals
polar_bear x2 @ 20,20              # names are case-insensitive; '_' = space
nest @ 90,30 colony=1 sectids=8    # Sectid nest + starter swarm
crystal @ 30,30                    # Faeling crystal
heart @ 48,48                      # Shroomer mycelium heart (parent=<Species> to override)
spore @ 25,25 x6 r4                # Shroomer spores

[logging]
decisions = true                   # behavioural decision log
decision_species = Wolf, Deer      # restrict it; empty = all species
terrain_interval = 100             # tile histogram + terraform activity; 0 = off
nutrition_interval = 100           # nutrition economy; 0 = off
snapshot_interval = 50             # population/stats CSV cadence
track = Wolf                       # per-entity TRACKED logging
```

Default map legend, overridable per scenario: `~` DeepWater · `=` ShallowWater · `r` River ·
`.` Grass · `,` Shrubland · `f` Forest · `w` Wetland · `B` Bog · `m` Mountain · `s` Sand ·
`d` Dirt · `a` Arid · `t` Tundra · `p` Steppe · `g` Taiga · `i` Ice · `v` Savanna · `j` Jungle ·
`L` Lava · `R` Reef · space = keep the underlying tile.

Shipped scenarios cover hunting and pack coordination, grazing depletion, Shroomer bloom mechanics,
the three-way terraform tug-of-war, aquatic habitat behaviour, inland fish regulation, a Sectid
siege of crystals, a keeper raid on a nest outpost, and a heart killed purely by drying.

## Gate 1 — scenario outcomes (`ScenarioRunner.cs`)

```
godot --headless --path godot res://Scenes/ScenarioRun.tscn -- \
    --scenario=crystal_siege --ticks=4000 --expect-all=Crystal
```

Watching a scenario answers *does this look right*; it does not answer *does this finish, and by
when* — a siege that completes at tick 900 and one that never completes look identical for the
first thirty seconds of watching. The runner ticks a fixed budget and reports, per `StructureKind`,
how many existed, how many survived, and the ticks of first and total loss.

`--expect` requires at least one of a kind destroyed; `--expect-all` requires the kind wiped out.
The distinction matters because factions **rebuild** — "the mechanism fired" and "the faction was
erased" are different claims. Exit code 0 when every expectation holds.

## Gate 2 — LOD differential (`LodDifferentialRunner.cs`, `LodExpectedVerdicts.cs`)

Runs each scenario at Full and at a forced coarse tier and compares outcome metrics.

**The contract is `docs/lod-differential-expected.csv`.** It records the verdict every
(scenario, metric) pair currently produces, and the exit code is computed from the **difference**
against it:

| recorded | observed | meaning | exit |
|---|---|---|---|
| FAIL | FAIL | known, explained exception | silent, 0 |
| passing | FAIL | **regression** | non-zero |
| FAIL | passing | **improvement** — re-record | 0, printed loudly |
| present in only one | — | **drift**: a metric was added or removed | non-zero |

Before this split the runner exited on the raw failure count, dozens of metrics fail for documented
reasons, and it had therefore never once exited 0 — a gate that is always red carries no signal.

`ok`, `within_noise` and `below_min_count` are one class for gating purposes: the last two both
mean "this metric had nothing to say" and which applies depends on where the dice fell. The exact
verdict is still recorded because it is worth reading.

Re-recording is deliberate and never a side effect:

```
godot --headless --path godot res://Scenes/LodDifferential.tscn -- --ticks=3000 --record
```

Standing exceptions and their reasoning are in
[`../archive/lod-differential-baseline-2026-08.md`](../archive/lod-differential-baseline-2026-08.md);
the live defects are D2 and D3 in [lod-and-performance.md](lod-and-performance.md).

**D1 blocks part of this gate**: scenarios with two or more factions are not reproducible across
processes, so their recorded verdicts are unstable. Single-faction and no-faction scenarios are
stable. See [species-data.md](species-data.md).

## Gate 3 — whole-game invariants (`PopulationSoakRunner.cs`)

```
godot --headless --path godot res://Scenes/PopulationSoak.tscn -- --ticks=20000
```

A small set of properties that must hold for the game *as a game*, asserted over a long run of the
standard world and enforced by an exit code. Roughly: no structures destroyed inside an early
grace window; every faction still holding an anchor and clearing a population floor at the end;
the Sectid colony economy having ignited at all; the world contested — neither untouched nor
converted — measured as deviation from the pristine generated world; and no class over its
population ceiling.

**This gate is binary and must not grow an exception list.** If an assertion needs an exception,
the threshold is wrong and should be changed deliberately with the reasoning recorded.

`--snapshot-world` additionally writes a `WorldSnapshot` pair — one straight after generation, one
after the last tick, each labelled so the two do not overwrite each other — for reading a faction's
advance off the biome maps instead of inferring it from the CSV. Off by default, and it changes
nothing the gate asserts: the runner otherwise builds nothing that exists only to be looked at, and
every capture walks the whole world.

It exists because three changes landed in sequence, each meeting its own acceptance criteria, and
together they broke the game — one faction dismantling the other two within a minute of world
start. Nothing else could have caught it: the LOD differential compares a build against itself, so
a change uniformly wrong at every tier passes; the soak measured composition and the composition
looked fine, because the factions were inside their budgets the whole time.

The gate's current status is a **run result**, not a documentation fact; see
[`../changelog.md`](../changelog.md).

## Instrumentation

- **`EcosystemLogger`** (`Systems/EcosystemLogger.cs`) — registered last, implements `ISystem`,
  observes only. Writes to `logs/`, each file also copied to a `latest_*` name:
  - **events** — `tick, event, species, entity_id, x, y, detail`. Events include births,
    reproduction, kills, hunt start and failure with a reason, starvation, age death, environment
    death, spore creation and maturation, structure damage and destruction, and population-budget
    refusals. **For a `kill` event the `species` column is the victim and `detail` carries the
    killer** — to build a predator→prey matrix, parse `detail`, not `species`.
  - **population** — per-species creature counts on an interval, plus columns for spores, nests
    and crystals, and per-class budget and refusal counts. Spores carry a Shroomer species id
    internally and are tallied apart, so `total` is living creatures only and matches the overlay.
  - **species stats** — per species per interval: population, births, deaths split by cause
    (starve / age / predation / environment), kills made, and average hunger and energy. A
    mass-perish alert fires when a species loses a large share of its population in one interval.
    Death-cause ratios here are the key signal for predator balance.

  All floats are written with `InvariantCulture` so a comma-decimal locale cannot corrupt columns.
  `TrackSpecies` adds per-entity snapshots and that species' inbound and outbound combat damage.

  Extended logs (decision log, terrain histogram, nutrition economy) are toggled per scenario via
  `[logging]` and gated by `EcosystemLogger.DecisionLoggingFor(speciesId)`, so detail strings are
  never built when logging is off. The instrumentation itself sits in `HuntingSystem`,
  `FleeingSystem` and `WanderSystem`.

- **`HuntFunnelProbe`** (`Systems/HuntFunnelProbe.cs`) — counts why prey candidates were rejected.
  The tool for "which gate is returning nothing", as opposed to guessing.

- **`WorldSnapshot`** — startup diagnostics; see [world-generation.md](world-generation.md).

- **`scripts/ecosystem_heatmap.py`** — offline event/population heatmaps from the CSVs.

## Worldgen preview — `Scripts/Tools/WorldgenPreviewer.cs`

`Scenes/WorldgenPreview.tscn`, run with F6. Sliders for every terrain export regenerate the map
live (debounced, climate-only, no rivers); a **Full detail** button runs the exact pipeline
including `PrecomputeRivers` for the current seed. Views: biome, elevation, moisture, temperature.
The stats panel shows the live biome distribution and niche coverage using the same code as the
snapshot report, plus the current parameter set for transcribing into the inspector.

It calls `TerrainGenerator.SampleTile` — the same per-tile function `GenerateChunk` loops over — so
the preview cannot drift from what the game generates.

## D7 — what is not tested

There is no unit-level coverage. Every gate above is a whole-run behavioural assertion, which
catches interaction failures well and localises them badly. A system-level test would have caught,
for example, the cooldown decrement overshooting zero without needing a 20,000-tick run to notice
prey looked invulnerable.
