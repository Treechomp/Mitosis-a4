# 3D Terrain & Movement Plan

> Status: **living plan**, started June 2026. Engine: Godot 4.6.3 + C#.
> Companion: [architecture.md](architecture.md), [FEATURES_AND_DESIGN.md](FEATURES_AND_DESIGN.md).

## Goal

Move the world onto an honest 3D footing so that:

1. **Movement is straight** — no zig-zag. (Root cause: the half-tile odd-row render
   stagger, `GridCoordinates.SmoothRowOffset`, applied to grid-space motion.)
2. **Terrain data is per-vertex and continuous** — each grid point carries
   `{elevation, moisture, temperature}`, and its colour is a *continuous blend* of those
   parameters (the "terrain cube"), with named biomes as anchors — not flat per-tile colours.
3. **Gameplay keeps working unchanged** — discrete `TileType` is *derived* from the
   parameters on demand, so the ~40 existing `GetTile(...)` consumers don't change.

## Current state (facts, from the code)

- Terrain is already a real triangulated 3D heightfield mesh (`RenderingManager.BuildChunkGeometry`),
  with per-vertex elevation, analytic normals, and lighting.
- Simulation is already continuous float space (`Position.X/Y` are floats; movement adds
  sub-tile deltas). There is **no** tile-snapping in movement.
- `Chunk` stores per grid point: `_tiles` (discrete `TileType`), `_elevation`, `_nutrition`.
  Moisture/temperature are computed in `TerrainGenerator` but **not stored**.
- Colour is discrete-then-blended: each vertex takes `Chunk.GetTileColor(classifiedType)`
  and the GPU interpolates between neighbours.
- The zig-zag is **solely** `SmoothRowOffset` (`GridCoordinates.cs:36`). Removing it makes
  `world = grid·tileSize` (linear) → straight motion, with zero movement-logic changes.
- **Single gateway:** all gameplay terrain reads go through `WorldManager.GetTile(x,y)`
  → `TileType` → extension methods (`IsGrazeable`, `GetSpeedMultiplier`, `CanSpawnOnTile`, …).

## Target architecture

```
Per grid vertex (canonical):  elevation, moisture, temperature   (floats)
                              + optional water-feature override   (river/lake)
                              + nutrition                         (grazing)

Derived for VISUALS:   colour = TerrainPalette.FromParams(moisture, temperature, elevation)
                       (continuous "cube" blend; water features use a discrete water colour)

Derived for GAMEPLAY:  TileType = classify(moisture, temperature, elevation)   [cached in _tiles]
                       → all existing TileType extension methods unchanged
                       → WorldManager.GetTile(x,y) keeps its signature

Coordinate space:      world = (gridX·tile, elevation·heightScale, −gridY·tile)   [linear, no offset]
```

`_tiles` becomes a **derived cache** of the classification (recomputed when params change),
so `GetTile` stays an O(1) array read and every consumer is untouched.

---

## Phase 1 — World-space foundation (remove the stagger)  ✅ implementing now

