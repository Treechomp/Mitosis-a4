# Mitosis Development Plan: Engine & World Generation Strategy

## Executive Summary

This document outlines a strategic development plan for Mitosis, evaluating alternative game engines beyond Pygame and exploring advanced world generation techniques. The goal is to establish a scalable foundation that can support 1000+ entities, procedurally generated worlds with rich terrain features, and eventual multiplayer/modding capabilities.

---

## Part 1: Engine Evaluation

### Current State: Pygame

**Strengths:**
- Simple, well-documented API
- Large community and learning resources
- Direct hardware access via SDL
- Good for prototyping

**Limitations:**
- Software rendering (CPU-bound graphics)
- No built-in sprite batching or GPU acceleration
- Performance degrades with many entities/tiles
- Limited to 2D (acceptable for this project)
- Manual implementation of features other engines provide (spatial partitioning, animation systems, etc.)

### Alternative Engine Analysis

#### 1. Arcade Library (Recommended for 2D)

**Overview:** Modern Python game library built on Pyglet/OpenGL

| Aspect | Details |
|--------|---------|
| **Performance** | GPU-accelerated via OpenGL, handles thousands of sprites efficiently |
| **Features** | Built-in sprite batching, physics (Pymunk), tilemaps, particle systems |
| **Learning Curve** | Low - similar API to Pygame but more Pythonic |
| **Migration Effort** | Medium - requires restructuring render pipeline |

**Key Advantages:**
- `arcade.SpriteList` with automatic GPU batching (critical for 1000+ entities)
- Built-in tilemap support with TMX file loading
- Native particle system for effects
- Type hints throughout (better IDE support)
- Active development (2025 releases)

**Sample Performance Comparison:**
```
Pygame:  ~500 sprites @ 60fps (software rendering)
Arcade: ~5000 sprites @ 60fps (GPU batching)
```

#### 2. Ursina Engine

**Overview:** High-level Panda3D wrapper focused on rapid development

| Aspect | Details |
|--------|---------|
| **Performance** | Excellent - Panda3D backend with modern rendering |
| **Features** | Entity-component system, 3D capable, built-in editor |
| **Learning Curve** | Low - extremely beginner-friendly |
| **Migration Effort** | High - different paradigm, primarily 3D-oriented |

**Best For:** If considering 3D or isometric perspective in future

#### 3. Pyglet (Direct OpenGL)

**Overview:** Low-level multimedia library with OpenGL bindings

| Aspect | Details |
|--------|---------|
| **Performance** | Excellent - direct OpenGL control |
| **Features** | Minimal - you build what you need |
| **Learning Curve** | High - requires OpenGL knowledge |
| **Migration Effort** | High - significant restructuring needed |

**Best For:** Maximum control over rendering pipeline

#### 4. Godot with Python Bindings (gdpython)

**Overview:** Full game engine with Python scripting option

| Aspect | Details |
|--------|---------|
| **Performance** | Excellent - mature optimized engine |
| **Features** | Complete engine: physics, animation, networking, etc. |
| **Learning Curve** | Medium - different architecture |
| **Migration Effort** | Very High - complete rewrite |

**Best For:** Long-term projects needing full engine capabilities

### Engine Recommendation

**Primary Recommendation: Arcade Library**

Reasons:
1. **Minimal Migration Cost** - Similar patterns to Pygame, can migrate incrementally
2. **Significant Performance Gain** - GPU-accelerated rendering solves immediate bottleneck
3. **Built-in Features** - Tilemaps, particles, physics integration
4. **Python Native** - No context switching to other languages/tools
5. **Active Community** - Regular updates, good documentation

**Alternative: Pyglet** (if maximum control needed for custom optimization)

---

## Part 2: World Generation Approaches

### Current Approach: Perlin Noise

**Implementation:**
- Single-layer Perlin noise for elevation
- Moisture layer for biome determination
- Direct noise-to-tile mapping
- Basic river generation (currently broken)

**Limitations:**
- Uniform, repetitive terrain patterns
- No large-scale structure (continents, mountain ranges)
- Biome transitions are abrupt
- Rivers don't follow realistic hydrology
- No geological realism

### Advanced World Generation Techniques

#### 1. Multi-Scale Noise Composition

**Technique:** Layer multiple noise functions at different scales

```
Final Height =
  Continent Noise (scale: 0.001) × 0.5 +   // Large landmasses
  Mountain Noise (scale: 0.01)  × 0.3 +    // Regional terrain
  Detail Noise   (scale: 0.1)   × 0.2      // Local variation
```

**Benefits:**
- Creates natural-looking continents and oceans
- Mountain ranges emerge from noise interactions
- Local detail without losing large structure

**Implementation:**
- Use `simplex_noise_3d` with different seeds for each layer
- Apply domain warping for organic shapes

#### 2. Voronoi-Based Biome Distribution

