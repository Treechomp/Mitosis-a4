# Mitosis — Features, Systems & Design Reference

> **Purpose**: Comprehensive reference for the currently implemented features, systems,
> mechanics, and design decisions. Read this before diving into source code.
>
> **Engine**: Godot 4.6.3 with C# · **Rendering**: 3D (`Node3D`, orthographic `Camera3D`)
> **Architecture**: Custom SoA ECS, fixed 20 TPS simulation
> **Last updated**: June 2026
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

Mitosis is a **top-down 3D ecosystem simulation**: procedurally generated worlds with
elevation, biomes, and rivers, populated by autonomous creatures. Herbivores graze and form
herds, predators hunt with solo/pack/swarm/ambush tactics, and three faction species
terraform the world in competing directions. The simulation runs at a fixed tick rate,
decoupled from rendering.

### Design principles

- **Simulation-first**: all logic lives in ECS systems; rendering only visualizes state.
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
| Elevation | 0.012 | 4 | +0 | Base topography (height map) |
| Moisture | 0.008 | 4 | +1000 | Wet/dry regions |
| Temperature | 0.005 | 2 | +5000 | Large-scale climate gradient |
| Warp X / Warp Y | 0.008 | 2 | +7000 / +8000 | Domain warping (organic boundaries) |
| Landmark | 0.04 | 2 | +9000 | Feature placement (oases, clearings, caves) |
| Detail | 0.045 | 3 | +11000 | Surface relief added to the **rendered** elevation (not classification) |
| Roughness | 0.006 | 2 | +13000 | Low-freq mask: which regions are rugged vs smooth |

- **Domain warping**: elevation/moisture/temperature are sampled at coordinates offset by the
  warp noise (**amplitude 12 tiles** by default; lower = calmer boundaries), reducing blobby
  artifacts. Frequencies/amplitudes here are defaults — the key ones are tunable via GameManager
  exports (see Configuration Reference).
- **Surface detail**: the Detail noise — scaled by the Roughness mask and a per-biome
  `TileType.GetRuggedness()` factor (mountains rugged, plains smooth, water flat), and faded out
  over water — is added to the **stored/rendered** elevation only; classification uses the base
  elevation, so biome boundaries and water levels are unaffected.
- **Elevation range**: noise normalized to **0.0–1.0**.
- **Temperature model**: `temp = 0.6·noise + 0.4·latitudeGradient` (latitude runs
  `worldY / worldSizeTiles`, top cold → bottom warm); high altitude cools via
  `temp −= max(0, elevation − 0.65) · 1.5`, then clamped 0–1.

### Tile classification (elevation → moisture → temperature)

| Elevation | Result |
|-----------|--------|
| < 0.30 | DeepWater |
| 0.30–0.40 | ShallowWater (**Reef** if temp > 0.62 and elev > 0.33) |
| 0.40–0.46 | Sand (beach) |
| 0.46–0.80 | biome bands by temperature (below) |
| > 0.80 | Mountain (**Ice** if temp < 0.22; **Lava** if temp > 0.55 and moisture < 0.32) |

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

Runs once before chunk generation, on a full-world elevation map (same noise + warp):

1. **Sources**: high-elevation tiles (elev **0.68–0.80**), highest first, spaced ≥ **18**
   tiles apart, ~65% randomly accepted, capped at **80** sources.
2. **Tracing**: steepest descent across the **6 offset-row hex neighbors** (separate
   even/odd-row neighbor sets), accumulating a flow count per tile, until reaching ocean
   (elev < 0.40).
3. **Depressions**: a stuck trace flood-fills a lake (water rise ≤ **0.04**, ≤ **80** tiles),
   then overflows to continue downstream.
4. **Marking**: flow ≥ **1** → River; flow ≥ **3** → widened to hex neighbors. Land tiles
   adjacent to river/lake become **Wetland** banks. (Water level 0.40, land level 0.45.)

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

Grazeable tiles carry per-tile nutrition (0–1). Herbivores consume it; it regrows slowly,
driving migration. Stored per-tile in `Chunk`.

