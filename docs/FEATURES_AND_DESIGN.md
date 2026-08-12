# Mitosis — Features, Systems & Design Reference

> **Project**: an early-development creature-sandbox **video game** — the world-ecology is a game
> mechanic (a background system that keeps the world alive), not a scientific model. See
> [CLAUDE.md](../CLAUDE.md).
> **Purpose**: Comprehensive reference for the currently implemented game features, systems,
> mechanics, and design decisions. Read this before diving into source code.
>
> **Engine**: Godot 4.6.3 with C# · **Rendering**: 3D (`Node3D`, orthographic `Camera3D`)
> **Architecture**: Custom SoA ECS, fixed 20 TPS simulation
> **Last updated**: July 2026
>
> Companion docs: [architecture.md](architecture.md) (high-level design),
> [godot-roadmap.md](godot-roadmap.md) (status & roadmap),
> [terrain-handling-audit.md](terrain-handling-audit.md) /
> [terrain-profile-design.md](terrain-profile-design.md) /
> [terrain-roadmap.md](terrain-roadmap.md) (terrain subsystem). Superseded docs are in
> [archive/](archive/).

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Architecture Recap](#2-architecture-recap)
3. [World Generation](#3-world-generation)
4. [Terrain Types](#4-terrain-types)
5. [Species System](#5-species-system)
6. [Core Simulation Systems](#6-core-simulation-systems)
7. [Faction Systems](#7-faction-systems)
8. [Rendering & Player Controls](#8-rendering--player-controls)
9. [Configuration Reference](#9-configuration-reference)
10. [File Map](#10-file-map)

---

## 1. Project Overview

Mitosis is a **top-down creature-sandbox video game** (early development): procedurally generated
game worlds with elevation, biomes, and rivers, populated by AI creatures. Grazers form herds,
hunters use solo/pack/swarm/ambush tactics, and three rival factions reshape the world in
competing directions — an emergent, watchable drama the player roams and (eventually) shapes. The
world runs as a fixed-tick background **simulation** — here a game mechanic for a living world, not
a scientific model — decoupled from rendering.

### Design principles

- **Simulation-first**: all game logic lives in ECS systems; rendering only visualizes state.
- **Data-driven species**: behavior is configured through `SpeciesDefinition`; systems
  contain no per-species hardcoding.
- **Cache-efficient SoA**: components are flat arrays indexed by entity ID.
- **Scale via LOD**: distance-based level-of-detail gates per-entity work each tick.
- **Emergent behavior**: complex dynamics arise from simple per-entity rules.

### Lineage & two notable removals

The project went **Python/Arcade → Godot 2D → Godot 3D** (see
[architecture.md](architecture.md) for the full lineage). Two things older docs described
are **gone** from the code:

- **`StatisticalSimSystem`** (chunk-level population math for distant areas) was removed; its
  job is now done by the LOD system. Real entities are simulated everywhere, just at lower
  tick rates when far from the player. (Confirmed: no `StatisticalSimSystem`,
  `ChunkPopulationData`, or `SpeciesPopulation` exists in the codebase.)
- **`TileType.IsWalkable()`** was removed; passability is now elevation/cliff-based (every
  tile is traversable — Mountain and Lava are just slow and hostile).

---

## 2. Architecture Recap

> Full detail in [architecture.md](architecture.md); summarized here for context.

- **EntityManager** (`ECS/EntityManager.cs`): SoA storage, fixed capacity **16,384**,
  **25 component types**, a `ComponentFlags` bitmask per entity, and a per-entity
  `DueThisTick[]` LOD gate.
- **Components** (4 files): Core (`Position`, `Velocity`, `ChunkPosition`); Creature
  (`Species`, `Hunger`, `Energy`, `Age`, `Reproduction`, `SimulationLOD`,
  `TerrainDiscomfort`, `Fear`, `VenomEffect`); Behavior (`Predator`, `Prey`, `Wander`,
  `Renderable`, `Social`, `Terraform`); Faction (`Nest`, `FoodCarrier`, `Spore`, `Growth`,
  `Crystal`, `FaelingPower`, `RangedAttack`).
- **Spatial hash** (`Utils/SpatialHash.cs`): O(1) neighbor queries, owned by `WorldManager`.
- **Simulation loop**: `GameManager` runs a fixed-timestep accumulator at **20 TPS**. System
  execution order (see [architecture.md](architecture.md#system-execution-order)) is
  LOD → Movement → SpatialHashUpdate → TerrainDiscomfort → Hunger → Grazing → Wander →
  Herding → Separation → Collision → Hunting → Fleeing → Carrion → Aging → Reproduction →
  Terraform → TileRegeneration → Nest → Spore → Crystal → (EcosystemLogger).

### Coordinate model

The simulation runs entirely on an **abstract integer grid** `(x, y)`. The 3D renderer maps
grid coordinates + elevation to world space via `Utils/GridCoordinates.cs`
(`VertexToWorld3D`): grid Y → world **−Z**, elevation → world **+Y**, and odd grid rows are
shifted half a tile (`SmoothRowOffset`) so terrain triangulates into an offset/hex-like mesh.
`WorldToGrid` does the inverse for input. Gameplay is unaffected by the 3D rendering.

---

## 3. World Generation

Terrain is generated per chunk by `World/TerrainGenerator.cs`, with a global river pre-pass
by `World/RiverMapper.cs`. All noise is Godot `FastNoiseLite`, `SimplexSmooth`, FBM.

### Noise layers

| Layer | Freq | Octaves | Seed offset | Purpose |
|-------|------|---------|-------------|---------|
| Elevation | 0.003 | 4 | +0 | Base topography (height map) |
| Moisture | 0.002 | 4 | +1000 | Wet/dry regions (kept in scale with elevation/temperature so coherent desert/rainforest/bog REGIONS can form; normalised with a slight dry bias (×0.45) and contrast-stretched (~1.81) so the Arid/Bog extremes raw FBM starves actually occur, tilted dry-ward) |
| Temperature | 0.005 | 2 | +5000 | Large-scale climate gradient |
| Warp X / Warp Y | 0.008 | 2 | +7000 / +8000 | Domain warping (organic boundaries) |
| Landmark | 0.04 | 2 | +9000 | Feature placement (oases, clearings, caves) |
| Detail | 0.045 | 3 | +11000 | Surface relief added to the **rendered** elevation (not classification) |
| Roughness | 0.006 | 2 | +13000 | Low-freq mask: which regions are rugged vs smooth |
| Ridge | 0.010 | 3 (ridged) | +15000 | Sharp ridgeline crests added to the **base** elevation |
| Orogeny | 0.003 | 2 | +16000 | Low-freq belt mask: where mountain ranges form |
| Cliff | 0.0044 | 2 | +17000 | Low-freq mask: terraced mesa/bluff regions (rendered elevation) |

- **Domain warping**: elevation/moisture/temperature are sampled at coordinates offset by the
  warp noise (**amplitude 12 tiles** by default; lower = calmer boundaries), reducing blobby
  artifacts. Frequencies/amplitudes here are defaults — the key ones are tunable via GameManager
  exports (see Configuration Reference).
- **Base elevation** = warped FBM **+ ridged mountain ranges**: a ridged fractal's crests
  (cubed, so only the crest line lifts), gated by the low-frequency Orogeny belt mask and an
  upland mask (`smoothstep` over elevation 0.50–0.64), add up to `RidgeAmplitude` (0.2). Ranges
  therefore rise as a few connected chains out of existing highlands — they classify as
  Mountain/Ice, cool with altitude, and shed rivers. `TerrainGenerator.SampleBaseElevation` is
  the **single authority** for this value: the RiverMapper builds its full-world flow map with
  it and chunk generation reads that cached map back, so terrain and hydrology cannot drift.
- **Surface detail**: the Detail noise — scaled by the Roughness mask and a per-biome
  `TileType.GetRuggedness()` factor (mountains rugged, plains smooth, water flat), and faded out
  over water — is added to the **stored/rendered** elevation only; classification uses the base
  elevation, so biome boundaries and water levels are unaffected.
- **Terraced cliffs**: where the Cliff mask is strong (and above the shore band), the stored
  elevation is quantised into flat treads joined by short steep risers (`CliffStepHeight` 0.16
  per step, top 15% of each band carries the riser), with surface detail damped so treads read
  flat. Like detail this never touches classification or rivers; creatures feel the risers as
  strong slope resistance (mesas, bluffs, stepped valley sides).
- **Elevation range**: noise normalized to **0.0–1.0**.
- **Temperature model**: `temp = 0.48·noise + 0.52·latitudeGradient` (latitude runs
  `worldY / worldSizeTiles`, top cold → bottom warm); high altitude cools via
  `temp −= max(0, elevation − 0.65) · 1.5`, then clamped 0–1.

### Hydrology → climate coupling (two-way biomes)

Classification is no longer a one-way function of independent noise fields — the generated
water features and relief feed back into the moisture that biomes are classified from
(all applied before `DetermineTileType`, and **stored**, so the palette, terraform nudges,
and renderer checks all see the same values):

- **Riparian halo**: every river/lake tile radiates moisture into surrounding land
  (chamfer-propagated, linear falloff ~0.022/tile): base 0.10 per river tile
  (+0.02 per unit of flow, capped), 0.15 around lakes — a ~5-tile corridor along streams,
  wider along big rivers. Wet climates grow wetland/bog margins along rivers; dry climates
  get green riparian corridors, and the oasis landmark reads this hydrology-aware moisture
  (riverside deserts sprout oases).
- **Delta fans**: a river tile at shore elevation touching the sea seeds a 0.30 boost
  (~13-tile fan), and the tidal-marsh classification rule (below) turns the fan into
  Wetland down to the waterline.
- **Drainage**: slopes shed climate moisture while flats hold it
  (`moisture += clamp((0.025 − slope)·2.5, −0.20, +0.08)`, centred on a typical slope so the
  world's moisture budget is unchanged) — swamps/bogs settle into flat lowland basins,
  hillsides dry toward forest/scrub.

### Tile classification (elevation → moisture → temperature)

| Elevation | Result |
|-----------|--------|
| < 0.30 | DeepWater |
| 0.30–0.40 | ShallowWater (**Reef** if temp > 0.62 and elev > 0.33) |
| 0.40–0.80 | biome bands by temperature (below) |
| > 0.80 | Mountain (**Ice** if temp < 0.22; **Lava** if temp > 0.55 and moisture < 0.32) |

There is **no elevation beach band** — shores are a post-pass in `GenerateChunk`. Land tiles
within **2 tiles** (chamfer distance, `RiverMapper.GetOceanDistance`) of sea water become
shore, **typed by climate**: `Wetland` (marshy shore/mangrove — also how river-delta fans
reach the waterline) when moisture > 0.68, else `Sand`. Frozen coasts (temp < 0.25) get no
beach (ice/tundra runs to the water), and steep coasts (elev ≥ 0.55 at the sea) keep their
biome as rocky cliff shoreline. Distance-based width means beaches stay narrow on flat
worlds (the old 0.40–0.43 band grew huge Sand rings at low elevation frequencies).

Mid-elevation biome bands:

- **Arctic** (temp < 0.22): Ice (high moisture / low elev) else Tundra.
- **Cold temperate** (0.22–0.40): Wetland > 0.72 · Taiga > 0.54 · Steppe > 0.33 ·
  Tundra > 0.17 · else Ice.
- **Temperate** (0.40–0.60): Bog > 0.76 · Wetland > 0.65 · Forest > 0.50 · Grass > 0.33 ·
  Shrubland > 0.22 · Steppe > 0.11 · else Arid.
- **Warm temperate** (0.60–0.75): Wetland > 0.70 · Forest > 0.54 · Savanna/Grass > 0.36 ·
  Shrubland > 0.24 · Dirt > 0.13 · else Arid.
- **Tropical** (> 0.75): Jungle > 0.62 · Savanna > 0.40 · Dirt > 0.26 · Sand > 0.15 · else Arid.

### Rivers & lakes (RiverMapper, global pre-pass)

Runs once before chunk generation, on a full-world base-elevation map built with the shared
`SampleBaseElevation` (so rivers see the ridged ranges and align exactly with the chunks):

1. **Sources**: high-elevation tiles (elev **0.68–0.80**), highest first, spaced ≥ **18**
   tiles apart, ~65% randomly accepted, capped at **worldSizeTiles/4 × `RiverDensity`**
   sources (clamped 4–480) — the per-area budget stays constant across world sizes, and
   `TerrainRiverDensity` scales taste (default 0.4; 1 = the original dense network).
2. **Tracing**: steepest descent across the **6 offset-row hex neighbors** (separate
   even/odd-row neighbor sets), accumulating a flow count per tile, until reaching ocean
   (elev < 0.40) or the world edge (outflow). Candidates are limited to true descents, but
   the choice among them is jittered by a deterministic per-tile dither scaled to the
   world's mean slope — on near-flat terrain pure steepest descent degenerates into the hex
   layout's row-parity bias (straight runs with staircase kinks); the dither turns those
   near-ties into natural meanders.
3. **Depressions**: a stuck trace flood-fills a lake (water rise ≤ **0.04**, ≤ **80** tiles),
   then overflows to continue downstream.
4. **Marking**: flow ≥ **1** → River; flow ≥ **3** → widened to hex neighbors. Land tiles
   adjacent to river/lake become **Wetland** banks. Marking runs all the way down to the
   **waterline (0.40)** — it previously stopped at 0.45, which severed every river from the
   sea across the Sand shore band (the long-standing "rivers end before the ocean" bug).
5. **Chunk overrides**: river/lake markers override any dry land tile — including Sand shores
   and Ice (an arctic river stays continuous across a sheet) — but never Mountain/Lava or
   existing water; Wetland banks apply to ordinary spawnable land only.
6. **Moisture feedback**: the mapper also emits the riparian/delta moisture-boost field and
   slope queries used by the hydrology→climate coupling (§ above).

### Landmark post-processing (per chunk)

| Feature | Source | Condition | Result |
|---------|--------|-----------|--------|
| Oasis | Arid/Sand | landmark > 0.7 and moisture > 0.35 | Grass |
| Forest clearing | Forest | landmark < −0.65 | Grass |
| Taiga clearing | Taiga | landmark < −0.68 | Steppe |
| Jungle clearing | Jungle | landmark < −0.70 | Savanna |
| Permafrost spot | Tundra | landmark > 0.72 | Ice |
| Dry patch | Shrubland | landmark > 0.74 | Steppe |
| Surface cave | Mountain | landmark > 0.75 and elev < 0.85 | Grass |

### Elevation, passability & cliffs

- Each `Chunk` stores a per-vertex `float[,]` elevation array (0–1).
  `WorldManager.GetElevation(x, y)` returns a **barycentric-interpolated** height at any
  float position (matching the rendered mesh surface).
- There is **no tile-based walkability**. Difficulty comes from elevation:
  - **Movement cliff block** (`MovementSystem`): a non-flying creature **cannot move** to a
    destination whose elevation differs by more than **0.28** (hard block).
  - **Uphill slope resistance** (`MovementSystem`): speed is scaled by
    `max(0.25, 1 − rise×8)` when climbing.
  - **Spawn cliff avoidance** (`WorldManager.GetSpawnablePositionsForSpecies`): ground species
    won't spawn where a cardinal neighbour differs by more than **0.18**.
  - Flying and aquatic species bypass these checks.

### Tile nutrition (grazing economy)

Tiles carry per-tile fertility (0–1) up to a per-biome `NutritionCap`. Herbivores consume it; it
regrows slowly, driving migration. Stored per-tile in `Chunk`.

**Water carries fertility too** — plankton, not pasture. Water is deliberately still excluded
from `IsGrazeable`, so land herbivores can't treat a lake as a meadow; only species that list a
water tile in their `FeedTiles` draw on it. Before this, water had a cap of 0 and fish fed from
an infinite flat supply, so a shoal could only ever be limited by predation — which is exactly
why an inland pond with no shark or penguin anywhere near it filled solid with fish. The
gradient (Reef 1.0 > ShallowWater 0.7 > River 0.45 > DeepWater 0.2) also gives shoals a reason
to hold on the shelf, where their predators can reach them, instead of dispersing into the deep.

| Parameter | Value |
|-----------|-------|
| Max nutrition | 1.0 |
| Consume rate | 0.02 / grazing tick (`GrazeConsumeRate`); water feeders use `FeedConsumeRate` |
| Regen rate | 0.0005 / tick — see TileRegenerationSystem for how the passes are batched |
| Grazeable start | at the tile's `NutritionCap` (Tundra 0.25, Arid 0.2 — sparse) |

**Fertility-coupled breeding** (`BreedingNutritionSensitivity`, 0–1): the chance to reproduce is
scaled by the parent tile's fraction of its own cap. At 1.0 (Fish) rich water breeds a shoal
back fast after predation and exhausted water barely breeds at all — a population brake that
works with no predator present, which is what a landlocked pond needs.

### World parameters

| Parameter | Value |
|-----------|-------|
| Chunk size | 32×32 tiles |
| World size | **36×36 chunks (1152×1152)** — the standard test/default config |
| Tile size | 16 (world units per tile) |
| Elevation height scale | 64 (world units of lift per elevation unit, 3D) |
| World seed | 0 = random each run; non-zero = reproducible |

---

## 4. Terrain Types

20 tile types (`World/TileType.cs`). Values below are exact from `TileTypeExtensions`. Every
tile is traversable; "Water" tiles and Mountain/Lava are simply slow and uncomfortable.

| Tile | Speed | Discomfort | Avoidance | Cover | Grazeable | Spawnable | Terraformable |
|------|-------|-----------|-----------|-------|-----------|-----------|---------------|
| DeepWater | 0.25 | 12.0 | 0.95 | 0 | – | – | – |
| ShallowWater | 0.40 | 5.0 | 0.75 | 0 | – | – | – |
| River | 0.35 | 8.0 | 0.80 | 0 | – | – | – |
| Reef | 0.35 | 6.0 | 0.70 | 0 | – | – | – |
| Sand | 0.70 | 1.0 | 0.30 | 0 | – | yes | yes |
| Dirt | 0.90 | 0.2 | 0.05 | 0 | – | yes | yes |
| Shrubland | 0.95 | 0.0 | 0.02 | 0.10 | yes | yes | yes |
| Grass | 1.00 | 0.0 | 0.00 | 0 | yes | yes | yes |
| Savanna | 1.05 | 0.0 | 0.00 | 0.05 | yes | yes | yes |
| Forest | 0.85 | 0.0 | 0.05 | 0.20 | yes | yes | yes |
| Taiga | 0.80 | 0.4 | 0.10 | 0.20 | yes | yes | yes |
| Jungle | 0.60 | 0.3 | 0.10 | 0.40 | yes | yes | yes |
| Wetland | 0.75 | 0.5 | 0.15 | 0.15 | – | yes | yes |
| Bog | 0.55 | 0.8 | 0.25 | 0.15 | – | yes | yes |
| Arid | 0.80 | 0.5 | 0.15 | 0 | yes* | yes | yes |
| Steppe | 0.90 | 0.8 | 0.12 | 0 | yes | yes | yes |
| Tundra | 0.50 | 2.0 | 0.40 | 0 | yes* | yes | yes |
| Ice | 0.45 | 4.0 | 0.60 | 0 | – | – | – |
| Mountain | 0.35 | 15.0 | 0.80 | 0 | – | – | – |
| Lava | 0.20 | 20.0 | 0.95 | 0 | – | – | – |

\* Arid (0.15) and Tundra (0.2) are grazeable at reduced starting nutrition for
desert/arctic herbivores. Cover is used by ambush hunters for stealth.

### Terraform shift chains

Faction terraformers move tiles along these chains (`ShiftWetter/Drier/Balanced`):

```
Wetter:   Arid→Sand→Dirt→Shrubland→Grass→Forest→Wetland→Bog
          Tundra→Steppe→Taiga→Forest · Savanna→Jungle
Drier:    Bog→Wetland→Forest→Grass→Shrubland→Dirt→Sand→Arid
          Taiga→Steppe→Tundra→Arid · Jungle→Savanna
Balanced: extremes converge on Grass (Arid→…→Grass, Bog→…→Grass,
          Tundra→Steppe→Grass, Taiga→Forest, Savanna→Grass, Jungle→Forest)
```

---

## 5. Species System

All behavior is driven by `Species/SpeciesDefinition.cs` — a data class with **100+
configurable properties** (identity, movement, hunting + ambush, fleeing, fear, survival,
reproduction, social + pack roles, terrain, trophic/mass, grazing, terraform, faction
spore/nest/crystal, AoE + growth, venom, visuals, and `StatVariation`). Species register in
`Species/SpeciesRegistry.cs` and are looked up by name-hash (`GetId(name) =
name.GetHashCode()`); **`SpeciesRegistry.cs` is the authoritative source for exact tuning.**

There are **29 species**: 5 generalists, 21 biome-specific, and 3 factions.

### Roster

| Species | Type | Mass | Wander / Hunt | Tactic | Notable |
|---------|------|------|---------------|--------|---------|
| Deer | Herbivore | 4.0 | 0.03 / – | – | Herd; main wolf prey |
| Rabbit | Herbivore | 1.0 | 0.04 / – | – | Herd; frail (low HP), fast-breeding; panics |
| Wolf | Carnivore | 3.5 | 0.06 / 0.12 | PackCoordinated | Prefers Deer/Rabbit |
| Fox | Carnivore | 2.0 | 0.05 / 0.11 | Ambush | Rabbit specialist; stealth pounce + scavenges |
| Crocodile | Carnivore | 8.0 | 0.02 / 0.08 | Ambush | Semi-aquatic; water stealth + pounce |
| Fish | Herbivore | 0.5 | 0.05 / – | – | **Aquatic**; strips water-column fertility (`FeedConsumeRate`) and breeds in proportion to it (`BreedingNutritionSensitivity` 1.0); shoals on shelf/reef; calorie-dense (`NutritionValue` 18) so one is a real meal |
| Shark | Carnivore | 10.0 | 0.04 / 0.22 | Solo | **Aquatic** apex; very fast + long detection (HuntRange 24); Penguin/Turtle, with Fish only as `FallbackPrey` below 45% hunger; prefers DeepWater by steering, tolerates the shallows |
| Otter | **Omnivore** | 1.2 | 0.03 / 0.14 | Solo | Semi-aquatic freshwater fish specialist (`ExclusivePrey` = Fish); works the **shallows and rivers, not the open deep** — that boundary is what makes deep water a fish refuge; dens ashore to breed (`BreedingTiles`) and flees to water when hunted; **territorial with a wide SocialRadius (18)**, which keeps it sparse enough to thin a shoal rather than eat it out; itself prey for wolves/foxes/bears/crocs |
| Frog | Herbivore | 0.3 | 0.03 / – | – | Wetland/Bog; panics |
| Turtle | Herbivore | 6.0 | 0.015 / – | – | Semi-aquatic; very slow; freezes |
| Elk | Herbivore | 7.0 | 0.025 / – | – | Large grassland herd |
| Boar | **Omnivore** | 4.5 | 0.035 / 0.09 | PackCoordinated | Grazes + hunts; defensive |
| Bear | Carnivore | 12.0 | 0.02 / 0.09 | Ambush | Burst charger; hunts Deer/Elk/Boar; fishes shallows |
| Hawk | Carnivore | 1.5 | 0.06 / 0.15 | Solo | **Flying**; hunts small prey |
| Lizard | Herbivore | 0.4 | 0.05 / – | – | Desert (Sand/Arid/Dirt) |
| Scorpion | Carnivore | 0.8 | 0.02 / 0.07 | Ambush | **Venom**; desert |
| Camel | Herbivore | 8.0 | 0.025 / – | – | Desert; very low hunger decay |
| Snake | Carnivore | 1.2 | 0.03 / 0.10 | Ambush | **Venom**; desert/scrub |
| Penguin | **Omnivore** | 1.5 | 0.025 / 0.10 | Solo | Semi-aquatic seabird: **feeds from water tiles** (`FeedTiles`, like a diving forager) and opportunistically hunts Fish (`ExclusivePrey` = Fish); strongly water-comfortable so it stays at sea to feed; huddle herd that breeds on the cold coast; itself prey to Arctic Fox / Polar Bear / Shark (dual predator+prey, like Boar). |
| Polar Bear | Carnivore | 14.0 | 0.02 / 0.10 | Solo | Semi-aquatic; arctic apex |
| Arctic Fox | Carnivore | 1.8 | 0.05 / 0.12 | Solo | Tundra/Steppe |
| Musk Ox | Herbivore | 10.0 | 0.02 / – | – | Tundra herd; defensive |
| Monkey | Herbivore | 1.2 | 0.05 / – | – | Jungle/Forest; erratic |
| Parrot | Herbivore | 0.4 | 0.055 / – | – | **Flying**; tropical |
| Jaguar | Carnivore | 7.0 | 0.04 / 0.12 | Ambush | Semi-aquatic; jungle stealth |
| Tapir | Herbivore | 5.0 | 0.025 / – | – | Tropical; semi-aquatic; panics |
| **Shroomer** | Terraformer | 2.5 | 0.02 / – | – | Spore reproduction; AoE + thorns; grows to 4× |
| **Sectid** | Terraformer | 0.5 | 0.06 / 0.13 | Swarm | Nest breeding; carries food; hibernates when prey-starved |
| **Faeling** | Terraformer | 3.0 | 0.09 / – | – | Crystal-spawned; unhuntable; starvation-immune |

Trait flags: **Flying** = Hawk, Parrot · **Aquatic** = Fish, Shark · **Semi-aquatic** =
Crocodile, Turtle, Penguin, Polar Bear, Tapir, Jaguar · **Omnivore** (predator+prey) = Boar,
Penguin · **Venom** = Scorpion, Snake · **Ambush** = Crocodile, Scorpion, Snake, Jaguar, Fox, Bear ·
**PackCoordinated** = Wolf, Boar · **Swarm** = Sectid · **Factions** = Shroomer, Sectid, Faeling.

`ExclusivePrey` (a hard prey-list filter) lets a specialist hunt only listed species — when a
Penguin *does* hunt, it targets only Fish (though its staple food is passive water-column
foraging via `FeedTiles`, §5 roster). Most predators are opportunists (no list); `PreferredPrey`
is only a soft scoring bias.

### Mass-based hunting

Predators can only take prey within a mass ratio:
- **Solo**: `prey.BodyMass ≤ predator.BodyMass × SoloHuntMaxRatio`
- **Pack/Swarm**: `prey.BodyMass ≤ baseMass × packSize^PackHuntMassExponent × SoloHuntMaxRatio`

So a Fox (mass 2) solos Rabbits but not Deer; a Wolf pack scales up to take Deer; a Sectid
swarm scales by colony size and can threaten large predators.

### Trophic sketch

```
Wolf → Deer, Rabbit      Fox → Rabbit       Crocodile → prey at water's edge
Shark → Penguin/Turtle (Fish only when hungry)   Bear → Deer/Elk/Boar   Hawk → Rabbit/Frog/Lizard/Fish

What a predator *can* take is bounded by mass (`BodyMass × SoloHuntMaxRatio`, times a pack bonus);
what it *prefers* is set by the payoff term in §6.7. The two together are what makes the trophic
layers hold: a solitary Fox (gate 2.0) simply cannot take a Deer (4.0), while a Bear (gate 18)
can take anything but will pass over a lizard for an elk several times further away.
Fresh water: Fish → Otter, Crocodile   (the inland shoal's predators; sharks/penguins are marine only)
Deep water is a refuge: no land or bank-dwelling hunter follows a fish into it, so a cropped
shoal always has a reservoir to recover from. Only Sharks, Penguins and Crocodiles reach there.
Cold web:  Fish → (Penguin) → Arctic Fox / Polar Bear / Shark   (Penguin both eats and is eaten)
Faeling (ranged) → Sectid, Shroomer
Shroomer (AoE)   → Sectid, Faeling
Sectid (swarm)   → spores + any living creature
```

---

## 6. Core Simulation Systems

> Behavior summaries; exact constants live in source. LOD-gated systems skip distant
> entities via `if (!em.DueThisTick[entity]) continue;`.

### 6.1 LOD System — `LODSystem.cs` (not gated)

Runs first each tick. Maps each entity to its spatial-hash cell (cell size ~32) and caches one
distance-to-player per cell (avoids per-entity `sqrt`), assigns a tier with hysteresis, and
writes `DueThisTick[]`. Visible radius is derived from camera zoom (floored at 16 tiles).
Five tiers (distance relative to visible radius `v`):

| Tier | Range | Tick interval |
|------|-------|---------------|
| Full | < 1.15·v | 1 (every tick) |
| High | < 2.3·v | 2 |
| Medium | < 3.45·v | 4 |
| Low | < 5.75·v | 10 |
| Minimal | beyond | 20 |

Downgrades require crossing the boundary by 10% (hysteresis) to avoid oscillation. Countdown
is decrement-first (`TicksUntilUpdate--`, due when ≤ 0), so newly spawned / re-tiered entities
process immediately at any tier. Rate-sensitive systems multiply per-tick deltas by
`SimulationLOD.TickInterval` so a throttled entity ages/starves at the correct rate.
**Invariant**: a resource producer (Grazing) and its consumer (Hunger) must share a gate
level, or distant entities starve unfairly. Every spawn site constructs `SimulationLOD` with an
explicit level (so `TickInterval` starts at 1, never 0), and `LODSystem` repairs any
`TickInterval ≤ 0` even when the tier doesn't change — a zero interval would otherwise feed a
divide-by-zero into rate math (it once produced `NaN` hunger that spread through the food web).

### 6.2 Movement — `MovementSystem.cs` (gated)

Per tick: clamps creature velocity to **0.25 tiles/tick** (player excluded); resolves the tile
speed via **`TerrainProfile.Speed`** (see §6.x Terrain Profile) — a species' own per-tile value
**REPLACES** the tile's intrinsic grip (so a Shark is fast in deep water where a Deer would
crawl), falling back to the tile base for unlisted tiles; applies **uphill slope resistance**
`max(0.25, 1 − rise×8)` and a **hard cliff block** (non-flying creatures can't cross an elevation
jump > **0.28**), sampling elevation via `WorldManager`; attempts diagonal then axis-aligned
sliding; clamps to world bounds; updates `ChunkPosition`; then damps creature velocity by
**0.85/frame** (micro-drift below 0.01 zeroed). Flying creatures bypass slope/cliff; the player
bypasses clamp/damping. **Creatures off their element** (`TerrainProfile.IsImpassable` — aquatic
on land, insect in water) move at 5% speed — they flounder in place and suffocate/drown (§6.3)
rather than chasing prey/corpses across the wrong terrain.

**World edge = barrier.** A creature whose step would cross the border has that velocity
component **reflected** inward (buffer 0.5 tiles), and every terrain-steering sampler maxes its
tile aversion with `WorldManager.EdgeAversion` — a ramp to 1 across the outer **3 tiles**. Both
halves are needed: clamping the position alone stopped creatures at the border without ever
changing where they were trying to go, so they pressed into it indefinitely. It hit swimmers
hardest, because out of bounds `GetTile` answers **DeepWater**, which reads as "more open sea,
keep going" to precisely the species that live in it — land animals were incidentally repelled by
the same answer.

### 6.x Terrain Profile — `Species/TerrainProfile.cs` (resolver, not a system)

The single resolver for how a species relates to a tile, consulted by Movement / Wander /
Fleeing / Hunting so terrain behaviour is consistent (it replaced three divergent water-handling
code paths). Three dimensions:

- **`Speed`** — movement multiplier, **REPLACE** semantics: a species' own per-tile value
  overrides the tile's intrinsic grip; unlisted tiles use the tile base. (Specialists fast in
  their element, slow where unsuited.)
- **`IsImpassable`** — the **hard** element barrier (aquatic ↔ land, insect ↔ water). Enforced as
  very strong steering aversion + the 5% flop speed + drowning/suffocation — strong avoidance,
  not a movement wall, so "only accidental shoring/drowning" emerges without trapping entities.
- **`SteerAversion`** `[0..1]` — species-aware steering dislike (aquatic avoid land, semi-aquatic
  fine in water, insects avoid water, land animals standard), used by all movement steering.
  A species' `TerrainAversionModifiers` are consulted on **every** tile including water, which is
  how a preference *within* an element is expressed: a Shark reads DeepWater 0 / ShallowWater 0.3,
  so it patrols the deep but hunts the shelf freely. Fish invert it (shelf 0, deep 0.25) and shoal
  where their predators can reach them.
- **`DiscomfortRate`** — species-aware discomfort accrual per tick, the input to §6.3. Element-
  aware: the wrong element is maximally uncomfortable, the **home element (water for aquatic and
  semi-aquatic species) costs nothing**, everything else takes the tile baseline, then the
  species' `TerrainComfortModifiers` are added and the result floored at zero.
- **`Concealment`** — per-tile camouflage (override else tile `GetCoverBonus`), reducing the
  species' detectability in both hunting (harder to target) and fleeing (noticed from closer).

Aversion and discomfort are deliberately different things: aversion is a **pull** (where would I
rather be), discomfort is an **intolerance** (how long can I stand it here). Habitat preference
belongs in aversion. Routing it through discomfort instead is what stranded sharks and fish: the
tile table rates open water as punishing, their negative comfort modifiers only partly cancelled
it, and the leftover positive rate accumulated until both species were permanently "escaping" —
in their own feeding grounds, with hunting disabled the whole time.

`IsSubmerged` (water **plus Reef**) is what the aquatic element checks use; `IsWater` — which
governs where land creatures drown and cannot spawn — deliberately excludes coral. A shark over a
reef is not beached.

See `docs/terrain-handling-audit.md` and `docs/terrain-profile-design.md`.

### 6.3 Terrain Discomfort — `TerrainSystems.cs` (gated)

Discomfort **settles toward a level** rather than piling up: each tile has an equilibrium of
`rate × 20` (capped at 3× the species' threshold), approached at 5% of the remaining gap per
tick and shed at the species' `DecayRate` once on better ground. So a tile is either tolerable
forever or drives a creature off within a few dozen ticks, decided by its rate — Wetland (0.5)
settles at ~0.2 of a typical threshold and a herd simply grazes there; Tundra (2) settles at ~0.8
and pushes animals on; Mountain (15) pins to the ceiling in about four ticks.

Under the old pure accumulation, **any** net-positive rate reached the ceiling eventually, so
"mildly disliked" and "lethal" differed only in how many ticks they took to lock a creature into
a permanent escape (which also disables hunting — see §6.x tolerance). That is the bug behind
both the 4000%-discomfort wolves and the sharks fleeing their own sea.

The accrual rate is resolved by `TerrainProfile.DiscomfortRate` (§6.2), so aquatic species feel
nothing in water. Flying creatures ignore discomfort entirely. Hungry herbivores on non-grazeable
**or stripped** tiles get extra `GrazingPressure` (scaled by hunger, at full strength once the
tile is bare — a dead pasture is as useless as bare rock). This is the **soft** terrain
preference — overridable by hunger/fear (a pressed Rabbit will cross a river it normally avoids).
Also tracks **drowning/suffocation**:
a creature in the wrong element (depth-aware — land creatures wade shallow water but drown in
deep, insects drown in any water, aquatics suffocate on land; aquatic/semi-aquatic never drown)
takes energy damage after a grace period (`WrongElementGraceTicks` / `WrongElementDamageRate`).
Both the grace counter and the damage are **LOD-compensated (`× tickMult`)** so a beached shark
suffocates at the same real-time rate at every LOD tier (without it, a coarse-tier aquatic roamed
the land near-immortally); health barely resists it (floor 0.7× damage — you can't out-HP a lack
of air). Predators also stay in their element while hunting via `HuntingSystem.SteerForHabitat`
(sharks steer off land, land waders always steer off DEEP water even mid-pounce), so the drowning
consequence only bites when a creature genuinely strays. The **hard** element barrier is a
separate concept (`TerrainProfile.IsImpassable`, §6.x).

### 6.4 Hunger — `SurvivalSystems.cs` (gated)

Decays hunger (× tick interval, × global `HungerDecayScale`; hibernating Sectids run at 10%
metabolism — see §7.2); starvation drains energy by `StarvationDamage`; zero energy →
death. Skips structures (Nests/Crystals) and starvation-immune species (Faelings). Regenerates
energy when not starving and off combat cooldown (`RegenCooldown`). Applies active
`VenomEffect` damage-over-time. Faeling death passes 50% power to its crystal.

`HungerDecayScale` (0.6) is a single global dial that slows starvation for every species at
once. Predators — which die almost entirely of starvation between kills — gain the most
survival runway and breeding headroom; continuously-grazing herbivores sit near full
regardless, so the imbalance isn't worsened. It is the primary lever for predator carrying
capacity.

### 6.5 Grazing — `SurvivalSystems.cs` (gated)

Herbivores on grazeable tiles consume tile nutrition (0.02/tick) and gain food scaled by what
remained (the consumed/requested ratio is guarded against a zero tick interval, which would
otherwise be `0/0 = NaN`); omnivores (Boar) graze and hunt; faction species feed on their
`FeedTiles`. Capped at max hunger.

**`FeedTiles` with a `FeedConsumeRate`** (Fish on water) work exactly like grazing: the tile's
fertility is stripped and food is paid in proportion to what was actually there, so a shoal eats
its patch of water down and has to move on. With no `FeedConsumeRate` the tile is an
inexhaustible supply — still the right model for a subsistence floor (Shroomers on their own
barren swamp, Penguins skimming the water column), but not for a species the water has to carry.
Everything downstream keys off the same question — foraging (`WanderSystem.DrawsFertility`),
food scoring, and the grazing-pressure term in terrain discomfort — so depleted water pushes a
shoal out the same way bare pasture pushes a herd.

**Fungivory (Shroomer-bloom control).** A species flagged `IsFungivore` (Boar — the primary,
widest reach; plus Rabbit, Lizard, Monkey) also consumes the nearest Shroomer **spore or
immature Shroomer** (`Growth.CurrentScale ≤ FungivoreMaxScale`, default 2.0, below the AoE/thorn
danger band) within `FungivoreFeedRadius` each feed tick, restoring `FungivoreFeedAmount` hunger
(spores are half a mouthful). This is grazing-adjacent, **not** hunting — no attack, no thorn
damage, no rally machinery — so it's a clean natural check on Shroomer blooms (the eater culls
sprouts while feeding itself). Consumption is a discrete meal, so it is *not* LOD-scaled; eaten
immature Shroomers are logged as kills (bloom control shows up in the events CSV), spores aren't
(too numerous). Grown Shroomers are immune — surviving to maturity is what earns their thorns/AoE.

### 6.6 Wander — `WanderSystem.cs` (gated)

Primary idle movement, with **angular interpolation** for smooth turning: turn rate
`Clamp(0.4 / bodyMass, 0.06, 0.3)` (heavy = ponderous, light = nimble), speed held constant
through turns. Random direction changes; **species-aware** terrain look-ahead (1.5 tiles) avoidance sampling 8
directions (via `TerrainProfile.SteerAversion`, so aquatics avoid *land* not water — fish no
longer wander or forage toward shore); **roaming** (long-distance travel when hungry predators find no prey, when a
hungry grazer has no food underfoot, or when herbivores are overcrowded) with hunger-scaled
speed and arrival at 5 tiles; **directed foraging** — a hungry grazer (or FeedTile species)
aims its roam at the best nearby food by sampling 8 directions out to its roam distance,
scoring tiles by remaining nutrition (grazers) or FeedTile presence, biased toward closer
food and never steering into hostile terrain. **Predators get the same directed seek toward
their `HuntTerrain`** (tiles where their prey concentrate — Penguin/Shark → water, Scorpion →
desert) when hungry with no prey in range, instead of roaming blind (fixes specialists starving
inland of their food); **hysteresis
discomfort escape** (enter at discomfort ratio > 0.6, exit < 0.1; direction-change chance cut
~70% while escaping). Faelings instead seek damaged (non-Grass) terrain. Skips entities
fleeing or actively hunting.

### 6.7 Hunting — `HuntingSystem.cs` (gated)

The most complex system, dispatched by `HuntingTactic`:

| Tactic | Species | Behavior |
|--------|---------|----------|
| Solo | Hawk, Shark, Polar Bear, Arctic Fox, Penguin | Direct chase |
| PackCoordinated | Wolf, Boar | Leader/Flanker/Disruptor roles, phased convergence |
| Swarm | Sectid | Colony rush, no retreat, counts all nearby kin, targets anything |
| Ambush | Crocodile, Scorpion, Snake, Jaguar, Fox, Bear | Build stealth → pounce burst |

Hunting is **opportunistic**: urgency scales from starving (1.0) to well-fed, and a predator
only stops hunting at ≥ 95% hunger. Target selection (spatial-hash query) scores by distance,
preferred-prey bias, a generic terrain penalty, a **species-specific terrain-comfort penalty**
(e.g. Sectids avoid prey in Wetland), and **prey concealment** (`TerrainProfile.Concealment` —
camouflaged prey such as a Rabbit in forest or Arctic Fox in snow inflate their score, so a
hunter only locks on when close, effectively shrinking detection range over matching terrain);
cannibalism, unhuntable targets, and species outside a predator's `ExclusivePrey` list (Penguin
→ Fish only) are excluded; land predators reject targets across water (both in target selection
and the wide `TrackingRange` scent scan). A hunter with `SporeHuntBias > 0` (Sectids, the
anti-bloom faction) multiplies the score of Shroomer spores/immature Shroomers by `1 − bias`, so
a swarm eats a bloom out before it fortifies rather than chasing the nearest random prey — grown
Shroomers still fail the mass gate, so the bias can't lure a swarm onto an elder.

> **`ExclusivePrey` covers the rally path too.** The specialist filter is applied in proactive
> target selection *and* the defensive counter-attack/rally path — the latter deliberately drops
> the usual mass/hunger gates for self-defense, and once dropped `ExclusivePrey` with it, so a
> Fish-only Penguin used to hunt down whatever bit it (killing its own predators). A specialist
> now flees off-diet threats instead. Species with no exclusive list (all packs/swarms) are
> unaffected, so a Sectid colony still mobs an attacker.

**Attack landing**: a strike lands when in `AttackRange` and the attack cooldown has elapsed.
The gate is `CurrentCooldown <= 0` and the per-tick decrement is clamped at 0 — at reduced LOD
the decrement subtracts `tickMult` (> 1), which previously overshot 0 into a stuck negative so
the `== 0` gate never re-fired and the predator paced its prey forever without hitting (prey
appeared "invulnerable"). This was the root of the long-standing prolonged-push bug.

**Prey payoff** (`GoodMealFraction` 0.25, `MinMealFraction` 0.05): a hunt costs about the same
effort whatever it catches, so what separates good prey from bad is how much of the predator it
actually feeds. Scoring used to be pure distance, which is why an apex predator spent its life
running down whatever happened to be nearest — a Bear chasing lizards while elk grazed past. Each
candidate's score is now multiplied by `1 / (nutrition / (MaxHunger × 0.25))`, clamped to 20×, so
a low-value target must be proportionally *closer* to win. For a Bear a lizard is ~5% of a meal
and must be inside ~23% of an elk's distance to be chosen; for a Hawk the same lizard is ~11% of
a meal and the threshold is ~34%. The effect therefore grades with predator size on its own.

Crucially this is a **preference, not a gate** — nothing is ever excluded for being small, so a
specialist whose entire diet is small game (Hawk, Penguin, Otter) never starves on principle, and
a predator in a world containing only small prey still hunts it normally. `HungerFlattensPayoff`
(0.85) erases most of the preference as hunger rises: a desperate predator takes what it can
reach. Measured on a stacked test (small game deliberately placed nearer than large), bears went
from 39% to 73% big-game targeting.

**Prey eligibility is one shared test** (`IsEligiblePrey`) used by target acquisition *and* hunger
tracking. It has to be: they used to disagree. Tracking asked only "is it prey, is it my own
species, is there water in the way" and ignored the mass ceiling, `ExclusivePrey`, the fallback
tier and the give-up blacklist — so a Fox (mass gate 2.4) would track a **Turtle** (mass 6), walk
all the way to it, be refused by acquisition on arrival, and then press into it indefinitely: no
target set, no attack, no damage either way. Several solitary foxes each picking the same nearest
turtle produced the observed pile-up. Because no target is ever assigned on that path, none of the
abandon logic below could rescue it — the only fix is to never start walking.

**Tracking has its own stall check**, mirroring the pursuit one: a tracked animal that stops
getting closer is blacklisted (`track_unreachable`) instead of being walked at forever.

**Hard pursuit ceiling** (`HuntMaxPursuitTicks`, 600): no single quarry may be pursued longer
than this, whatever the progress. The stall clock alone can be kept alive indefinitely by prey
that drifts into reach and back out — a fox pacing a shoreline sets a new closest-approach every
time the fish swims to its side of the pond. Note this deliberately does **not** stop a land
predator fishing where it genuinely can: fish that come into wading depth get hunted normally,
and only the truly unreachable ones hit the ceiling.

**Unreachable prey** (`HuntApproachStallTicks`, 200): a pursuit that never gets closer is
abandoned and logged as `unreachable`. This is the gap the engagement clock above leaves open —
it only runs *in* striking distance, so a target the hunter can neither reach nor lose had no
timeout of any kind. A Shark that locked onto a Penguin standing a few tiles inland paced the
shoreline indefinitely: never engaged (no progress check), never 3× hunt range away (no escape
check). The hunter tracks its closest approach and gives up if it fails to improve on it by
`HuntApproachMinGain` (0.5 tiles) within the window. Ambushers are exempt — lying in wait
without approaching *is* their tactic — as are pack members holding a coordination station.

**Refuge flight** (`FleeingSystem`, `RefugeSearchRadius` 10 / `RefugePull` 0.55): a semi-aquatic
animal caught ashore biases its flee vector toward the nearest water, which its land-bound
pursuers won't enter. Blended with the away-from-predator direction, never replacing it, so it
can't run into the threat. Without it an Otter flees in a straight line across open ground from a
faster Wolf — a race it always loses, which is why otters vanished from ordinary worlds shortly
after being introduced.

**Desperation** (`DesperationHunger`, default 0.3 — `FleeingSystem`): below that hunger ratio a
prey animal's effective flee radius shrinks toward 35% of normal, so it feeds in ground a
well-fed individual would refuse. It is the other half of the same deadlock: the penguin above
starved on the ice rather than enter water a shark was in. Risk beats certainty.

**Fallback prey** (`FallbackPrey` / `FallbackPreyHunger`): prey a predator only bothers with
when genuinely hungry. `PreferredPrey` can only express "I like these", one flat multiplier over
the whole list — so a Shark rated a Fish and a Penguin identically and simply ate whichever was
nearer, which is always the fish. Sharks and Polar Bears now take Fish only below 45% hunger,
leaving the base of the web to be thinned rather than cropped flat.

**Performance**: the across-water test (`GetWaterFractionOnPath`, which samples tiles along the
predator→prey line) is the dominant per-candidate cost and scales with prey *density*, not
predator count. It's a pure rejection filter, so it's deferred to only the candidate that would
actually become the new best (O(best-improvements) calls instead of O(candidates) — behaviour-
equivalent). Each scan also caps the number of scored candidates (`MaxHuntCandidates`, 48) to
bound work in pathological prey crowds. (Pack/isolation/flanking queries don't sample water and
are left for a future consolidation pass into a single neighbour query per predator per tick.)

**Target viability re-evaluation**: predators abandon prey they can't actually bring down rather
than fixating — but only once **engaged** (within striking distance, `max(AttackRange×4, 4)`
tiles). While still closing the gap, dealing no damage is expected and does **not** count as
failure (counting it made predators bail mid-approach and never land a hit, so they starved
without ever killing). Once engaged, every `HuntReevalInterval` (150 ticks) a hunter checks how
much of the target's HP it removed; near-zero progress (`HuntMinProgress`) means it can't
out-damage the prey, so it gives up. It also bails the instant a counterattacking target costs
it `HuntSelfDamageBailFraction` (40%) of its own HP. A given-up target is blacklisted for
`HuntAvoidDuration` (600 ticks); a *collective* no-progress stall also clears the shared pack
target (an individual peeling off because it's hurt does not). **Pack members are exempt from
the check while coordinating** (a Pack role in a non-`Converging` phase): a flanker circles
within engage range without striking, so judging it by its own damage output falsely aborted
the whole hunt right before the convergence kill landed. Prey that genuinely can't be caught is
still abandoned via the across-water/`3× hunt range` escape, and ambush hunters skip the
progress check entirely (their stalk is legitimately damage-free). This is what stops a Sectid
swarm chasing a Crocodile forever without breaking ordinary pack hunts.

Velocity uses mass-based agility (`Clamp(1.5/bodyMass, …)`). **On kill** the killer eats first —
an immediate "prime cut" of `prey.EffectiveNutrition × KillNutritionShare` (0.6, body-mass
scaled). The body still drops as a corpse (via the death hook, §6.12) for packmates and
scavengers, so pack sharing of the remainder stays emergent. Without this eat-on-kill bonus
predators relied solely on slow corpse-scavenging and starved before they could breed.

- **Pack flanking**: Leader holds at distance and triggers an all-in **Converging** phase (on
  timeout, a flanker reaching the prey's far side, or prey isolation); Flankers circle behind;
  Disruptors rush/retreat to scatter the herd.
- **Defensive rally (call-to-action)**: a pack/swarm predator picks a threat either **reactively**
  (a recent attacker — `Predator.LastAttacker`, remembered `RallyAlertDuration` ticks) or
  **proactively** (an idle member scans hunt-range for a predator of another species that is hunting
  it or a groupmate, or intruding close — so a stalker is spotted *before* it bites). If it has at
  least `RallyAllyThreshold` groupmates within `PackCoordinationRadius` and the threat is still close
  (`RallyRangeMult` × hunt range), it broadcasts the threat as the group target — summoning the
  colony/pack to **mob** it (bypassing the usual prey mass/hunger gates; defense overrides
  food-hunting, and even a sated member joins). Committing also stamps the reactive timer so the
  group keeps rallying without re-scanning each tick, and a committed mobber **won't flee its mob
  target** (FleeingSystem keeps it engaged so it isn't stuck oscillating between approach and
  flight) — if the mob is actually losing, the viability/self-damage bail clears the target and
  fear then takes over. Lone members with too few allies skip the rally and flee instead. This is
  what makes a Sectid colony swarm a Fox that's picking it off (or closing in) rather than getting
  eaten one by one; a bite also wakes a dormant Sectid.
- **Ambush**: stealth accrues while moving slowly (semi-aquatic ambushers gain extra on water);
  at/above the stealth threshold within pounce range, the predator bursts at `PounceSpeedMult`
  speed and `PounceAttackMult` damage, then reverts to a slow open chase. Pouncing resets
  stealth (prey can see it again — see Fleeing).

### 6.8 Fleeing — `FleeingSystem.cs` (gated)

Scans predators (with stealth levels), then for each prey computes a weighted flee-away
direction. **Stealth-aware detection**: a predator's effective detection range drops to ~10% of
normal at full stealth, **stacked with terrain concealment** (`TerrainProfile.Concealment` — an
Arctic Fox in snow or Scorpion in desert is noticed from closer, so camouflaged ambushers can
close the gap). Fear accumulates with proximity (vigilance window ~100 ticks of slower decay
after a threat leaves) and the response triggers at fear ratio > 0.5:

| Response | Behavior |
|----------|----------|
| Flee (default) | Run at `FleeSpeedMultiplier`; boost at fear > 0.7; terrain-aware |
| Freeze | Slow toward a stop at high fear (Turtle) |
| Panic | Erratic high-speed movement (Rabbit, Frog, Tapir) |
| Defensive | Stand ground in a herd (Boar, Musk Ox) |

If terrain discomfort is high and fear isn't extreme, the creature prioritizes escaping bad
terrain over the predator. Flee steering is **species-aware** (`TerrainProfile.SteerAversion`),
so a fleeing fish treats *land* as the hazard and won't bolt ashore and strand — the fix that
ended the chronic late-game fish suffocations.

### 6.9 Herding — `HerdingSystem.cs` (gated)

Manages groups, leader election (by age/lifespan score), cohesion (toward leader, ideal follow
distance 1.5 pack / 2.0 herd), and alignment (match leader velocity). Pack boost 3× (×2 again
during an active hunt). Suspended while fleeing, while solo-hunting, or while Wander's escape
flag is set (prevents tug-of-war jitter); packs keep cohesion during a hunt.

### 6.10 Separation & Collision — `SpatialSystems.cs` (gated)

Separation pushes same-species apart within `SeparationRadius` (force `1 − dist/radius`).
Collision resolves physical overlap for all entities (radius from `Renderable.Size × 0.5 /
tileSize`), pushing overlapping pairs apart by half the overlap each.

### 6.11 Aging — `SurvivalSystems.cs` (gated)

Increments age (× tick interval); death at `MaxLifespan`. Skips structures. Faeling death
passes power to its crystal.

### 6.12 Carrion — `CarrionSystem.cs`

Corpses persist and are scavenged — predators feed **after** a kill, not instantly. Corpse
creation is tied to the *event of dying itself*: `EntityManager.OnEntityDying` fires for every
entity the instant before it's destroyed (from **any** cause — predation, starvation, age,
drowning, faction AoE/ranged, or anything added later), and the registered handler calls
`CarrionSystem.SpawnCorpse`. That leaves a corpse entity (`Carrion` component + a dark Diamond
renderable) holding an edible nutrient pool scaled by **body size** (`EffectiveNutrition`) and
**condition** (hunger ratio at death — a well-fed animal leaves more; floor 0.4×) and growth scale
(big Shroomers). The condition factor guards a non-finite hunger ratio (falls back to the floor)
so a `NaN` can't propagate into corpse nutrition. `SpawnCorpse` self-filters structures/spores/
Faelings, so the hook stays generic and the ECS core needs no knowledge of gameplay. The corpse
holds the *remainder* after the killer's prime cut (§6.7) — pack "sharing" of it is emergent.

Each tick the system (1) **decays** corpses: a grace period (~200 ticks fresh), then nutrition
rots away (~1400 ticks), shrinking the renderable; depleted corpses are removed. `IsDepleted`
treats a non-finite pool as depleted (`!(Nutrition > 0)`) so a stray `NaN` corpse can't become
an immortal scavenger magnet (predators once piled motionless on such corpses and stopped
hunting). Rotting also **fertilises the soil** — a fraction of each tick's decay is added to the
tile's grazeable nutrition (`WorldManager.AddNutrition`), so naturally decomposing remains speed
local regrowth. (2) **Feeds scavengers**: a hungry predator/omnivore/Sectid with no live target
moves to the nearest corpse and eats at a rate set by its `MaxHunger` (sated at 95%). The
corpse-seek radius **scales with hunger** for dedicated hunters — small when well-fed, full
(`SeekRadius`) when starving — so a fed predator won't abandon the hunt to wander to a distant
carcass. Land/insect foragers won't path to a corpse across water (they'd drown). **Sectids**
range out to the full radius always and weigh a corpse and live prey equally — diverting to a
corpse only when it's nearer than their current target. A short feed-commit on the killer keeps
it on the kill instead of re-hunting; Sectids chop fast into their carrier sacks and `NestSystem`
holds one at the carcass until full, then ferries the load home — so a large kill takes repeated
trips, often the whole colony, to clear.

### 6.13 Reproduction — `ReproductionSystem.cs` (gated)

Standard reproduction for non-faction species. Requires: under population cap, off cooldown,
mature, hunger ≥ threshold, energy ≥ threshold, local same-species density below
`2× PreferredGroupSize` (within `1.5× SocialRadius`), and a global population-pressure roll
(linear ramp: 100% pass at ≤50% of cap → 0% at cap). Deducts hunger/energy, resets cooldown,
spawns `OffspringCount` offspring on a valid tile near the parent (offspring start at 60%
hunger, 100% energy, age 0). Both the spawn loop and `EntityFactory.SpawnCreature` re-check the
hard cap, and offspring of a species disabled via the species toggle (§9) are refused here too.

**Breeding grounds.** A species with `BreedingTiles` may only breed while the **parent** stands on
one of them, and `WanderSystem` gives a fed, mature adult that is off them a roam target on the
nearest patch (`seek_breeding_ground`, which overrides the roam cooldown the way lethal ground
does). This is what makes a semi-aquatic species genuinely amphibious rather than a land animal
that tolerates water: a Penguin's `HuntThreshold` (0.75) sits above its `ReproHungerThreshold`
(≈0.63), so it fishes until full, then hauls out onto the ice to breed, then goes back to sea when
the hunger cost of breeding lands — the colony's whole rhythm falls out of two numbers instead of
being scripted.

---

## 7. Faction Systems

### 7.1 Shroomer / Spore — `SporeSystem.cs`

**Feeding — Shroomers rely on and impact land fertility, more than herbivores.** A Shroomer's
*growth* fuel is tile nutrition (`FertilityConsumeRate` 0.06/tick on grazeable tiles — 3× a
grazer — gaining `FertilityFeedNutrition × richness`), so a bloom only grows where there's
fertility to strip and depletes the land as it does; combined with its **wetter** terraform,
it converts fertile grass/forest into barren (nutrition-0) swamp and must advance into fresh
ground — a pasture-destroying, self-limiting front (the same carrying-capacity mechanism that
caps herbivores). Wet-tile `FeedNutrition` is now only a **subsistence floor** (0.06 ≈ hunger
decay): a Shroomer survives on its own swamp but stays too hungry to keep spreading there.
(Previously 0.5 = "can't starve on wet tiles", which removed all fertility reliance and let
Shroomers grow immortally on self-made swamp — the root of the monoculture.)

Mature, well-fed Shroomers on wet tiles occasionally spread spores (cost: a fraction of max
hunger; spread probability is LOD-compensated as `1 − (1−p)^tickInterval`). Spores accrue
moisture on wet tiles and wither on dry ones; at the transform threshold they become a new
Shroomer. Shroomers **grow** continuously (to 4× scale), scaling via an S-curve (smoothstep):
- **AoE attack** — radius/damage grow from near-harmless at birth to devastating at full
  growth; a slow passive pulse plus a faster combat pulse when attacked. Targets
  Sectids/Faelings.
- **Thorn defense** — melee attackers take growth-scaled counter-damage.

**Self-limiting (so a bloom can be pushed back, not just grow immortally — `MaxLifespan` is
50 000 ticks, longer than a run, so age never checked them).** Each due Shroomer counts its
same-species neighbours once and that drives three effects:
- **Crowding attrition** — above `CrowdingLimit` neighbours within `CrowdingRadius`, energy
  drains `CrowdingDamage × (neighbours − limit)` per tick (LOD-compensated), so a dense mat
  self-thins into advancing fronts rather than a solid immortal field.
- **Drought death** — a mature Shroomer on a tile drier than its `SporeMoistureThreshold`
  drains `DroughtDamage`/tick and cannot spread, so **faction terraforming toward dry biomes
  (Sectid drying, Faeling neutralizing) actively collapses a bloom** — the in-world weapon
  that makes the faction territory war real. Both attrition deaths log as `environment_death`
  (`drought`/`crowding`).
- **Spread suppression** — local saturation scales spread to zero as neighbours climb
  `CrowdingLimit → CrowdingSaturation` (no open ground to colonise), and a **global
  population-pressure factor** (mirrors ReproductionSystem's 50→100% ramp) caps spread as the
  shared population fills — a safety ceiling so Shroomers can't convert the whole cap even if
  the in-world crowding/drought knobs are mistuned.

### 7.2 Sectid / Nest — `NestSystem.cs`

Sectids carry food from kills to the nearest nest (`FoodCarrier`, faster `CarryingSpeed`,
self-feed on delivery). Nests have **3 stages** (each adds a larva slot, up to 3); accumulating
`FoodPerSpawn` (18) starts a larva timer that hatches a new Sectid. After 3 spawns a nest
advances a stage; at max stage with surplus food it founds nearby nests or a distant colony.
Sectids cannot graze — they must hunt, so the economy is tuned so a kill is worth funding a
larva: `MaxCarryFood` 12 (big kills aren't wasted at the carry cap) against an 18-food larva.

**Swarm kills & field-feeding.** As a `Swarm` hunter, a Sectid's effective hunt mass scales with
nearby same-species count, letting a dense swarm bring down prey far larger than one insect. For
the pile-on to actually land hits, `AttackRange` (1.3) exceeds `SeparationRadius` (1.0) — earlier
the reverse held them spaced just outside biting distance, so a swarm chased big prey but only
landed the occasional jab. On a kill, the killer carries the colony's share toward the nest while
packmates are fed **directly in the field** (hunger only, no carrier fill) — so the swarm sustains
itself between kills and only one Sectid peels off to deliver, instead of the whole pack ferrying
tiny loads back. Starter colonies spawn denser (`sectidsPerNest` 8, `SectidShare` 0.10) so a swarm
reaches kill-mass immediately.

**Hibernation (food floor).** A pure consumer faction starves wholesale when prey is scarce, so
a hungry Sectid (below 35% hunger) that detects no huntable prey within 30 tiles for ~600 ticks
retreats to its nest and goes **dormant**: metabolism drops to 10% (`HungerSystem` reads the
`FoodCarrier.IsHibernating` flag), it idles motionless, and stops registering as a threat
(`FleeingSystem` excludes it, so prey neither flee nor fear a sleeping swarm) while
`WanderSystem`/`HuntingSystem` skip it.

**Nest-based terraforming.** Sectids no longer reshape terrain while roaming — they moved too
fast to leave a meaningful imprint (unlike slow, lingering Shroomers). Instead, the *nest*
terraforms its surroundings on each **hatch**: when a larva emerges, a concentrated burst of
moisture nudges is applied around the (stationary) nest, so the colony's drying influence
actually accumulates in one place. `TerraformSystem` skips all nest-breeders (and abandoned
nests don't terraform — it's gated on a successful hatch). It **wakes** the moment
huntable prey strays within 14 tiles (or it picks up food), rejoining the hunt. This keeps a
minimal viable colony alive through prey troughs as a defensive, ambush-from-the-nest posture
instead of the swarm wandering off to die.

### 7.3 Faeling / Crystal — `CrystalSystem.cs`

Crystals are indestructible structures (energy 999999), each linked to one Faeling. When its
Faeling dies, a crystal waits `CrystalSpawnDelay` then spawns a replacement carrying inherited
power. Faelings gain **power** from kills (+5) and balanced terraforming, boosting growth and
ranged damage (`BaseDamage + power×0.5`); 50% of power passes to the crystal on death (lineage
compounds). They are immune to starvation, terrain discomfort, and predation, and attack
Sectids/Shroomers at range (LOD-gated).

**Keeper of order (anti-dominance balancer).** Every `150` ticks a Faeling scans
`KeeperSenseRadius` (40) and judges which rival faction is **locally over-dominant**: the
leader needs ≥ `KeeperMinPresence` (5) members nearby and ≥ 1.5× the rival's count (spores
and structures don't count). While a dominance reading is active:
- **Attack preference** — ranged bolts prefer the dominant faction's members over the other's
  (suppress whoever is winning; today that's usually the Shroomer bloom, but it flips to
  Sectid swarms automatically if they surge).
- **Elder-safety** — a keeper never bolt-duels a Shroomer whose growth-scaled AoE reach rivals
  its 12-tile attack range (30 damage/pulse vs 150 HP is how pre-keeper Faelings bled out).
  With max AoE now 14, only near-elders (scale ≳3.2) are skipped; everything younger is
  boltable. Grown Shroomers are left to the siege.
- **Siege patrol** — instead of wandering to any damaged terrain, the keeper roams to a
  **standoff ring** (16 tiles — just outside a full-grown Shroomer's 14-tile AoE) around the
  dominant faction's sensed hotspot, and holds station there. Its balanced terraform then
  dries/restores the substrate the winner depends on — with §7.1's drought mechanic, drying a
  bloom's tiles *kills* the elders bolts can't touch, and restoring Sectid-dried land removes
  the desert their nests spread from. Containment and collapse, not a suicide charge; power
  gained per restored tile compounds the keeper lineage.

With no dominance reading (balanced surroundings), keepers fall back to patrolling toward
damaged (non-Grass) terrain and restoring it, as before.

### 7.4 Terraform summary — `TerrainSystems.cs` (TerraformSystem)

| Faction | Direction | Effect | Radius | Strength | Cooldown | Applied by |
|---------|-----------|--------|--------|----------|----------|------------|
| Shroomer | Wetter | toward Wetland/Bog | 2.0 | 0.03 | 8 | the roaming creature (`TerraformSystem`) |
| Sectid | Drier | toward Arid | 3.0 | 14×0.08/hatch | — | the **nest, on each hatch** (§7.2) |
| Faeling | Balanced | extremes toward Grass | 4.0 | 0.25 | 8 | the roaming creature (`TerraformSystem`) |

Note: `TerraformStrength` is a **probability per cooldown roll** (one random tile in radius gets
a 0.05 moisture nudge on success), not an amount — at the old Faeling 0.03 that was one tile per
~267 ticks, invisibly slow, which is why faction terraforming barely marked the map (raised
2026-07-13; the Sectid nest burst was likewise widened 6×0.05@r1.5 → 14×0.08@r3).

A tile change marks its chunk dirty so the renderer rebuilds that mesh. Three-way conflict:
Shroomers wet the world (helping themselves, hurting Sectids), Sectids dry it, Faelings
rebalance toward Grass. Shroomers and Faelings terraform from the moving creature;
`TerraformSystem` skips nest-breeders (Sectids), whose drying comes from their nests instead.

---

## 8. Rendering & Player Controls

Rendering is 3D (`Rendering/RenderingManager.cs`); it only reads simulation state.

> **Note:** terrain mesh detail and entity-on-terrain positioning are an area of active work;
> specifics here describe the current implementation and may change.

### Terrain

- One `MeshInstance3D` per chunk, built as a triangulated mesh: vertices via
  `GridCoordinates.VertexToWorld3D` (offset rows, elevation on +Y), per-vertex biome colors
  (`Chunk.GetTileColor`), and smooth per-vertex normals.
- Material `Shaders/TerrainDither.gdshader` (`shader_type spatial`, `unshaded`): manual Lambert
  sun shading + screen-space slope-edge darkening (`dFdx/dFdy` on elevation) + PS2-era
  posterization (24 color levels). Sun direction matches the scene `DirectionalLight3D` at
  (−50°, 45°, 0°).
- **Water**: sea vertices (below sea level 0.40) are flattened to a level surface and coloured
  by depth (shallow→deep blue) so the seabed shape isn't visible; lakes/rivers (≥ sea level)
  follow the terrain as shallow water. Movement still uses the real floor elevation. (A single
  global ocean plane — per-water-body levels / fluid are future work.)
- Meshes rebuild only for chunks in `WorldManager.DirtyChunks`; a terraform changes only tile
  colour, so just the colour stream is rebuilt (cached geometry/normals are reused).

### Entities

- One `MultiMeshInstance3D` per shape (13 `ShapeType` values), each a Godot 3D primitive:
  Circle→sphere, Triangle→prism, Square→box, Diamond→flat box, Star→6-sided cylinder,
  Chevron→wing prism, FishShape→capsule, Fin→tall prism, Teardrop→capsule, Crescent→torus,
  Serpent→thin capsule, Mushroom→flattened sphere, Fangs→broad box.
- Each instance sits at its terrain elevation (lifted by half its height; clamped to the water
  surface so aquatic creatures stay visible), rotates to face its velocity, is tinted by
  `Renderable.Color`, and scaled by `Renderable.Size` (× `Growth`). A large `ExtraCullMargin`
  keeps the batch from popping under Godot's culling.
- **Render interpolation**: positions are interpolated between the previous and current sim tick
  by the inter-tick fraction, so fast movers (the player at high speed) glide instead of stepping
  at 20 TPS.

### Camera & controls

The camera is an **orthographic `Camera3D`** in an isometric setup (pitch −45°, yaw 45°) that
smoothly follows the player entity (a yellow sphere). WASD moves the player **camera-relative**
(converted via `GridCoordinates.WorldToGrid`), so "up" is up-screen regardless of rotation.

| Input | Action |
|-------|--------|
| WASD / Arrows | Move player (camera-relative; camera follows) |
| Shift | Sprint (3× speed) |
| Mouse wheel / `+` `-` | Zoom (orthographic size, ~51–2560 world units) |
| F3 | Toggle profiling + per-species population overlay |

### Debug overlay & logging

`GameManager` shows FPS, TPS, entity count, LOD tier counts, and per-category counts (every
0.5s); F3 adds sorted per-system timings (with bars) and a per-species population list.
`EcosystemLogger` (registered as the final `ISystem`) writes to `logs/`:

- `events_YYYYMMDD_HHmmss.csv` (+ `latest_events.csv`): columns `tick, event, species,
  entity_id, x, y, detail`; events `birth`, `reproduce`, `kill`, `hunt_start`, `hunt_fail`
  (detail carries the reason: `not_viable`, `prey_escaped`, `discomfort`, `target_died`),
  `starvation`, `age_death`, `environment_death`, `spore_created`, `spore_matured`, and — when a
  species is tracked (below) — `damage_dealt` / `damage_taken` (melee/thorn/aoe/ranged source).
- `population_YYYYMMDD_HHmmss.csv` (+ `latest_population.csv`): per-species **creature** counts
  every 100 ticks (~5 s at 20 TPS), plus three trailing columns `spores,nests,crystals` for
  structures/spores. Spores carry `Species(Shroomer)` internally and used to be counted as
  Shroomers here (silently inflating every Shroomer figure while the F3 overlay excluded them);
  they are now tallied apart, and `total` is living creatures only — matching the overlay.
- `species_stats_YYYYMMDD_HHmmss.csv` (+ `latest_species_stats.csv`): per-species per-interval
  breakdown — population, births, deaths split by cause (`starve`/`age`/`predation`/
  `environment`), `kills_made`, and average hunger/energy %. A `MASS_PERISH` alert is emitted
  when a species loses ≥ 40% of its population in one interval. (Death-cause ratios here — e.g.
  starvation vs predation — are the key signal for predator-balance tuning.)

  For `kill` events the **`species` column is the victim** and **`detail` is
  `killed_by:<Predator>:<entity_id>`** — to count kills *by* a predator, parse `detail`, not the
  `species` column (which is what got eaten). Easy to misread when building a predator→prey matrix.

All floats are written with `InvariantCulture` so a comma-decimal locale can't corrupt CSV
columns. Set **`TrackSpecies`** (Inspector, §9) to one species' exact name to additionally log
per-entity `TRACKED` snapshots and that species' inbound/outbound combat damage.

At world generation, `WorldSnapshot` writes a one-shot diagnostic set to `logs/`, all sharing
the base name **`world_<ts>_seed…_…ch_ef…_df…_rf…_ra…`**:

- **`.png`** — biome map;
- **`_elev.png`** — stored elevation (sea tinted by depth, land dark→white; ridges, cliffs
  and surface detail are visible since this is the rendered heightfield);
- **`_moist.png`** / **`_temp.png`** — the other two classification parameters (dry tan → wet
  teal; cold blue → hot red), for judging climate bands independently of the biome result;
- **`_spawns.png`** — written after initial spawning: every renderable entity (creatures,
  nests, crystals, player) as a dot in its species colour over a dimmed biome map, for
  validating spawn distribution against the niche-placement rules;
- **`.txt`** — the full worldgen parameter set + per-tile-type biome distribution + a
  river-connectivity line (river tiles / outlet tiles touching the sea, with a warning if
  rivers are severed) + niche-coverage roll-ups/warnings.

### Worldgen preview tool

For parameter exploration without relaunching the game: open **`Scenes/WorldgenPreview.tscn`**
and run it (**F6**). Sliders for every terrain export (seed, world size, elevation/warp,
moisture freq+contrast, ridges, cliffs, detail/roughness) regenerate the map live
(debounced, climate-only — no rivers), and a **Full detail** button runs the exact pipeline
including `PrecomputeRivers` (rivers, lakes, deltas, shores, hydrology moisture feedback) for
the current seed. Views: biome / elevation / moisture / temperature; the stats panel shows
the live biome distribution + niche coverage (same code as the snapshot report) and the
current parameter set for transcribing into the GameManager inspector. It calls
`TerrainGenerator.SampleTile` — the same per-tile function `GenerateChunk` loops over — so
the preview can never drift from what the game generates.

---

## 9. Configuration Reference

`GameManager` exported fields. Defaults in code are the **standard test configuration**
(36-chunk world, 2000 initial, 12000 cap — the setup balance runs use); final production
values TBD once all features are in and compute/render costs are known:

| Field | Default | Description |
|-------|---------|-------------|
| ChunkSize | 32 | Tiles per chunk side |
| WorldSizeChunks | 36 | World is N×N chunks (36 = 1152×1152 tiles) |
| WorldSeed | 0 | 0 = random; non-zero = reproducible |
| TileSize | 16 | World units per tile |
| ElevationHeightScale | 64 | World units of lift per elevation unit (3D) |
| ElevationFrequency | 0.003 | Base elevation frequency (lower = larger landmasses) |
| WarpAmplitude | 12 | Domain-warp swirl in tiles (lower = calmer boundaries) |
| TerrainDetailFrequency | 0.045 | Surface-relief noise frequency |
| TerrainDetailAmplitude | 0.035 | Surface-relief height added to elevation (0 disables) |
| TerrainRoughnessFrequency | 0.006 | Size of rugged vs smooth regions |
| TerrainRoughnessFloor | 0.15 | Min detail in smoothest regions (0–1) |
| TerrainMoistureFrequency | 0.002 | Humid/arid region scale (keep in scale with elevation) |
| TerrainMoistureContrast | 1.81 | Moisture stretch toward wet/dry extremes (1 = raw noise) |
| TerrainRidgeFrequency | 0.010 | Ridgeline scale (lower = longer ranges) |
| TerrainRidgeAmplitude | 0.2 | Ridge crest height added to base elevation (0 = no ranges) |
| TerrainOrogenyFrequency | 0.003 | Mountain-belt mask scale (lower = fewer, larger ranges) |
| TerrainCliffFrequency | 0.0044 | Size of terraced mesa/bluff regions |
| TerrainCliffStrength | 1.0 | Terracing blend in cliff regions (0 = off, 1 = fully stepped) |
| TerrainCliffStepHeight | 0.16 | Elevation per terrace step |
| TerrainRiverDensity | 0.2 | River-source budget scale (1 = the original dense network) |
| TargetTPS | 20 | Simulation ticks/second |
| MaxPopulation | 12000 | Hard entity cap |
| InitialPopulation | 2000 | Starting creatures (incl. faction budgets) |
| HerbivoreRatio | 0.85 | Herbivore share of non-faction creatures |
| CreaturesPerChunk | 2.0 | Spawn-density hint |
| FaelingShare | 0.04 | Faeling (crystal) budget as share of initial pop |
| SectidShare | 0.10 | Sectid (nest) budget as share of initial pop |
| PlayerSpeed / Sprint | 1.0 / 3.0 | Player move speed and sprint multiplier |
| ZoomMin / Max / Speed | 0.1 / 5.0 / 0.15 | Orthographic zoom range and step |
| TrackSpecies | "" | Exact species name to deep-log (per-entity `TRACKED` snapshots + combat); empty = off |
| DisabledSpecies | "" | Species names to exclude this run (comma/newline separated); empty = all enabled |
| DisableFactionSpecies | false | Disable all factions (Shroomer/Sectid/Faeling) for a "no-faction" run |

**Species toggles** (`SpeciesToggle`, configured from the two fields above before spawning): a
disabled species never spawns — `WorldSpawner` filters it from the spawn lists, Faeling crystal
and Sectid nest seeding are skipped, and `ReproductionSystem` refuses it as a runtime safety net.
A disabled faction's spawn budget folds back into the creature budget, so a no-faction run still
spawns a full `InitialPopulation`. Unknown names are validated against the registry and logged as
warnings; the active disable set is printed at startup. Use this for granular balance snapshots
(e.g. ecosystem viability with no factions, or isolating why one species thrives/perishes).

**Initial population split** (`WorldSpawner`): Faeling budget = `InitialPopulation ×
FaelingShare` (spawned as **crystals**, ~1 per 10 Faelings); Sectid budget = `× SectidShare`
(spawned as **nests** + starter Sectids, ~1 nest per 5); the remaining creature budget is split
into terraformers (Shroomers, ~12%), then predators and herbivores by `HerbivoreRatio`, each
species weighted by its `SpawnWeight`. Creatures spawn in groups (alpha at center with higher
leadership), herbivores wide, predators near prey chunks. The player spawns near world center;
`EntityFactory.SetPopulationCap(MaxPopulation)` enforces the hard cap.

---

## 10. File Map

```
godot/
├── Mitosis.sln / Mitosis.csproj / project.godot   # Godot 4.6 C# project
├── Scenes/Main.tscn                               # Node3D root + orthographic Camera3D
├── Shaders/TerrainDither.gdshader                 # terrain vertex-colour + Lambert + slope
└── Scripts/
    ├── ECS/EntityManager.cs                        # SoA storage, 16,384 cap, 25 flags, DueThisTick[]
    ├── Components/
    │   ├── CoreComponents.cs                       # Position, Velocity, ChunkPosition
    │   ├── CreatureComponents.cs                   # Species, Hunger, Energy, Age, Reproduction,
    │   │                                           #   SimulationLOD, TerrainDiscomfort, Fear, VenomEffect
    │   ├── BehaviorComponents.cs                   # Predator, Prey, Wander, Renderable, Social,
    │   │                                           #   Terraform + enums (HuntingTactic, PackRole/Phase,
    │   │                                           #   ShapeType, SocialType, FearResponse, LODLevel…)
    │   └── FactionComponents.cs                    # Nest, FoodCarrier, Spore, Growth, Crystal,
    │                                               #   FaelingPower, RangedAttack
    ├── Systems/
    │   ├── ISystem.cs                              # Process(EntityManager em)
    │   ├── LODSystem.cs                            # cell-based LOD + DueThisTick
    │   ├── MovementSystem.cs                       # velocity, terrain speed, slope/cliff, damping
    │   ├── SpatialHashUpdateSystem.cs              # refresh spatial-hash positions
    │   ├── TerrainSystems.cs                       # TerrainDiscomfort + Terraform + TileRegeneration
    │   ├── SurvivalSystems.cs                      # Hunger + Grazing + Aging
    │   ├── WanderSystem.cs  HerdingSystem.cs       # idle movement / social
    │   ├── SpatialSystems.cs                       # Separation + Collision
    │   ├── HuntingSystem.cs  FleeingSystem.cs      # predator / prey
    │   ├── ReproductionSystem.cs                   # standard offspring
    │   ├── NestSystem.cs  SporeSystem.cs  CrystalSystem.cs  # faction reproduction/combat
    │   └── EcosystemLogger.cs                      # CSV event/population logging
    ├── World/
    │   ├── TileType.cs                             # 20 tile types + extension methods
    │   ├── Chunk.cs                                # tiles, elevation, nutrition, GetTileColor
    │   ├── WorldManager.cs                         # chunks, GetTile/GetElevation, DirtyChunks, SpatialHash
    │   ├── TerrainGenerator.cs                     # noise + warp + ridges/cliffs + temperature + landmarks
    │   ├── WorldSnapshot.cs                        # startup diagnostic: biome-map PNG + params/distribution report
    │   └── RiverMapper.cs                          # flow-based rivers/lakes + hydrology→moisture feedback
    ├── Species/
    │   ├── SpeciesDefinition.cs                    # 100+ property data class
    │   ├── SpeciesRegistry.cs                      # all 28 species
    │   ├── TerrainProfile.cs                       # unified per-species/per-tile resolver (speed/avoid/conceal)
    │   └── SpeciesToggle.cs                        # per-species enable/disable for balance runs
    ├── Rendering/RenderingManager.cs               # 3D chunk meshes + entity MultiMesh3D
    ├── Utils/
    │   ├── GridCoordinates.cs                      # grid ↔ screen/3D-world + row offset
    │   ├── SpatialHash.cs  MathUtils.cs
    ├── GameManager.cs                              # Node3D root: loop, init, 3D scene setup
    ├── EntityFactory.cs                            # entity creation + population cap
    ├── WorldSpawner.cs                             # initial population distribution
    └── PlayerController.cs                         # isometric camera + player input

archived/   # Python prototype (+ requirements.txt)
docs/       # current docs (+ docs/archive for superseded)
logs/       # EcosystemLogger CSV output (runtime)
```

### Adding a component / system

See [architecture.md](architecture.md#adding-components--systems). In short: define the struct
+ `ComponentFlags` + backing array for components; implement `ISystem`, register in
`GameManager._Ready()` at the right order, and LOD-gate with `DueThisTick[]` (keep resource
producers/consumers at the same gate level).

---

*Last updated: June 2026 · Godot 4.6.3 + C# · 3D renderer · 28 species · 19 systems + logger*
