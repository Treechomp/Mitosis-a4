# Mitosis Godot 4.6 Development Roadmap

> For comprehensive documentation of all implemented features, systems, and species,
> see **[FEATURES_AND_DESIGN.md](FEATURES_AND_DESIGN.md)**.

## Current State Summary (v0.3 - Full Ecosystem with Factions)

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
│  • Systems - 17 systems across 15 files (20 TPS)           │
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
- [x] 8 species defined: Deer, Rabbit, Wolf, Fox, Crocodile, Shroomer, Sectid, Faeling
- [x] Diet types: Herbivore, Carnivore, Terraformer (Omnivore prepared)
- [x] Social types: Herd, Pack, Solitary
- [x] Biome preferences per species
- [x] Spawn tile preferences (IsAquatic, AllowedSpawnTiles, CanSpawnOnTile)
- [x] Terrain speed/comfort modifiers per species
- [x] Faction species (Shroomer, Sectid, Faeling) with full definitions
- [x] Mass-based hunting system (solo/pack hunt ratios)
- [x] Preferred prey system with bias scoring
- [x] Faction-specific fields: spore, nest, crystal, terraform, AoE, growth, ranged attack

#### Terrain & World (90% Complete)
- [x] Chunk-based storage (32x32 tiles)
- [x] Simplex noise terrain generation
- [x] 9 tile types with walkability/speed rules
- [x] 8 biome types (Ocean, Coast, Grassland, Forest, Desert, Mountain, Wetland, River)
- [x] IsSpawnable(), IsWater(), IsGrazeable() tile queries
- [x] BiomeType calculation from temperature/moisture
- [x] Species-specific spawn filtering (biome + tile)
- [x] River generation (noise-based zero-crossing with variable width and wetland banks)
- [ ] Missing: Tile depletion/regrowth

#### Behavior Systems (97% Complete)
- [x] MovementSystem - terrain speed modifiers, wall sliding, **velocity damping (0.85/frame)**
- [x] TerrainDiscomfortSystem - accumulating discomfort on bad terrain
- [x] HungerSystem - decay, starvation damage, faction immunity
- [x] GrazingSystem - herbivores feed on grass/forest, factions on FeedTiles
- [x] WanderSystem - random exploration, roaming, terrain avoidance, **hysteresis escape (enter 0.6, exit 0.1)**
- [x] HerdingSystem - flocking, cohesion, alignment, leader election, group management, **skip when IsEscaping**
- [x] SeparationSystem - same-species collision avoidance
- [x] CollisionSystem - physical overlap resolution (all entities)
- [x] HuntingSystem - **mass-based agility**, pack coordination, **coordinated flanking (Leader/Flanker/Disruptor roles, convergence triggers)**, **ambush hunting (stealth, stalking, pounce burst)**, food sharing
- [x] FleeingSystem - prey escape with fear integration, 4 response types, **mass-based agility**, **stealth-aware detection (stealthed predators reduce effective flee range by up to 90%)**
- [x] AgingSystem - maturity, natural death
- [x] ReproductionSystem - species-aware offspring spawning with population cap
- [x] LODSystem - 4-tier distance-based simulation detail
- [x] TerraformSystem - faction tile modification (wetter/drier/balanced)
- [x] NestSystem - Sectid nest breeding, food delivery, colony expansion
- [x] SporeSystem - Shroomer spore lifecycle, growth, AoE attacks
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

## Phase 2: World Generation v2 (Priority: HIGH)

**Goal**: Diverse, interesting terrain with ecosystem implications

### 2.1 River System — COMPLETE
Rivers implemented via noise-based zero-crossing (not flow-based pathfinding).

- [x] River tile type added to TileType enum
- [x] River generation in TerrainGenerator (noise zero-crossing with variable width)
- [x] Wetland fringe banks alongside rivers
- [x] River speed (0.35x), discomfort (8.0), avoidance (0.8) rules
- [x] Rivers are walkable but very uncomfortable (drives creatures away naturally)
- [ ] Future: Flow-based rivers (A* from peaks to ocean), fish species

### 2.2 Tile Depletion & Regrowth
Design:
- Grazed tiles become depleted (lower nutrition)
- Depleted tiles slowly regenerate
- Enables migration patterns as herds move to fresh grass

Tasks:
- [ ] Add nutrition field to Chunk (per-tile float)
- [ ] GrazingSystem reduces tile nutrition when feeding
- [ ] Add TileRegenerationSystem
- [ ] Herbivores prefer high-nutrition tiles
- [ ] Consider seasonal variation later

### 2.3 Domain Warping
- [ ] Implement domain warping in TerrainGenerator
- [ ] Create organic biome boundaries
- [ ] Reduce "blobby" noise artifacts

---

## Phase 3: Aquatic Ecosystem (Priority: MEDIUM)

**Goal**: Populate water bodies with appropriate creatures

### 3.1 Aquatic Movement
Design:
- Aquatic creatures have IsAquatic = true
- They spawn in water, move fast in water, slow/die on land
- Semi-aquatic (like Crocodile) work in both

Tasks:
- [ ] Verify MovementSystem handles aquatic speed correctly
- [ ] Add drowning equivalent (land creatures in deep water)
- [ ] Add suffocation equivalent (fish out of water)
- [ ] Test Crocodile spawning and behavior

### 3.2 Fish Species
Design:
- Small fish: prey, school behavior
- Large fish: predators