| Parameter | Value |
|-----------|-------|
| Max nutrition | 1.0 |
| Consume rate | 0.02 / grazing tick |
| Regen rate | 0.0005 / tick (applied every 4 ticks at 4× — see TileRegenerationSystem) |
| Grazeable start | 1.0 (Tundra starts 0.2, Arid starts 0.15 — sparse) |

### World parameters

| Parameter | Value |
|-----------|-------|
| Chunk size | 32×32 tiles |
| World size | **18×18 chunks (576×576) intended default**; ships as **9×9 (DEBUG)** |
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

There are **28 species**: 5 generalists, 20 biome-specific, and 3 factions.

### Roster

| Species | Type | Mass | Wander / Hunt | Tactic | Notable |
|---------|------|------|---------------|--------|---------|
| Deer | Herbivore | 4.0 | 0.03 / – | – | Herd; main wolf prey |
| Rabbit | Herbivore | 1.0 | 0.04 / – | – | Herd; frail (low HP), fast-breeding; panics |
| Wolf | Carnivore | 3.5 | 0.06 / 0.12 | PackCoordinated | Prefers Deer/Rabbit |
| Fox | Carnivore | 2.0 | 0.05 / 0.11 | Ambush | Rabbit specialist; stealth pounce + scavenges |
| Crocodile | Carnivore | 8.0 | 0.02 / 0.08 | Ambush | Semi-aquatic; water stealth + pounce |
| Fish | Herbivore | 0.5 | 0.05 / – | – | **Aquatic**; feeds from water |
| Shark | Carnivore | 10.0 | 0.04 / 0.22 | Solo | **Aquatic** apex; very fast + long detection (HuntRange 24); Fish/Penguin/Turtle |
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
Shark → Fish, Penguin, Turtle     Bear → Deer/Elk/Boar    Hawk → Rabbit/Frog/Lizard/Fish
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
- **`Concealment`** — per-tile camouflage (override else tile `GetCoverBonus`), reducing the
  species' detectability in both hunting (harder to target) and fleeing (noticed from closer).

**Soft** terrain preference (overridable by hunger/fear) stays in TerrainDiscomfortSystem's
comfort/discomfort. See `docs/terrain-handling-audit.md` and `docs/terrain-profile-design.md`.

### 6.3 Terrain Discomfort — `TerrainSystems.cs` (gated)

Accumulates discomfort on uncomfortable tiles, decays on comfortable ones. Flying creatures
ignore it. Hungry herbivores on non-grazeable tiles get extra `GrazingPressure` (scaled by
hunger and tile depletion). This is the **soft** terrain preference — overridable by hunger/fear
(a pressed Rabbit will cross a river it normally avoids). Also tracks **drowning/suffocation**:
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
and the wide `TrackingRange` scent scan).

**Attack landing**: a strike lands when in `AttackRange` and the attack cooldown has elapsed.
The gate is `CurrentCooldown <= 0` and the per-tick decrement is clamped at 0 — at reduced LOD
the decrement subtracts `tickMult` (> 1), which previously overshot 0 into a stuck negative so
the `== 0` gate never re-fired and the predator paced its prey forever without hitting (prey
appeared "invulnerable"). This was the root of the long-standing prolonged-push bug.

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

---

## 7. Faction Systems

### 7.1 Shroomer / Spore — `SporeSystem.cs`

Mature, well-fed Shroomers on wet tiles occasionally spread spores (cost: a fraction of max
hunger; spread probability is LOD-compensated as `1 − (1−p)^tickInterval`). Spores accrue
moisture on wet tiles and wither on dry ones; at the transform threshold they become a new
Shroomer. Shroomers **grow** continuously (to 4× scale), scaling via an S-curve (smoothstep):
- **AoE attack** — radius/damage grow from near-harmless at birth to devastating at full
  growth; a slow passive pulse plus a faster combat pulse when attacked. Targets
  Sectids/Faelings.
- **Thorn defense** — melee attackers take growth-scaled counter-damage.

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
compounds). Faelings patrol toward damaged (non-Grass) terrain, are immune to starvation,
terrain discomfort, and predation, and attack Sectids/Shroomers at range (LOD-gated).

