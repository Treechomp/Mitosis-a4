# Archived Documentation

These documents are **superseded** and kept for historical reference only. They do **not**
reflect the current codebase. For current docs see the parent [`docs/`](../) folder
(`architecture.md`, `FEATURES_AND_DESIGN.md`, `godot-roadmap.md`) and the root `README.md`.

## What's here

| File | Era | Why archived |
|------|-----|--------------|
| `migration-evaluation-report.md` | Pre-migration research | Decision record for choosing Godot 4 + C# over Python/Arcade, Unity DOTS, Bevy. The decision was made and executed; kept as rationale. |
| `development-plan.md` | Python/Arcade (Jan 2026) | Original design plan written for Esper ECS + Arcade + NumPy. Implementation details no longer apply. |
| `roadmap-old.md` | Python/Arcade (Jan 2026) | Original Python-era task roadmap. Superseded by `godot-roadmap.md`. |
| `TRIANGLE_GRID_PLAN.md` | 2D→3D migration | Phased plan for moving the renderer from flat 2D to a triangulated 3D mesh. The migration (through Phase 5B, true 3D) is essentially complete; current rendering is described in `architecture.md` / `FEATURES_AND_DESIGN.md`. |
| `plan-optimization.md` | 2D era | Rendering/LOD optimization plan describing MultiMesh**2D**, chunk textures, the old LOD tier names, and the (removed) statistical sim. Contains session balance reviews tied to an old config. |
| `statistical_sim_validation_report.md` | Statistical-sim experiment | A/B validation of a `StatisticalSimSystem` that no longer exists in the code. |
| `documentation-review.md` | Feb 2026 audit | A prior docs-vs-code audit log. Its "all resolved" status predates the 3D migration and statistical-sim removal, so it is itself stale. |

## Why the docs were overhauled (June 2026)

A full docs-vs-code audit found the active docs described a **2D** game and a simulation
architecture that had both moved on. Key code realities the old docs missed:

- **Renderer is 3D.** `GameManager` is a `Node3D`; terrain is `MeshInstance3D` meshes with
  elevation and directional lighting; entities are `MultiMeshInstance3D` 3D primitives
  (13 shapes). The old docs described `Node2D` + `MultiMeshInstance2D` + flat tile textures.
- **`StatisticalSimSystem` was removed**, its role replaced by the LOD system. The old docs
  described it as implemented (and a whole validation report analyzed it).
- **Tile walkability was removed** — `TileType.IsWalkable()` no longer exists; passability is
  elevation/cliff-based, and Mountain/Lava are now traversable (just slow).
- **Config and engine version drift** — the documented world size / population / Godot
  version no longer matched the project.

The current documents were rewritten from a direct reading of the code.
