# Mitosis Godot 4.6 Development Roadmap

## Current State (v0.1 - Foundation Complete)

The Godot 4.6 migration has established a solid foundation:

### Implemented
- **ECS Architecture** (Structure of Arrays for cache efficiency)
  - EntityManager supporting 16,384 entities
  - 12 component types with flag-based querying
  - Pre-allocated arrays for zero-allocation gameplay

- **World System**
  - Chunk-based terrain (32x32 tiles per chunk)
  - Simplex noise terrain generation
  - 6 tile types with walkability rules
  - WorldManager with frustum culling

- **Core Behaviors**
  - Movement with collision
  - Hunger decay and starvation
  - Predator hunting (spatial hash queries)
  - Prey fleeing
  - Random wandering
  - Aging and natural death

- **Rendering**
  - Tile rendering with visibility culling
  - Entity shapes (circles, triangles, squares)
  - Camera following with smooth lerp
  - Zoom controls

### Not Yet Implemented
- Grazing (herbivores eating from terrain)
- Reproduction system
- Additional species (Shroomer, Sectid, Faeling)
- LOD simulation for off-screen entities
- UI/HUD
- Save/Load
- Evolution mechanics

---

## Phase 1: Ecosystem Completion (Priority: HIGH)

**Goal:** Complete the basic food chain so populations self-sustain

### 1.1 Grazing System
- [ ] Add `GrazingSystem.cs` to process herbivore feeding
- [ ] Herbivores on Grass/Forest tiles restore hunger
- [ ] Grazing rate: 0.5 hunger/tick (configurable)
- [ ] Consider tile "depletion" for future (grass regrows over time)

### 1.2 Reproduction System
- [ ] Add `ReproductionSystem.cs`
- [ ] Check maturity (Age >= maturity_age)
- [ ] Check resources (Hunger >= threshold, Energy >= threshold)
- [ ] Spawn offspring nearby on walkable tiles
- [ ] Apply reproduction cost (hunger/energy drain)
- [ ] Cooldown between reproductions
- [ ] Track generation number for evolution prep

### 1.3 Balance Tuning
- [ ] Adjust hunger decay rates for sustainability
- [ ] Tune reproduction thresholds
- [ ] Balance predator attack power vs prey flee speed
- [ ] Test population stability over extended runs

**Success Criteria:**
- Population stabilizes (no extinction, no explosion)
- Predator-prey cycles emerge naturally
- 500+ entities sustained for 10+ minutes

---

## Phase 2: Performance & Scale (Priority: HIGH)

**Goal:** Support 10,000+ simulated entities with smooth gameplay

### 2.1 LOD Simulation System
- [ ] Add `SimulationLOD` component
- [ ] Calculate LOD level based on distance from player:
  - LOD 0 (visible): Full simulation every tick
  - LOD 1 (near): Every 5 ticks
  - LOD 2 (far): Every 30 ticks (statistical)
  - LOD 3 (distant): Every 60 ticks (aggregate)
- [ ] Skip expensive systems for high-LOD entities

### 2.2 Statistical Simulation
- [ ] Per-chunk population tracking
- [ ] Statistical birth/death rates for distant chunks
- [ ] Chunk-level ecosystem balance
- [ ] Entity "promotion" when chunks come into view

### 2.3 Chunk Streaming
- [ ] Async chunk generation
- [ ] Chunk unloading for distant areas
- [ ] Entity serialization for unloaded chunks
- [ ] Progressive detail loading

### 2.4 Profiling & Optimization
- [ ] Add performance metrics to debug overlay
- [ ] Profile system execution times
- [ ] Optimize hot paths with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
- [ ] Consider SIMD for position updates if needed

**Success Criteria:**
- 10,000+ total entities simulated
- 60 FPS with 500+ visible entities
- Simulation tick rate >= 20 TPS

---

## Phase 3: World Generation v2 (Priority: MEDIUM)

**Goal:** Create varied, interesting worlds with distinct biomes