### 7.4 Terraform summary — `TerrainSystems.cs` (TerraformSystem)

| Faction | Direction | Effect | Radius | Strength | Cooldown | Applied by |
|---------|-----------|--------|--------|----------|----------|------------|
| Shroomer | Wetter | toward Wetland/Bog | 2.0 | 0.03 | 8 | the roaming creature (`TerraformSystem`) |
| Sectid | Drier | toward Arid | 1.5 | 0.04 | 6 | the **nest, on each hatch** (§7.2) |
| Faeling | Balanced | extremes toward Grass | 3.0 | 0.03 | 8 | the roaming creature (`TerraformSystem`) |

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
- `population_YYYYMMDD_HHmmss.csv` (+ `latest_population.csv`): per-species counts every 100
  ticks (~5 s at 20 TPS).
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

At world generation, `WorldSnapshot` also writes a one-shot **`world_<ts>_seed…_…ch_ef…_df…_rf….png`**
(biome map) plus a sidecar **`.txt`** (worldgen parameters + per-tile-type biome distribution +
niche-coverage roll-ups/warnings) to `logs/` — for inspecting what a parameter set produces and
validating that each specialist's niche has enough habitat.

---

## 9. Configuration Reference

`GameManager` exported fields. Defaults in code are tuned **DEBUG** values for lightweight
species-balancing sessions; the intended production defaults are listed alongside (final values
TBD once all features are in and compute/render costs are known):

| Field | Code (DEBUG) | Intended default | Description |
|-------|--------------|------------------|-------------|
| ChunkSize | 32 | 32 | Tiles per chunk side |
| WorldSizeChunks | 9 | ~18 | World is N×N chunks |
| WorldSeed | 0 | 0 | 0 = random; non-zero = reproducible |
| TileSize | 16 | 16 | World units per tile |
| ElevationHeightScale | 64 | 64 | World units of lift per elevation unit (3D) |
| ElevationFrequency | 0.012 | 0.012 | Base elevation frequency (lower = larger landmasses) |
| WarpAmplitude | 12 | 12 | Domain-warp swirl in tiles (lower = calmer boundaries) |
| TerrainDetailFrequency | 0.045 | 0.045 | Surface-relief noise frequency |
| TerrainDetailAmplitude | 0.035 | 0.035 | Surface-relief height added to elevation (0 disables) |
| TerrainRoughnessFrequency | 0.006 | 0.006 | Size of rugged vs smooth regions |
| TerrainRoughnessFloor | 0.15 | 0.15 | Min detail in smoothest regions (0–1) |
| TargetTPS | 20 | 20 | Simulation ticks/second |
| MaxPopulation | 2000 | ~10000 | Hard entity cap |
| InitialPopulation | 500 | ~1500 | Starting creatures (incl. faction budgets) |
| HerbivoreRatio | 0.85 | 0.85 | Herbivore share of non-faction creatures |
| CreaturesPerChunk | 2.0 | 2.0 | Spawn-density hint |
| FaelingShare | 0.04 | 0.04 | Faeling (crystal) budget as share of initial pop |
| SectidShare | 0.10 | 0.06 | Sectid (nest) budget as share of initial pop |
| PlayerSpeed / Sprint | 1.0 / 3.0 | – | Player move speed and sprint multiplier |
| ZoomMin / Max / Speed | 0.1 / 5.0 / 0.15 | – | Orthographic zoom range and step |
| TrackSpecies | "" | "" | Exact species name to deep-log (per-entity `TRACKED` snapshots + combat); empty = off |
| DisabledSpecies | "" | "" | Species names to exclude this run (comma/newline separated); empty = all enabled |
| DisableFactionSpecies | false | false | Disable all factions (Shroomer/Sectid/Faeling) for a "no-faction" run |

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
    │   ├── TerrainGenerator.cs                     # noise + warp + temperature + landmarks
    │   ├── WorldSnapshot.cs                        # startup diagnostic: biome-map PNG + params/distribution report
    │   └── RiverMapper.cs                          # flow-based rivers/lakes (hex topology)
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
