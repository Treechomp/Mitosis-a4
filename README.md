# Mitosis

A top-down ecosystem simulation game with procedurally generated worlds and emergent creature behaviors.

## Features

- **Large procedural worlds** - Chunk-based terrain with domain-warped noise generation
- **Ecosystem simulation** - Creatures with hunger, energy, predator/prey dynamics
- **ECS architecture** - Esper-based Entity-Component-System for scalable simulation
- **Off-screen LOD** - Simplified simulation for distant entities (WIP)

## Quick Start

```bash
# Install dependencies
pip install -r requirements.txt

# Run the game
python run.py
```

## Controls

| Key | Action |
|-----|--------|
| WASD / Arrows | Move player |
| +/- | Zoom in/out |

## Project Structure

```
src/
├── config.py           # Game configuration
├── game.py             # Main game loop (Arcade)
├── components/         # ECS components (data)
├── systems/            # ECS systems (logic)
├── world/              # World & terrain generation
└── rendering/          # Arcade rendering
```

## Tech Stack

- **Arcade** - GPU-accelerated 2D rendering
- **Esper** - Entity-Component-System framework
- **OpenSimplex** - Noise generation for terrain
- **NumPy** - Fast array operations

## Documentation

See [docs/development-plan.md](docs/development-plan.md) for architecture details and roadmap.

## License

MIT
