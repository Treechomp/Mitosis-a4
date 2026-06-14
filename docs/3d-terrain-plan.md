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

### 2a — Visual: store params + "cube" palette (no gameplay change)  ✅ done

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

### 2b — Gameplay: classify-on-demand + terraform-on-moisture  ✅ done

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

## Phase 3 — (optional, larger) organic detail / true surface movement  ◑ partial

**Done:** subtle procedural surface detail in `TerrainDither.gdshader` (world-space value
noise modulating brightness; uniforms `detail_strength` / `detail_scale`) — breaks up flat
squares with **zero** geometry/movement/alignment impact. Tune or disable in-editor.

**Deferred (future):**
- Smooth deterministic XZ vertex warp (low-frequency noise, visual only) + higher mesh
  resolution / fractal elevation detail, for more relief without re-introducing wobble.
- True 3D surface locomotion: movement direction follows the slope, AI distances measured on
  the surface. Today's model (2D move + elevation affects speed/cliffs) is kept unless wanted.
- **Entity/player render interpolation** ✅ done: entity render positions are now lerped
  between the previous and current tick positions by the inter-tick fraction (`EntityManager`
  snapshots positions each tick; the renderer interpolates by `alpha = accumulator/dt`).
  Fast movers (the player at high speed) glide instead of stepping at 20 TPS. Rendering-only.

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

## Terrain shape & variety — surface detail + tunable noise  ✅ first pass

To reduce the uniform "wrangled fabric" look, the generator layers roughness-modulated
**surface detail** on top of the base elevation, and the key noise parameters are exposed as
`GameManager` `[Export]`s (→ `TerrainSettings` → `TerrainGenerator`), editable in the inspector:

| Export | Default | Effect |
|--------|---------|--------|
| `ElevationFrequency` | 0.012 | base shape scale; lower = larger landmasses |
| `WarpAmplitude` | 30 | domain-warp swirl; lower = calmer/straighter (less "fabric") |
| `TerrainDetailFrequency` | 0.045 | surface-relief frequency |
| `TerrainDetailAmplitude` | 0.035 | surface-relief height added to elevation (keep < ~0.1 or slopes get steep) |
| `TerrainRoughnessFrequency` | 0.006 | size of rugged vs smooth regions |
| `TerrainRoughnessFloor` | 0.15 | minimum detail in the smoothest regions (0 = some areas fully flat) |

Detail is added to the **stored/rendered** elevation only — classification uses the base
elevation, so biome boundaries and water levels are unchanged — and it's faded out over water
so the sea stays flat. The roughness mask modulates detail amplitude so some regions are rugged
and others smooth. Tune live: raise `TerrainDetailAmplitude` and/or lower `WarpAmplitude` to
push variety. Detail adds a little slope (rough ground slows movement slightly; never blocks).

**Done:** biome-aware roughness — detail is scaled per biome by `TileType.GetRuggedness()`
(mountains rugged, forest/jungle moderate, plains smooth, water flat).

**Future levers (not done):** elevation redistribution / power curve for flat basins +
concentrated mountain ranges. `WarpAmplitude` default lowered 30 → 12 (below ~15 reads best).

## Water rendering — flat depth-coloured sea  ✅ first pass

The sea now renders as a flat surface at `SeaLevel` (0.40), coloured by depth (shallow → deep
blue) instead of showing the seabed relief — so deep water no longer looks like "blue terrain".
In `RenderingManager`: vertices/normals below sea level are flattened; `WaterColor(depth)` tints
by depth; entities over water are clamped to the surface so they stay visible. Movement is
unaffected (it still uses the real floor elevation).

**Scope / deferred:** this is a single global ocean plane. **Lakes at altitude** keep their own
floor level (rendered as shallow water following the terrain) and **rivers** follow the terrain.
Proper per-water-body surface levels — and any wave/fluid sim — are the larger "water-level
approximation" and remain future work.

Also done: **B3** (coastline crispness via the flat sea + crisp shore) and **G3** (negative
world-coordinate guards on `GetTile`/`SetTile`/nutrition/`Terraform`).

## Order

`Phase 1` → `Phase 2a` → `Phase 2b` → (optional) `Phase 3`. Each is independently shippable.