**Technique:** Use Voronoi diagrams to create distinct biome regions

```
1. Scatter seed points across world
2. Generate Voronoi cells from seeds
3. Assign biome type to each cell based on:
   - Distance from center
   - Noise-based climate zones
   - Proximity to water/mountains
4. Blend edges using noise
```

**Benefits:**
- Creates coherent, visually distinct regions
- Natural-looking biome boundaries
- Easy to implement biome-specific features
- Better for gameplay (clear territories)

**Libraries:** `scipy.spatial.Voronoi` or custom implementation

#### 3. Wave Function Collapse (WFC) for Structures

**Technique:** Constraint-based tile placement

**Best Use Cases:**
- Dungeon/cave generation
- Settlement layouts
- Road/path networks
- Consistent terrain patterns (coastlines, forest edges)

**Hybrid Approach:**
```
1. Generate base terrain with noise
2. Use WFC to place structures that fit terrain
3. WFC ensures adjacent tiles are compatible
```

**Libraries:** `wfc` Python package or custom implementation

#### 4. Hydraulic Erosion Simulation

**Technique:** Simulate water flow to carve realistic terrain

```python
for iteration in range(num_iterations):
    # Drop water particle at random location
    droplet = WaterDroplet(random_position)

    while droplet.has_water:
        # Calculate flow direction (downhill)
        gradient = calculate_gradient(heightmap, droplet.position)

        # Move droplet
        droplet.move(gradient)

        # Erode terrain (pick up sediment)
        sediment = erode(heightmap, droplet.position, droplet.velocity)
        droplet.sediment += sediment

        # Deposit sediment (when slowing down)
        if droplet.velocity < threshold:
            deposit(heightmap, droplet.position, droplet.sediment * factor)

        # Evaporate
        droplet.water -= evaporation_rate
```

**Benefits:**
- Realistic river valleys and canyons
- Natural-looking coastlines
- Mountain erosion patterns
- Creates visual interest without manual design

#### 5. Cellular Automata for Local Features

**Technique:** Iterative rules for organic patterns

**Applications:**
- Cave systems
- Forest growth patterns
- Swamp/marsh distribution
- City sprawl simulation

**Example - Cave Generation:**
```python
def cellular_automata_step(grid):
    new_grid = grid.copy()
    for x, y in grid:
        neighbors = count_neighbors(grid, x, y, type=WALL)
        if neighbors > 4:
            new_grid[x, y] = WALL
        elif neighbors < 4:
            new_grid[x, y] = FLOOR
    return new_grid
```

#### 6. Graph-Based River Networks

**Technique:** Use graph algorithms for realistic hydrology

```
1. Generate heightmap
2. Identify water sources (peaks, rain catchment)
3. Build flow graph: each cell points to lowest neighbor
4. Calculate water accumulation (sum upstream cells)
5. Carve rivers where accumulation > threshold
6. Rivers merge naturally at confluence points
```

**Benefits:**
- Physically accurate river systems
- Natural tributaries and deltas
- Rivers follow terrain realistically
- Easy to determine watersheds

### Recommended World Generation Stack

**Layered Approach:**

```
Layer 1: Continental Structure
├── Multi-scale noise for base elevation
├── Voronoi cells for tectonic plates (optional)
└── Output: Rough continent shapes

Layer 2: Terrain Features
├── Hydraulic erosion simulation
├── Mountain range generation (noise ridges)
└── Output: Detailed heightmap

Layer 3: Climate & Biomes
├── Temperature gradient (latitude-based + elevation)
├── Moisture simulation (wind patterns, rain shadows)
├── Voronoi-based biome assignment
└── Output: Biome map with natural transitions

Layer 4: Hydrology
├── Graph-based river generation
├── Lake detection (closed basins)
├── Coastal erosion
└── Output: Water features

Layer 5: Local Detail
├── Cellular automata for caves/forests
├── WFC for structure placement
├── Resource distribution
└── Output: Final world
```

---

## Part 3: Implementation Roadmap

### Phase 0: Engine Migration (2-3 weeks)

**Goal:** Migrate from Pygame to Arcade while maintaining functionality

| Task | Priority | Effort |
|------|----------|--------|
| Set up Arcade project structure | High | 1 day |
| Port basic game loop | High | 1 day |
| Migrate tile rendering to SpriteList | High | 2 days |
| Port player movement and input | High | 1 day |
| Migrate entity rendering | Medium | 2 days |
| Add camera/viewport system | Medium | 1 day |
| Implement debug overlay | Low | 1 day |
| Performance benchmarking | Medium | 1 day |

**Key Arcade Patterns:**
```python
class MitosisGame(arcade.Window):
    def __init__(self):
        super().__init__(800, 600, "Mitosis")
        self.tile_sprites = arcade.SpriteList()  # GPU batched
        self.entity_sprites = arcade.SpriteList()

    def on_draw(self):
        self.clear()
        self.tile_sprites.draw()  # Single draw call for all tiles
        self.entity_sprites.draw()
```

