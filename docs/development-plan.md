# Mitosis Development Plan: Ecosystem Simulation Architecture

## Executive Summary

This document outlines a **complete restart strategy** for Mitosis, optimized for:
- **Large procedurally-generated worlds** (1000x1000+ tiles)
- **Ecosystem simulation** with thousands of entities
- **Off-screen simulation** with simplified behavior models
- **Scalable architecture** using Entity-Component-System (ECS)

Given these requirements, we prioritize **simulation fidelity over graphical complexity**.

---

## Part 1: Architecture Philosophy

### Core Insight: Simulation-First Design

Games like Dwarf Fortress, RimWorld, and Caves of Qud succeed because they prioritize **simulation depth** over rendering. The key architectural principle:

```
┌─────────────────────────────────────────────────────────────┐
│                    SIMULATION LAYER                         │
│  (Runs independently of rendering - the "true" game state)  │
│  • Entity behaviors, AI decisions, ecosystem dynamics       │
│  • Processes ALL entities (on-screen and off-screen)        │
│  • Uses simplified models for distant/off-screen entities   │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    RENDERING LAYER                          │
│  (Visualizes a subset of the simulation state)              │
│  • Only renders visible area + small buffer                 │
│  • Can run at different tick rate than simulation           │
│  • Decoupled - simulation continues if rendering lags       │
└─────────────────────────────────────────────────────────────┘
```

### Off-Screen Simulation Strategy

**Level of Detail (LOD) for Simulation:**

| Distance from Player | Update Frequency | Behavior Complexity |
|---------------------|------------------|---------------------|
| On-screen (visible) | Every frame | Full AI, pathfinding, animations |
| Near off-screen (1-2 chunks) | Every 5 ticks | Simplified AI, no pathfinding |
| Far off-screen (3-5 chunks) | Every 30 ticks | Statistical updates only |
| Very distant (5+ chunks) | Every 60 ticks | Aggregate population changes |

**Statistical Simulation for Distant Entities:**
```python
# Instead of simulating each rabbit individually far away:
# "This forest chunk has 47 rabbits and 3 foxes"
# Statistical model: foxes eat ~2 rabbits per day, rabbits reproduce at 5%/day
# Update population numbers without tracking individuals
```

---

## Part 2: Engine Evaluation (Revised for Simulation Focus)

### Evaluation Criteria (Weighted for Ecosystem Sim)

| Criterion | Weight | Description |
|-----------|--------|-------------|
| Entity Performance | 30% | Handle 10,000+ simulated entities |
| Separation of Concerns | 25% | Clean simulation/rendering split |
| Python Ecosystem | 20% | NumPy, SciPy, ML libraries accessible |
| Rendering Efficiency | 15% | GPU batching for visible tiles |
| Development Speed | 10% | Time to working prototype |

### Option A: Arcade + Esper ECS (Recommended)

**Architecture:**
```
┌──────────────────┐     ┌──────────────────┐
│   Esper ECS      │────▶│   Arcade         │
│   (Simulation)   │     │   (Rendering)    │
│                  │     │                  │
│ • Components     │     │ • SpriteList     │
│ • Systems        │     │ • Camera         │
│ • World state    │     │ • Input          │
└──────────────────┘     └──────────────────┘
        │
        ▼
┌──────────────────┐
│   NumPy Arrays   │
│   (Fast math)    │
└──────────────────┘
```

**Pros:**
- Clean separation: Esper handles simulation, Arcade handles rendering
- Arcade benchmarks: 10,000+ sprites @ 60fps
- Esper is pure Python, lightweight, designed for games
- Full Python ecosystem (NumPy, SciPy, Numba for optimization)
- Can swap rendering layer without touching simulation

**Cons:**
- Two libraries to learn/maintain
- Manual integration work required

**Performance Profile:**
- Esper: ~100,000 component queries/second (pure Python)
- Arcade: ~10,000 sprites @ 60fps (GPU batched)
- Combined: 5,000+ visible entities, 50,000+ simulated entities

### Option B: Godot 4 with GDScript

**Architecture:** Native scene tree with custom simulation layer