**Change:** `GridCoordinates.SmoothRowOffset` returns `0` (the staggered/hex layout is
removed). All callers (`VertexToWorld3D`, `WorldToGrid`, `ScreenToVertex`, and via them the
mesh, entities, camera, and `PlayerController`'s world round-trip) automatically get a linear
square mapping. The triangle-diagonal alternation in `BuildChunkGeometry` / `GetElevation`
stays (harmless on a square grid; avoids directional shading bias).

**Result:** entities and player move perfectly straight in 3D; terrain stays a real
elevation mesh. The only change is the loss of the offset-row look (which *is* the wobble).

**Verify in-editor:** walk N/S and diagonally — no wobble; entities track straight; camera
follows straight; entities still sit on the surface (elevation sampling unchanged).

**Rollback:** restore the one function body.

---

## Phase 2 — Per-vertex parameters + continuous colour

### 2a — Visual: store params + "cube" palette (no gameplay change)  ⬅ starting this turn

- `Chunk`: add `_moisture[,]`, `_temperature[,]` + get/set (mirrors `_elevation`).
- `TerrainGenerator.GenerateChunk`: persist the moisture & temperature it already computes.
- `WorldManager`: add `GetVertexMoisture/GetVertexTemperature` (clamped), mirroring
  `GetVertexElevation`, for the +1 mesh edge.
- `TerrainPalette.FromParams(moisture, temperature, elevation)`: continuous colour, a smooth
  weighted blend of biome anchor colours positioned in `(m,t,e)` space (the uploaded cube).
  **Tunable** — anchor positions/colours are a first pass to refine visually in-editor.
- `RenderingManager.BuildChunkColors`: land vertices use `FromParams`; water features
  (`IsWater()` / Reef) keep their discrete water colour so rivers/lakes/coasts stay crisp.

Gameplay is untouched in 2a (`GetTile` still returns the cached classified tile; terraform
still discrete). This delivers the continuous look with low risk.

### 2b — Gameplay: classify-on-demand + terraform-on-moisture

- Make `_tiles` an explicit derived cache: `Chunk.ReclassifyTile(x,y)` runs the existing
  `DetermineTileType` on the stored params; called at generation and whenever a param changes.
- `TerraformSystem` shifts the **moisture** parameter (Wetter +, Drier −, Balanced → toward
  centre) instead of swapping discrete tiles via `ShiftWetter/Drier/Balanced`; then
  reclassify + mark the chunk dirty. Colour follows automatically (it reads params), and the
  M2 colour-only mesh update already handles dirty chunks.
- Keep the river/lake/wetland markers as a discrete per-vertex override that wins over
  classification (they aren't a smooth function of climate).
- `DetermineTileType` moves to a shared location callable per position (it's currently a
  private static in `TerrainGenerator`).

No consumer of `GetTile` changes in 2b either — only how the cached value is produced.

---

## Phase 3 — (optional, larger) organic detail / true surface movement

- Smooth deterministic XZ vertex warp (low-frequency noise, visual only) + higher mesh
  resolution / fractal elevation detail, so the square base never reads as a grid — without
  re-introducing wobble (a smooth warp doesn't reverse per row).
- Optional true 3D surface locomotion: movement direction follows the slope, AI distances
  measured on the surface. Today's model (2D move + elevation affects speed/cliffs) is kept
  unless this is explicitly wanted; it's the heaviest item and lowest priority.

---

## Touch-point map (so nothing is missed)

- **Offset (Phase 1):** `GridCoordinates.{SmoothRowOffset,VertexToWorld3D,WorldToGrid,ScreenToVertex}`;
  callers `RenderingManager` (mesh + entities), `PlayerController` (input round-trip + camera).
- **Colour (2a):** `RenderingManager.BuildChunkColors`; `Chunk.GetTileColor` (kept for water).
- **Params (2a):** `Chunk`, `TerrainGenerator.GenerateChunk`, `WorldManager` vertex accessors.
- **Classification / consumers (unchanged):** everything reading `WorldManager.GetTile(...)`
  — Movement, Wander, Fleeing, Hunting, Survival/Grazing, TerrainDiscomfort, Spore, Nest,
  Crystal, Reproduction, EntityFactory, WorldSpawner. ~40 sites, all via the `GetTile` gateway.
- **Terraform (2b):** `TerrainSystems.TerraformSystem` (+ `ShiftWetter/Drier/Balanced` retired
  in favour of moisture nudges); `WorldManager.SetTile` / `Chunk.SetTile`.

## Risks & verification

- **No build here.** This container has no `dotnet`/Godot, so nothing below is compiled or
  run. Each phase must be built and eyeballed in the editor. Phases are committed separately
  so Phase 1 (the safe zig-zag fix) is isolated.
- **Palette tuning (2a):** the cube colours are a reasoned first pass; expect to tune anchors
  visually. Localised to `TerrainPalette`.
- **Terraform semantics (2b):** moisture-nudge must reproduce the three directions; verify a
  faction can still convert terrain over time and the classified type/colour shift together.
- **Perf:** params add 2 floats/vertex (~2.6 MB at 18×18). Classification stays cached, so
  `GetTile` cost is unchanged.

## Order

`Phase 1` → `Phase 2a` → `Phase 2b` → (optional) `Phase 3`. Each is independently shippable.
