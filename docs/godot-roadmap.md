# Mitosis Godot 4.6 Development Roadmap

## Current State Summary (v0.2 - Ecosystem Foundations)

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
│  • Components - Position, Velocity, Hunger, Fear, etc.      │
│  • Systems - Movement, Hunting, Fleeing, Reproduction       │
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
- [x] 15 component types (Position, Velocity, Hunger, Energy, Age, Species, etc.)

#### Species System (90% Complete)
- [x] SpeciesDefinition - comprehensive data class with 40+ configurable properties
- [x] SpeciesRegistry - lookup by name or ID (hash)
- [x] 5 species defined: Deer, Rabbit, Wolf, Fox, Crocodile
- [x] Diet types: Herbivore, Carnivore (Omnivore prepared)
- [x] Social types: Herd, Pack, Solitary
- [x] Biome preferences per species
- [x] Spawn tile preferences (IsAquatic, AllowedSpawnTiles, CanSpawnOnTile)
- [x] Terrain speed/comfort modifiers per species
- [ ] Missing: Faction species (Shroomer, Sectid, Faeling)

#### Terrain & World (85% Complete)
- [x] Chunk-based storage (32x32 tiles)
- [x] Simplex noise terrain generation
- [x] 9 tile types with walkability/speed rules
- [x] 6 biome types (Arctic, Temperate, Tropical, Desert, Highland, Wetland)
- [x] IsSpawnable(), IsWater(), IsGrazeable() tile queries
- [x] BiomeType calculation from temperature/moisture
- [x] Species-specific spawn filtering (biome + tile)
- [ ] Missing: Rivers, advanced hydrology
- [ ] Missing: Tile depletion/regrowth

#### Behavior Systems (80% Complete)
- [x] MovementSystem - terrain speed modifiers, panic boost
- [x] TerrainDiscomfortSystem - accumulating discomfort on bad terrain
- [x] HungerSystem - decay, starvation damage
- [x] GrazingSystem - herbivores feed on grass/forest
- [x] WanderSystem - random exploration with terrain awareness
- [x] HerdingSystem - flocking, cohesion, alignment
- [x] SeparationSystem - collision avoidance
- [x] HuntingSystem - predator behavior with pack roles
- [x] FleeingSystem - prey escape with fear integration
- [x] AgingSystem - maturity, natural death
- [x] ReproductionSystem - species-aware offspring spawning
- [x] LODSystem - distance-based simulation detail
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

### 2.1 River System
Design:
- Rivers flow from high elevation to low
- River tiles: RiverSource, River, RiverDelta
- Rivers block land movement but some species can swim

Tasks:
- [ ] Add river tile types to TileType enum
- [ ] Implement river generation in TerrainGenerator
  - [ ] Find high elevation points as sources
  - [ ] Path downhill using A* or gradient descent
  - [ ] Join rivers at confluences
  - [ ] End at ocean/lake
- [ ] Add river speed/walkability rules
- [ ] Add river spawnable rules (fish species later)

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
- [ ] Crocodile hunts prey at water's edge
- [ ] Prey avoid water edges when predators present
- [ ] Amphibian species concept (later)

---

## Phase 4: Faction Species (Priority: MEDIUM)

**Goal**: Three unique faction species with emergent dynamics

### 4.1 Shroomer (Fungi Faction)
Design:
- Reproduction: Spore-based, spreads to adjacent tiles
- Behavior: Passive, doesn't hunt or flee
- Resource: Absorbs nutrients from dead entities (decomposer)
- Social: Network formation (connected shroomers share resources)

Tasks:
- [ ] Add Shroomer to SpeciesRegistry
- [ ] Create SporeReproductionSystem
  - [ ] Check for adjacent valid tiles
  - [ ] Spawn probability based on local shroomer density
  - [ ] Resource sharing in network
- [ ] Create DecomposerSystem
  - [ ] Detect death events
  - [ ] Shroomers near corpses gain hunger
- [ ] Add ShroomerNetwork component for resource sharing

### 4.2 Sectid (Insect Faction)
Design:
- Reproduction: Queen spawns workers
- Behavior: Hive-based, workers gather food, soldiers defend
- Structure: Queen + Workers + Soldiers (role differentiation)

Tasks:
- [ ] Add Sectid roles (Queen, Worker, Soldier) to components
- [ ] Create HiveSystem
  - [ ] Queens spawn workers periodically
  - [ ] Workers bring food to queen
  - [ ] Soldiers patrol hive perimeter
- [ ] Add SwarmBehavior for defense
- [ ] Hive territory marking

### 4.3 Faeling (Crystal Faction)
Design:
- Reproduction: Budding (crystal growth)
- Behavior: Territorial, defends crystal formations
- Resource: Energy absorption from environment
- Movement: Slow but durable

Tasks:
- [ ] Add Faeling to SpeciesRegistry
- [ ] Create BuddingReproductionSystem
  - [ ] Requires high energy threshold
  - [ ] Creates adjacent crystal (slower than spores)
- [ ] Create CrystalFormationSystem
  - [ ] Track connected crystals
  - [ ] Formation size affects defense bonus
- [ ] Add territory defense behavior

### 4.4 Faction Interactions
- [ ] Shroomer decomposes Sectid/Faeling corpses
- [ ] Sectid raids Shroomer networks
- [ ] Faeling crystals block Sectid expansion
- [ ] Territory conflict system

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
- [ ] Pack hunting coordination could be tighter
- [ ] Fear decay might be too slow for some species

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

---

*Last Updated: February 2026*
*Engine: Godot 4.6 with C#*
