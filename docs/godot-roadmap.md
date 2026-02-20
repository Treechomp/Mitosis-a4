# Mitosis Godot 4.6 Development Roadmap

> For comprehensive documentation of all implemented features, systems, and species,
> see **[FEATURES_AND_DESIGN.md](FEATURES_AND_DESIGN.md)**.

## Current State Summary (v0.5 - World Generation v2)

### Architecture Overview
```
┌─────────────────────────────────────────────────────────────┐
│                    SPECIES DEFINITION LAYER                  │
│  (Data-driven creature configuration)                        │
│  • SpeciesDefinition - all stats, behaviors, preferences    │
│  • SpeciesRegistry - lookup by name/ID                       │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    ECS SIMULATION LAYER                      │
│  (Structure of Arrays for cache efficiency)                  │
│  • EntityManager - 16,384 entity capacity                   │
│  • Components - Position, Velocity, Hunger, Fear,           │
│                 Predator (Stealth, Pounce), Social, etc.    │
│  • Systems - 18 systems across 15 files (20 TPS)           │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    WORLD LAYER                               │
│  (Chunk-based terrain with biomes)                           │
│  • WorldManager - chunk loading, spatial queries            │
│  • TerrainGenerator - noise-based generation                 │
│  • SpatialHash - efficient entity proximity queries          │
└─────────────────────────────────────────────────────────────┘
```

### Implemented Features

#### Core ECS (100% Complete)
- [x] EntityManager with SoA layout (16,384 entities)
- [x] Flag-based component queries
- [x] 22+ component types (Position, Velocity, Hunger, Energy, Age, Species, Fear,
      Wander, Predator, Prey, Social, Terraform, Nest, FoodCarrier, Spore, Growth,
      Crystal, FaelingPower, RangedAttack, Renderable, TerrainDiscomfort, SimulationLOD)


#### Species System (100% Complete)
- [x] SpeciesDefinition - comprehensive data class with 90+ configurable properties
- [x] SpeciesRegistry - lookup by name or ID (hash)
- [x] 28 species defined across 6 biome categories (8 original + 20 Phase 3)
- [x] Diet types: Herbivore, Carnivore, Omnivore, Terraformer
- [x] Social types: Herd, Pack, Solitary
- [x] Biome preferences per species
- [x] Spawn tile preferences (IsAquatic, AllowedSpawnTiles, CanSpawnOnTile)
- [x] Terrain speed/comfort modifiers per species
- [x] Faction species (Shroomer, Sectid, Faeling) with full definitions
- [x] Mass-based hunting system (solo/pack hunt ratios)
- [x] Preferred prey system with bias scoring
- [x] Faction-specific fields: spore, nest, crystal, terraform, AoE, growth, ranged attack

#### Terrain & World (100% Complete)
- [x] Chunk-based storage (32x32 tiles)
- [x] Simplex noise terrain generation with domain warping
- [x] 20 tile types with walkability/speed/discomfort/avoidance rules
- [x] 11 biome types (Ocean, Coast, Grassland, Forest, Desert, Mountain, Wetland, River, Arctic, Tropical, Volcanic)
- [x] IsSpawnable(), IsWater(), IsGrazeable(), IsTerraformable() tile queries
- [x] GetCoverBonus() for stealth mechanics (Jungle/Forest/Taiga/Wetland/Bog/Shrubland/Savanna)
- [x] BiomeType calculation from elevation/moisture/temperature
- [x] Temperature noise + latitude gradient for regional biome distribution
- [x] Species-specific spawn filtering (biome + tile)
- [x] Flow-based river generation (RiverMapper: elevation tracing, flow accumulation, depression lakes, wetland banks)
- [x] Per-tile nutrition with depletion and regrowth (migration-driving mechanic)
- [x] Landmark post-processing (lakes, oases, clearings, caves)
- [x] Terraform chains extended for new biome tiles (Tundra, Savanna, Jungle)

#### Behavior Systems (97% Complete)
- [x] MovementSystem - terrain speed modifiers, wall sliding, **velocity damping (0.85/frame)**
- [x] TerrainDiscomfortSystem - accumulating discomfort on bad terrain
- [x] HungerSystem - decay, starvation damage, faction immunity
- [x] GrazingSystem - herbivores feed on grass/forest, factions on FeedTiles
- [x] WanderSystem - random exploration, roaming, terrain avoidance, **hysteresis escape (enter 0.6, exit 0.1)**
- [x] HerdingSystem - flocking, cohesion, alignment, leader election, group management, **skip when IsEscaping**
- [x] SeparationSystem - same-species collision avoidance
- [x] CollisionSystem - physical overlap resolution (all entities)
- [x] HuntingSystem - **HuntingTactic enum** (Solo/PackCoordinated/Swarm/Ambush), **per-species tactic assignment**, mass-based agility, pack coordination, **coordinated flanking (Leader/Flanker/Disruptor roles, convergence triggers)**, **ambush hunting (stealth, stalking, pounce burst)**, **swarm tactic (Sectids)**, **species-specific terrain comfort in target selection**, food sharing
- [x] FleeingSystem - prey escape with fear integration, 4 response types, **mass-based agility**, **stealth-aware detection (stealthed predators reduce effective flee range by up to 90%)**
- [x] AgingSystem - maturity, natural death
- [x] ReproductionSystem - species-aware offspring spawning with population cap
- [x] LODSystem - 4-tier distance-based simulation detail
- [x] TerraformSystem - faction tile modification (wetter/drier/balanced)
- [x] NestSystem - Sectid nest breeding, food delivery, colony expansion
- [x] SporeSystem - Shroomer spore lifecycle, growth, **S-curve AoE attacks** (smoothstep scaling), **thorn defense**
- [x] CrystalSystem - Faeling crystal management, ranged attacks, power inheritance
- [ ] Missing: TerritorySystem
- [ ] Missing: StatisticalSimSystem (chunk-level populations)

#### Fear & Threat Response (100% Complete)
- [x] Fear component with accumulation/decay
- [x] FearResponse types: Flee, Freeze, Defensive, Panic
- [x] Vigilance state (slower fear decay after threats)
- [x] Integration with FleeingSystem

#### Spectator/Player (70% Complete)
- [x] Player entity spawning
- [x] WASD movement with sprint (Shift)
- [x] Configurable speed (Export variables)
- [x] Zoom controls (mouse wheel, +/- keys)
- [x] Camera follow with smooth lerp
- [x] Debug overlay (FPS, entity counts)
- [ ] Missing: Entity selection/inspection
- [ ] Missing: Time controls (pause, speed)
- [ ] Missing: Minimap

---

## Architectural Guidelines

### Design Principles

1. **Data-Driven Species**: All creature behavior should be configurable through SpeciesDefinition. Avoid hardcoding species-specific logic in systems.