### 3.1 Biome System
- [ ] Define BiomeType enum (Forest, Grassland, Desert, Swamp, Tundra)
- [ ] Biome distribution based on elevation + moisture
- [ ] Biome-specific tile distributions
- [ ] Visual variety per biome

### 3.2 Advanced Terrain
- [ ] Domain warping for organic patterns
- [ ] River generation (graph-based hydrology)
- [ ] Lake formation in low elevation areas
- [ ] Beach transitions between water/land

### 3.3 Ecosystem Zones
- [ ] Biome-specific spawn rates per species
- [ ] Carrying capacity per biome
- [ ] Movement modifiers (swamp = slow)
- [ ] Visibility modifiers (forest = reduced hunt range)

### 3.4 World Size Expansion
- [ ] Support 64x64 chunks (2048x2048 tiles)
- [ ] Efficient chunk indexing
- [ ] Memory-mapped chunk storage for huge worlds

**Success Criteria:**
- 5+ distinct biome types
- Natural-looking terrain patterns
- Biome influences creature behavior

---

## Phase 4: Species Diversity (Priority: MEDIUM)

**Goal:** Implement unique faction species with distinct behaviors

### 4.1 Shroomer (Fungi Faction)
- [ ] Spore-based reproduction (spread to nearby tiles)
- [ ] Passive - doesn't hunt or flee
- [ ] Absorbs nutrients from dead entities
- [ ] Forms networks (connected Shroomers share resources)
- [ ] Purple/pink coloring

### 4.2 Sectid (Insect Faction)
- [ ] Hive-based social structure
- [ ] Queen entity spawns workers
- [ ] Workers gather food for hive
- [ ] Soldiers defend territory
- [ ] Swarm behavior when threatened
- [ ] Orange/brown coloring

### 4.3 Faeling (Crystal Faction)
- [ ] Crystal growth reproduction (budding)
- [ ] Energy absorption from environment
- [ ] Territorial - defends crystal formations
- [ ] Slow but durable (high energy)
- [ ] Blue/cyan coloring

### 4.4 Species Interactions
- [ ] Faction territory conflicts
- [ ] Resource competition
- [ ] Symbiotic relationships (Shroomer + Faeling?)
- [ ] Faction-specific AI behaviors

**Success Criteria:**
- 3 unique factions with distinct behaviors
- Factions compete for territory
- Emergent ecosystem dynamics

---

## Phase 5: Player Systems (Priority: MEDIUM)

**Goal:** Transform observer into interactive participant

### 5.1 Player Entity Enhancement
- [ ] Player stats (hunger, energy) - optional survival mode
- [ ] Inventory system
- [ ] Interaction with world (place/remove tiles)
- [ ] Creature interaction (taming, feeding)

### 5.2 UI/HUD
- [ ] Entity count display
- [ ] Population graph over time
- [ ] Selected entity info panel
- [ ] Minimap with biome colors
- [ ] Time controls (pause, 1x, 2x, 4x speed)

### 5.3 Debug Tools
- [ ] Entity inspector (click to view stats)
- [ ] Spawn entities manually
- [ ] Kill/heal entities
- [ ] Teleport player
- [ ] Chunk visualization overlay

### 5.4 Camera Improvements
- [ ] Edge panning
- [ ] Click-drag panning
- [ ] Zoom to mouse position
- [ ] Follow selected entity mode

**Success Criteria:**
- Full simulation observation capabilities
- Time control for analysis
- Debug tools for development

---

## Phase 6: Persistence (Priority: MEDIUM)

**Goal:** Save and load world state

### 6.1 Save System
- [ ] Serialize chunk terrain data
- [ ] Serialize entity components
- [ ] Save format (JSON or binary)
- [ ] Compression for large worlds
- [ ] Auto-save functionality

### 6.2 Load System
- [ ] Deserialize world state
- [ ] Validate save file integrity
- [ ] Version compatibility checks
- [ ] Load progress indicator

