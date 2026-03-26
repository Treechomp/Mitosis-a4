# Mitosis - Architecture Overview

> **NOTE**: This project has been migrated from Python/Arcade to **Godot 4.6 with C#**.
> The Python version is preserved in `archived/` for reference only.
>
> For comprehensive documentation of all current features, systems, mechanics, and
> species, see **[FEATURES_AND_DESIGN.md](FEATURES_AND_DESIGN.md)**.

---

## Current Architecture (Godot 4.6 + C#)

### Core Design: Entity-Component-System (ECS)

Mitosis uses a custom **Structure of Arrays (SoA)** ECS architecture, not Godot's
built-in scene tree, for maximum cache efficiency with thousands of entities.

```
┌──────────────────────────────────────────────────────────────┐
│                    SPECIES DEFINITION LAYER                    │
│  SpeciesDefinition (90+ properties per species)               │
│  SpeciesRegistry (28 species: 8 core + 20 biome-specific)        │
│                                                                  │
└──────────────────────────┬───────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────┐
│                    ECS SIMULATION LAYER                        │
│  EntityManager: 16,384 entity capacity, SoA layout            │
│  Components: Position, Velocity, Hunger, Energy, Age, Fear,      │
│              Species, Wander, Predator (with Stealth/Pounce),    │
│              Prey, Social, Terraform, Nest, Spore, Crystal...    │
│  Systems: 19 systems across 16 files, 20 TPS fixed timestep     │
│  SpatialHash: O(1) grid-based neighbor queries                │
└──────────────────────────┬───────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────┐
│                    WORLD LAYER                                 │
│  WorldManager: 24x24 chunks (768x768 tiles)                   │
│  TerrainGenerator: FastNoiseLite (SimplexSmooth) generation     │
│  20 tile types with walkability, grazeability, moisture values │
│  Terraformable: tiles shift along moisture axis                │
└──────────────────────────┬───────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────┐
│                    RENDERING LAYER                             │
│  Terrain: Chunk texture caching (ImageTexture per chunk)       │
│  Entities: MultiMeshInstance2D batching (3 shape types)        │
│  Per-entity frustum culling, chunk-based terrain culling       │
│  Debug overlay: FPS, entity counts                             │
│  Camera: WASD movement, zoom controls                          │
└──────────────────────────────────────────────────────────────┘
```

### Key Design Decisions

1. **Custom SoA ECS** over Godot scene tree: Cache-efficient iteration over 16,000+
   entities. Each component type is a flat array indexed by entity ID. ComponentFlags
   bitmask enables fast entity filtering.

2. **Spatial hashing** over quadtrees: O(1) insert/remove for moving entities.
   Uniform distribution better suited to ecosystem simulation. Used by 8+ systems.

3. **Fixed timestep simulation** (20 TPS): Decoupled from rendering frame rate.
   Ensures reproducible, deterministic behavior.

4. **Level of Detail (LOD)**: Five tiers (Full/High/Medium/Low/Minimal) based on
   distance from player. LODSystem uses **cell-based distance caching** (~32-tile
   spatial hash cells) to avoid per-entity sqrt, and populates a `DueThisTick[]`
   flag array that all downstream systems gate on with a single array read.

5. **Data-driven species**: All behavior configured through SpeciesDefinition.
   No species-specific logic hardcoded in systems.

6. **Simulation-first**: Rendering is a pure visualization layer. All game logic
   lives in ECS systems that process every entity (LOD-modulated).

### System Execution Order

Systems run in this order each tick (order matters for data dependencies):