### Phase 1: World Generation Overhaul (3-4 weeks)

| Week | Focus | Tasks |
|------|-------|-------|
| 1 | Multi-scale terrain | Implement noise layering, continent generation |
| 2 | Erosion & rivers | Add hydraulic erosion, graph-based rivers |
| 3 | Biomes | Voronoi biome distribution, climate simulation |
| 4 | Polish | Transitions, local detail, optimization |

**New World Generator Structure:**
```
world_generation/
├── __init__.py
├── noise.py          # Multi-scale noise utilities
├── heightmap.py      # Elevation generation
├── erosion.py        # Hydraulic erosion simulation
├── hydrology.py      # Rivers, lakes, watersheds
├── climate.py        # Temperature, moisture, wind
├── biomes.py         # Biome classification & distribution
├── features.py       # Caves, forests, landmarks
└── generator.py      # Main world generator orchestrator
```

### Phase 2: Performance & Entity System (2-3 weeks)

| Task | Priority |
|------|----------|
| Implement spatial hash grid | High |
| Entity pooling with SpriteList | High |
| Chunk loading/unloading | Medium |
| LOD system for distant entities | Medium |
| Optimize update loops | Medium |

### Phase 3: Gameplay Foundation (4-6 weeks)

Following existing roadmap for entity behaviors, player systems, and UI.

### Phase 4: Visual Polish (Ongoing)

- Sprite assets (consider procedural generation or asset packs)
- Particle effects using Arcade's built-in system
- Lighting effects
- Weather systems

---

## Part 4: Technical Decisions

### Recommended Technology Stack

| Component | Current | Recommended | Reason |
|-----------|---------|-------------|--------|
| **Engine** | Pygame | Arcade | GPU acceleration, built-in features |
| **Noise** | `noise` lib | `opensimplex` or `fastnoiselite` | Better performance, more options |
| **Math** | NumPy | NumPy + Numba | JIT compilation for erosion sim |
| **Spatial** | None | `scipy.spatial` | KD-trees, Voronoi |
| **Data** | Basic | `dataclasses` | Clean entity/tile definitions |

### Architecture Considerations

**Entity-Component System (ECS):**
Consider adopting ECS pattern for scalability:
```python
# Components
@dataclass
class Position:
    x: float
    y: float

@dataclass
class Velocity:
    dx: float
    dy: float

@dataclass
class Renderable:
    sprite: arcade.Sprite

# Systems process entities with specific components
def movement_system(entities_with_position_and_velocity):
    for entity in entities:
        entity.position.x += entity.velocity.dx
        entity.position.y += entity.velocity.dy
```

**Benefits:**
- Cache-friendly data layout
- Easy to add new behaviors
- Natural parallelization
- Decoupled, testable systems

---

## Part 5: Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Arcade migration breaks features | High | Incremental migration, keep Pygame version |
| World gen too slow | Medium | Use Numba, parallel processing |
| Scope creep | High | Strict phase gates, MVP focus |
| Art assets unavailable | Medium | Use procedural graphics initially |

---

## Conclusion & Next Steps

### Immediate Actions (This Week)

1. **Set up Arcade development branch**
2. **Create minimal Arcade prototype** with tile rendering
3. **Benchmark performance** comparison vs Pygame
4. **Design world generation module structure**

### Decision Points

- [ ] Confirm Arcade as target engine (after prototype)
- [ ] Select specific world gen techniques to implement
- [ ] Decide on ECS adoption timeline
- [ ] Establish art direction (sprites vs procedural)

### Success Metrics

| Metric | Target |
|--------|--------|
| Tile render performance | 10,000+ tiles @ 60fps |
| Entity performance | 1,000+ active entities @ 60fps |
| World gen time (1024x1024) | < 5 seconds |
| World variety | Visually distinct regions |

---

## Resources

### Engine Documentation
- [Arcade Library](https://api.arcade.academy/)
- [Pyglet](https://pyglet.readthedocs.io/)
- [Ursina Engine](https://www.ursinaengine.org/)

### World Generation References
- [Wave Function Collapse](https://github.com/mxgmn/WaveFunctionCollapse)
- [Hydraulic Erosion](https://www.firespark.de/resources/downloads/implementation%20of%20a%20methode%20for%20hydraulic%20erosion.pdf)
- [Red Blob Games - Map Generation](https://www.redblobgames.com/maps/terrain-from-noise/)

### Python Libraries
- `arcade` - Game engine
- `opensimplex` - Noise generation
- `scipy.spatial` - Voronoi, spatial trees
- `numba` - JIT compilation for performance

---

*Document Version: 1.0*
*Created: January 2026*
