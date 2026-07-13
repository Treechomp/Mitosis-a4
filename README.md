# Mitosis

**A top-down creature-sandbox video game — early in development.** Roam a living,
procedurally generated game world where AI creatures and three rival factions play out an
emergent, watchable drama you can nudge and reshape. Built in **Godot 4.6.3** / **C#**. The
creature simulation is the game's engine; the player-facing game layer (progression,
observation tools, directed creature evolution) is the upcoming work — see the
[roadmap](docs/godot-roadmap.md).

> **Genre**: creature sandbox / god-game. Think a living toybox world, not a lab — the
> "simulation" is a game mechanic (a lightweight background ecology that keeps the world
> feeling alive), not a scientific model.

## Game features

- **Procedural game worlds (3D)** — chunk-based terrain with per-vertex elevation, simplex-noise
  generation, domain warping, temperature/moisture biomes, ridged mountain ranges, terraced
  cliff regions, flow-based rivers and lakes that feed moisture back into the biomes around
  them (riparian corridors, marshy deltas, drainage), and landmark features (oases,
  clearings, caves). Rendered as lit 3D meshes with a live in-editor world previewer.
- **28 creatures** — 5 generalist grazers/hunters, 20 biome-themed creatures
  (aquatic, arctic, desert, tropical, temperate), and 3 world-shaping factions.
- **Built to scale** — a custom Structure-of-Arrays Entity-Component-System runs thousands of
  creatures cheaply (16,384 capacity), with five-tier distance LOD so the far reaches of the
  map stay cheap while the action near the player runs at full detail.
- **Three-way faction war** — Shroomers (spread swamp), Sectids (spread desert), and Faelings
  (restore balance) reshape the land against each other, a background conflict the player can
  tip.
- **Emergent creature AI** — solo, coordinated pack (leader/flanker/disruptor), swarm, and
  stealth/ambush hunting; herds and fleeing; venom and fear — simple per-creature rules that
  produce watchable, unscripted behavior.

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

- **Standard test configuration (in code):** `36×36` chunks (1152×1152 tiles), `2000` initial
  population, `12000` population cap — the setup balance runs use. Final production values
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

## Tech stack

- **Godot 4.6.3** — game engine, C# / .NET scripting, 3D renderer (Forward+)
- **Custom SoA ECS** — replaces the scene tree for cheap creature updates at scale
- **FastNoiseLite** — terrain noise (built into Godot)
- **MeshInstance3D / MultiMeshInstance3D** — instanced GPU rendering for terrain and creatures

## Documentation

- **[docs/FEATURES_AND_DESIGN.md](docs/FEATURES_AND_DESIGN.md)** — comprehensive reference
  for all game systems, creatures, terrain, and mechanics.
- **[docs/architecture.md](docs/architecture.md)** — architecture overview and design decisions.
- **[docs/godot-roadmap.md](docs/godot-roadmap.md)** — development status and roadmap.
- **[docs/archive/](docs/archive/)** — superseded/historical documents (Python era, the
  2D→3D migration plan, a removed distant-area experiment, prior audits).

## License

MIT