2. **Extensible Components**: When adding new features, check if existing components can be extended rather than creating new ones.

3. **Species-Aware Spawning**: Always use species-specific methods for spawn location validation:
   - `species.CanSpawnOnTile(tile)` for tile checks
   - `species.CanSpawnInBiome(biome)` for biome checks
   - `WorldManager.IsSpawnableForSpecies(x, y, species)` for world queries

4. **Terrain Abstraction**: Use tile type methods rather than raw enums:
   - `tile.IsWalkable()`, `tile.IsSpawnable()`, `tile.IsWater()`, `tile.IsGrazeable()`
   - Add new methods to TileType.cs when needed

5. **LOD Awareness**: New systems should check LOD level before expensive operations:
   ```csharp
   if (lod.Level > LODLevel.Reduced) continue; // Skip for distant entities
   ```

### Component Addition Checklist

When adding a new component:
1. Define struct in appropriate Components file with `[StructLayout(LayoutKind.Sequential)]`
2. Add array to EntityManager (matching MAX_ENTITIES size)
3. Add flag to ComponentFlags enum
4. Update CreateEntity() if needed for default values
5. Consider if SpeciesDefinition should configure it

### System Addition Checklist

When adding a new system:
1. Implement ISystem interface
2. Register in GameManager._Ready() in correct order
3. Document dependencies (must run before/after X)
4. Consider LOD levels - when to skip processing
5. Use SpatialHash for proximity queries (not O(n²) loops)

---

## Phase 1: Ecosystem Stability (Priority: CRITICAL)

**Goal**: Self-sustaining populations that don't collapse or explode

### 1.1 Population Monitoring
- [ ] Add per-species population tracking in GameManager
- [ ] Log population every 1000 ticks to file
- [ ] Detect extinction events (species count = 0)
- [ ] Detect explosion events (species count = cap)
- [ ] Add population graph to debug overlay

### 1.2 Balance Tuning
Current balance status:
| Species | Hunger Decay | Hunt Success | Reproduction | Status |
|---------|-------------|--------------|--------------|--------|
| Deer | 0.05/tick | N/A | Works | Stable |
| Rabbit | 0.08/tick | N/A | Works | Stable |
| Wolf | 0.04/tick | ~60% | Works | Needs testing |
| Fox | 0.035/tick | ~70% | Works | Needs testing |

Tasks:
- [ ] Run 10-minute simulation, log population curves
- [ ] Adjust predator hunger decay if dying out
- [ ] Adjust reproduction thresholds if population unstable
- [ ] Verify hunt success rates with spatial hash optimization

### 1.3 Spawn Distribution
- [x] Fix spawn ratio (entity counts, not spawn events)
- [x] Biome-aware spawning
- [x] Tile-aware spawning (species-specific)
- [ ] Verify initial distribution across world
- [ ] Add spawn logging for debugging

---

## Phase 2: World Generation v2 (Priority: HIGH) — COMPLETE

**Goal**: Diverse, biome-rich terrain with ecological variety and landmarks

### 2.1 River System — COMPLETE (v2: Flow-Based)
Rivers generated via elevation-based flow accumulation. A global pre-pass builds a
512x512 elevation map, selects high-altitude sources, traces rivers downhill using
steepest descent, and fills depressions into lakes. Replaces old noise zero-crossing.

- [x] RiverMapper class: global elevation pre-pass using same noise + domain warping
- [x] Source selection from high-elevation tiles (>0.68, spaced 12+ tiles apart, up to 80 sources)
- [x] Steepest-descent river tracing from sources to ocean/edge
- [x] Flow accumulation: multiple streams merge into wider rivers (flow >= 12 = 2-tile wide)
- [x] Depression filling: rivers that hit local minima fill to form lakes (ShallowWater)
- [x] Lake overflow: filled depressions overflow and continue river downstream
- [x] Wetland banks: land tiles adjacent to rivers/lakes become Wetland
- [x] River speed (0.35x), discomfort (8.0), avoidance (0.8) rules
- [x] Rivers are walkable but very uncomfortable (drives creatures away naturally)
- [x] Old noise-based _riverNoise removed; lakes moved from landmark pass to flow system
- [ ] Future: Fish species, waterfall features at elevation drops

### 2.2 Tile Depletion & Regrowth — COMPLETE
Grazed tiles deplete over time, slowly regenerating — driving natural migration patterns.

- [x] Per-tile nutrition field in Chunk (float array, 0.0 depleted → 1.0 full)
- [x] Nutrition initialized on chunk generation (grazeable tiles start at 1.0)
- [x] GrazingSystem consumes tile nutrition when herbivores feed (0.02/tick)
- [x] Food gained scales with remaining nutrition (depleted tiles yield less food)
- [x] TileRegenerationSystem regenerates nutrition on all grazeable tiles (0.0005/tick)
- [x] TerrainDiscomfortSystem applies grazing pressure on depleted tiles (< 0.3 nutrition)
- [x] Nutrition resets when tile type changes (terraform or landmark overwrite)
- [ ] Future: Seasonal variation, visual depletion indicator

### 2.3 Domain Warping & Biome Shaping — COMPLETE
Organic biome boundaries with temperature-driven regional biome distribution.

- [x] Domain warping via two independent noise layers (warpX, warpY, amplitude 12 tiles)
- [x] Warped coordinates fed to elevation and moisture noise for organic boundaries
- [x] Temperature noise layer (frequency 0.008, 3 octaves) for regional variation
- [x] Latitude gradient (30% weight) — top of world cooler, bottom warmer
- [x] Altitude cooling — high elevation reduces temperature (mountains are cold)
- [x] Temperature drives biome selection: arctic (<0.35), temperate (0.35-0.6), tropical (>0.6)
- [x] Biomes form large coherent regions via low-frequency temperature noise
- [x] 3 new BiomeType values: Arctic, Tropical, Volcanic

### 2.4 New Tile Types — COMPLETE
Eleven new tile types expand biome variety across arctic, tropical, volcanic, and
transitional zones. Six initial biome tiles plus five moisture-spectrum transitional tiles.

