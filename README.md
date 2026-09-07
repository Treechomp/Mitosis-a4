# Mitosis

**A top-down creature-sandbox game — early in development.** A procedurally generated world where
AI creatures hunt, graze, herd, flee and die on their own, and three rival factions reshape the
ground itself in opposed directions. You play as one creature of one faction.

Built in **Godot 4.6.3** / **C#**.

> **Genre**: creature sandbox / god game. The world-ecology is a **game mechanic** — a lightweight
> background process that keeps the world alive — not a scientific model. Design intent is in
> [`docs/design/01-vision.md`](docs/design/01-vision.md).

## What is in it

- **Procedurally generated 3D worlds** — chunked terrain with per-vertex elevation, ridged mountain
  ranges, terraced cliff regions, flow-based rivers and lakes, and a two-way coupling where water
  and relief feed back into the climate the biomes are classified from. Rendered as lit 3D meshes,
  with a live in-editor previewer.
- **A data-driven creature roster** — generalists, biome specialists and three factions, all
  configured through one species definition with no per-species branching in any system.
- **Emergent behaviour** — solo, coordinated pack, swarm and stealth/ambush hunting; herding,
  fear responses, venom, scavenging and decomposition. Simple per-creature rules, unscripted
  outcomes.
- **A three-way faction war** fought over the physical state of the ground: one faction wets it,
  one dries it, one restores it, and each dies of a ground condition a rival can impose.
- **Built to scale** — a custom Structure-of-Arrays ECS with distance-based level of detail that
  gates *decisions* and never motion, so the world advances at the same rate everywhere.

## Quick start

1. Install [Godot 4.6.3](https://godotengine.org/), the **.NET / C#** edition.
2. Open `godot/project.godot`.
3. Build the solution (**Build → Build Solution**, or on first run).
4. **F5** runs the game (`Scenes/Main.tscn`).

Other entry points: `Scenes/TestScene.tscn` (scenario worlds, F6),
`Scenes/WorldgenPreview.tscn` (worldgen sliders, F6), and three headless gates — see
[`docs/implementation/tooling-and-tests.md`](docs/implementation/tooling-and-tests.md).

## Controls

| Input | Action |
|---|---|
| WASD / Arrows | Move (camera-relative) |
| Shift | Sprint |
| Mouse wheel / `+` `-` | Zoom |
| F3 | Per-system profiling + per-species population overlay |
| LMB | Inspect the creature under the cursor |
| Tab / Shift+Tab | Cycle highlighted species · `H` highlight the selection's species · `G` jump to the next member |
| F | Free camera · `Esc` clear selection |

Test scenes add pause, single-step, speed control and terrain painting.

## Configuration

World size, population and every terrain parameter are `[Export]` fields on `GameManager`,
editable in the inspector. **The defaults are a standard test configuration**, chosen so balance
runs are comparable — not final production values. Set `WorldSeed` to a non-zero value for a
reproducible world.

## Repository layout

```
godot/          the game — Scenes/, Shaders/, Scripts/{ECS,Components,Systems,World,Species,
                Rendering,Utils,Testing,Tools}, TestScenarios/
docs/           documentation — see below
scripts/        offline analysis (ecosystem_heatmap.py)
archived/       the original Python/Arcade prototype, reference only
logs/           runtime CSV output and world snapshots (gitignored)
```

## Documentation

Two layers, kept deliberately apart — [`docs/README.md`](docs/README.md) explains why.

- **[`docs/design/`](docs/design/)** — what the game is and why, readable without the codebase.
  Start at [`01-vision.md`](docs/design/01-vision.md). Contains no tuning constants and no class
  names.
- **[`docs/implementation/`](docs/implementation/)** — what the code does and where. Names files,
  types and fields; does not copy their values.
- **[`docs/changelog.md`](docs/changelog.md)** — what changed, in which commit, and what it
  measured.
- **[`docs/archive/`](docs/archive/)** — superseded documents, each banner-stamped with the date it
  was last true.

Counts of species, systems and components are deliberately absent from all documentation: they
were restated in five files with no shared source and were wrong in every one of them.
`SpeciesRegistry` and `SimulationStack.Build` are the answer.

## License

MIT
