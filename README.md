# Mitosis

A top-down **3D** ecosystem simulation: procedurally generated worlds with elevation,
biomes, and rivers, populated by thousands of autonomous creatures with emergent behavior.
Built with **Godot 4.6.3** and **C#**.

## Features

- **Procedural 3D worlds** — chunk-based terrain with per-vertex elevation, simplex-noise
  generation, domain warping, temperature/moisture biomes, flow-based rivers and lakes,
  and landmark features (oases, clearings, caves). Rendered as lit 3D meshes.
- **28 species** — 5 generalist herbivores/predators, 20 biome-specific species
  (aquatic, arctic, desert, tropical, temperate), and 3 terraforming factions.
- **Custom SoA ECS** — Structure-of-Arrays Entity-Component-System for cache-efficient
  simulation of thousands of entities (16,384 capacity).
- **Distance-based LOD** — five tiers gate per-entity work each tick so distant parts of
  the world stay cheap without a separate statistical model.
- **Faction terraforming** — Shroomers (wetter), Sectids (drier), and Faelings (balanced)
  reshape terrain along a moisture axis, creating a three-way ecological conflict.
- **Rich predation** — solo, coordinated pack (leader/flanker/disruptor), swarm, and
  stealth/ambush-pounce hunting tactics; mass-based hunt ratios; venom; fear responses.

## Quick Start

1. Install [Godot 4.6.3](https://godotengine.org/) (latest), the **.NET / C#** edition.
2. Open the project: `godot/project.godot`.
3. Build the C# solution (**Build → Build Solution**, or it builds on first run).
4. Press **F5** to run (`Scenes/Main.tscn`).

## Controls

| Input | Action |
|-------|--------|
| WASD / Arrows | Move player (camera-relative; camera follows) |
| Shift | Sprint (3× speed) |
| Mouse wheel / `+` `-` | Zoom (orthographic, isometric view) |
| F3 | Toggle per-system profiling + per-species population overlay |

## Configuration

Default world/population are configured via exported fields on `GameManager`:

- **Intended default:** ~`18×18` chunks (576×576 tiles), `1500` initial population,
  `10000` population cap.
- **Currently in code:** smaller **DEBUG** values (`9×9` chunks, `500` initial, `2000` cap)
  for lightweight species-balancing sessions that don't need LOD. Final production values
  will be set once all features are in and compute/render costs are known.

Set `WorldSeed` to a non-zero value for a reproducible world (0 = random each run).

## Project Structure

```
godot/Scripts/
├── ECS/EntityManager.cs        # SoA storage (16,384 cap, 25 component types)
├── Components/                  # Core, Creature, Behavior, Faction components + enums
├── Systems/                     # 19 simulation systems (+ EcosystemLogger) @ 20 TPS
├── World/                       # TerrainGenerator, WorldManager, Chunk, TileType, RiverMapper
├── Species/                     # SpeciesDefinition + SpeciesRegistry (28 species)
├── Rendering/RenderingManager.cs# 3D chunk meshes + entity MultiMesh3D batching
├── Utils/                       # GridCoordinates (grid↔3D), SpatialHash, MathUtils
├── GameManager.cs              # Node3D root: main loop, init, 3D scene setup
├── EntityFactory.cs            # entity creation from species definitions
├── WorldSpawner.cs             # initial population distribution
└── PlayerController.cs         # camera movement + input

archived/                        # original Python/Arcade prototype (reference only)
docs/                            # current documentation (+ docs/archive for superseded)
logs/                            # EcosystemLogger CSV output (runtime)
```

## Tech Stack

- **Godot 4.6.3** — game engine, C# / .NET scripting, 3D renderer (Forward+)
- **Custom SoA ECS** — replaces the scene tree for entity simulation
- **FastNoiseLite** — terrain noise (built into Godot)
- **MeshInstance3D / MultiMeshInstance3D** — instanced GPU rendering for terrain and entities

## Documentation

- **[docs/FEATURES_AND_DESIGN.md](docs/FEATURES_AND_DESIGN.md)** — comprehensive reference
  for all systems, species, terrain, and mechanics.
- **[docs/architecture.md](docs/architecture.md)** — architecture overview and design decisions.
- **[docs/godot-roadmap.md](docs/godot-roadmap.md)** — development status and roadmap.
- **[docs/archive/](docs/archive/)** — superseded/historical documents (Python era, the
  2D→3D migration plan, the removed statistical-sim experiment, prior audits).

## License

MIT
