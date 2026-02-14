# Mitosis

A top-down ecosystem simulation game with procedurally generated worlds and emergent creature behaviors. Built with **Godot 4.6** and **C#**.

## Features

- **Large procedural worlds** — Chunk-based terrain with simplex noise generation (512x512 tiles)
- **Ecosystem simulation** — 8 species with hunger, energy, predator/prey dynamics, and faction warfare
- **Custom SoA ECS** — Structure of Arrays Entity-Component-System for cache-efficient simulation of 10,000+ entities
- **Level of Detail** — Distance-based simulation fidelity (Full / Reduced / Statistical / Aggregate)
- **Faction terraforming** — Three factions (Shroomer, Sectid, Faeling) compete to reshape the world's terrain
- **Pack hunting** — Wolves coordinate with Leader/Flanker/Disruptor roles and convergence tactics
- **Ambush hunting** — Crocodiles use stealth mechanics, stalking, and explosive pounce bursts

## Quick Start

1. Install [Godot 4.6](https://godotengine.org/) with .NET/C# support
2. Open the project: `godot/project.godot`
3. Build the C# solution (Build > Build Solution)
4. Run the scene (F5)

## Controls

| Input | Action |
|-------|--------|
| WASD / Arrows | Move camera (8-directional with diagonal normalization) |
| Shift | Sprint (3x speed) |
| Mouse wheel / +/- | Zoom (0.1x to 5.0x) |

## Project Structure

```
godot/Scripts/
├── ECS/EntityManager.cs              # SoA entity storage (16,384 capacity)
├── Components/                       # Position, Velocity, Hunger, Fear, etc.
├── Systems/                          # 17 simulation systems (20 TPS)
├── World/                            # Terrain generation, chunks, tile types
├── Species/                          # SpeciesDefinition + SpeciesRegistry (8 species)
├── Rendering/RenderingManager.cs     # MultiMesh entity batching + chunk textures
├── Utils/                            # SpatialHash, MathUtils
├── GameManager.cs                    # Main loop, initialization
├── EntityFactory.cs                  # Entity creation from species definitions
├── WorldSpawner.cs                   # Initial population distribution
└── PlayerController.cs               # Camera movement and controls

archived/                             # Original Python/Arcade version (reference only)
```

## Tech Stack

- **Godot 4.6** — Game engine with C# scripting
- **Custom ECS** — Structure of Arrays architecture (replaces Godot's scene tree for entities)
- **FastNoiseLite** — Simplex noise terrain generation (built into Godot)
- **MultiMesh2D** — Instanced GPU rendering for thousands of entities

## Documentation

- **[docs/FEATURES_AND_DESIGN.md](docs/FEATURES_AND_DESIGN.md)** — Comprehensive reference for all systems, species, and mechanics
- **[docs/architecture.md](docs/architecture.md)** — Architecture overview and design decisions
- **[docs/godot-roadmap.md](docs/godot-roadmap.md)** — Development roadmap and task tracking
- **[docs/plan-optimization.md](docs/plan-optimization.md)** — Performance optimization strategy

## License

MIT
