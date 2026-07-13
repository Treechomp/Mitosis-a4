# Mitosis — Architecture Overview

> **Project**: an early-development creature-sandbox **video game** (the world-ecology is a game
> mechanic, not a research model — see [CLAUDE.md](../CLAUDE.md)).
> **Engine**: Godot 4.6.3 with C# · **Rendering**: 3D (`Node3D` scene, orthographic `Camera3D`)
> **Architecture**: Custom Structure-of-Arrays (SoA) ECS, simulation-first
>
> For the full feature/systems/creature reference, see **[FEATURES_AND_DESIGN.md](FEATURES_AND_DESIGN.md)**.
> For development status and the roadmap, see **[godot-roadmap.md](godot-roadmap.md)**.
> Historical/superseded documents live in **[archive/](archive/)**.

---

## Project lineage (so the archive makes sense)

1. **Python / Arcade + Esper ECS** (Jan 2026) — original prototype, hit a ceiling around
   ~500 entities. Preserved in `archived/`. Rationale for moving on is in
   [archive/migration-evaluation-report.md](archive/migration-evaluation-report.md).
2. **Godot 4 + C#, 2D top-down** — the migration target. Custom SoA ECS, MultiMesh2D
   rendering, chunk textures.
3. **Godot 4 + C#, 3D** *(current)* — the renderer was rebuilt on `Node3D`: terrain is a
   triangulated 3D mesh per chunk with real elevation and directional lighting, and
   entities are 3D primitive meshes. The simulation still runs on the same abstract
   2D integer grid; 3D is a rendering/coordinate concern. The 2D→3D migration plan is
   archived at [archive/TRIANGLE_GRID_PLAN.md](archive/TRIANGLE_GRID_PLAN.md).

> **Note:** A `StatisticalSimSystem` (chunk-level population math for distant areas) was
> prototyped and then **removed**. Its job — keeping distant simulation cheap — is now
> handled entirely by the **LOD system** (see below). Documents describing the statistical
> sim are archived and no longer reflect the codebase.

---

## Current architecture (Godot 4.6.3 + C#)

Mitosis uses a custom **Structure of Arrays (SoA)** ECS rather than Godot's scene tree,
for cache-efficient iteration over thousands of entities. Game state lives entirely in the
simulation; rendering is a pure visualization layer that reads simulation state each frame.

```
┌──────────────────────────────────────────────────────────────┐
│                    SPECIES DEFINITION LAYER                    │
│  SpeciesDefinition — data-driven config per species           │
│  SpeciesRegistry   — 28 species (5 generalist + 20 biome      │
│                      specific + 3 factions), name-hash lookup │
└──────────────────────────┬───────────────────────────────────┘
                           ▼
┌──────────────────────────────────────────────────────────────┐
│                    ECS SIMULATION LAYER                        │
│  EntityManager — 16,384-entity SoA, 25 component types,        │
│                  ComponentFlags bitmask, DueThisTick[] gate    │
│  Systems — 19 simulation systems (+ EcosystemLogger),         │
│            20 TPS fixed timestep                               │
│  SpatialHash — O(1) grid-based neighbor queries               │
└──────────────────────────┬───────────────────────────────────┘
                           ▼
┌──────────────────────────────────────────────────────────────┐
│                    WORLD LAYER                                 │
│  WorldManager — chunk storage, tile/elevation queries         │
│  TerrainGenerator — FastNoiseLite elevation/moisture/         │
│                     temperature + domain warp + ridged ranges │
│                     + terraced cliffs + landmarks             │
│  RiverMapper — flow-based rivers/lakes (hex-neighbor          │
│                topology) + riparian/delta moisture feedback   │
│  20 TileType values · per-vertex elevation · per-tile nutrition│
└──────────────────────────┬───────────────────────────────────┘
                           ▼
┌──────────────────────────────────────────────────────────────┐
│                    RENDERING LAYER (3D)                        │
│  Terrain — one MeshInstance3D per chunk (triangulated mesh,    │
│            elevation on +Y, smooth normals, Lambert shading)  │
│  Entities — MultiMeshInstance3D per shape (13 3D primitives), │
│             placed at terrain elevation, facing velocity      │
│  Camera3D (orthographic) + DirectionalLight3D + ambient env   │
└──────────────────────────────────────────────────────────────┘
```

### Coordinate model (important)

There are two coordinate spaces, bridged by `Utils/GridCoordinates.cs`:

- **Abstract grid space** — integer/float `(x, y)` tile coordinates. This is the canonical
  space; *all* gameplay (movement, spatial hash, rivers, spawning, AI) operates here.