| Tile | Biome | Walkable | Spawnable | Speed | Discomfort | Notes |
|------|-------|----------|-----------|-------|-----------|-------|
| Tundra | Arctic | Yes | Yes | 0.5x | 2.0 | Frozen ground; grazeable=no |
| Ice | Arctic | Yes | No | 0.45x | 4.0 | Frozen water; barren |
| Steppe | Arctic | Yes | Yes | 0.9x | 0.8 | Cold open grassland; grazeable |
| Taiga | Forest | Yes | Yes | 0.8x | 0.4 | Cold dense forest; grazeable, cover (0.2) |
| Savanna | Tropical | Yes | Yes | 1.05x | 0.0 | Sparse grass; grazeable, cover (0.05) |
| Jungle | Tropical | Yes | Yes | 0.6x | 0.3 | Dense vegetation; grazeable, high cover (0.4) |
| Dirt | Desert | Yes | Yes | 0.9x | 0.2 | Packed earth; between Sand and Shrubland |
| Shrubland | Grassland | Yes | Yes | 0.95x | 0.0 | Sparse bushes; grazeable, cover (0.1) |
| Bog | Wetland | Yes | Yes | 0.55x | 0.8 | Waterlogged ground; wettest extreme, cover (0.15) |
| Reef | Coast | Yes* | No | 0.35x | 6.0 | Shallow rocky water; walkable but uncomfortable |
| Lava | Volcanic | No | No | 0.05x | 20.0 | Impassable volcanic terrain |

*Reef is technically walkable but slow and uncomfortable, like other water tiles.

- [x] Add tile types to TileType enum (Tundra=9, Ice=10, Savanna=11, Jungle=12, Reef=13, Lava=14, Dirt=15, Taiga=16, Steppe=17, Shrubland=18, Bog=19)
- [x] Extend all tile extension methods (IsWalkable, IsSpawnable, IsGrazeable, IsTerraformable, speed, discomfort, avoidance)
- [x] Added GetCoverBonus() extension for stealth mechanics (Jungle: 0.4, Forest/Taiga: 0.2, Wetland/Bog: 0.15, Shrubland: 0.1, Savanna: 0.05)
- [x] Wire into TerrainGenerator with temperature-based thresholds
- [x] Full moisture spectrum: Arid → Sand → Dirt → Shrubland → Grass → Forest → Wetland → Bog
- [x] Arctic temperature band: Ice/Tundra/Steppe (cold) → Taiga → temperate
- [x] Update terraform shift chains:
  - Full moisture chain: Arid↔Sand↔Dirt↔Shrubland↔Grass↔Forest↔Wetland↔Bog
  - Arctic chain: Tundra↔Steppe↔Taiga→Forest (wetter), Tundra→Arid (drier)
  - Tropical chain: Savanna↔Jungle
  - Balanced: all extremes shift toward Grass
- [x] Reef generated in warm shallow coastal zones (temperature > 0.7, elevation > 0.35)
- [x] Lava generated in hot dry mountain zones (temperature > 0.65, moisture < 0.3)
- [x] Update tile colors in Chunk.GetTileColor()

### 2.5 Terrain Features & Landmarks — COMPLETE
Post-processing landmark pass runs after base terrain generation in each chunk.

| Feature | Trigger | Effect |
|---------|---------|--------|
| Lakes | Low elevation + high moisture + high landmark noise | Grass/Wetland → ShallowWater |
| Oases | Desert + high landmark noise + moderate moisture | Arid/Sand → Grass |
| Forest Clearings | Forest + low landmark noise | Forest → Grass |
| Jungle Clearings | Jungle + very low landmark noise | Jungle → Savanna |
| Surface Caves | Mountain + high landmark noise + low elevation | Mountain → Grass |

- [x] Landmark noise layer (frequency 0.04, seed+9000)
- [x] ApplyLandmarks() post-processing pass in TerrainGenerator
- [x] Lake generation from low-elevation wet land depressions
- [x] Oasis placement in desert biomes (rare, noise > 0.7)
- [x] Forest clearing generation (grass gaps inside forest, noise < -0.65)
- [x] Jungle clearing generation (savanna patches inside jungle, noise < -0.7)
- [x] Surface cave generation (walkable grass in mountain edges, noise > 0.75)
- [ ] Future: Mountain ridge noise layer for linear barriers with passes

---

## Phase 3: Species Diversity (Priority: MEDIUM) — IN PROGRESS

**Goal**: Expand from 8 species to ~25-30, with multiple species per biome
and distinct aquatic/arctic/tropical/desert ecosystems

The world now has 28 species across 6 biome categories: 5 original generalists,
3 faction species, and 20 new biome-specific species.

### 3.1 Aquatic Species — COMPLETE

Shore interactions already working:
- [x] Crocodile hunts prey at water's edge via ambush hunting (stealth + pounce)
- [x] Prey detection reduced by crocodile stealth (up to 90% range reduction)

Movement prerequisites:
- [ ] Verify MovementSystem handles aquatic speed correctly
- [ ] Add drowning: land creatures in deep water take energy damage over time
- [ ] Add suffocation: aquatic creatures on land take energy damage over time

New species:
| Species | Role | Social | Notes |
|---------|------|--------|-------|
| Fish | Aquatic prey | Herd (schooling) | Small, fast in water, school behavior, primary aquatic food source |
| Shark | Aquatic apex | Solitary | Large, fast, hunts fish and anything in water |
| Frog | Semi-aquatic prey | Solitary | Spawns on river/wetland banks, eats insects (future), eaten by Fox/Crocodile |
| Turtle | Semi-aquatic herb. | Solitary | Very slow, very tough (high mass), grazes on water-adjacent grass |

Tasks:
- [x] Add Fish species (IsAquatic=true, schooling herd, small mass, feeds from water tiles)
- [x] Add Shark species (IsAquatic=true, solitary, high mass, hunts fish)
- [x] Add Frog species (semi-aquatic, river/wetland spawning, low mass)
- [x] Add Turtle species (semi-aquatic, high mass, very slow, long-lived)
- [ ] Fish schooling behavior: tighter herding in water, scatter on predator

### 3.2 Grassland/Forest Species (Temperate) — COMPLETE

| Species | Role | Social | Notes |
|---------|------|--------|-------|
| Elk | Large herbivore | Herd | Bigger than Deer, harder to hunt solo, grassland specialist |
| Boar | Omnivore | Pack | Aggressive herbivore, fights back (Defensive fear), forest specialist |
| Bear | Apex predator | Solitary | Very large, slow, powerful — hunts Deer/Elk/Boar, forest specialist |
| Hawk | Small predator | Solitary | Fast, hunts Rabbit/Frog, ignores terrain speed penalties (flying) |

Tasks:
- [x] Add Elk species (high mass, large herd, grassland biome preference)
- [x] Add Boar species (Omnivore diet, Defensive fear response, forest preference)
- [x] Add Bear species (solitary apex, very high mass, slow, forest biome)
- [x] Add Hawk species (IsFlying=true, ignores terrain speed mods, hunts small prey)
- [x] Implement Omnivore diet in GrazingSystem (SpeciesType.Omnivore, graze + hunt)
- [x] Implement flying movement flag (bypass terrain speed in MovementSystem, skip discomfort in TerrainDiscomfortSystem)

### 3.3 Desert/Arid Species — COMPLETE

