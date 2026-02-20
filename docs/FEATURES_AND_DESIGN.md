# Mitosis - Features, Systems & Design Reference

> **Purpose**: Comprehensive reference for all currently implemented features, systems,
> mechanics, and design decisions. Consult this document before reading source code.
>
> **Engine**: Godot 4.6 with C#
> **Architecture**: Custom ECS with Structure of Arrays (SoA)
> **Last Updated**: February 2026

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Architecture](#2-architecture)
3. [World Generation](#3-world-generation)
4. [Species System](#4-species-system)
5. [Core Simulation Systems](#5-core-simulation-systems)
6. [Faction Systems](#6-faction-systems)
7. [Rendering & Player Controls](#7-rendering--player-controls)
8. [Configuration Reference](#8-configuration-reference)
9. [File Map](#9-file-map)

---

## 1. Project Overview

Mitosis is a **top-down ecosystem simulation** featuring procedurally generated worlds
populated by autonomous creatures. Herbivores graze and form herds, predators hunt in
packs, and three faction species (Shroomers, Sectids, Faelings) terraform the world in
competing directions. The simulation runs at a fixed tick rate independent of rendering.

### Design Principles

- **Simulation-first**: All logic lives in ECS systems. Rendering is a visualization layer.
- **Data-driven species**: All creature behavior is configured through `SpeciesDefinition`.
  No species-specific logic is hardcoded in systems.
- **Cache-efficient SoA**: Components stored as flat arrays indexed by entity ID.
- **Scalability by design**: LOD system, spatial hashing, and population caps from day one.
- **Emergent behavior**: Complex dynamics emerge from simple per-entity rules interacting.

### Project History

The project began as a Python/Arcade implementation (now in `archived/`), which hit a
performance ceiling around 500 entities. After evaluating engines (see
`migration evaluation report.md`), it was migrated to Godot 4 with C# to target 10,000+
entities at 60 FPS. The Godot version is the active codebase.

---

## 2. Architecture

### Entity-Component-System (ECS)

```
EntityManager (SoA)          Systems                    SpeciesRegistry
┌──────────────────┐    ┌──────────────────┐    ┌──────────────────────┐
│ Position[]       │◄───│ MovementSystem   │    │ "Deer" → Definition  │
│ Velocity[]       │◄───│ WanderSystem     │    │ "Wolf" → Definition  │
│ Hunger[]         │◄───│ HungerSystem     │    │ "Shroomer" → ...     │
│ Species[]        │    │ HuntingSystem    │    │ "Sectid" → ...       │
│ Fear[]           │    │ FleeingSystem    │    │ "Faeling" → ...      │
│ ...15+ arrays    │    │ ...14+ systems   │    └──────────────────────┘
│ ComponentFlags[] │    └──────────────────┘
│ Alive[]          │
│ MAX = 16,384     │
└──────────────────┘
```

**EntityManager** (`Scripts/ECS/EntityManager.cs`):
- Fixed capacity: 16,384 entities
- Each entity is an integer ID (index into all component arrays)
- `ComponentFlags` bitmask per entity tracks which components are present
- `Alive` boolean array tracks active entities
- Entity creation returns an ID; destruction marks it dead and recycles the slot

**Component Types** (all C# structs with `StructLayout.Sequential`):

| Category | Components |
|----------|-----------|
| Core | `Position`, `Velocity`, `ChunkPosition` |
| Creature | `Species`, `Hunger`, `Energy`, `Age`, `Reproduction` |
| Simulation | `SimulationLOD`, `TerrainDiscomfort` |
| Behavior | `Wander`, `Predator`, `Prey`, `Fear`, `Social`, `Terraform` |
| Visual | `Renderable` (Color, Size, Shape) |
| Faction | `Nest`, `FoodCarrier`, `Spore`, `Growth`, `Crystal`, `FaelingPower`, `RangedAttack` |

**System Interface**: All systems implement `ISystem` with a `Process(EntityManager em)` method. They iterate over entities matching required component flags. Systems that need additional dependencies (WorldManager, SpatialHash) receive them via constructor injection.

### Spatial Hash

`SpatialHash` (`Scripts/Utils/SpatialHash.cs`) provides O(1) neighbor queries:
- Grid of cells, cell size matches perception radius
- Entities inserted by position, queried by radius
- Used by: HuntingSystem, FleeingSystem, HerdingSystem, SeparationSystem,
  CollisionSystem, NestSystem, SporeSystem, CrystalSystem

### Simulation Loop

GameManager runs a fixed-timestep loop at **20 TPS** (ticks per second):

```
Each Tick (0.05 seconds):
  1.  LODSystem           - Update distance-based fidelity levels
  2.  MovementSystem      - Apply velocity, terrain speed, velocity damping (0.85/frame)
  3.  TerrainDiscomfort   - Accumulate/decay terrain discomfort     [TerrainSystems.cs]
  4.  HungerSystem        - Decay hunger, apply starvation damage   [SurvivalSystems.cs]
  5.  GrazingSystem       - Herbivores/factions feed on tiles       [SurvivalSystems.cs]
  6.  WanderSystem        - Random movement, roaming, terrain escape (hysteresis)
  7.  HerdingSystem       - Social cohesion, alignment, leader following
  8.  SeparationSystem    - Prevent creature overlap within species  [SpatialSystems.cs]
  9.  CollisionSystem     - Physical overlap resolution (all)        [SpatialSystems.cs]
  10. HuntingSystem       - Solo/pack/ambush hunting, flanking, stealth, pounce
  11. FleeingSystem       - Stealth-aware threat detection, fear, escape
  12. AgingSystem          - Increment age, natural death            [SurvivalSystems.cs]
  13. ReproductionSystem  - Spawn offspring when thresholds met
  14. TerraformSystem     - Faction tile modification                [TerrainSystems.cs]
  15. TileRegenerationSystem - Regrow nutrition on grazeable tiles   [TerrainSystems.cs]
  16. NestSystem           - Sectid nest breeding & food delivery
  17. SporeSystem          - Shroomer spore lifecycle & AoE attacks
  18. CrystalSystem        - Faeling crystal management & ranged attacks
```

Order matters: LOD must be first (sets up skip flags). Movement before behavior
(position must be current). Hunger before grazing (must know if hungry). Hunting
before fleeing (predator positions needed for flee calculations).

---

## 3. World Generation

### Terrain Types

| Tile | Walkable | Spawnable | Grazeable | Speed | Discomfort | Avoidance | Cover |
|------|----------|-----------|-----------|-------|-----------|-----------|-------|
| DeepWater | Yes* | No | No | 0.25x | 12.0 | 0.95 | 0 |
| ShallowWater | Yes* | No | No | 0.4x | 5.0 | 0.75 | 0 |
| River | Yes* | No | No | 0.35x | 8.0 | 0.8 | 0 |
| Sand | Yes | Yes | No | 0.7x | 1.0 | 0.3 | 0 |
| Dirt | Yes | Yes | No | 0.9x | 0.2 | 0.05 | 0 |
| Shrubland | Yes | Yes | Yes | 0.95x | 0 | 0.02 | 0.1 |
| Grass | Yes | Yes | Yes | 1.0x | 0 | 0 | 0 |
| Forest | Yes | Yes | Yes | 0.85x | 0 | 0.05 | 0.2 |
| Taiga | Yes | Yes | Yes | 0.8x | 0.4 | 0.1 | 0.2 |
| Wetland | Yes | Yes | No | 0.75x | 0.5 | 0.15 | 0.15 |
| Bog | Yes | Yes | No | 0.55x | 0.8 | 0.25 | 0.15 |
| Arid | Yes | Yes | No | 0.8x | 0.5 | 0.15 | 0 |
| Steppe | Yes | Yes | Yes | 0.9x | 0.8 | 0.12 | 0 |
| Tundra | Yes | Yes | No | 0.5x | 2.0 | 0.4 | 0 |
| Ice | Yes | No | No | 0.45x | 4.0 | 0.6 | 0 |
| Savanna | Yes | Yes | Yes | 1.05x | 0 | 0 | 0.05 |
| Jungle | Yes | Yes | Yes | 0.6x | 0.3 | 0.1 | 0.4 |
| Mountain | No | No | No | 0.05x | 15.0 | 1.0 | 0 |
| Reef | Yes* | No | No | 0.35x | 6.0 | 0.7 | 0 |
| Lava | No | No | No | 0.05x | 20.0 | 1.0 | 0 |

*Water tiles are walkable by all species but very slow and uncomfortable
(high discomfort rates drive creatures away). Crocodiles get species-specific
speed bonuses on water via `TerrainSpeedModifiers`.

### Tile Nutrition (Depletion & Regrowth)

Grazeable tiles (Grass, Forest, Savanna, Jungle, Shrubland, Taiga, Steppe) have per-tile nutrition (0.0-1.0).
Herbivores consume nutrition when grazing; food gained scales with remaining nutrition.
Depleted tiles (< 0.3 nutrition) generate grazing pressure via TerrainDiscomfortSystem,
driving herbivores to migrate to richer areas.

| Parameter | Value |
|-----------|-------|
| Max nutrition | 1.0 |
| Consume rate | 0.02 per grazing tick |
| Regen rate | 0.0005 per tick |
| Depletion pressure threshold | 0.3 (below this, grazing pressure applies) |

### Tile Moisture Scale (for Spore System)

| Tile | Moisture Value |
|------|---------------|
| Bog / Wetland | 1.0 |
| ShallowWater / DeepWater | 1.0 |
| River | 0.95 |
| Reef | 0.9 |
| Jungle | 0.8 |
| Forest / Taiga | 0.7 |
| Ice | 0.5 |
| Grass | 0.4 |
| Shrubland | 0.35 |
| Savanna / Steppe | 0.25 |
| Tundra | 0.2 |
| Dirt | 0.15 |
| Sand | 0.1 |
| Arid / Lava | 0.0 |

### Terraform Shifts

Tiles can be shifted along a moisture axis by faction terraformers:

```
Drier direction:  Bog → Wetland → Forest → Grass → Shrubland → Dirt → Sand → Arid
                  Jungle → Savanna
                  Taiga → Steppe → Tundra → Arid
Wetter direction: Arid → Sand → Dirt → Shrubland → Grass → Forest → Wetland → Bog
                  Savanna → Jungle
                  Tundra → Steppe → Taiga → Forest
Balanced:         Extremes shift toward Grass (center)
                  Bog → Wetland → Forest → Grass
                  Arid → Sand → Dirt → Shrubland → Grass
                  Tundra → Steppe → Grass, Taiga → Forest
                  Savanna → Grass, Jungle → Forest
```

### Generation Algorithm

1. **Noise layers** (Godot FastNoiseLite, SimplexSmooth):
   - Elevation noise: defines height map (frequency 0.02, 4 octaves FBM)
   - Moisture noise: defines biome moisture (frequency 0.03, 3 octaves FBM, seed offset +1000)
   - Temperature noise: regional climate zones (frequency 0.008, 3 octaves FBM, seed offset +5000)
   - Domain warp X/Y: organic biome boundaries (frequency 0.015, 2 octaves FBM, amplitude 12 tiles)
   - Landmark noise: feature placement (frequency 0.04, 2 octaves FBM, seed offset +9000)

2. **Domain warping**: Elevation and moisture noise coordinates are warped by ±12 tiles
   using independent warp noise, eliminating blobby noise artifacts and creating
   organic, irregular biome boundaries.

3. **Temperature**: Combines noise (70% weight) with a latitude gradient (30% weight,
   top=cold, bottom=warm). High altitude reduces temperature by up to 0.525.
   Temperature determines biome zone: Arctic (<0.35), Temperate (0.35-0.6), Tropical (>0.6).

4. **Tile classification** (elevation + moisture + temperature thresholds):
   - Elevation < 0.30 → DeepWater
   - Elevation 0.30-0.40 → ShallowWater (Reef if temperature > 0.7 and elevation > 0.35)
   - Elevation 0.40-0.45 → Sand (beach/coast)
   - Elevation > 0.80 → Mountain (Lava if temperature > 0.65 and moisture < 0.3)
   - Arctic (temp < 0.2): Ice (high moisture/low elev) or Tundra
   - Arctic (temp 0.2-0.35): Tundra (Wetland if moisture > 0.7)
   - Tropical (temp > 0.75): Jungle (moist) → Savanna → Sand → Arid (dry)
   - Warm (temp 0.6-0.75): Jungle/Forest/Savanna/Grass/Sand/Arid (moisture-based)
   - Temperate (temp 0.35-0.6): Wetland/Forest/Grass/Sand/Arid (moisture-based)

5. **Rivers** (flow-based, global pre-pass via `RiverMapper`):
   - Pre-computes a 512x512 elevation map using the same noise + domain warping
   - Selects up to 80 river sources from high-elevation tiles (> 0.68, spaced 12+ apart)
   - Traces each river downhill using steepest descent among 8 neighbors
   - Flow accumulation: each tile counts how many river paths pass through it
   - Tiles with flow >= 3 become River; flow >= 12 widens to adjacent tiles
   - Depressions (no lower neighbor) are flood-filled to form lakes (ShallowWater)
   - Lakes can overflow and continue the river downstream
   - Land tiles adjacent to rivers/lakes become Wetland banks
   - Pre-computation runs once in WorldManager constructor before chunk generation

6. **Landmarks** (post-processing pass per chunk):
   - Oases: Arid/Sand + high landmark noise + moderate moisture → Grass
   - Forest Clearings: Forest + low landmark noise → Grass
   - Jungle Clearings: Jungle + very low landmark noise → Savanna
   - Surface Caves: Mountain + high landmark noise + low elevation → Grass

### World Parameters

| Parameter | Value |
|-----------|-------|
| Chunk size | 32x32 tiles |
| World size | 24x24 chunks (768x768 tiles) |
| Tile size | 16 pixels |
| World seed | 0 (random each run; set non-zero for reproducible worlds) |

---

## 4. Species System

### Overview

All creature behavior is driven by `SpeciesDefinition` - a data class with 90+
configurable properties. Species are registered in `SpeciesRegistry` and looked up
by name hash. Systems read species parameters at runtime; no species-specific logic
is hardcoded.

### Species Categories

| Category | Species | Diet | Social | Reproduction |
|----------|---------|------|--------|--------------|
| Herbivore | Deer | Herbivore | Herd | Standard |
| Herbivore | Rabbit | Herbivore | Herd | Standard (2 offspring) |
| Predator | Wolf | Carnivore | Pack | Standard |
| Predator | Fox | Carnivore | Solitary | Standard |
| Predator | Crocodile | Carnivore | Solitary | Standard (aquatic) |
| Faction | Shroomer | Terraformer | Herd | Spore-based |
| Faction | Sectid | Terraformer | Pack | Nest-based |
| Faction | Faeling | Terraformer | Solitary | Crystal-based |

### Registered Species Details

#### Deer
- **Role**: Primary large herbivore, main prey for wolves
- **Speed**: 0.03 wander, 2.2x flee multiplier
- **Survival**: 240 max hunger, 0.05 decay/tick, 30000 lifespan, mature at 2000
- **Social**: Herds of ~6, cohesion 0.025, alignment 0.015, affinity 0.7
- **Fear**: Threshold 50, accumulation 6/tick, standard flee response
- **Reproduction**: Needs 210 hunger + 80 energy, costs 50 hunger + 30 energy, 600 tick cooldown
- **Terrain**: Prefers grass (1.1x speed), slower in forest (0.75x), grazing pressure 1.5
- **Body mass**: 4.0 (too heavy for fox to solo hunt)
- **Visual**: Green circle, size 8

#### Rabbit
- **Role**: Small fast herbivore, primary prey for foxes
- **Speed**: 0.04 wander (faster than deer), 2.4x flee multiplier
- **Survival**: 180 max hunger, 0.08 decay/tick (high metabolism), 15000 lifespan, mature at 1000
- **Social**: Loose herds of ~4, low cohesion (0.015), low affinity (0.4)
- **Fear**: Very skittish - threshold 30, accumulation 10/tick, **panic** response, long vigilance (200 ticks)
- **Reproduction**: Breeds in pairs - **800 tick cooldown**, **2 offspring**, costs 55 hunger + 35 energy
- **Terrain**: Faster in forest (1.2x), roams shorter distances (40), high grazing pressure (2.0)
- **Body mass**: 1.0 (huntable by all predators)
- **Visual**: Brown teardrop, size 5

#### Wolf
- **Role**: Pack hunter, apex predator of the grasslands
- **Speed**: 0.06 wander, 0.12 hunt speed
- **Combat**: 12 range, 0.8 attack range, 30 damage, 20 tick cooldown
- **Survival**: 210 max hunger, 0.04 decay/tick, 24000 lifespan, mature at 1500
- **Hunting**: Threshold 75% (hunts when below 75% hunger), tracks at 50% hunger over 80 tile range
- **Pack**: Coordination radius 10, share radius 12, killer gets 50%, 70% pack hunter chance
- **Pack roles**: Leader 1.0x speed, flankers 1.1x, disruptors 1.15x
- **Flanking tactics**: Leader holds at distance, flankers circle behind prey,
  disruptors scatter the herd, leader triggers convergence (all-in kill rush).
  Convergence timeout 120 ticks.
- **Social**: Packs of ~3, high cohesion (0.04), high alignment (0.03), affinity 0.7
- **Reproduction**: 1000 tick cooldown, costs 60 hunger + 40 energy
- **Preferred prey**: Deer, Rabbit (0.5 bias)
- **Body mass**: 3.5, solo hunt max ratio 1.2 (can solo up to mass 4.2)
- **Visual**: Gray triangle, size 10

#### Fox
- **Role**: Solitary small predator, rabbit specialist
- **Speed**: 0.05 wander, 0.11 hunt speed
- **Combat**: 8 range, 0.6 attack range, 20 damage, 15 tick cooldown
- **Survival**: 180 max hunger, 0.035 decay/tick, 20000 lifespan, mature at 1200
- **Hunting**: Threshold 75%, tracks at 50% over 60 tile range, long roam (80 tiles)
- **Social**: Solitary (affinity 0.2, group size 1), 10% pack hunter chance
- **Reproduction**: 800 tick cooldown, costs 35 hunger + 25 energy
- **Preferred prey**: Rabbit (0.4 bias)
- **Body mass**: 2.0, solo hunt max ratio 1.0 (can solo up to mass 2.0 - rabbits only)
- **Visual**: Orange triangle, size 7

#### Crocodile
- **Role**: Aquatic ambush predator with stealth and pounce mechanics
- **Speed**: 0.02 wander (very slow on land), 0.08 hunt speed
- **Combat**: 6 range, 1.2 attack range (large bite), **50 damage** (highest), 40 tick cooldown
- **Ambush hunting**: Lurks in water building stealth (0.008/tick + 0.012 water bonus).
  When stealthed (≥0.65) and within 2.5 tiles: explosive pounce at 3.5x speed (0.28!)
  with 2.5x damage (125 per hit) for 15 ticks. Prey detection reduced by up to 90%
  while stealthed. After pounce, slow land chase — prey escapes if it survives the burst.
- **Survival**: 300 max hunger, 0.03 decay/tick (slow metabolism), **50000 lifespan**, mature at 4000
- **Terrain**: Fast in water (shallow 1.5x, deep 1.8x), very slow on land (grass 0.4x, forest 0.3x)
- **Social**: Completely solitary (all social values 0)
- **Reproduction**: Very slow - 1500 tick cooldown, costs 80 hunger + 50 energy
- **Body mass**: 8.0, solo hunt max ratio 1.5 (can solo up to mass 12.0)
- **Visual**: Dark green triangle, size 14

#### Shroomer (Fungi Faction)
- **Role**: Slow-growing terraformer, moisturizes terrain toward Wetland
- **Speed**: 0.02 wander, 1.4x flee multiplier
- **Survival**: 210 max hunger, 0.04 decay/tick, **50000 lifespan**, mature at 2500
- **Growth**: Grows continuously up to 4x scale at rate 0.00008/tick
- **Fear**: High threshold (70), slow accumulation (3/tick), **freeze** response
- **Terraforming**: Wetter direction, radius 2, strength 0.03, cooldown 8 ticks
- **Feeding**: On Wetland and Forest tiles, 0.5 nutrition
- **Spore reproduction**: 0.03% chance/tick when mature, hungry >50%, on wet tile.
  Costs 15% max hunger. Spreads 2 spores within 8 tile radius.
- **AoE attack**: S-curve growth scaling (smoothstep). At birth (scale 1.0): radius 2,
  damage 2.4 (nearly harmless). At max growth (scale 4.0): radius 25, damage 30
  (devastating area denial). Passive pulse every 350 ticks (17.5s), combat pulse every
  60 ticks (3s) when attacked. Targets Sectids and Faelings only.
- **Thorn defense**: Melee attackers take counter-damage scaled by same S-curve.
  At birth: 1.6 dmg/hit. At max growth: 20 dmg/hit (lethal to small attackers).
- **Spawns on**: Wetland, Forest tiles only
- **Terrain**: Fast on wetland (1.2x), very slow on arid (0.4x)
- **Visual**: Purple mushroom, size 9

#### Sectid (Insect Faction)
- **Role**: Fast swarming terraformer, dries terrain toward Arid
- **Speed**: 0.06 wander (fastest faction), 0.13 hunt speed (fast closing)
- **Hunting tactic**: `Swarm` — colony-wide rush, no retreat, counts all nearby same-species
  for effective mass. Can target any living creature including predators.
- **Combat**: 12 range, 0.5 attack range, **16 damage**, 10 tick cooldown
- **Survival**: 165 max hunger, **0.05 decay/tick**, **25000 lifespan**, mature at 800
- **Fear**: Threshold 40, fast accumulation (8/tick), **panic** response
- **Social**: Packs of ~8, high cohesion (0.035), high affinity (0.8), 90% pack hunter chance
- **Terraforming**: Drier direction, radius 1.5, strength 0.04, cooldown 6 ticks
- **Discomfort**: High tolerance (120 threshold, +0.8 swarm bonus when hunting).
  Species-specific terrain penalty in target selection prevents chasing into enemy biomes.
- **Nest reproduction**: NestBreeder flag. Cannot reproduce normally. Carries food to nests.
  Max carry 5 food, delivery range 4 tiles, **carrying speed 0.11** (faster than hunt speed).
- **Feeding**: **No tile feeding** (`CanGraze = false`, no `FeedTiles`). Sectids must hunt to survive.
- **Spawns on**: Arid, Sand tiles only
- **Terrain**: Fast on arid (1.2x), very slow on wetland (0.4x)
- **Body mass**: 0.5, SoloHuntMaxRatio 2.5, pack exponent 0.8
  (swarm of 3: max prey 3.0, swarm of 8: max prey 6.6, swarm of 12: max prey 9.2)
- **Visual**: Amber star, size 5

#### Faeling (Crystal Faction)
- **Role**: Solitary guardian terraformer, balances terrain toward Grass
- **Speed**: 0.09 wander (fastest of all factions), patrols toward damaged terrain
- **Combat**: Ranged attack - 12 range, 12 base damage, 20 tick cooldown. Targets Sectids and Shroomers.
- **Survival**: **Immune to starvation**. 195 max hunger (always full), 0 decay. 150 energy. 60000 lifespan.
- **Unhuntable**: Cannot be targeted by predators (wolves, foxes, etc.)
- **Fear**: Effectively fearless (threshold 999, accumulation 0)
- **Terrain-seeking AI**: Samples 8 compass directions, roams toward most damaged (non-Grass) terrain.
  Immune to terrain discomfort (threshold 999) — walks INTO damaged zones to restore them.
- **Growth**: Up to 2.5x scale at rate 0.00003/tick
- **Power system**: Gains power from kills (+5) and tile restoration (+0.2). Power boosts
  growth rate (+1% per point) and ranged damage (+0.5 per point). On death, 50% of power
  passes to linked crystal for the replacement Faeling.
- **Crystal reproduction**: CrystalSpawned flag. Linked to a crystal. When Faeling dies,
  crystal spawns replacement after 500 tick delay with inherited power.
- **Terraforming**: Balanced direction, radius 3 (largest), strength 0.03, cooldown 8 ticks
- **Feeding**: On Grass tiles, 0.45 nutrition
- **Spawns on**: Grass tiles only
- **Terrain**: Fast on grass (1.2x), slower on wet/arid extremes (0.7x)
- **Roaming**: Long range (120 tiles), short cooldown (300 ticks)
- **Visual**: Teal square, size 8

### Trophic Interactions (Who Eats Whom)

```
                    Wolf ──────► Deer
                      │           │
                      │           ▼
                      └────► Rabbit ◄──── Fox

              Crocodile ──► Deer, Rabbit (at water's edge)

Faction Combat:
  Faeling (ranged) ──► Sectid, Shroomer
  Shroomer (AoE)   ──► Sectid, Faeling
  Sectid (melee)   ──► Spores, small prey
```

### Mass-Based Hunting

Predators can only hunt prey up to a mass ratio limit:
- **Solo**: `prey.BodyMass <= predator.BodyMass * SoloHuntMaxRatio`
- **Pack**: `prey.BodyMass <= baseMass * (packSize ^ PackHuntMassExponent) * SoloHuntMaxRatio`

Examples:
- Fox (mass 2.0, ratio 1.0) can solo hunt Rabbit (1.0) but NOT Deer (4.0)
- Wolf (mass 3.5, ratio 1.2) can solo hunt up to mass 4.2 (barely gets Deer)
- Wolf pack of 3: max prey mass = 3.5 * 3^0.7 * 1.2 = 9.4, easily hunts Deer
- Sectid swarm of 8: max prey mass = 0.5 * 8^0.8 * 2.5 = 6.6, can take wolves

---

## 5. Core Simulation Systems

### 5.1 LOD System

**File**: `Scripts/Systems/LODSystem.cs`
**Components**: Position, SimulationLOD

Calculates each entity's distance from the player and assigns an LOD level.
Other systems check `ShouldUpdate()` to skip distant entities.

| Distance (tiles) | LOD Level | Update Interval | Description |
|-------------------|-----------|-----------------|-------------|
| 0 - 50 | Full | Every tick | Full AI, all behaviors |
| 50 - 100 | Reduced | Every 5 ticks | Simplified AI |
| 100 - 200 | Statistical | Every 30 ticks | Statistical updates only |
| 200+ | Aggregate | Every 60 ticks | Population-level only |

**LOD-aware systems** (checked via `ShouldUpdate()` or explicit level gate):

| System | LOD Gate | Source |
|--------|----------|--------|
| WanderSystem | ShouldUpdate() | WanderSystem.cs |
| SeparationSystem | ShouldUpdate() | SpatialSystems.cs |
| HerdingSystem | ShouldUpdate() | HerdingSystem.cs |
| CollisionSystem | Reduced+ | SpatialSystems.cs |
| TerrainDiscomfortSystem | Reduced+ | TerrainSystems.cs |
| SporeSystem (spread) | Reduced+ | SporeSystem.cs |
| SporeSystem (AoE) | Reduced+ | SporeSystem.cs |
| TerraformSystem | Statistical+ | TerrainSystems.cs |
| SporeSystem (maturation) | Statistical+ | SporeSystem.cs |
| HungerSystem | Statistical+ | SurvivalSystems.cs |
| AgingSystem | Statistical+ | SurvivalSystems.cs |
| GrazingSystem | Statistical+ | SurvivalSystems.cs |
| ReproductionSystem | Statistical+ | ReproductionSystem.cs |
| HuntingSystem | Aggregate+ | HuntingSystem.cs |
| FleeingSystem | Aggregate+ | FleeingSystem.cs |

Systems that must NOT be LOD-gated: MovementSystem, LODSystem.

**Critical design rule**: Systems that produce resources (GrazingSystem) and systems that
consume them (HungerSystem) must be gated at the **same LOD level**. If hunger decays but
grazing is skipped, entities starve unfairly. If grazing runs but reproduction is skipped
at a lower threshold, entities accumulate food without paying breeding costs. The
StatisticalSimSystem handles population dynamics for entities beyond the Statistical
threshold using aggregate math.

### 5.2 Movement System

**File**: `Scripts/Systems/MovementSystem.cs`
**Components**: Position, Velocity

Applies velocity to position each tick:
1. **Velocity clamping**: Creature velocity is hard-capped at **0.25 tiles/tick**
   to prevent unbounded accumulation from additive systems (hunting, fleeing,
   herding). Player entities are excluded from the cap.
2. Multiplies velocity by terrain speed modifier for current tile
3. Attempts diagonal move; if blocked, tries axis-aligned sliding
4. If still blocked, attempts perpendicular nudges (0.05 tiles) to escape corners
5. Clamps to world bounds
6. Updates `ChunkPosition` if entity has one
7. **Velocity damping**: After position update, species entities get `vel *= 0.85`
   per frame. Prevents stale velocity forces from persisting. Micro-drift cleanup
   zeroes velocity below 0.01 magnitude. Player entities are excluded.

### 5.3 Terrain Discomfort System

**File**: `Scripts/Systems/TerrainSystems.cs`
**Components**: Position, TerrainDiscomfort

Tracks how uncomfortable a creature is on its current terrain:
- Each tile type has a discomfort rate per species (via `TerrainComfortModifiers`)
- Positive rate: discomfort accumulates (`current += rate`)
- Zero/negative rate: discomfort decays (`current -= DecayRate`)
- Herbivores get extra pressure when hungry on non-grazeable tiles:
  `pressure = GrazingPressure * (1 - hunger.Current / hunger.Max)`
- When `Current >= Threshold`: triggers behavior changes (escape wander, reduced hunt range, may override fleeing)

### 5.4 Hunger System

**File**: `Scripts/Systems/SurvivalSystems.cs`
**Components**: Hunger (+ Energy for starvation)

Each tick:
1. Skips structures (Nests, Crystals) and immune species (Faelings)
2. `hunger.Current -= DecayRate`
3. If starving (hunger <= 0): `energy.Current -= StarvationDamage`
4. If energy <= 0: entity dies (queued for destruction)
5. Faeling death: passes 50% power to linked crystal
6. **Energy regeneration**: When not starving and `RegenCooldown == 0`,
   regenerates energy at `EnergyRegenRate` per tick (species-configurable).
   `RegenCooldown` is set by combat systems to prevent instant regen after taking damage.

### 5.5 Grazing System

**File**: `Scripts/Systems/SurvivalSystems.cs`
**Components**: Position, Species, Hunger

- **Herbivores**: If on grazeable tile (Grass/Forest/Savanna/Jungle/Shrubland/Taiga/Steppe), gain `GrazeNutrition` per tick
- **Faction species**: If on one of their `FeedTiles`, gain `FeedNutrition` per tick
- Capped at `hunger.Max`

### 5.6 Wander System

**File**: `Scripts/Systems/WanderSystem.cs`
**Components**: Wander, Velocity, Position (LOD-aware)

The primary movement behavior for creatures not actively hunting or fleeing:

**Normal wandering**:
- Random direction changes at `DirectionChangeChance` probability per tick
- Terrain avoidance: samples ahead, left, right; blends away from unwalkable tiles
- Look-ahead distance: 1.5 tiles

**Roaming** (long-distance directed travel):
- **Predators roam when**: hungry (< 70% hunger) AND no prey within hunt range.
  Chance: 3% + up to 12% based on hunger.
- **Herbivores roam when**: too many competitors (> 2x preferred group size).
  Chance: 2% (hungry) or 0.8% (not hungry).
- Roam speed scales with hunger urgency: `speed * roamSpeedMultiplier * (1 + urgency * 1.5)` (up to 3x when starving)
- Arrival threshold: 5 tiles from target
- Cooldown after arrival: ~500 ticks (reduced when hungry)

**Discomfort-driven escape**: Uses hysteresis to prevent edge-oscillation jitter:
- **Enter escape mode**: When `discomfort.Ratio > 0.6` → sets `IsEscaping = true`
- **Exit escape mode**: When `discomfort.Ratio < 0.1` → sets `IsEscaping = false`
- While escaping: prioritizes moving away from uncomfortable terrain, direction change
  chance reduced by 0.3x to maintain escape heading. Other systems (herding) check
  `IsEscaping` to avoid conflicting forces.

**Skips**: Entities currently fleeing (Prey.IsFleeing) or actively hunting (Predator.TargetEntity >= 0).

### 5.7 Hunting System

**File**: `Scripts/Systems/HuntingSystem.cs`
**Components**: Position, Predator, Hunger

The most complex system. Handles four distinct hunting tactics via the `HuntingTactic`
enum, each with different movement patterns and coordination behavior.

**Hunting tactics** (`HuntingTactic` enum):
| Tactic | Species | Behavior |
|--------|---------|----------|
| Solo | Fox, Bear, Hawk, Shark, Polar Bear, Arctic Fox | Direct chase, no coordination |
| PackCoordinated | Wolf, Boar | Leader/flanker/disruptor roles with phases |
| Swarm | Sectid | Colony-wide rush, no retreat, count all same-species |
| Ambush | Crocodile, Jaguar, Snake, Scorpion | Stealth accumulation → pounce burst |

Movement dispatch uses a clean `switch (speciesDef.HuntingTactic)` block, with each
tactic handled by a dedicated method (`ApplyPackTactics`, `ApplyAmbushMovement`, etc.).

**Mass-based direction blending**: All velocity changes in hunting use
`agility = Clamp(1.5 / bodyMass, 0.25, 1.0)`. Lighter creatures snap to new directions
instantly while heavy predators (crocs) commit to their trajectory. Implemented via
`BlendVelocity(ref vel, targetDx, targetDy, agility)`.

**Hunt activation**: Only when `hunger.Current / hunger.Max < HuntThreshold` (default 0.75).
Higher urgency = longer effective range and faster movement.

**Target selection**:
1. Spatial hash query within effective hunt range
2. Filter: must have Prey component, mass within huntable ratio, not unhuntable
3. Score: distance (closer = better) + generic terrain penalty + preferred prey bias
4. **Species-specific terrain comfort penalty**: prey on tiles the hunter finds
   uncomfortable gets a large score penalty (e.g. Sectids avoid targeting prey in Wetland).
   Scaled by urgency — desperate hunters tolerate more.
5. Land predators reject targets across water (>15% water fraction on path)
6. Pack members share targets via group target dict

**Pursuit**:
- Speed: `BaseHuntSpeed * (1 + urgency * 0.5)`
- Range modifier: `HuntRange * (1 + urgency * 0.5) * (1 - discomfortRatio * 0.5)`

**Attack**: When within AttackRange and cooldown is 0, deal AttackPower damage
(multiplied by PounceAttackMult during ambush pounce burst).
On kill:
- Nutrition = `prey.NutritionValue` (or `prey.BodyMass * 20` if unset)
- Solo: hunter gets all nutrition
- Pack: killer gets `KillerShareRatio` (50%), rest split remainder within `PackShareRadius`
- Sectids: food carriers get portion as cargo (for nest delivery), rest as hunger

**Hunger-driven tracking**: When hungry (< `TrackingHungerThreshold`) with no target,
scans `TrackingRange` at 0.8x hunt speed (slow search mode). Uses mass-based agility
for tracking direction blending.

#### 5.7.1 Pack Flanking Tactics (Wolves)

Pack roles are assigned when a pack member acquires a target. Roles determine
behavior during the positioning phase before the all-in convergence kill rush.

**Roles** (`PackRole` enum):
| Role | Count | Behavior |
|------|-------|----------|
| Leader | 1 | Holds at 6-tile observation distance, circles slowly, monitors flanker positions, triggers convergence |
| Flanker | 2+ | Circles to OPPOSITE side of prey from leader (behind prey), forms V-formation with perpendicular spread |
| Disruptor | 1-2 | Positioning → Disrupting (rush at 1.3x) → Retreating cycle to scatter the herd |

**Assignment priority**: 1 leader (highest leadership score) → 1 disruptor → fill flankers → 2nd disruptor in larger packs.

**Phases** (`PackPhase` enum):
| Phase | Description |
|-------|-------------|
| Positioning | All roles move to starting positions |
| Disrupting | Disruptors rush prey herd to scatter it |
| Retreating | Disruptors back off between rushes |
| Converging | **ALL-IN kill rush** — all roles abandon tactics and charge at 1.3x speed |

**Convergence triggers** (any one triggers the all-in):
1. Leader's `ConvergenceTimeout` expires (default 120 ticks / 6 seconds)
2. A flanker reaches opposite side of prey (dot product of prey→leader · prey→flanker < 0)
3. Prey becomes isolated from herd (< 2 nearby herd members within 6 tiles)

**Convergence propagation**: Leader sets `PackPhase.Converging`, which propagates to
all group members via `_groupConverging` dictionary in the first pass.

**Flanker positioning geometry**:
- Direction from leader to prey = "front" of attack
- Flanker target = behind prey (past it from leader's perspective) at 4-tile distance
- Two flankers spread to opposite sides using perpendicular offset (3-tile spread)
- Side determination: cross product of relative position vs attack axis

**Swarm tactic**: Species with `HuntingTactic.Swarm` (Sectids) skip tactical positioning
entirely and use direct swarm rush at 1.2x speed. They never retreat, count all nearby
same-species for effective mass (colony-wide bravery), and can target any living creature
including predators. Post-discomfort suppress timer is 120 ticks (vs 60 for others) to
prevent target fixation loops.

#### 5.7.2 Ambush Hunting (Crocodiles, Jaguars, Snakes, Scorpions)

Ambush predators use a stealth mechanic to approach prey undetected, then trigger
a devastating pounce burst. Activated for species with `HuntingTactic.Ambush`.

**Stealth accumulation** (`Predator.Stealth`, 0.0 to 1.0):
- **Gain**: When moving at or below `AmbushSpeedThreshold` fraction of BaseHuntSpeed.
  Rate = `AmbushStealthGain` per tick (0.008 for crocs).
- **Water bonus**: Semi-aquatic ambushers gain extra `WaterStealthBonus` on water tiles
  (0.012 for crocs, total 0.02/tick on water — full stealth in ~50 ticks / 2.5s).
- **Decay**: When moving faster than speed threshold, stealth drops at
  `AmbushStealthDecay` per tick (0.04 for crocs — fast loss).
- **Passive**: Stealth builds while idle (no target) too — crocs lurk in water.

**Three movement phases**:

| Phase | Condition | Movement | Agility |
|-------|-----------|----------|---------|
| Stalking | Stealth > 0.1, no pounce | Slow approach at `BaseHuntSpeed × AmbushSpeedThreshold × 0.9` | 0.5× normal |
| Pounce | Stealth ≥ threshold AND dist ≤ PounceRange | Burst at `BaseHuntSpeed × PounceSpeedMult` | 0.8 (snap to target) |
| Open chase | Stealth ≤ 0.1 or post-pounce | Normal `BaseHuntSpeed × urgency` | Normal mass-based |

**Pounce mechanics**:
- Trigger: `Stealth >= PounceStealthThreshold` AND distance ≤ `PounceRange`
- On trigger: `PounceTimer = PounceDuration`, `Stealth = 0` (breaks cover)
- During pounce: speed × PounceSpeedMult, damage × PounceAttackMult
- Duration: `PounceDuration` ticks (15 for crocs = 0.75 seconds)

**Crocodile pounce numbers**:
- Pounce speed: 0.08 × 3.5 = 0.28 (faster than any prey's flee speed)
- Pounce damage: 50 × 2.5 = 125 per hit (often one-shot on small prey)
- After pounce: slow open chase at 0.08 — prey escapes if it survives initial burst

**Water avoidance**: Ambush predators skip water-avoidance steering while stalking or
pouncing (they deliberately hunt from water).

### 5.8 Fleeing System

**File**: `Scripts/Systems/FleeingSystem.cs`
**Components**: Position, Prey, Velocity, Wander (+ optional Fear)

Two phases:
1. **Predator scan**: Collects all predator positions, species IDs, and stealth levels
2. **Per-prey processing**: Calculates flee direction (stealth-aware) and applies fear

**Mass-based direction blending**: Like hunting, flee velocity uses mass-based agility:
`agility = Clamp(1.5 / bodyMass, 0.25, 1.0)`. Rabbits (mass 1.0) juke instantly while
deer (mass 4.0) commit to escape routes. Applies to both normal flee and panic responses.

**Stealth-aware detection**: Each predator's stealth level reduces the effective flee
range for prey detecting that predator:
- `effectiveRange = fleeRange × (1 - stealth × 0.9)`
- At stealth 0: full detection range
- At stealth 1.0: only 10% of normal range (nearly invisible)
- Pounce resets stealth to 0 → prey sees predator at full range again and can flee

**Flee direction**: Weighted sum away from *detected* nearby predators.
Weight = `1 / distanceSquared` (closer predators are far more influential).

**Fear accumulation**:
- When predator within FleeRange: `fear += AccumulationRate * proximityFactor`
- Proximity factor: `1 - (distSq / fleeRangeSq)` (closer = more fear)
- Sets vigilance: 100 ticks of slower fear decay after threat leaves
- Fear triggers behavior when: threat nearby OR fear ratio > 0.5

**Fear responses**:

| Response | Behavior |
|----------|----------|
| **Flee** (default) | Run away at `speed * FleeSpeedMultiplier`. Boost at fear > 0.7. Terrain-aware side-stepping. |
| **Freeze** | Reduce velocity by `1 - (fearRatio * 0.9)`. Nearly stops at max fear. |
| **Panic** | Erratic movement at high speed with random angle variation. Ignores terrain at high panic. |
| **Defensive** | Not yet implemented, defaults to Flee. |

**Discomfort override**: If terrain discomfort exceeds threshold AND fear < 0.9, creature
stops fleeing to escape bad terrain instead (survival priority over predator avoidance).

### 5.9 Herding System

**File**: `Scripts/Systems/HerdingSystem.cs`
**Components**: Position, Velocity, Species, Social (LOD-aware)

Manages social grouping, leader selection, and cohesion/alignment forces.

**Priority system** (higher priorities override social behavior):
1. Terrain `IsEscaping` flag set → skip herding entirely (prevents tug-of-war jitter)
2. Actively fleeing from predator → suspend social
3. Actively hunting (non-pack) → suspend social
4. **Exception**: Packs maintain cohesion during hunt (with 3-6x boost)

**Leadership**:
- Score = `age.Current / age.MaxLifespan` (older = better leader)
- Each group tracks a recognized leader
- If leader lost for `LeaderLostThreshold` ticks (default 40), seek new leader
- Alpha spawns: 0.8 score, others: 0.3 ± 0.5 random

**Group membership**:
- Ungrouped entities join nearby groups below max size
- Max size: `PreferredGroupSize * GroupSizeTolerance` (e.g., 6 * 1.3 = 7.8)
- Low-score members leave oversized groups (if leadership < 0.3)
- No nearby groups: form new group with nearby ungrouped entities

**Social forces** (when leader is visible):
- **Cohesion**: Move toward leader, maintain ideal follow distance (1.5 for packs, 2.0 for herds)
- **Alignment**: Match leader's velocity direction
- Both modified by: distance factor, group size factor, GroupAffinity
- **Pack boost**: 3x cohesion/alignment normally, 6x during active hunt

**Fallback** (no visible leader): Local cohesion toward center of nearby same-species entities
at reduced radius (0.8x) and strength (0.5x).

### 5.10 Separation System

**File**: `Scripts/Systems/SpatialSystems.cs`
**Components**: Position, Velocity, Species (LOD-aware)

Prevents same-species creatures from overlapping:
- Query nearby same-species within `SeparationRadius`
- Push force: `(1 - distance / radius)` (stronger when closer)
- Applied as velocity adjustment scaled by `SeparationStrength`

### 5.11 Collision System

**File**: `Scripts/Systems/SpatialSystems.cs`
**Components**: Position, Renderable

Physical overlap resolution for all entities (regardless of species):
- Collision radius: `renderable.Size * 0.5 / 16` (scaled to tile units)
- Two resolution passes per tick
- Overlapping entities pushed apart equally (each gets `overlap * 0.5`)

### 5.12 Aging System

**File**: `Scripts/Systems/SurvivalSystems.cs`
**Components**: Age

Each tick: `age.Current++`. When `age.Current >= MaxLifespan`: entity dies.
Skips structures (Nests, Crystals). Faeling death passes 50% power to crystal.

Elderly threshold: 80% of MaxLifespan (available for future mechanics).

### 5.13 Reproduction System

**File**: `Scripts/Systems/ReproductionSystem.cs`
**Components**: Position, Species, Hunger, Energy, Age, Reproduction

Standard reproduction for non-faction species (LOD gate: Statistical+):

**Requirements**:
1. Population below `MaxPopulation` (default 15000)
2. `CurrentCooldown == 0`
3. `age.Current >= MaturityAge`
4. NOT a faction reproducer (NestBreeder, SporeReproducer, CrystalSpawned skip)
5. `hunger.Current >= HungerThreshold`
6. `energy.Current >= EnergyThreshold`
7. **Local density check**: Nearby same-species count (within `1.5× SocialRadius`)
   must be below `2× PreferredGroupSize`. Prevents local clustering.
8. **Global population pressure**: Random skip based on `entityCount / maxPopulation`.
   Linear ramp: 100% pass rate at ≤50% cap, 0% pass rate at 100% cap.
9. **Hard cap in spawn loop**: Both the spawn-queue loop and `SpawnCreature()` itself
   re-check `EntityCount >= MaxPopulation` before each entity creation.

**Process**:
1. Find walkable spawn location within `SpawnRadius` of parent
2. Deduct `HungerCost` and `EnergyCost` from parent
3. Reset `CurrentCooldown`
4. Spawn `OffspringCount` offspring at location

**Offspring start with**:
- Age: 0
- Hunger: 60% of max
- Energy: 100% of max
- All species components inherited

---

### 5.14 Statistical Simulation System

**File**: `Scripts/Systems/StatisticalSimSystem.cs`
**Data**: `Scripts/World/ChunkPopulationData.cs`

Replaces per-entity simulation for distant chunks (>100 tiles from player) with
population-level birth/death math. Runs every 30 ticks.

**Three transitions:**
1. **Aggregation** (entity → stats): When chunk becomes distant, scan entities via
   spatial hash, accumulate per-species counts/hunger/age, destroy entities.
2. **Statistical tick**: Apply birth/death rates to population counts.
3. **Materialization** (stats → entities): When chunk becomes nearby, spawn entities
   from population data via `EntityFactory.SpawnCreature()`, capped at `MaxPopulation`.

**Birth rate model** (per species per tick):
```
matureFraction = 1 - MaturityAge / MaxLifespan
wellFedFraction = 0.8 if fed, 0.3 if marginal, 0.0 if starving
birthRate = count × matureFraction × wellFedFraction × (OffspringCount / ReproCooldown)
```
Suppressed by density: linear ramp from 50% to 100% of carrying capacity → 0 births.
When carrying capacity = 0 (no food): births forced to 0.

**Death rate model**:
- Natural: `count / MaxLifespan` (uniform age distribution assumption)
- Starvation: when `AverageHungerRatio < 0.1`, deaths at rate `count / (MaxEnergy / StarvationDamage)`
- Predation: `totalPredators × (HungerDecayRate / EffectiveNutrition) × preyShareFraction`
  (terraformers excluded from herbivore count to avoid diluting predation)

**Carrying capacity** (per chunk, per species):
- Herbivores: `(GrazeableTileCount × RegenerationRate) / (HungerDecayRate / GrazeNutrition)`
  — typically 10-20 per chunk depending on grazeable tile count
- Predators: `totalHerbivores × 0.15` (~1 per 7 prey)
- Terraformers: `CountFeedTiles() / (HungerDecayRate / FeedNutrition)`
  — actual tile scan of chunk for species' FeedTiles list
- Hard per-chunk cap: `min(pop.Count, carryingCapacity × 2)` safety net

**Hunger model** (drift per 30-tick interval):
- Herbivores with food: +0.01 if supply > demand, else −0.02 × deficit ratio
- Herbivores without food: −0.1 (rapid starvation)
- Predators: +0.01 if prey abundant (>3× predators), else −0.02
- Terraformers: based on feed tile count vs population ratio

**Statistical terraforming** (faction species):
Each terraformer changes `count × (Strength / Cooldown) × 30` tiles per interval.
Picks random tiles in chunk, applies directional shift (Wetter/Drier/Balanced).
Marks chunk dirty for re-rendering when player approaches.

**Population control** — `EntityFactory.SpawnCreature()` enforces hard cap via
`SetPopulationCap()`. `ReproductionSystem` applies global population pressure
(linear ramp: 100% birth chance at 50% cap → 0% at 100%).

---

## 6. Faction Systems

The three faction species have unique reproduction and combat mechanics that replace
the standard ReproductionSystem.

### 6.1 Shroomer / Spore System

**File**: `Scripts/Systems/SporeSystem.cs`

Shroomers reproduce by spreading spores that grow on wet terrain.

**Spore spreading** (by mature Shroomers):
- Requirements: mature, hunger > 50%, on wet tile (moisture >= threshold), random chance (0.03%/tick)
- Cost: 15% of max hunger
- Creates 2 spores at random positions within 8 tile radius
- Spores: small purple dots, low energy (40), can be eaten (have Prey component)

**Spore lifecycle**:
- On wet tile (moisture >= 0.6): accumulate moisture at `MoistureGainRate * tileMoisture`
- On dry tile: wither (take damage at `WitherRate` per tick)
- When `MoistureAccumulated >= TransformThreshold` (60): transform into new Shroomer
- When energy reaches 0: spore dies

**Shroomer growth**:
- Grows continuously: `CurrentScale += GrowthRate` each tick (capped at `MaxScale`)
- Visual size: `baseSize * currentScale`
- HP scales with growth (max energy × current scale)
- Growth unlocks AoE attack at 1.0x scale (active from birth, but very weak)

**AoE attack** (S-curve growth scaling via smoothstep):
- Scaling: `factor = AoEMinScaleFactor + (1 - AoEMinScaleFactor) * smoothstep(t)`
  where `t = (currentScale - 1) / (maxScale - 1)`, smoothstep = `t²(3-2t)`
- Radius: `AoEAttackRadius * factor` (25 max → 2 at birth, 25 at full growth)
- Damage: `AoEAttackDamage * factor` (30 max → 2.4 at birth, 30 at full growth)
- **Passive pulse**: every 350 ticks (17.5s) — slow background radiation
- **Combat pulse**: every 60 ticks (3s) — reactive defense when being attacked
- Targets: Faelings and Sectids only (enemy terraformers, NOT other Shroomers or spores)

**Thorn defense** (melee counter-damage):
- Attackers take `ThornDamageBase * growthFactor` damage per hit (same S-curve)
- At birth: 1.6 dmg/hit (barely stings). At max: 20 dmg/hit (lethal to small attackers)

### 6.2 Sectid / Nest System

**File**: `Scripts/Systems/NestSystem.cs`

Sectids reproduce through nests that convert food into larvae.

**Food delivery**:
- Sectids with `FoodCarrier` component carry food from kills to nearest nest
- While carrying: move at `CarryingSpeed` (0.11, faster than hunt speed)
- On delivery within `FoodDeliveryRange` (4 tiles): add food to nest, self-feed 30%

**Nest mechanics**:
- **Stages**: 1 → 2 → 3 (max). Each stage allows that many simultaneous larvae.
- **Advancement**: After 3 successful spawns at current stage, advance to next
- **Larvae creation**: When `FoodStored >= FoodPerSpawn` (30), start larva timer
- **Incubation**: `NestSpawnDuration` ticks (200 default)
- **Hatching**: Spawn Sectid at random offset (±3 tiles from nest)
- **Visual growth**: Size = `6 + stage * 3` (9, 12, 15 pixels)

**Colony expansion**:
- At max stage (3) with enough food (`FoodPerSpawn * 2`):
  - Count nearby nests within `NestColonyRadius` (40 tiles)
  - If fewer than `NestsForExpedition` (5): found nest within `NestSearchRadius` (15 tiles)
  - If enough: found distant colony at `ExpeditionDistance` (80 tiles) with new ColonyId

**Newly spawned Sectids**:
- Start with 70% hunger
- Have Prey + Predator components (can hunt spores/small prey, can be hunted)
- FoodCarrier component for nest delivery
- Social: Pack type, GroupId = ColonyId
- Terraform: Drier direction

### 6.3 Faeling / Crystal System

**File**: `Scripts/Systems/CrystalSystem.cs`

Faelings are spawned by crystals and gain power over their lifetime.

**Crystal mechanics**:
- Each crystal is linked to one Faeling
- When linked Faeling dies: crystal inherits Faeling's power, starts spawn timer
- Spawn delay: `CrystalSpawnDelay` ticks (500 default)
- New Faeling spawns 2 tiles from crystal with 50% of inherited power
- Crystals are indestructible (999999 energy)
- Visual: Teal diamond, size based on crystal tier

**Faeling power system**:
- Gains power from:
  - Kills: +5 power per kill (Sectids/Shroomers)
  - Tile restoration: +0.2 power per balanced terraform
- Power boosts:
  - Growth rate: `+1% per power point`
  - Ranged damage: `baseDamage + power * 0.5`
- On death: 50% of power passes to linked crystal → inherited by next Faeling
- Power compounds across generations (lineage gets stronger over time)

**Ranged attack**:
- Range: 12 tiles, base damage: 12, cooldown: 20 ticks
- Targets nearest Sectid or Shroomer within range (NOT other Faelings, NOT spores)
- On kill: gain `PowerPerKill` (5), increment kill count

**Faeling properties**:
- Immune to starvation (hunger never decays)
- Unhuntable by predators
- Effectively fearless
- Solitary (no social behavior)
- Very long lifespan (60000 ticks)

### 6.4 Faction Terraforming Summary

| Faction | Direction | Effect | Radius | Strength | Cooldown |
|---------|-----------|--------|--------|----------|----------|
| Shroomer | Wetter | Shifts tiles toward Wetland | 2.0 | 0.03 | 8 ticks |
| Sectid | Drier | Shifts tiles toward Arid | 1.5 | 0.04 | 6 ticks |
| Faeling | Balanced | Shifts extremes toward Grass | 3.0 | 0.03 | 8 ticks |

The three factions create a dynamic terraforming conflict:
- Shroomers wet the world → benefits Shroomer spawning, hinders Sectids
- Sectids dry the world → benefits Sectid spawning, hinders Shroomers
- Faelings balance the world → benefits general ecosystem, limits faction extremes

---

## 7. Rendering & Player Controls

### Rendering

- **Terrain**: Each chunk is rendered to a cached `ImageTexture` (4 pixels per tile).
  Textures are rebuilt only when terraform modifies a tile (`WorldManager.DirtyChunks`).
  One `DrawTextureRect()` call per visible chunk instead of 1,024 individual draw calls.
- **Entities**: Three `MultiMeshInstance2D` nodes batch-render all entities by shape
  (Circle, Triangle, Square). Per-entity frustum culling skips off-screen entities.
  Instance transforms and colors are updated each frame. This reduces entity draw calls
  from N to 3 regardless of entity count.
- **Chunk-based frustum culling**: Only visible chunks are drawn (camera viewport bounds)
- Entity size: `renderable.Size` pixels (scaled by zoom), modified by Growth component

### Entity Visuals

| Species | Color | Shape | Base Size |
|---------|-------|-------|-----------|
| Deer | Green (0.4, 1, 0.4) | Circle | 8 |
| Rabbit | Brown (0.6, 0.5, 0.4) | Teardrop | 5 |
| Wolf | Gray (0.6, 0.6, 0.6) | Triangle | 10 |
| Fox | Orange (1, 0.5, 0.2) | Triangle | 7 |
| Crocodile | Dark green (0.3, 0.5, 0.3) | Fangs | 14 |
| Shroomer | Purple (0.55, 0.23, 0.78) | Mushroom | 9 (grows to 4x) |
| Sectid | Amber (0.86, 0.55, 0.16) | Star | 5 |
| Faeling | Teal (0.24, 0.86, 0.78) | Square | 8 (grows to 2.5x) |
| Crystal | Teal (0.1, 1, 0.9) | Diamond | Special |
| Nest | Brown (0.6, 0.4, 0.2) | Square | 9-15 (by stage) |
| Spore | Purple (0.7, 0.3, 0.9) | Circle | 3 |
| Player | Yellow (1, 1, 0) | Circle | 12 |

### Player Controls

| Input | Action |
|-------|--------|
| WASD / Arrows | Move camera (8-directional with diagonal normalization) |
| Shift | Sprint (3x speed) |
| Mouse wheel / +/- | Zoom (0.1x to 5.0x, step 0.15) |

### Debug Overlay

Displays: FPS, entity counts by type (Herbivores, Predators, Shroomers, Sectids,
Faelings, Nests, Spores, Crystals), total population. Updated every 0.5 seconds.

---

## 8. Configuration Reference

### GameManager Settings

| Parameter | Default | Description |
|-----------|---------|-------------|
| ChunkSize | 32 | Tiles per chunk side |
| WorldSizeChunks | 24 | Chunks per world axis (24x24 = 768x768 tiles) |
| WorldSeed | 0 | Seed for terrain generation (0 = random each run) |
| TileSize | 16 | Pixel size of each tile |
| TargetTPS | 20 | Simulation ticks per second |
| MaxPopulation | 15000 | Hard entity cap |
| InitialPopulation | 1500 | Starting creature count |
| HerbivoreRatio | 0.85 | 85% herbivores at start |
| CrystalCount | 5 | Number of Faeling crystals |
| InitialSectidColonies | 3 | Starting Sectid colony count |
| InitialNestsPerColony | 2 | Nests per starting colony |
| CreaturesPerChunk | 2.0 | Creatures per chunk for spawning density |
| PlayerSpeed | 1.0 | Base camera movement speed |
| PlayerSprintMultiplier | 3.0 | Sprint speed multiplier |

### Initial Population Budget

From `InitialPopulation` (1500 default):
- Terraformer share: 12% → ~180 terraformers (split among Shroomers/Sectids/Faelings)
- Predator share: (1 - 0.85) * (1 - 0.12) → ~13.2% → ~198 predators
- Herbivore share: remainder → ~74.8% → ~1122 herbivores

### Spawning Rules

- Creatures spawn in groups (3-tile radius around center)
- Alpha member: 0.8 leadership score; others: 0.3 ± 0.5 random
- Predators spawn near prey chunks (±1 chunk)
- Up to 10 attempts per spawn location, 20 round-robin passes
- All stats varied ±20% (species `StatVariation`)

---

## 9. File Map

```
godot/
├── Scripts/
│   ├── ECS/
│   │   └── EntityManager.cs         # Entity storage, creation, destruction (SoA)
│   ├── Components/
│   │   ├── CoreComponents.cs        # Position, Velocity, ChunkPosition
│   │   ├── CreatureComponents.cs    # Species, Hunger, Energy, Age, Reproduction,
│   │   │                            # SimulationLOD, TerrainDiscomfort, Fear
│   │   ├── BehaviorComponents.cs    # Wander, Predator, Prey, Social, Renderable,
│   │   │                            # Terraform + enums (PackRole, ShapeType, etc.)
│   │   └── FactionComponents.cs     # Nest, FoodCarrier, Spore, Growth, Crystal,
│   │                                # FaelingPower, RangedAttack
│   ├── Systems/
│   │   ├── ISystem.cs               # System interface
│   │   ├── LODSystem.cs             # Distance-based simulation LOD
│   │   ├── MovementSystem.cs        # Position + velocity damping (0.85/frame)
│   │   ├── TerrainSystems.cs        # TerrainDiscomfort + TerraformSystem + TileRegeneration
│   │   ├── SurvivalSystems.cs       # HungerSystem + AgingSystem + GrazingSystem
│   │   ├── WanderSystem.cs          # Random movement, roaming, terrain escape (hysteresis)
│   │   ├── HerdingSystem.cs         # Social cohesion, alignment, leader following
│   │   ├── SpatialSystems.cs        # SeparationSystem + CollisionSystem
│   │   ├── HuntingSystem.cs         # Solo/pack/ambush hunting, flanking tactics, stealth
│   │   ├── FleeingSystem.cs         # Threat detection (stealth-aware), fear, escape
│   │   ├── ReproductionSystem.cs    # Standard offspring spawning
│   │   ├── CrystalSystem.cs         # Faeling crystal management & ranged attacks
│   │   ├── NestSystem.cs            # Sectid nest breeding & food delivery
│   │   └── SporeSystem.cs           # Shroomer spore lifecycle & AoE attacks
│   ├── World/
│   │   ├── TerrainGenerator.cs      # Noise-based terrain generation
│   │   ├── WorldManager.cs          # Chunk loading, tile queries, walkability
│   │   ├── Chunk.cs                 # Tile storage and access
│   │   ├── TileType.cs             # Tile enum, extensions (IsWater, IsWalkable, etc.)
│   │   └── RiverMapper.cs          # Flow-based river pre-computation
│   ├── Species/
│   │   ├── SpeciesDefinition.cs     # 90+ property data class for species config
│   │   └── SpeciesRegistry.cs       # All 8 species registered with full parameters
│   ├── Rendering/
│   │   └── RenderingManager.cs      # Chunk-based entity/tile rendering
│   ├── Utils/
│   │   ├── SpatialHash.cs           # Grid-based spatial queries
│   │   └── MathUtils.cs             # Distance, normalization helpers
│   ├── GameManager.cs               # Main loop, initialization, system registration
│   ├── EntityFactory.cs             # Entity creation from SpeciesDefinition
│   ├── WorldSpawner.cs              # Initial population spawning
│   └── PlayerController.cs          # Camera movement and controls
├── Scenes/
│   └── Main.tscn                    # Main Godot scene
└── project.godot                    # Godot project configuration

archived/                            # Original Python/Arcade version (reference only)
docs/
├── FEATURES_AND_DESIGN.md           # This document
├── architecture.md                  # Architecture overview
├── godot-roadmap.md                 # Development roadmap & task tracking
├── documentation-review.md          # Docs-vs-code audit log
├── plan-optimization.md             # Performance optimization strategy
└── development-plan.md              # Original design rationale (historical)
```

### Adding New Components

1. Define struct in appropriate Components file with `[StructLayout(LayoutKind.Sequential)]`
2. Add array to `EntityManager` (matching `MAX_ENTITIES` size)
3. Add flag to `ComponentFlags` enum
4. Update `CreateEntity()` if needed for default values
5. Add configuration properties to `SpeciesDefinition` if species-configurable

### Adding New Systems

1. Implement `ISystem` interface
2. Register in `GameManager._Ready()` in correct execution order
3. Document dependencies (must run before/after which systems)
4. Consider LOD levels: check `ShouldUpdate()` to skip distant entities
5. Use `SpatialHash` for proximity queries (never O(n^2) all-entity loops)

---

*Last Updated: February 2026*