- **3D world space** — the renderer maps grid `(x, y)` + elevation to a 3D position via
  `GridCoordinates.VertexToWorld3D`. Grid Y maps to world **−Z**, elevation maps to world
  **+Y**. Odd grid rows are shifted half a tile (`SmoothRowOffset`), so the terrain
  triangulates into an offset/hex-like mesh instead of axis-aligned squares.

This separation means the simulation is unaffected by the 2D→3D rendering change.

### Key design decisions

1. **Custom SoA ECS** over Godot's scene tree — each component type is a flat array
   indexed by entity ID; a `ComponentFlags` bitmask drives fast filtering. Capacity is a
   fixed 16,384 entities (`EntityManager.MaxEntities`).
2. **Spatial hashing** over quadtrees — O(1) insert/query for constantly-moving entities;
   owned by `WorldManager` and shared by the hunting, fleeing, herding, separation,
   collision, nest, spore, crystal, and LOD systems.
3. **Fixed-timestep simulation at 20 TPS**, decoupled from render frame rate
   (`GameManager` runs an accumulator loop).
4. **Distance-based LOD via `DueThisTick[]`** — `LODSystem` runs first each tick, computes
   a per-spatial-cell distance to the player (cell-based caching avoids per-entity `sqrt`),
   assigns one of five tiers with hysteresis, and writes a single boolean per entity into
   `EntityManager.DueThisTick[]`. Every gated system skips with one array read:
   `if (!em.DueThisTick[entity]) continue;`. Tiers and tick intervals:
   Full=1, High=2, Medium=4, Low=10, Minimal=20 ticks.
5. **Data-driven species** — all behavior is configured through `SpeciesDefinition`; no
   species-specific branching is hardcoded in systems.
6. **Simulation-first / rendering-as-view** — `RenderingManager` only reads simulation
   state; deleting it would not change the simulation.

### System execution order

Systems run in this order every tick (order encodes data dependencies). Registered in
`GameManager._Ready()`:

```
 1. LODSystem               — cell-based LOD, populates DueThisTick[]        [LODSystem.cs]
 2. MovementSystem          — apply velocity, terrain speed, slope, damping  [MovementSystem.cs]
 3. SpatialHashUpdateSystem — refresh spatial-hash positions (LOD-gated)     [SpatialHashUpdateSystem.cs]
 4. TerrainDiscomfortSystem — accumulate/decay terrain discomfort            [TerrainSystems.cs]
 5. HungerSystem            — hunger decay, starvation, energy regen         [SurvivalSystems.cs]
 6. GrazingSystem           — herbivore/faction feeding on tiles             [SurvivalSystems.cs]
 7. WanderSystem            — smooth-turn wander, roaming, terrain escape    [WanderSystem.cs]
 8. HerdingSystem           — cohesion, alignment, leadership                [HerdingSystem.cs]
 9. SeparationSystem        — prevent same-species overlap                   [SpatialSystems.cs]
10. CollisionSystem         — physical overlap resolution (all)             [SpatialSystems.cs]
11. HuntingSystem           — solo/pack/swarm/ambush hunting                 [HuntingSystem.cs]
12. FleeingSystem           — stealth-aware threat detection, fear           [FleeingSystem.cs]
13. AgingSystem             — age increment, natural death                   [SurvivalSystems.cs]
14. ReproductionSystem      — standard offspring spawning                    [ReproductionSystem.cs]
15. TerraformSystem         — faction tile modification                      [TerrainSystems.cs]
16. TileRegenerationSystem  — regrow tile nutrition (throttled)              [TerrainSystems.cs]
17. NestSystem              — Sectid nest breeding & food delivery           [NestSystem.cs]
18. SporeSystem             — Shroomer spore lifecycle & AoE                 [SporeSystem.cs]
19. CrystalSystem           — Faeling crystal management & ranged attack     [CrystalSystem.cs]
    EcosystemLogger         — CSV event/population logging (not a sim step)  [EcosystemLogger.cs]
```

`LODSystem` and `MovementSystem` are intentionally **not** LOD-gated (entities must move
and re-tier every tick). `EcosystemLogger` implements `ISystem` so it ticks with the rest,
but it only observes — it writes per-event and per-population CSVs under `logs/`.

### File structure