**Pros:**
- Full game engine with editor
- Built-in tilemap, animation, physics
- GDScript is Python-like
- Large community, tutorials

**Cons:**
- **Tilemap performance issues** with large maps (documented bugs with 500x500+ tiles)
- **Y-sort performance** drops to 1-2 fps on large isometric maps
- Harder to integrate Python scientific libraries
- Simulation/rendering more tightly coupled
- Overkill for 2D top-down pixel art

**Verdict:** Not recommended for large-world simulation games

### Option C: Raylib + Custom ECS

**Architecture:** Low-level C library with Python bindings

**Pros:**
- Extremely fast (C-based, OpenGL)
- Minimal overhead
- Cross-platform including web (WASM)

**Cons:**
- Very low-level, build everything yourself
- Less Pythonic API
- Smaller Python community

**Verdict:** Good if maximum performance needed, but higher development cost

### Option D: Pure Python Simulation + Minimal Rendering

**Architecture:** Focus entirely on simulation, use simple rendering

**Pros:**
- Maximum focus on simulation quality
- Can use curses/terminal for prototype
- Easiest to iterate on game logic

**Cons:**
- Limited visual appeal
- Harder to attract playtesters

**Verdict:** Good for prototyping core mechanics before committing to engine

### Final Recommendation: Arcade + Esper ECS

**Rationale:**
1. **Best simulation/rendering separation** - Esper World is independent
2. **Proven performance** - Both libraries benchmarked for game use
3. **Python-native** - Full access to NumPy, SciPy, ML libraries
4. **Flexible** - Can optimize hot paths with Numba/Cython later
5. **Active maintenance** - Both projects updated in 2025

---

## Part 3: ECS Architecture for Ecosystem Simulation

### Why ECS is Essential

Traditional OOP (what Mitosis currently uses):
```python
class Rabbit(Entity):
    def update(self):
        self.find_food()      # Each rabbit has its own logic
        self.avoid_predators()
        self.reproduce()
```

**Problems:**
- Cache-unfriendly (objects scattered in memory)
- Hard to query ("find all hungry entities near water")
- Behaviors tightly coupled to entity types
- Difficult to implement LOD simulation

ECS Approach:
```python
# Components (pure data)
@dataclass
class Position: x: float; y: float
@dataclass
class Hunger: value: float; max_value: float
@dataclass
class Prey: fear_radius: float
@dataclass
class Predator: hunt_radius: float

# Systems (pure logic, operate on component sets)
def hunger_system(world):
    for entity, (hunger,) in world.get_component(Hunger):
        hunger.value -= 0.1  # All hungry things get hungrier

def predator_hunt_system(world):
    for pred, (pos, predator) in world.get_components(Position, Predator):
        nearby_prey = spatial_query(pos, predator.hunt_radius)
        # Hunt logic...
```

**Benefits:**
- **Cache-friendly**: Components stored contiguously
- **Flexible queries**: "Get all entities with Position AND Hunger"
- **Composable behaviors**: Rabbit = Position + Velocity + Hunger + Prey
- **LOD-friendly**: Skip expensive systems for distant entities

### Proposed Component Design