| Species | Role | Social | Notes |
|---------|------|--------|-------|
| Lizard | Desert prey | Solitary | Small, fast on sand/arid, slow elsewhere, eaten by Scorpion/Snake |
| Scorpion | Desert predator | Solitary | Ambush hunter on sand, venomous (DOT damage), hunts Lizard/Rabbit |
| Camel | Desert herbivore | Herd | Large, slow hunger decay (desert adapted), grazes on sparse arid tiles |
| Snake | Desert predator | Solitary | Stealth hunter, very fast strike, hunts Lizard/Frog |

Tasks:
- [x] Add Lizard species (small, fast on arid/sand, desert biome only)
- [x] Add Scorpion species (ambush, venom DOT, desert biome only)
- [x] Add Camel species (herd, very slow hunger decay, desert specialist)
- [x] Add Snake species (stealth hunter + venom, fast attack, desert/grassland)
- [x] Implement venom DOT mechanic (VenomEffect component, applied in HuntingSystem, processed in HungerSystem)
- [x] Make Arid tiles grazeable at reduced nutrition (0.15) for desert herbivores

### 3.4 Arctic/Tundra Species — COMPLETE

| Species | Role | Social | Notes |
|---------|------|--------|-------|
| Penguin | Arctic prey | Herd | Semi-aquatic, huddles for warmth (tight herding), ice/tundra biome |
| Polar Bear | Arctic apex | Solitary | Hunts penguins and fish, semi-aquatic, ice/tundra biome |
| Arctic Fox | Arctic predator | Solitary | Small, fast, hunts Penguin/Rabbit, tundra/grassland edge |
| Musk Ox | Arctic herbivore | Herd | Tough, slow, Defensive fear response, tundra grazer |

Tasks:
- [x] Add Penguin species (semi-aquatic, tight huddle herding, ice/tundra)
- [x] Add Polar Bear species (semi-aquatic apex, ice/tundra)
- [x] Add Arctic Fox species (small predator, tundra biome)
- [x] Add Musk Ox species (high mass, Defensive fear, tundra grazer)
- [x] Tundra tiles grazeable at reduced nutrition (0.2)

### 3.5 Tropical/Jungle Species — COMPLETE

| Species | Role | Social | Notes |
|---------|------|--------|-------|
| Monkey | Tropical prey | Herd | Fast, forest/jungle specialist, erratic movement, hard to catch |
| Parrot | Tropical prey | Herd | Flying, small, forest/jungle specialist |
| Jaguar | Tropical apex | Solitary | Ambush hunter in jungle, stealth bonus in dense vegetation |
| Tapir | Tropical herbivore | Herd | Medium size, jungle grazer, shy (low fear threshold) |

Tasks:
- [x] Add Monkey species (fast, erratic, forest/jungle preference)
- [x] Add Parrot species (IsFlying=true, small, herd, tropical biome)
- [x] Add Jaguar species (ambush hunter, jungle stealth bonus)
- [x] Add Tapir species (jungle herbivore, shy, medium mass)
- [x] Jungle tile: high cover bonus for stealth hunters (GetCoverBonus: Jungle 0.4)

### 3.6 Species Framework Tasks — MOSTLY COMPLETE
Shared work needed before/during species expansion:

- [x] Omnivore diet type implementation (SpeciesType.Omnivore, GrazingSystem + HuntingSystem)
- [x] Flying movement flag (IsFlying: bypass terrain speed in MovementSystem, skip discomfort in TerrainDiscomfortSystem)
- [x] Venom/DOT damage component (VenomEffect struct, ComponentFlags.VenomEffect, applied in HuntingSystem, processed in HungerSystem)
- [x] Terrain-based stealth bonuses (GetCoverBonus: Jungle 0.4, Forest/Taiga 0.2, Wetland/Bog 0.15, Shrubland 0.1, Savanna 0.05)
- [x] Biome-restricted spawning (PreferredBiomes + AllowedSpawnTiles on all 20 new species)
- [x] Rebalance InitialPopulation distribution for 28 species (SpawnWeight per species, weighted budget in WorldSpawner)
- [x] Soft hunt threshold (predators hunt opportunistically when well-fed, stop at 95%+)
- [x] Reproduction tuning for prolific species (higher cooldowns/costs for Fish, Frog, Lizard, Rabbit, Boar)
- [x] Drowning/suffocation mechanics (WrongElementTicks grace period, energy-scaled damage in TerrainDiscomfortSystem)
- [ ] Per-biome spawn budgets (species count proportional to biome tile area)

---

## Phase 4: Faction Species (Priority: MEDIUM) - COMPLETE

**Goal**: Three unique faction species with emergent dynamics

### 4.1 Shroomer (Fungi Faction) - COMPLETE
- [x] Added Shroomer to SpeciesRegistry (Terraformer diet, Herd social)
- [x] SporeSystem: Mature shroomers spread spores on wet terrain
- [x] Spore lifecycle: moisture accumulation on wet tiles, withering on dry, transformation
- [x] Growth system: continuous scale increase up to 4x, affects AoE and HP
- [x] AoE attack: S-curve (smoothstep) growth scaling — nearly harmless at birth, devastating
  at maturity. Passive pulse every 350 ticks, combat pulse every 60 ticks.
- [x] Thorn defense: melee attackers take growth-scaled counter-damage (1.6→20 dmg/hit)
- [x] Terraform: Wetter direction (shifts tiles toward Wetland)
- [x] Feeds on Wetland/Forest tiles
- [x] Freeze fear response (plays dead when threatened)

### 4.2 Sectid (Insect Faction) - COMPLETE
- [x] Added Sectid to SpeciesRegistry (Terraformer diet, Pack social)
- [x] NestSystem: 3-stage nests convert food into larvae
- [x] Food delivery: FoodCarrier component, Sectids carry kills to nests
- [x] Colony expansion: nests found new nests nearby or distant colonies
- [x] HuntingTactic: Swarm — colony-wide rush, no retreat, targets any living creature
- [x] Species-specific terrain comfort penalty in target selection
- [x] Terraform: Drier direction (shifts tiles toward Arid)
- [x] Hunts for food (no tile feeding — must hunt to survive)

### 4.3 Faeling (Crystal Faction) - COMPLETE
- [x] Added Faeling to SpeciesRegistry (Terraformer diet, Solitary)
- [x] CrystalSystem: crystals spawn/respawn linked Faelings
- [x] Power system: gains power from kills and tile restoration
- [x] Power inheritance: 50% passes to crystal on death, then to next Faeling
- [x] Ranged attack: targets Sectids/Shroomers at 12 tile range, 12 damage, 20 tick cooldown
- [x] Terrain-seeking patrol AI: samples 8 directions, roams toward damaged terrain
- [x] Immune to starvation, predator hunting, and terrain discomfort
- [x] Terraform: Balanced direction (shifts extremes toward Grass)
- [x] Growth system: up to 2.5x scale, boosted by power