### 6.3 World Management
- [ ] Multiple save slots
- [ ] World browser UI
- [ ] Delete/rename worlds
- [ ] Export/import worlds

**Success Criteria:**
- Save/load works reliably
- Large worlds save in reasonable time
- Backwards compatibility considered

---

## Phase 7: Evolution & Genetics (Priority: LOW)

**Goal:** Creatures evolve over generations

### 7.1 Genetic System
- [ ] Gene component with trait values
- [ ] Trait inheritance from parents
- [ ] Random mutations
- [ ] Dominant/recessive traits

### 7.2 Evolvable Traits
- [ ] Speed (movement rate)
- [ ] Size (affects visibility, energy needs)
- [ ] Aggression (hunt behavior)
- [ ] Fertility (reproduction rate)
- [ ] Lifespan
- [ ] Sensory range (hunt/flee detection)

### 7.3 Natural Selection
- [ ] Traits affect survival
- [ ] Better adapted creatures reproduce more
- [ ] Speciation over many generations
- [ ] Visualization of trait distributions

**Success Criteria:**
- Observable evolution over time
- Populations adapt to environment
- Trait distributions shift naturally

---

## Phase 8: Polish (Priority: LOW)

**Goal:** Production-ready quality

### 8.1 Audio
- [ ] Ambient sounds per biome
- [ ] Creature sounds
- [ ] UI feedback sounds
- [ ] Music system

### 8.2 Visual Polish
- [ ] Sprite-based rendering (replace shapes)
- [ ] Animations (idle, walk, attack)
- [ ] Particle effects (death, birth, combat)
- [ ] Day/night cycle
- [ ] Weather effects

### 8.3 Performance Polish
- [ ] Memory optimization
- [ ] Load time optimization
- [ ] Battery-friendly mode for laptops
- [ ] Settings menu

### 8.4 Accessibility
- [ ] Colorblind modes
- [ ] Font size options
- [ ] Control remapping
- [ ] Speed options for simulation

**Success Criteria:**
- Professional presentation
- Smooth user experience
- Accessible to wide audience

---

## Technical Debt & Maintenance

### Ongoing
- [ ] Unit tests for core systems
- [ ] Integration tests for ecosystem
- [ ] Documentation for all public APIs
- [ ] Performance regression tests
- [ ] Code review standards

### Refactoring Candidates
- [ ] Extract creature creation into factory pattern
- [ ] Centralize configuration constants
- [ ] Add dependency injection for systems
- [ ] Improve error handling and logging

---

## Milestone Summary

| Milestone | Phase | Target | Status |
|-----------|-------|--------|--------|
| **v0.1** | Foundation | Core ECS + Rendering | COMPLETE |
| **v0.2** | Phase 1 | Ecosystem Complete | PENDING |
| **v0.3** | Phase 2 | Scale to 10K entities | PENDING |
| **v0.4** | Phase 3+4 | Biomes + Species | PENDING |
| **v0.5** | Phase 5 | Player Systems | PENDING |
| **v0.6** | Phase 6 | Save/Load | PENDING |
| **v1.0** | Phase 7+8 | Evolution + Polish | PENDING |

---

## Quick Start for Contributors

### Priority Work Items
1. Implement GrazingSystem (Phase 1.1)
2. Implement ReproductionSystem (Phase 1.2)
3. Balance creature stats for population stability (Phase 1.3)

### Key Files to Modify (in godot/ folder)
- `Scripts/Systems/BehaviorSystems.cs` - Add new systems here
- `Scripts/GameManager.cs` - Register new systems
- `Scripts/Components/CreatureComponents.cs` - Add new components if needed
- `Scripts/ECS/EntityManager.cs` - Add component arrays if needed

### Testing Approach
1. Run game, observe population counter
2. Check for population collapse (all die) or explosion (hit cap immediately)
3. Watch predator-prey interactions
4. Monitor FPS and tick rate in debug overlay

---

*Last Updated: January 2026*
*Engine: Godot 4.6 with C#*