```python
# === CORE COMPONENTS ===

@dataclass
class Position:
    x: float
    y: float
    chunk_x: int  # Pre-computed for spatial queries
    chunk_y: int

@dataclass
class Velocity:
    dx: float
    dy: float

@dataclass
class SimulationLOD:
    """Determines update frequency based on distance from player"""
    level: int  # 0=full, 1=reduced, 2=statistical, 3=aggregate
    ticks_until_update: int

# === ECOSYSTEM COMPONENTS ===

@dataclass
class Species:
    type: SpeciesType  # enum: SHROOMER, SECTID, FAELING, etc.
    base_reproduction_rate: float
    base_metabolism: float

@dataclass
class Hunger:
    current: float
    max: float
    starvation_threshold: float

@dataclass
class Energy:
    current: float
    max: float

@dataclass
class Age:
    current: int  # in ticks
    max_lifespan: int
    maturity_age: int

@dataclass
class Predator:
    prey_species: list[SpeciesType]
    hunt_range: float
    attack_power: float

@dataclass
class Prey:
    predator_species: list[SpeciesType]
    flee_range: float
    flee_speed_multiplier: float

@dataclass
class Territorial:
    home_position: tuple[float, float]
    territory_radius: float
    aggression: float

@dataclass
class Social:
    group_id: int | None
    preferred_group_size: int
    cohesion_strength: float

# === REPRODUCTION COMPONENTS ===

@dataclass
class SexualReproduction:
    gender: Gender
    fertility: float
    gestation_ticks: int
    offspring_count_range: tuple[int, int]

@dataclass
class AsexualReproduction:
    """For Shroomers - spore-based"""
    spore_range: float
    spore_cooldown: int
    spore_success_rate: float

@dataclass
class Budding:
    """For Faelings - crystal growth"""
    crystal_energy_required: float
    bud_cooldown: int

# === RENDERING COMPONENTS (only for visible entities) ===

@dataclass
class Renderable:
    sprite_key: str
    layer: int
    animation_state: str

@dataclass
class VisibleToPlayer:
    """Tag component - entity is currently visible"""
    pass
```

### System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    SIMULATION SYSTEMS                        │
│              (Run every tick, LOD-aware)                     │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │
│  │ LODSystem   │  │ HungerSystem│  │ AgeSystem   │         │
│  │ (updates    │  │ (metabolism)│  │ (aging,     │         │
│  │  LOD levels)│  │             │  │  death)     │         │
│  └─────────────┘  └─────────────┘  └─────────────┘         │
│                                                              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │
│  │ MovementSys │  │ HuntingSystem│  │ FleeSystem  │         │
│  │ (pathfinding│  │ (predator   │  │ (prey       │         │
│  │  for LOD 0) │  │  behavior)  │  │  behavior)  │         │
│  └─────────────┘  └─────────────┘  └─────────────┘         │
│                                                              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │
│  │ReproduceSys │  │ TerritorySystem│ │ SocialSystem│        │
│  │ (spawning   │  │ (territory  │  │ (flocking,  │         │
│  │  offspring) │  │  defense)   │  │  herding)   │         │
│  └─────────────┘  └─────────────┘  └─────────────┘         │
│                                                              │
│  ┌─────────────────────────────────────────────────┐       │
│  │ StatisticalSimSystem (for LOD 2-3 entities)     │       │
│  │ • Population dynamics without individual tracking│       │
│  │ • Chunk-level ecosystem balance                  │       │
│  └─────────────────────────────────────────────────┘       │
│                                                              │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    RENDERING SYSTEMS                         │
│              (Run every frame, visible only)                 │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │
│  │VisibilitySys│  │ SpriteSync  │  │ AnimationSys│         │
│  │ (culling)   │  │ (ECS→Arcade)│  │             │         │
│  └─────────────┘  └─────────────┘  └─────────────┘         │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

---

## Part 4: World Generation (Revised)

### Design Goals for Large Worlds

1. **Chunk-based generation** - Generate on-demand, not all at once
2. **Deterministic** - Same seed = same world (for debugging, saving)
3. **Biome coherence** - Large-scale patterns, not just noise
4. **Ecosystem support** - Biomes define entity spawn rules

### Recommended Approach: Hierarchical Generation

```
┌────────────────────────────────────────────────────────────┐
│ LAYER 1: Continental Template (generated once at start)    │
│ • Voronoi-based tectonic plates                            │
│ • Defines land/ocean distribution                          │
│ • Resolution: 1 cell = 64x64 tiles                         │
└────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌────────────────────────────────────────────────────────────┐
│ LAYER 2: Climate Zones (generated once at start)           │
│ • Temperature gradient (latitude + elevation)              │
│ • Moisture patterns (wind simulation or noise)             │
│ • Defines biome types                                      │
└────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌────────────────────────────────────────────────────────────┐
│ LAYER 3: Chunk Detail (generated on-demand)                │
│ • Multi-octave noise for elevation                         │
│ • Biome-specific features (forests, rocks, etc.)           │
│ • River/water placement                                    │
│ • Entity spawn points                                      │
└────────────────────────────────────────────────────────────┘
```