```
 1. LODSystem              - Cell-based LOD + populate DueThisTick[]
 2. MovementSystem         - Apply velocity + damping (0.85/frame)
 3. SpatialHashUpdateSystem - Update spatial hash positions (LOD-gated)
 4. TerrainDiscomfort      - Track terrain comfort/discomfort        [TerrainSystems.cs]
 5. HungerSystem           - Hunger decay, starvation                [SurvivalSystems.cs]
 6. GrazingSystem          - Feeding on appropriate tiles            [SurvivalSystems.cs]
 7. WanderSystem           - Smooth-turning wander, roaming, terrain escape
 8. HerdingSystem          - Social cohesion, leadership
 9. SeparationSystem       - Prevent same-species overlap            [SpatialSystems.cs]
10. CollisionSystem        - Physical overlap resolution             [SpatialSystems.cs]
11. HuntingSystem          - Solo/pack/ambush hunting, flanking, stealth
12. FleeingSystem          - Stealth-aware threat detection, fear, escape
13. AgingSystem            - Age increment, natural death            [SurvivalSystems.cs]
14. ReproductionSystem     - Standard offspring spawning
15. TerraformSystem        - Faction tile modification               [TerrainSystems.cs]
16. TileRegenerationSystem - Regrow nutrition (4-tick throttle)      [TerrainSystems.cs]
17. NestSystem             - Sectid nest breeding
18. SporeSystem            - Shroomer spore lifecycle
19. CrystalSystem          - Faeling crystal management
```

### File Structure

```
godot/Scripts/
├── ECS/EntityManager.cs              # SoA entity storage (16,384 capacity)
├── Components/
│   ├── CoreComponents.cs             # Position, Velocity, ChunkPosition
│   ├── CreatureComponents.cs         # Species, Hunger, Energy, Age, Fear, etc.
│   ├── BehaviorComponents.cs         # Wander, Predator (Stealth, Pounce),
│   │                                 # Prey, Social, Renderable + enums
│   └── FactionComponents.cs          # Nest, Spore, Crystal, FaelingPower, etc.
├── Systems/
│   ├── ISystem.cs                    # System interface
│   ├── LODSystem.cs                  # Cell-based LOD + DueThisTick flag
│   ├── MovementSystem.cs             # Position updates + velocity damping
│   ├── SpatialHashUpdateSystem.cs    # Centralized spatial hash updates
│   ├── TerrainSystems.cs             # TerrainDiscomfort + Terraform + TileRegeneration
│   ├── SurvivalSystems.cs            # Hunger + Aging + Grazing
│   ├── WanderSystem.cs               # Smooth-turning wander, roaming, terrain escape
│   ├── HerdingSystem.cs              # Social cohesion, alignment, leadership
│   ├── SpatialSystems.cs             # Separation + Collision
│   ├── HuntingSystem.cs              # Solo/pack/ambush hunting, flanking, stealth
│   ├── FleeingSystem.cs              # Threat detection (stealth-aware), fear, escape
│   ├── ReproductionSystem.cs         # Standard offspring spawning
│   ├── CrystalSystem.cs              # Faeling crystal management & ranged attacks
│   ├── NestSystem.cs                 # Sectid nest breeding & food delivery
│   └── SporeSystem.cs                # Shroomer spore lifecycle & AoE attacks
├── Rendering/
│   └── RenderingManager.cs           # Chunk-based entity/tile rendering
├── World/
│   ├── TerrainGenerator.cs           # Noise-based terrain generation
│   ├── WorldManager.cs               # Chunk loading, tile queries
│   ├── Chunk.cs                      # Tile storage
│   └── TileType.cs                   # Tile enum + extensions
├── Species/
│   ├── SpeciesDefinition.cs          # 90+ configurable properties
│   └── SpeciesRegistry.cs            # All 28 species defined
├── Utils/
│   ├── SpatialHash.cs                # Grid-based neighbor queries
│   └── MathUtils.cs                  # Distance, normalization
├── GameManager.cs                    # Main loop, initialization, system registration
├── EntityFactory.cs                  # Entity creation from SpeciesDefinition
├── WorldSpawner.cs                   # Initial population spawning
└── PlayerController.cs               # Camera movement and controls
```

### Adding New Features

See the checklists in [FEATURES_AND_DESIGN.md](FEATURES_AND_DESIGN.md#9-file-map)
for step-by-step guides on adding new components and systems.

---

## Legacy Architecture (Python/Arcade - Archived)

The original Python implementation in `archived/` used:
- **Esper ECS** library for entity management
- **Arcade** library for GPU-accelerated 2D rendering
- **NumPy + Numba** for optimized math
- **OpenSimplex** for terrain generation

It was limited to ~500 entities and was migrated to Godot for scalability.
See `migration evaluation report.md` for the decision rationale and
`development-plan.md` for the original architecture design.

---

*Last Updated: February 2026*
