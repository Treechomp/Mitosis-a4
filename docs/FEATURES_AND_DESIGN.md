# Mitosis — Features, Systems & Design Reference

> **Purpose**: Comprehensive reference for the currently implemented features, systems,
> mechanics, and design decisions. Read this before diving into source code.
>
> **Engine**: Godot 4.6.3 with C# · **Rendering**: 3D (`Node3D`, orthographic `Camera3D`)
> **Architecture**: Custom SoA ECS, fixed 20 TPS simulation
> **Last updated**: June 2026
>
> Companion docs: [architecture.md](architecture.md) (high-level design),
> [godot-roadmap.md](godot-roadmap.md) (status & roadmap). Superseded docs are in
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
  Herding → Separation → Collision → Hunting → Fleeing → Aging → Reproduction → Terraform →
  TileRegeneration → Nest → Spore → Crystal → (EcosystemLogger).

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
| Rabbit | Herbivore | 1.0 | 0.04 / – | – | Herd; 2 offspring; panics |
| Wolf | Carnivore | 3.5 | 0.06 / 0.12 | PackCoordinated | Prefers Deer/Rabbit |
| Fox | Carnivore | 2.0 | 0.05 / 0.11 | Solo | Rabbit specialist |
| Crocodile | Carnivore | 8.0 | 0.02 / 0.08 | Ambush | Semi-aquatic; water stealth + pounce |
| Fish | Herbivore | 0.5 | 0.05 / – | – | **Aquatic**; feeds from water |
| Shark | Carnivore | 10.0 | 0.04 / 0.14 | Solo | **Aquatic**; hunts Fish/Turtle |
| Frog | Herbivore | 0.3 | 0.03 / – | – | Wetland/Bog; panics |
| Turtle | Herbivore | 6.0 | 0.015 / – | – | Semi-aquatic; very slow; freezes |
| Elk | Herbivore | 7.0 | 0.025 / – | – | Large grassland herd |
| Boar | **Omnivore** | 4.5 | 0.035 / 0.09 | PackCoordinated | Grazes + hunts; defensive |
| Bear | Carnivore | 12.0 | 0.02 / 0.09 | Solo | Hunts Deer/Elk/Boar |
| Hawk | Carnivore | 1.5 | 0.06 / 0.15 | Solo | **Flying**; hunts small prey |
| Lizard | Herbivore | 0.4 | 0.05 / – | – | Desert (Sand/Arid/Dirt) |
| Scorpion | Carnivore | 0.8 | 0.02 / 0.07 | Ambush | **Venom**; desert |
| Camel | Herbivore | 8.0 | 0.025 / – | – | Desert; very low hunger decay |
| Snake | Carnivore | 1.2 | 0.03 / 0.10 | Ambush | **Venom**; desert/scrub |
| Penguin | Herbivore | 1.5 | 0.025 / – | – | Semi-aquatic; tight huddle herd |
| Polar Bear | Carnivore | 14.0 | 0.02 / 0.10 | Solo | Semi-aquatic; arctic apex |
| Arctic Fox | Carnivore | 1.8 | 0.05 / 0.12 | Solo | Tundra/Steppe |
| Musk Ox | Herbivore | 10.0 | 0.02 / – | – | Tundra herd; defensive |
| Monkey | Herbivore | 1.2 | 0.05 / – | – | Jungle/Forest; erratic |
| Parrot | Herbivore | 0.4 | 0.055 / – | – | **Flying**; tropical |
| Jaguar | Carnivore | 7.0 | 0.04 / 0.12 | Ambush | Semi-aquatic; jungle stealth |
| Tapir | Herbivore | 5.0 | 0.025 / – | – | Tropical; semi-aquatic; panics |
| **Shroomer** | Terraformer | 2.5 | 0.02 / – | – | Spore reproduction; AoE + thorns; grows to 4× |
| **Sectid** | Terraformer | 0.5 | 0.06 / 0.13 | Swarm | Nest breeding; carries food |
| **Faeling** | Terraformer | 3.0 | 0.09 / – | – | Crystal-spawned; unhuntable; starvation-immune |

Trait flags: **Flying** = Hawk, Parrot · **Aquatic** = Fish, Shark · **Venom** = Scorpion,
Snake · **Ambush** = Crocodile, Scorpion, Snake, Jaguar · **PackCoordinated** = Wolf, Boar ·
**Swarm** = Sectid · **Factions** = Shroomer, Sectid, Faeling.

### Mass-based hunting