### Key Technique: Domain Warping

Instead of plain Perlin noise (which creates uniform blobs), use domain warping:

```python
def warped_noise(x, y, seed):
    # First layer of noise warps the input coordinates
    warp_x = noise2d(x * 0.01, y * 0.01, seed=seed) * 50
    warp_y = noise2d(x * 0.01 + 100, y * 0.01, seed=seed) * 50

    # Second layer uses warped coordinates
    return noise2d((x + warp_x) * 0.02, (y + warp_y) * 0.02, seed=seed+1)
```

This creates organic, swirling patterns instead of uniform noise blobs.

### Ecosystem-Aware Biomes

Each biome defines:
```python
@dataclass
class BiomeDefinition:
    type: BiomeType

    # Terrain
    base_elevation_range: tuple[float, float]
    tile_distribution: dict[TileType, float]  # e.g., {GRASS: 0.7, FLOWER: 0.2, ROCK: 0.1}

    # Ecosystem
    carrying_capacity: dict[SpeciesType, int]  # max entities per chunk
    spawn_rates: dict[SpeciesType, float]
    food_abundance: float  # affects hunger drain

    # Environment
    movement_modifier: float  # 1.0 = normal, 0.5 = slow (swamp)
    visibility_modifier: float  # affects predator detection range
```

---

## Part 5: Implementation Roadmap (Complete Restart)

### Phase 0: Foundation (Week 1-2)

**Goal:** Minimal working prototype with new architecture

| Task | Description |
|------|-------------|
| Project setup | New repo structure, dependencies (arcade, esper, numpy) |
| Basic ECS | Core components (Position, Velocity, Species) |
| Simple world | Chunk-based tile storage, basic noise generation |
| Minimal render | Arcade window, tile rendering, camera |
| Test entity | One entity type moving around |

**Deliverable:** Entity moves through procedurally generated world

### Phase 1: Ecosystem Core (Week 3-5)

| Task | Description |
|------|-------------|
| Hunger/Energy systems | Basic metabolism simulation |
| Predator/Prey | Simple hunting and fleeing |
| Reproduction | Spawn offspring with inherited traits |
| Death | Starvation, age, predation |
| Population tracking | Per-chunk entity counts |

**Deliverable:** Self-sustaining ecosystem (populations rise and fall)

### Phase 2: LOD Simulation (Week 6-7)

| Task | Description |
|------|-------------|
| LOD component | Track distance from player |
| Tiered updates | Different tick rates by LOD |
| Statistical sim | Population-level simulation for distant chunks |
| Chunk activation | Load/unload entity detail by distance |

**Deliverable:** 10,000+ simulated entities, ~500 visible

### Phase 3: World Generation v2 (Week 8-9)

| Task | Description |
|------|-------------|
| Voronoi biomes | Large-scale biome distribution |
| Domain warping | Organic terrain patterns |
| River generation | Graph-based hydrology |
| Biome ecosystems | Species distribution by biome |

**Deliverable:** Varied, interesting world with distinct regions

### Phase 4: Player & Gameplay (Week 10-12)

| Task | Description |
|------|-------------|
| Player entity | Movement, collision |
| Interaction | Select/observe entities |
| Time controls | Pause, speed up simulation |
| Debug UI | Entity inspector, population graphs |

**Deliverable:** Playable prototype with ecosystem observation

### Phase 5: Polish & Expand (Ongoing)

- Faction behaviors (Shroomer/Sectid/Faeling)
- Combat and territory
- Visual improvements
- Sound
- Save/Load

---

## Part 6: Project Structure