Tasks:
- [ ] Add Fish species to SpeciesRegistry (IsAquatic = true)
- [ ] Add Shark species (aquatic predator)
- [ ] Verify aquatic spawning works correctly
- [ ] Add fish-specific behaviors (schooling)

### 3.3 Shore Interactions
- [x] Crocodile hunts prey at water's edge via ambush hunting (stealth + pounce)
- [x] Prey detection reduced by crocodile stealth (up to 90% range reduction)
- [ ] Amphibian species concept (later)

---

## Phase 4: Faction Species (Priority: MEDIUM) - COMPLETE

**Goal**: Three unique faction species with emergent dynamics

### 4.1 Shroomer (Fungi Faction) - COMPLETE
- [x] Added Shroomer to SpeciesRegistry (Terraformer diet, Herd social)
- [x] SporeSystem: Mature shroomers spread spores on wet terrain
- [x] Spore lifecycle: moisture accumulation on wet tiles, withering on dry, transformation
- [x] Growth system: continuous scale increase up to 4x, affects AoE
- [x] AoE attack: periodic splash damage scaling with growth, targets Sectids/Faelings
- [x] Terraform: Wetter direction (shifts tiles toward Wetland)
- [x] Feeds on Wetland/Forest tiles
- [x] Freeze fear response (plays dead when threatened)

### 4.2 Sectid (Insect Faction) - COMPLETE
- [x] Added Sectid to SpeciesRegistry (Terraformer diet, Pack social)
- [x] NestSystem: 3-stage nests convert food into larvae
- [x] Food delivery: FoodCarrier component, Sectids carry kills to nests
- [x] Colony expansion: nests found new nests nearby or distant colonies
- [x] Pack hunting: Sectids hunt spores and small prey in packs
- [x] Terraform: Drier direction (shifts tiles toward Arid)
- [x] Hunts for food (no tile feeding — must hunt to survive)

### 4.3 Faeling (Crystal Faction) - COMPLETE
- [x] Added Faeling to SpeciesRegistry (Terraformer diet, Solitary)
- [x] CrystalSystem: crystals spawn/respawn linked Faelings
- [x] Power system: gains power from kills and tile restoration
- [x] Power inheritance: 50% passes to crystal on death, then to next Faeling
- [x] Ranged attack: targets Sectids/Shroomers at 8 tile range
- [x] Immune to starvation and predator hunting
- [x] Terraform: Balanced direction (shifts extremes toward Grass)
- [x] Growth system: up to 2.5x scale, boosted by power

### 4.4 Faction Interactions - COMPLETE
- [x] Faeling ranged attacks target Sectids and Shroomers
- [x] Shroomer AoE attacks target Sectids and Faelings
- [x] Sectids hunt spores (Shroomer offspring)
- [x] Competing terraforming: wet vs dry vs balanced creates dynamic terrain conflict
- [ ] Not implemented: DecomposerSystem, formal territory conflict system

---

## Phase 5: LOD & Scale (Priority: HIGH)

**Goal**: 10,000+ entities with smooth performance

### 5.1 Statistical Simulation
Design:
- Distant chunks use population-level math, not individual entities
- When player approaches, entities are "materialized" from stats

Tasks:
- [ ] Add ChunkPopulationData structure
  - [ ] Per-species count
  - [ ] Average hunger/age
  - [ ] Birth/death rates
- [ ] Create StatisticalSimSystem
  - [ ] Run for LOD 2+ chunks
  - [ ] Apply birth/death rates to counts
  - [ ] Don't track individuals
- [ ] Entity materialization when chunk becomes visible
- [ ] Entity aggregation when chunk becomes distant

### 5.2 Performance Profiling
- [ ] Add per-system timing to debug overlay
- [ ] Identify hotspots in each system
- [ ] Profile memory allocation patterns
- [ ] Test with 5K, 10K, 20K entities

### 5.3 Optimization Targets
Based on profiling:
- [ ] SpatialHash optimization if needed
- [ ] Consider SIMD for position updates
- [ ] Batch entity creation/destruction
- [ ] Object pooling for temporary lists

---

## Phase 6: Player Interaction (Priority: MEDIUM)

### 6.1 Entity Selection
- [ ] Click to select entity
- [ ] Display entity stats panel
- [ ] Follow selected entity mode
- [ ] Highlight selected entity

### 6.2 Time Controls
- [ ] Pause simulation (P key)
- [ ] Speed controls: 0.5x, 1x, 2x, 4x
- [ ] Display current speed in overlay

### 6.3 Debug Commands
- [ ] Spawn entity at cursor
- [ ] Kill entity under cursor
- [ ] Teleport player to location
- [ ] Force reproduction

---

## Phase 7: Persistence (Priority: LOW)

### 7.1 Save Format Design
Design considerations:
- JSON for human readability and debugging
- Optional binary for performance
- Version field for compatibility

Structure:
```json
{
  "version": "0.2",
  "seed": 42,
  "tick": 12345,
  "chunks": [...],
  "entities": [...]
}
```

### 7.2 Implementation
- [ ] Serialize chunk terrain data
- [ ] Serialize entity components
- [ ] Save/load UI
- [ ] Auto-save timer

---

## Phase 8: Evolution (Priority: LOW)

### 8.1 Genetic System
- [ ] Add Genetics component
- [ ] Trait inheritance on reproduction
- [ ] Mutation rates
- [ ] Trait visualization

### 8.2 Natural Selection
- [ ] Better traits = better survival
- [ ] Track trait distributions over time
- [ ] Speciation detection

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

---

*Last Updated: February 2026*
*Engine: Godot 4.6 with C#*
