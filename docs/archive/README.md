# Archive

*Last updated: 2026-09-07*

Superseded documents, kept for the reasoning they contain. **None of them describes the current
code or the current design**, and each carries a banner saying as of when it was true.

Current documentation: [`../design/`](../design/) for intent, [`../implementation/`](../implementation/)
for code, [`../changelog.md`](../changelog.md) for history.

**Internal links inside archived files are left as they were written** and mostly point at
documents that have since moved. They are preserved rather than rewritten because an archived
document that has been edited is no longer a record of what was said.

## Why anything is kept

A superseded document is worth keeping when it records *why* something was decided, or preserves a
worked argument that the replacement compresses to a sentence. It is worth deleting when it only
records *what* the code used to be — the repository already has that. Everything below is here for
the first reason.

## Superseded 2026-09 (the design/implementation split)

| File | As of | Contains |
|---|---|---|
| `features-and-design-2026-07.md` | 2026-07 | The single combined reference the split replaced. Long explanatory passages on nearly every system, most of which were carried forward into one layer or the other |
| `architecture-2026-06.md` | 2026-06 | The previous architecture overview |
| `godot-roadmap-2026-07.md` | 2026-07 | Status and roadmap. Direction is now in `design/`, history in `changelog.md` |
| `documentation-audit-2026-08.md` | 2026-08-25 | **The audit that motivated the split.** 31 verified discrepancies, and a "why this keeps happening" section that is the reasoning behind the current structure |
| `faction-balance-plan-2026-08.md` | 2026-08-29 | The faction balance workstream log, session by session. Several diagnoses in it have since been invalidated |
| `terrain-roadmap-2026-07.md` | 2026-07-09 | Terrain workstream log |
| `terrain-handling-audit-2026-06.md` | 2026-06-23 | Terrain handling *before* the unified resolver, plus the decisions that produced it |
| `terrain-profile-design-2026-06.md` | 2026-06-23 | The unified terrain profile spec, with a built-vs-spec comparison |
| `behavior-arbitration-2026-07.md` | 2026-07-06 | Drive-arbitration inventory; the Option A / Option B choice behind design question C1 |
| `behavior-constants-audit-2026-06.md` | 2026-06-23 | The data-vs-logic audit that established "systems must not name species" |
| `species-attribution-audit-2026-07.md` | 2026-07-06 | Per-species attribution across long runs, plus a balance philosophy |
| `3d-terrain-plan-2026-06.md` | 2026-06-14 | Per-vertex parameters, world-space mapping, surface detail |
| `lod-differential-baseline-2026-08.md` | 2026-08-29 | Why each LOD standing exception is accepted. **The gate is live** — its contract is `../lod-differential-expected.csv` |
| `whole-game-invariants-2026-08.md` | 2026-08-29 | The reasoning behind the invariant gate and what it caught. **The gate is live** |
| `test-scenes-2026-08.md` | 2026-08 | The original harness reference, with worked explanations of each shipped scenario |

## Superseded earlier

| File | As of | Contains |
|---|---|---|
| `migration-evaluation-report.md` | 2026-01-29 | The engine decision: Godot 4 + C# over Python/Arcade, Unity DOTS, Bevy |
| `development-plan.md` | 2026-01 | The original Python/Arcade + Esper design plan |
| `roadmap-old.md` | 2026-01 | The original Python-era roadmap |
| `TRIANGLE_GRID_PLAN.md` | 2026-02 | The 2D→3D renderer migration plan, delivered |
| `plan-optimization.md` | 2026-02 | 2D-era rendering/LOD optimisation plan |
| `statistical_sim_validation_report.md` | 2026-02-20 | A/B validation of a system that no longer exists |
| `documentation-review.md` | 2026-02-14 | The first docs-vs-code audit — itself now stale, which is the lesson |

## Three things the code no longer does

Older documents describe all three as present. They are gone.

- **`StatisticalSimSystem`** — chunk-level population maths for distant areas. Removed; distance
  LOD does the job with real entities everywhere.
- **`TileType.IsWalkable`** — tile-based impassability. Removed; passability is elevation-based and
  every tile is traversable.
- **The global population-pressure ramp** — one shared birth-probability multiplier across all
  species. Removed; replaced by per-class ceilings and local density.