```
godot/
├── Mitosis.sln, Mitosis.csproj, project.godot   # Godot 4.6 C# project
├── Scenes/Main.tscn                             # Node3D root + orthographic Camera3D
├── Shaders/TerrainDither.gdshader               # terrain vertex-colour + Lambert shading
└── Scripts/
    ├── ECS/EntityManager.cs                      # SoA storage, 16,384 cap, 25 flags, DueThisTick[]
    ├── Components/
    │   ├── CoreComponents.cs                     # Position, Velocity, ChunkPosition
    │   ├── CreatureComponents.cs                 # Species, Hunger, Energy, Age, Reproduction,
    │   │                                         #   SimulationLOD, TerrainDiscomfort, Fear, VenomEffect
    │   ├── BehaviorComponents.cs                 # Predator, Prey, Wander, Renderable, Social,
    │   │                                         #   Terraform + enums (HuntingTactic, PackRole/Phase,
    │   │                                         #   ShapeType, SocialType, FearResponse…)
    │   └── FactionComponents.cs                  # Nest, FoodCarrier, Spore, Growth, Crystal,
    │                                             #   FaelingPower, RangedAttack
    ├── Systems/                                  # 19 sim systems across 13 files + EcosystemLogger
    │   ├── ISystem.cs  LODSystem.cs  MovementSystem.cs  SpatialHashUpdateSystem.cs
    │   ├── TerrainSystems.cs (Discomfort/Terraform/TileRegeneration)
    │   ├── SurvivalSystems.cs (Hunger/Grazing/Aging)
    │   ├── SpatialSystems.cs (Separation/Collision)
    │   ├── WanderSystem.cs  HerdingSystem.cs  HuntingSystem.cs  FleeingSystem.cs
    │   ├── ReproductionSystem.cs  NestSystem.cs  SporeSystem.cs  CrystalSystem.cs
    │   └── EcosystemLogger.cs
    ├── World/
    │   ├── TileType.cs         # 20 tile types + extension methods (NO IsWalkable — see below)
    │   ├── Chunk.cs            # tiles, per-vertex elevation, per-tile nutrition, GetTileColor
    │   ├── WorldManager.cs     # chunk storage, GetTile/GetElevation, DirtyChunks, SpatialHash
    │   ├── TerrainGenerator.cs # noise + warp + ridges/cliffs + temperature + landmarks
    │   └── RiverMapper.cs      # flow-based rivers/lakes + hydrology→moisture feedback
    ├── Species/
    │   ├── SpeciesDefinition.cs # data-driven species config
    │   └── SpeciesRegistry.cs   # all 28 species
    ├── Rendering/RenderingManager.cs   # 3D chunk meshes + entity MultiMesh3D batching
    ├── Utils/
    │   ├── GridCoordinates.cs  # abstract grid ↔ screen/3D-world conversion + row offset
    │   ├── SpatialHash.cs      # grid-based neighbor queries
    │   └── MathUtils.cs        # distance/normalization helpers
    ├── GameManager.cs          # Node3D root: main loop, system registration, 3D scene setup
    ├── EntityFactory.cs        # entity creation from SpeciesDefinition + population cap
    ├── WorldSpawner.cs         # initial population distribution
    └── PlayerController.cs     # camera + input

archived/                      # original Python/Arcade prototype (+ requirements.txt)
docs/                          # this folder
└── archive/                   # superseded docs (see archive/README.md)
logs/                          # EcosystemLogger CSV output (runtime)
```

### Passability is elevation-based, not tile-based

Earlier versions had `TileType.IsWalkable()` marking Mountain/Lava impassable. That method
has been **removed**. Every tile is now traversable (Mountain and Lava are simply very slow
and very uncomfortable). Movement difficulty comes from two places instead:

- **`MovementSystem`** — non-flying creatures are slowed uphill by `max(0.25, 1 − rise×8)`,
  and **cannot cross a "cliff"** where the destination elevation differs by more than
  **0.28** (a hard movement block).
- **`WorldManager.GetSpawnablePositionsForSpecies`** — ground species (not aquatic, not
  flying) won't spawn on a tile whose cardinal neighbours differ in elevation by more than
  **0.18**, so they don't start stranded on a sheer face.

Flying and aquatic species bypass these checks.

---

## Adding components / systems

**New component:** define the struct (with `[StructLayout(LayoutKind.Sequential)]`) in the
appropriate `Components/*.cs`, add a flag to `EntityManager.ComponentFlags`, add the backing
array, and expose configuration on `SpeciesDefinition` if it's species-driven.

**New system:** implement `ISystem` (`void Process(EntityManager em)`), register it in
`GameManager._Ready()` at the correct point in the execution order, gate it with
`if (!em.DueThisTick[entity]) continue;` unless it must run every tick, and use the
`SpatialHash` for proximity queries (never O(n²) all-entity loops). If it produces a
resource consumed by another system, keep both at the same LOD gate.

---

*Last updated: June 2026 · Godot 4.6.3 + C# · 3D renderer*