### 4.4 Faction Interactions - COMPLETE
- [x] Faeling ranged attacks target Sectids and Shroomers
- [x] Shroomer AoE attacks target Sectids and Faelings (S-curve scaling)
- [x] Sectids hunt spores (Shroomer offspring) and any living creature via swarm tactic
- [x] Competing terraforming: wet vs dry vs balanced creates dynamic terrain conflict
- [ ] Not implemented: DecomposerSystem, formal territory conflict system

### 4.5 Hunting System Refactoring - COMPLETE
- [x] HuntingTactic enum: Solo, PackCoordinated, Swarm, Ambush
- [x] Clean tactic-based movement dispatch (switch instead of scattered boolean checks)
- [x] Per-species hunting behavior assignment in SpeciesRegistry (13 predator species)
- [x] Species-specific terrain comfort penalty in target selection
- [x] Anti-target-fixation: longer suppress timers after discomfort abandon (120 ticks swarm)
- [x] Extracted ApplyAmbushMovement as dedicated method
- [x] Thorn damage data-driven via SpeciesDefinition (ThornDamageBase, S-curve scaling)

---

## Phase 5: LOD & Scale (Priority: HIGH) — IN PROGRESS

**Goal**: 10,000+ entities with smooth performance via statistical simulation of distant chunks

### 5.1 Statistical Simulation — IMPLEMENTED

The StatisticalSimSystem runs population-level birth/death math for chunks beyond 100 tiles
from the player. When the player approaches, entities are materialized from aggregate data.

**Core architecture:**
- `ChunkPopulationData` — per-chunk, per-species aggregate (count, avg hunger, avg age)
- `StatisticalSimSystem` — aggregation, statistical tick (every 30 ticks), materialization
- Aggregation: entity → SpeciesPopulation (destroy entity, record stats)
- Materialization: SpeciesPopulation → entities (spawn via EntityFactory with cap check)

Implemented:
- [x] ChunkPopulationData structure (per-species count, avg hunger/age, fractional births/deaths)
- [x] StatisticalSimSystem with aggregation, statistical tick, materialization
- [x] Entity aggregation when chunk becomes distant (>100 tiles)
- [x] Entity materialization when chunk becomes nearby (capped at MaxPopulation)
- [x] Statistical terraforming (faction species change tiles while distant)
- [x] CountFeedTiles for faction species carrying capacity

Remaining:
- [ ] Population monitoring: log per-species counts every N ticks for balance verification
- [ ] Smoother materialization (currently spawns all entities in tight cluster)
- [ ] Validate statistical sim produces similar outcomes to entity sim over time

### 5.2 LOD Gating — REVISED

All survival/reproduction systems are now gated at consistent LOD thresholds to prevent
asymmetric behavior between near and far entities. The key invariant: **if a system that
produces a resource (food, offspring) is gated, the system that consumes that resource
(hunger, death) must be gated at the same level.**

Current LOD gate map:

| System | LOD Gate | Rationale |
|--------|----------|-----------|
| LODSystem | None (always) | Must run first to set LOD levels |
| MovementSystem | None (always) | Position must be current for all queries |
| **HungerSystem** | **Statistical+** | Must match GrazingSystem — otherwise entities starve without ability to eat |
| **AgingSystem** | **Statistical+** | Statistical sim handles age/death for distant entities |
| **GrazingSystem** | **Statistical+** | Must match HungerSystem — food and hunger in same LOD band |
| **ReproductionSystem** | **Statistical+** | Must match hunger/grazing — otherwise entities breed without hunger cost |
| **SporeSystem (spread)** | **Reduced+** | Shroomer spore creation — most aggressive gate to break feedback loop |
| **SporeSystem (maturation)** | **Statistical+** | Spore moisture accumulation |
| SporeSystem (AoE) | Reduced+ | Combat — fine to skip at distance |
| HuntingSystem | Aggregate+ | Predators need to hunt to survive |
| FleeingSystem | Aggregate+ | Must match hunting gate |
| TerraformSystem | Statistical+ | Tile changes handled by statistical terraforming |
| TerrainDiscomfort | Reduced+ | Behavioral comfort — cosmetic at distance |
| CollisionSystem | Reduced+ | Spatial overlap — cosmetic at distance |
| WanderSystem | ShouldUpdate() | Tick-rate throttled per LOD |
| SocialSystem | ShouldUpdate() | Tick-rate throttled per LOD |

**Critical invariant violated previously:**
- HungerSystem, AgingSystem, GrazingSystem had **no LOD gate** (ran every tick for all entities)
- ReproductionSystem gated at Aggregate (LOD 3) only
- SporeSystem had **no LOD gate** on spread or maturation
- Result: entities at Reduced/Statistical LOD could eat and breed every tick but predators
  couldn't hunt (collision gated at Reduced+), creating massive prey population explosion
  near the player

### 5.3 Population Control — IMPLEMENTED

Multiple layers of population control prevent runaway growth:

**Entity-level (near player):**
1. **EntityFactory hard cap** — `SpawnCreature()` refuses to spawn beyond `MaxPopulation`
2. **ReproductionSystem cap checks** — checked before queuing AND before spawning
3. **Global population pressure** — linear ramp: 100% birth chance at 50% cap → 0% at 100% cap
4. **Local density suppression** — spatial hash query within `SocialRadius * 1.5`:
   hard cap at `PreferredGroupSize * 2` same-species nearby

**Statistical-level (distant chunks):**
1. **Carrying capacity** — per-species, per-chunk based on actual food availability:
   - Herbivores: `(GrazeableTileCount * RegenerationRate) / (HungerDecayRate / GrazeNutrition)`
   - Predators: `totalHerbivores * 0.15` (1 predator per ~7 prey)
   - Terraformers: `CountFeedTiles() / (HungerDecayRate / FeedNutrition)` (actual tile scan)
2. **Density suppression** — linear ramp from 50% to 100% of carrying capacity
3. **Zero-capacity starvation** — when `carryingCapacity <= 0`: births = 0, hunger drops rapidly
4. **Hard per-chunk cap** — `min(pop.Count, carryingCapacity * 2)` safety net
5. **Hunger model** — per-species hunger drift based on food availability:
   - Herbivores: supply vs demand comparison (demand = count × HungerDecayRate / GrazeNutrition)
   - Predators: prey/predator ratio check
   - Terraformers: feed tile count vs population (actual chunk tile scan)

### 5.4 Known Issues & Remaining Work

**Observed behavior that still needs investigation:**
- Rabbits tend to die within seconds of simulation start — may indicate balance issue
  with hunger decay (0.08) vs grazing opportunity, or initial spawn placement on non-food tiles
- Predators die off shortly after herbivores establish — hunt cycle timing vs hunger drain
  rate may be unfavorable (time-to-find + time-to-chase + cooldown > hunger budget)