```
mitosis/
├── src/
│   ├── __init__.py
│   ├── main.py                 # Entry point
│   │
│   ├── core/
│   │   ├── __init__.py
│   │   ├── game.py            # Main game loop
│   │   ├── config.py          # Constants, settings
│   │   └── events.py          # Event types
│   │
│   ├── ecs/
│   │   ├── __init__.py
│   │   ├── components/
│   │   │   ├── __init__.py
│   │   │   ├── core.py        # Position, Velocity, etc.
│   │   │   ├── ecosystem.py   # Hunger, Species, etc.
│   │   │   ├── behavior.py    # Predator, Prey, Social
│   │   │   └── render.py      # Renderable, Animation
│   │   │
│   │   ├── systems/
│   │   │   ├── __init__.py
│   │   │   ├── simulation/
│   │   │   │   ├── __init__.py
│   │   │   │   ├── lod.py
│   │   │   │   ├── hunger.py
│   │   │   │   ├── movement.py
│   │   │   │   ├── hunting.py
│   │   │   │   ├── reproduction.py
│   │   │   │   └── statistics.py
│   │   │   │
│   │   │   └── rendering/
│   │   │       ├── __init__.py
│   │   │       ├── visibility.py
│   │   │       ├── sprite_sync.py
│   │   │       └── animation.py
│   │   │
│   │   └── archetypes.py      # Entity templates (Rabbit, Fox, etc.)
│   │
│   ├── world/
│   │   ├── __init__.py
│   │   ├── chunk.py           # Chunk data structure
│   │   ├── world_manager.py   # Chunk loading/unloading
│   │   ├── generation/
│   │   │   ├── __init__.py
│   │   │   ├── noise.py
│   │   │   ├── biomes.py
│   │   │   ├── terrain.py
│   │   │   └── rivers.py
│   │   └── spatial.py         # Spatial hash grid
│   │
│   ├── rendering/
│   │   ├── __init__.py
│   │   ├── renderer.py        # Arcade integration
│   │   ├── camera.py
│   │   ├── tilemap.py
│   │   └── sprites.py
│   │
│   └── ui/
│       ├── __init__.py
│       ├── hud.py
│       └── debug.py
│
├── assets/
│   ├── sprites/
│   └── fonts/
│
├── tests/
│   ├── test_ecs.py
│   ├── test_world_gen.py
│   └── test_simulation.py
│
├── requirements.txt
├── pyproject.toml
└── README.md
```

---

## Part 7: Technology Stack (Final)

| Component | Library | Version | Purpose |
|-----------|---------|---------|---------|
| **ECS** | esper | 3.2+ | Entity-Component-System |
| **Rendering** | arcade | 2.6+ | GPU-accelerated sprites |
| **Noise** | opensimplex | 0.4+ | Terrain generation |
| **Math** | numpy | 1.24+ | Fast array operations |
| **Spatial** | scipy | 1.10+ | KD-trees, Voronoi |
| **JIT** | numba | 0.57+ | Performance-critical loops |
| **Data** | dataclasses | stdlib | Component definitions |

### requirements.txt
```
arcade>=2.6.17
esper>=3.2
numpy>=1.24.0
scipy>=1.10.0
opensimplex>=0.4
numba>=0.57.0
```

---

## Part 8: Success Metrics (Revised)

| Metric | Target | Measurement |
|--------|--------|-------------|
| **Simulated entities** | 50,000+ | Count all entities in world |
| **Visible entities** | 500+ @ 60fps | On-screen entity count |
| **Simulation tick rate** | 20+ ticks/sec | Even with full world |
| **World size** | 2048x2048 tiles | Chunk-based, stream |
| **Ecosystem stability** | Self-sustaining | Populations don't collapse or explode |
| **Memory usage** | <1GB | For full world state |

---

## Appendix A: References

### Game Architecture
- [Dwarf Fortress Simulation (Python)](https://github.com/kevshakes/dwarf-fortress-simulation)
- [ECS Architecture in Games](https://www.daydreamsoft.com/blog/mastering-entity-component-system-ecs-in-game-development)
- [Data-Oriented Design for Games](https://www.dataorienteddesign.com/dodbook/)

### Engine Documentation
- [Arcade Library](https://api.arcade.academy/)
- [Esper ECS](https://github.com/benmoran56/esper)
- [OpenSimplex Noise](https://github.com/lmas/opensimplex)

### World Generation
- [Red Blob Games - Terrain](https://www.redblobgames.com/maps/terrain-from-noise/)
- [Procedural World Generation](https://www.procjam.com/)

---

*Document Version: 2.0*
*Created: January 2026*
*Focus: Large-world ecosystem simulation*