Predators can only take prey within a mass ratio:
- **Solo**: `prey.BodyMass ≤ predator.BodyMass × SoloHuntMaxRatio`
- **Pack/Swarm**: `prey.BodyMass ≤ baseMass × packSize^PackHuntMassExponent × SoloHuntMaxRatio`

So a Fox (mass 2) solos Rabbits but not Deer; a Wolf pack scales up to take Deer; a Sectid
swarm scales by colony size and can threaten large predators.

### Trophic sketch

```
Wolf → Deer, Rabbit      Fox → Rabbit       Crocodile → prey at water's edge
Shark → Fish, Turtle     Bear → Deer/Elk/Boar    Hawk → Rabbit/Frog/Lizard
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
level, or distant entities starve unfairly.

### 6.2 Movement — `MovementSystem.cs` (gated)

Per tick: clamps creature velocity to **0.25 tiles/tick** (player excluded); multiplies by the
current tile's speed multiplier; applies **uphill slope resistance** `max(0.25, 1 − rise×8)`
and a **hard cliff block** (non-flying creatures can't cross an elevation jump > **0.28**),
sampling elevation via `WorldManager`; attempts diagonal then axis-aligned sliding; clamps to
world bounds; updates `ChunkPosition`; then damps creature velocity by **0.85/frame**
(micro-drift below 0.01 zeroed). Flying creatures bypass slope/cliff; the player bypasses
clamp/damping.

### 6.3 Terrain Discomfort — `TerrainSystems.cs` (gated)

Accumulates discomfort on uncomfortable tiles, decays on comfortable ones. Flying creatures
ignore it. Hungry herbivores on non-grazeable tiles get extra `GrazingPressure` (scaled by
hunger and tile depletion). Also tracks **drowning/suffocation**: a creature in the wrong
element (land creature in deep water, aquatic on land) takes energy damage after a grace
period (`WrongElementGraceTicks` / `WrongElementDamageRate`).

### 6.4 Hunger — `SurvivalSystems.cs` (gated)

Decays hunger (× tick interval); starvation drains energy by `StarvationDamage`; zero energy →
death. Skips structures (Nests/Crystals) and starvation-immune species (Faelings). Regenerates
energy when not starving and off combat cooldown (`RegenCooldown`). Applies active
`VenomEffect` damage-over-time. Faeling death passes 50% power to its crystal.

### 6.5 Grazing — `SurvivalSystems.cs` (gated)

Herbivores on grazeable tiles consume tile nutrition (0.02/tick) and gain food scaled by what
remained; omnivores (Boar) graze and hunt; faction species feed on their `FeedTiles`. Capped
at max hunger.

### 6.6 Wander — `WanderSystem.cs` (gated)

Primary idle movement, with **angular interpolation** for smooth turning: turn rate
`Clamp(0.4 / bodyMass, 0.06, 0.3)` (heavy = ponderous, light = nimble), speed held constant
through turns. Random direction changes; terrain look-ahead (1.5 tiles) avoidance sampling 8
directions; **roaming** (long-distance travel when hungry predators find no prey, or
herbivores are overcrowded) with hunger-scaled speed and arrival at 5 tiles;
**hysteresis discomfort escape** (enter at discomfort ratio > 0.6, exit < 0.1; direction-change
chance cut ~70% while escaping). Faelings instead seek damaged (non-Grass) terrain. Skips
entities fleeing or actively hunting.

### 6.7 Hunting — `HuntingSystem.cs` (gated)

The most complex system, dispatched by `HuntingTactic`:

| Tactic | Species | Behavior |
|--------|---------|----------|
| Solo | Fox, Bear, Hawk, Shark, Polar Bear, Arctic Fox | Direct chase |
| PackCoordinated | Wolf, Boar | Leader/Flanker/Disruptor roles, phased convergence |
| Swarm | Sectid | Colony rush, no retreat, counts all nearby kin, targets anything |
| Ambush | Crocodile, Scorpion, Snake, Jaguar | Build stealth → pounce burst |

Hunting is **opportunistic**: urgency scales from starving (1.0) to well-fed, and a predator
only stops hunting at ≥ 95% hunger. Target selection (spatial-hash query) scores by distance,
preferred-prey bias, a generic terrain penalty, and a **species-specific terrain-comfort
penalty** (e.g. Sectids avoid prey in Wetland); cannibalism and unhuntable targets are
excluded; land predators reject targets across water. Velocity uses mass-based agility
(`Clamp(1.5/bodyMass, …)`). On kill: solo takes all nutrition; packs give the killer
`KillerShareRatio` and split the rest within `PackShareRadius`; Sectids carry a share to nests
(`FallbackNutrition` 40 when a prey's nutrition is unset).

- **Pack flanking**: Leader holds at distance and triggers an all-in **Converging** phase (on
  timeout, a flanker reaching the prey's far side, or prey isolation); Flankers circle behind;
  Disruptors rush/retreat to scatter the herd.
- **Ambush**: stealth accrues while moving slowly (semi-aquatic ambushers gain extra on water);
  at/above the stealth threshold within pounce range, the predator bursts at `PounceSpeedMult`
  speed and `PounceAttackMult` damage, then reverts to a slow open chase. Pouncing resets
  stealth (prey can see it again — see Fleeing).

### 6.8 Fleeing — `FleeingSystem.cs` (gated)

Scans predators (with stealth levels), then for each prey computes a weighted flee-away
direction. **Stealth-aware detection**: a predator's effective detection range drops to ~10% of
normal at full stealth. Fear accumulates with proximity (vigilance window ~100 ticks of slower
decay after a threat leaves) and the response triggers at fear ratio > 0.5:

| Response | Behavior |
|----------|----------|
| Flee (default) | Run at `FleeSpeedMultiplier`; boost at fear > 0.7; terrain-aware |
| Freeze | Slow toward a stop at high fear (Turtle) |
| Panic | Erratic high-speed movement (Rabbit, Frog, Tapir) |
| Defensive | Stand ground in a herd (Boar, Musk Ox) |

If terrain discomfort is high and fear isn't extreme, the creature prioritizes escaping bad
terrain over the predator.

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

### 6.12 Reproduction — `ReproductionSystem.cs` (gated)

Standard reproduction for non-faction species. Requires: under population cap, off cooldown,
mature, hunger ≥ threshold, energy ≥ threshold, local same-species density below
`2× PreferredGroupSize` (within `1.5× SocialRadius`), and a global population-pressure roll
(linear ramp: 100% pass at ≤50% of cap → 0% at cap). Deducts hunger/energy, resets cooldown,
spawns `OffspringCount` offspring on a valid tile near the parent (offspring start at 60%
hunger, 100% energy, age 0). Both the spawn loop and `EntityFactory.SpawnCreature` re-check the
hard cap.

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
`FoodPerSpawn` starts a larva timer that hatches a new Sectid. After 3 spawns a nest advances a
stage; at max stage with surplus food it founds nearby nests or a distant colony. Sectids
cannot graze — they must hunt.

### 7.3 Faeling / Crystal — `CrystalSystem.cs`

Crystals are indestructible structures (energy 999999), each linked to one Faeling. When its
Faeling dies, a crystal waits `CrystalSpawnDelay` then spawns a replacement carrying inherited
power. Faelings gain **power** from kills (+5) and balanced terraforming, boosting growth and
ranged damage (`BaseDamage + power×0.5`); 50% of power passes to the crystal on death (lineage
compounds). Faelings patrol toward damaged (non-Grass) terrain, are immune to starvation,
terrain discomfort, and predation, and attack Sectids/Shroomers at range (LOD-gated).

### 7.4 Terraform summary — `TerrainSystems.cs` (TerraformSystem)

| Faction | Direction | Effect | Radius | Strength | Cooldown |
|---------|-----------|--------|--------|----------|----------|
| Shroomer | Wetter | toward Wetland/Bog | 2.0 | 0.03 | 8 |
| Sectid | Drier | toward Arid | 1.5 | 0.04 | 6 |
| Faeling | Balanced | extremes toward Grass | 3.0 | 0.03 | 8 |

A tile change marks its chunk dirty so the renderer rebuilds that mesh. Three-way conflict:
Shroomers wet the world (helping themselves, hurting Sectids), Sectids dry it, Faelings
rebalance toward Grass.

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
  entity_id, x, y, detail`; events `birth`, `reproduce`, `kill`, `hunt_start`, `hunt_fail`,
  `starvation`, `age_death`, `environment_death`, `spore_created`, `spore_matured`.
- `population_YYYYMMDD_HHmmss.csv` (+ `latest_population.csv`): per-species counts every 100
  ticks (~5 s at 20 TPS).

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
| SectidShare | 0.06 | 0.06 | Sectid (nest) budget as share of initial pop |
| PlayerSpeed / Sprint | 1.0 / 3.0 | – | Player move speed and sprint multiplier |
| ZoomMin / Max / Speed | 0.1 / 5.0 / 0.15 | – | Orthographic zoom range and step |

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
    │   └── RiverMapper.cs                          # flow-based rivers/lakes (hex topology)
    ├── Species/
    │   ├── SpeciesDefinition.cs                    # 100+ property data class
    │   └── SpeciesRegistry.cs                      # all 28 species
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