- Ecological cascades (prey explosion after predator collapse) are natural but the timing
  and magnitude should be comparable between entity sim and statistical sim
- Materialization spawns entities in tight cluster at chunk center — should spread across
  chunk using `GetSpawnablePositionsForSpecies` more effectively

**Entity sim vs statistical sim consistency:**
The fundamental challenge is that the statistical sim is an approximation. It cannot model:
- Individual hunting behavior (chase duration, success rate, terrain obstacles)
- Spatial distribution within a chunk (clustering, edge effects)
- Spore lifecycle (moisture accumulation, withering, predation on spores)
- Terrain discomfort driving migration

To improve consistency:
- [ ] Run parallel entity/statistical sims and compare population curves
- [ ] Calibrate statistical birth/death rates from observed entity sim data
- [ ] Add migration model (species moving between adjacent statistical chunks)
- [ ] Model spore lifecycle statistically (spread rate × survival rate × maturation rate)

### 5.5 Performance Profiling
- [ ] Add per-system timing to debug overlay
- [ ] Identify hotspots in each system
- [ ] Profile memory allocation patterns
- [ ] Test with 5K, 10K, 20K entities

### 5.6 Optimization Targets
Based on profiling:
- [ ] SpatialHash optimization if needed
- [ ] Consider SIMD for position updates
- [ ] Batch entity creation/destruction
- [ ] Object pooling for temporary lists

---

## Phase 6: Inner Species Variety (Priority: HIGH)

**Goal**: Individual variation within species, making each creature unique
and enabling natural selection to emerge before player-directed mutation

Currently all creatures of a species spawn with identical stats. The
`StatVariation` property (0.15-0.25) exists on SpeciesDefinition but is
unused. The `Generation` counter in the Species component is also unused.

### 6.1 Trait Variation at Birth
Design:
- On spawn, each creature gets ±StatVariation applied to key stats
- Stats vary independently (one rabbit can be fast but fragile, another slow but tough)
- Stored per-entity as multipliers so base species values still apply

Varied stats (per-entity float multipliers, centered at 1.0):
| Trait | Affects | Range (at 0.15 variation) |
|-------|---------|---------------------------|
| SpeedTrait | WanderSpeed, HuntSpeed, FleeSpeed | 0.85 - 1.15 |
| SizeTrait | BodyMass, BaseSize, AttackPower | 0.85 - 1.15 |
| MetabolismTrait | HungerDecayRate, MaxHunger | 0.85 - 1.15 |
| SensesTrait | HuntRange, FleeRange, FearAccumulation | 0.85 - 1.15 |
| FertilityTrait | ReproCooldown (inverse), OffspringCount (rounded) | 0.85 - 1.15 |
| ResilienceTrait | MaxEnergy, StarvationDamage (inverse), MaxLifespan | 0.85 - 1.15 |

Tasks:
- [ ] Add Traits component (6 float multipliers, generation counter)
- [ ] EntityFactory applies random variation on creation (Gaussian, clamped)
- [ ] Systems read trait multipliers: Movement×Speed, Hunting×Senses, etc.
- [ ] Visual indicator: size varies by SizeTrait (already scales via Renderable)
- [ ] Generation counter increments on reproduction

### 6.2 Trait Inheritance
Design:
- Offspring inherit parent traits with slight mutation (drift)
- Single parent: offspring = parent traits ± small mutation
- Nearby mate bonus: if a same-species entity is within SocialRadius at
  reproduction time, average both parents' traits then mutate

| Parameter | Value |
|-----------|-------|
| MutationRate | 0.03 per trait per generation |
| MutationDistribution | Gaussian, mean 0, stddev = MutationRate |
| Clamp range | 0.5 - 2.0 (hard limits to prevent runaway) |

Tasks:
- [ ] ReproductionSystem passes parent Traits to offspring
- [ ] Apply per-trait Gaussian mutation
- [ ] Mate detection: find nearest same-species within SocialRadius
- [ ] Two-parent averaging when mate found
- [ ] Track generation number

### 6.3 Faction Subspecies / Morphs
Faction species (Shroomer, Sectid, Faeling) should have the most visible
inner variety since they're the species the player will interact with most.

**Shroomer Morphs** (differentiated by growth path):
| Morph | Trigger | Effect |
|-------|---------|--------|
| Spreader | High FertilityTrait | More spores, smaller adult size, weaker AoE |
| Titan | High SizeTrait + old age | Massive AoE radius, slow, few spores |
| Runner | High SpeedTrait | Mobile shroomer, lower terraform but can colonize faster |

**Sectid Castes** (differentiated at nest spawn based on colony needs):
| Caste | Trigger | Effect |
|-------|---------|--------|
| Worker | Default | Current behavior (hunt, carry food, terraform) |
| Soldier | Colony under attack | Higher mass, attack power, lower carry capacity |
| Scout | Colony needs expansion | Higher speed, range, lower attack, finds nest sites |

**Faeling Aspects** (differentiated by power source):
| Aspect | Trigger | Effect |
|--------|---------|--------|
| Warden | High terraform count | Stronger terraform radius/strength, lower attack |
| Sentinel | High kill count | Stronger ranged attack, larger range, lower terraform |
| Wanderer | High roam distance | Faster, wider patrol, balanced stats |

Tasks:
- [ ] Add Morph/Caste/Aspect enum fields to faction components
- [ ] Shroomer morph selection: based on dominant trait at maturity
- [ ] Sectid caste assignment: NestSystem picks caste based on colony state
- [ ] Faeling aspect evolution: CrystalSystem tracks activity and assigns aspect
- [ ] Visual differentiation: color tint or size variation per morph/caste/aspect
- [ ] Morph-specific stat modifiers applied on top of trait variation

### 6.4 Natural Selection Observation
Design:
- Track trait distribution per species over time (mean + stddev per trait)
- Traits that improve survival should drift over generations
- Provides the foundation for player-directed mutation (Phase 9)

Tasks:
- [ ] Per-species trait statistics tracker (running mean/variance)
- [ ] Log trait distributions periodically
- [ ] Debug overlay: trait distribution graphs per species
- [ ] Detect trait drift: alert when mean shifts by >1 stddev from baseline

---

## Phase 7: Game Systems & UI (Priority: HIGH)

**Goal**: Full game interface — menus, HUD, controls, settings, camera modes

Currently the game has only a debug text overlay and WASD camera movement.
No menus, no pause, no entity inspection, no settings.

### 7.1 Main Menu
- [ ] Title screen with New Game / Continue / Settings / Quit
- [ ] New Game: seed input, world size, initial population sliders
- [ ] Continue: load from save (requires Phase 8 persistence)
- [ ] Godot UI scene (Control nodes), separate from game scene

### 7.2 HUD Overlay
Replace debug label with proper HUD:

| Element | Position | Content |
|---------|----------|---------|
| Minimap | Top-right | Chunk-resolution world view, player dot, biome colors |
| Population bar | Bottom | Per-species population counts, color-coded |
| Selected entity panel | Left | Stats, traits, species, age, hunger, energy, generation |
| Resource panel | Top-left | Player's collected resources (Phase 9) |
| Speed indicator | Bottom-right | Current sim speed, pause icon |
| Notification feed | Top-center | Events: extinctions, explosions, faction conflicts |

Tasks:
- [ ] Design HUD layout in Godot scene tree (CanvasLayer + Control nodes)
- [ ] Minimap: render chunk-resolution biome map to texture, update periodically
- [ ] Population bars: per-species colored bar, click to filter view
- [ ] Entity panel: shows on click/select, updates per-tick
- [ ] Notification system: event queue with fade-out messages
- [ ] Toggle HUD visibility (H key)

### 7.3 Entity Selection & Inspection
- [ ] Mouse click ray → find nearest entity to world position (SpatialHash query)
- [ ] Highlight selected entity (outline ring or glow)
- [ ] Follow mode: camera tracks selected entity (F key to toggle)
- [ ] Stats panel: hunger, energy, age, generation, traits, morph/caste
- [ ] AI state display: current behavior (wandering, hunting, fleeing, etc.)
- [ ] Lineage view: show parent/offspring chain if tracked

### 7.4 Time Controls
- [ ] Pause (Space): freeze simulation, rendering continues
- [ ] Speed: 0.5x, 1x, 2x, 4x, 8x (adjust TargetTPS or process multiple ticks/frame)
- [ ] Step: advance one tick while paused (. key)
- [ ] Visual indicator for current speed
- [ ] Pause on event option (extinction, first kill, etc.)

### 7.5 Camera Modes
- [ ] Free camera (current WASD, default)
- [ ] Follow entity (lock to selected creature)
- [ ] Overview mode (zoom out to full world, minimap replaces main view)
- [ ] Cinematic mode (auto-pan to interesting events: hunts, extinctions, faction wars)

### 7.6 Settings / Options
- [ ] Resolution, fullscreen, vsync
- [ ] Simulation: TPS, max entities, initial population
- [ ] World: seed, size, biome balance
- [ ] Audio: volume sliders (when audio is added)
- [ ] Controls: rebindable keys
- [ ] Persist settings to user config file

### 7.7 Debug Console (Developer)
- [ ] Toggle with backtick (`) key
- [ ] Commands: spawn [species] [x] [y], kill [id], tp [x] [y], setspeed [n]
- [ ] Commands: population [species], traits [species], forcerepr [id]
- [ ] God mode: invincible player, instant kill, teleport

---

## Phase 8: Persistence (Priority: MEDIUM)

**Goal**: Save and load full world state

### 8.1 Save Format Design
Design considerations:
- JSON for human readability and debugging
- Optional binary (MessagePack or custom) for performance with 10K+ entities
- Version field for forward compatibility
- Delta saves for auto-save performance

Structure:
```json
{
  "version": "0.5",
  "seed": 42,
  "tick": 12345,
  "settings": { ... },
  "player": { "position": [x,y], "resources": { ... } },
  "chunks": [ { "cx": 0, "cy": 0, "tiles": [...], "nutrition": [...] } ],
  "entities": [ { "id": 0, "components": { ... }, "traits": { ... } } ],
  "speciesStats": { "Deer": { "traitMeans": [...] }, ... }
}
```

### 8.2 Implementation
- [ ] Serialize chunk terrain data (tile types + nutrition if implemented)
- [ ] Serialize entity components (all SoA arrays for alive entities)
- [ ] Serialize trait data and generation counters
- [ ] Serialize player state (position, resources, abilities)
- [ ] Save/load UI integration with main menu
- [ ] Auto-save timer (configurable interval, 3 rotating slots)
- [ ] Save file browser with world preview (seed, tick, species counts)

---

## Phase 9: Player Progression & Directed Mutation (Priority: MEDIUM)

**Goal**: Transform the player from a passive spectator into an active
participant who gathers resources from the ecosystem and uses them to
direct species mutations, shaping evolution organically

Design philosophy:
- The player moves through the world observing creatures, not fighting them
- Interacting with (near, observing, collecting from) creatures yields resources
- Resources are species-specific and trait-specific
- Resources are spent to nudge mutation direction in target species
- The player doesn't control creatures directly — they influence evolution

### 9.1 Resource System

**Resource types** — gathered passively by proximity and actively by interaction:

| Resource | Source | Gathering Method |
|----------|--------|-----------------|
| Genetic Sample | Any creature | Be near creature for N seconds (observation radius) |
| Vital Essence | Creature death nearby | Automatic on death within collection radius |
| Predator Instinct | Predator kill nearby | Automatic when hunt kill happens near player |
| Herd Wisdom | Large herd nearby | Automatic when near 5+ same-species group |
| Terraform Residue | Faction terraform nearby | Automatic when terrain shifts near player |
| Spore Extract | Shroomer spore nearby | Collect from mature spores (click interaction) |
| Nest Material | Sectid nest nearby | Collect from nests (click interaction, risks aggro) |
| Crystal Shard | Faeling crystal | Collect from crystals (click interaction) |

Tasks:
- [ ] Add ResourceInventory component/class on player entity
- [ ] ResourceGatheringSystem: proximity-based passive collection
- [ ] Click-to-collect for active resources (Spore, Nest, Crystal)
- [ ] Collection radius (configurable, ~3-5 tiles)
- [ ] Visual feedback: floating resource icons on collection
- [ ] Resource panel in HUD (Phase 7.2)
- [ ] Species-specific resource quality (rarer species = rarer resources)

### 9.2 Species Catalog / Bestiary

As the player observes species, they unlock knowledge:

| Observation Level | Requirement | Unlocks |
|-------------------|-------------|---------|
| Discovered | See 1 creature of species | Name, appearance, diet type |
| Studied | Observe 10 for 30s+ each | Base stats, behavior patterns, biome |
| Analyzed | Collect 5 Genetic Samples | Trait distribution, generation count |
| Mastered | Collect 20 samples + 5 Essences | Mutation interface unlocked for this species |

Tasks:
- [ ] Add CatalogEntry per species (observation count, time observed, samples collected)
- [ ] CatalogSystem: tracks player proximity to creatures, increments counters
- [ ] Bestiary UI panel: species list with unlock progress
- [ ] Species detail view: stats, trait graphs, population history
- [ ] "Mastered" state gates access to mutation interface

### 9.3 Directed Mutation Interface

Once a species is Mastered, the player can spend resources to bias its evolution:

**Mutation nudges** (not direct stat editing — influence, not control):

| Action | Cost | Effect |
|--------|------|--------|
| Boost Speed | 3 Genetic Samples + 1 Predator Instinct | +0.05 to SpeedTrait mean for next 10 generations |
| Boost Size | 3 Genetic Samples + 1 Herd Wisdom | +0.05 to SizeTrait mean for next 10 generations |
| Boost Senses | 3 Genetic Samples + 1 Vital Essence | +0.05 to SensesTrait mean for next 10 generations |
| Boost Resilience | 5 Genetic Samples + 2 Vital Essences | +0.05 to ResilienceTrait mean for next 10 generations |
| Boost Metabolism | 3 Genetic Samples + 1 Predator Instinct | +0.05 to MetabolismTrait mean for next 10 generations |
| Boost Fertility | 5 Genetic Samples + 2 Herd Wisdoms | +0.05 to FertilityTrait mean for next 10 generations |
| Faction Enhance | 5 faction-specific resources | Enhance faction morph/caste probabilities |

Mechanism:
- Mutation nudges are stored per-species as temporary biases
- ReproductionSystem applies bias to offspring trait mutation (shift Gaussian mean)
- Bias decays over generations (10-gen half-life), not permanent
- Multiple nudges stack but with diminishing returns
- Player can see current bias and projected trait shift in Bestiary

Tasks:
- [ ] Add MutationBias struct per species (per-trait bias, remaining generations)
- [ ] Mutation UI: select species → select trait → confirm cost → apply bias
- [ ] ReproductionSystem reads bias and shifts offspring trait mutation mean
- [ ] Bias decay: reduce by 10% per generation
- [ ] Visual: species with active bias get a subtle glow/icon
- [ ] Faction-specific mutations: boost morph/caste probabilities

### 9.4 Expanded SpeciesDefinition for Progression

New properties needed on SpeciesDefinition to support resource gathering
and player interaction:

| Property | Type | Purpose |
|----------|------|---------|
| ResourceYield | Dictionary<ResourceType, float> | How much of each resource this species gives |
| ObservationDifficulty | float | How long player must be near to gather (shy species = longer) |
| AggroOnCollect | bool | Whether active collection triggers aggression (Sectid nests) |
| RarityTier | int (1-5) | Affects resource quality multiplier |
| TraitVisibility | float | How easily traits are visible (for Bestiary analysis) |

Tasks:
- [ ] Add new properties to SpeciesDefinition
- [ ] Configure per-species: rare species yield better resources
- [ ] Faction species have unique collectible resources
- [ ] Rarity tiers correlate with biome exclusivity

### 9.5 Player Abilities (Resource-Gated)

Long-term unlocks that cost large resource pools:

| Ability | Cost | Effect |
|---------|------|--------|
| Calm Aura | 10 Herd Wisdom | Creatures within 3 tiles ignore player (no flee) |
| Predator Cloak | 10 Predator Instinct | Predators within 3 tiles don't aggro player |
| Terraform Touch | 10 Terraform Residue + 5 Crystal Shards | Player can shift 1 tile per click |
| Migration Call | 15 Herd Wisdom + 5 Genetic Samples | Attract nearest herd to player position |
| Extinction Insight | 20 Vital Essences | Highlight endangered species on minimap |

Tasks:
- [ ] Add PlayerAbilities component with unlock flags
- [ ] Ability unlock UI in resource panel
- [ ] Implement each ability as a system or modifier
- [ ] Ability cooldowns where appropriate
- [ ] Visual feedback for active abilities

---

## Phase 10: Evolution & Speciation (Priority: LOW)

**Goal**: Long-running trait drift leads to observable speciation events

Builds on Phase 6 (trait variation) and Phase 9 (directed mutation).

### 10.1 Genetic Divergence Tracking
- [ ] Track per-population trait variance (geographically separated groups)
- [ ] Detect when two groups of same species diverge beyond threshold
- [ ] Speciation event: split into two species entries in registry
- [ ] New species inherits parent's base stats modified by diverged traits

### 10.2 Speciation Effects
- [ ] New species gets unique name (procedural: "Mountain Deer", "Swamp Rabbit")
- [ ] New species may not interbreed with parent species
- [ ] New species gets own population tracking and Bestiary entry
- [ ] Notification to player: "A new species has emerged!"

### 10.3 Extinction Mechanics
- [ ] Detect population = 0 for a species
- [ ] Log extinction event with cause analysis (starvation, predation, habitat loss)
- [ ] Bestiary marks species as extinct (grayed out, preserves data)
- [ ] Ecological cascade: track downstream effects on food web

---

## Technical Debt

### Known Issues
- [ ] Creatures can still spawn in valid tiles but isolated positions
- [x] ~~Pack hunting coordination could be tighter~~ → Flanking system with roles
- [ ] Fear decay might be too slow for some species
- [ ] Sectid population balance — starve out despite hunting; will worsen once Shroomers fight back
- [ ] TerrainSpeedModifiers not yet wired into MovementSystem (uses tile defaults only)

### Code Quality
- [ ] Add XML documentation to all public methods
- [ ] Unit tests for core systems
- [ ] Integration tests for ecosystem balance

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| v0.1 | Jan 2026 | Core ECS, basic rendering |
| v0.2 | Feb 2026 | Species system, reproduction, fear, terrain discomfort, biome spawning |
| v0.3 | Feb 2026 | Faction species (Shroomer/Sectid/Faeling), terraform system, nest/spore/crystal systems, pack tactics, mass-based hunting, AoE/ranged attacks, growth system, power inheritance |
| v0.4 | Feb 2026 | Code reorganization (BehaviorSystems.cs split into 8 files, GameManager split into 4), entity jitter fixes (velocity damping, mass-based direction blending, hysteresis terrain escape, herding/escape priority), coordinated wolf flanking (Leader/Flanker/Disruptor roles, convergence triggers), crocodile ambush hunting (stealth mechanics, stalking, pounce burst), stealth-aware prey detection |
| v0.5 | Feb 2026 | World Generation v2: 11 new tile types (Tundra, Ice, Steppe, Taiga, Savanna, Jungle, Dirt, Shrubland, Bog, Reef, Lava), 3 new biome types (Arctic, Tropical, Volcanic), full moisture spectrum (Arid→Sand→Dirt→Shrubland→Grass→Forest→Wetland→Bog), domain warping for organic biome boundaries, temperature noise + latitude gradient, tile depletion/regrowth system (per-tile nutrition), TileRegenerationSystem, nutrition-dependent grazing, landmark post-processing (oases, clearings, caves), terrain cover bonus for stealth (Jungle/Forest/Taiga/Wetland/Bog/Shrubland/Savanna), flow-based river system (RiverMapper: elevation tracing, flow accumulation, depression lakes, wetland banks) replacing old noise zero-crossing |

---

*Last Updated: February 2026*
*Engine: Godot 4.6 with C#*
