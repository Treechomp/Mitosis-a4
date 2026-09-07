# World generation

*Last updated: 2026-09-07 · verified against `7a7fb28`*

## Files

| File | Role |
|---|---|
| `World/TerrainGenerator.cs` | per-chunk generation; implements `IChunkGenerator` |
| `World/RiverMapper.cs` | global hydrology pre-pass: rivers, lakes, moisture feedback |
| `World/TerrainSettings.cs` | the tunable worldgen parameter set |
| `World/TerrainPalette.cs` | tile colours |
| `World/TileType.cs` | `TileType`, `BiomeType`, `TileTypeExtensions` |
| `World/Chunk.cs` | per-chunk tiles, per-vertex elevation/moisture/temperature, nutrition, mycelium |
| `World/WorldManager.cs` | chunk storage and every query into the world |
| `World/WorldSnapshot.cs` | one-shot startup diagnostics (PNG maps + report) |
| `World/ScenarioTileParams.cs` | canonical per-tile climate values, used by painting and scenarios |

## Order of generation

1. `SimRandom.SetSeed` — everything below derives from the world seed.
2. `RiverMapper.PrecomputeRivers` — builds a full-world base-elevation map, traces flow, fills
   depressions into lakes, marks rivers, and emits the moisture-feedback field.
3. Per chunk: `TerrainGenerator.GenerateChunk` samples climate, applies hydrology feedback,
   classifies tiles, applies shores and landmarks, and stores rendered elevation.
4. `WorldSnapshot` writes its diagnostic set.
5. `WorldSpawner` places the initial population, then `SpawnMyceliumHearts`.

`TerrainGenerator.SampleBaseElevation` is the **single authority** for base elevation. The river
mapper builds its flow map with it and chunk generation reads that cached map back, so terrain and
hydrology cannot drift apart. `TerrainGenerator.SampleTile` is the same per-tile function
`GenerateChunk` loops over, which is what lets the preview tool be exact rather than approximate.

## Noise layers

All noise is Godot `FastNoiseLite`, `SimplexSmooth`, FBM, built by
`WorldManager.CreateNoiseGenerator`. The layer set — elevation, moisture, temperature, two warp
axes, landmark, surface detail, roughness mask, ridge, orogeny belt, cliff mask — and every
frequency, octave count and seed offset is declared in `TerrainSettings` / `TerrainGenerator`.
The exported subset is on `GameManager`; the rest are constants.

Three separations matter and are easy to break:

- **Base elevation vs. rendered elevation.** Ridged mountain crests are added to the *base*
  elevation, so classification and hydrology see them. Surface detail and cliff terracing are added
  to the *stored/rendered* elevation only, so they never move a biome boundary or a waterline.
- **Warping applies to sampling coordinates**, not to results.
- **Detail is masked twice** — by the low-frequency roughness field and by
  `TileTypeExtensions.GetRuggedness`, and faded out over water.

## Climate coupling

`RiverMapper` emits a moisture-boost field that `TerrainGenerator` applies **before**
classification, and the boosted value is what gets stored — so the palette, terraform nudges and
every later read see the same number. Three contributions:

- **Riparian halo** — chamfer-propagated moisture around river and lake tiles, scaled by flow.
- **Delta fans** — a river tile at shore elevation touching the sea seeds a wide boost; the
  tidal-marsh classification rule turns the fan into wetland down to the waterline.
- **Drainage** — slope sheds moisture, flats hold it, centred on the world's measured mean land
  slope so the total moisture budget is unchanged.

`RiverMapper.GetOceanDistance` also backs the shore pass: shores are a *distance* post-pass, not an
elevation band, so beaches stay narrow on flat worlds.

## Hydrology

Sources are high-elevation tiles, spaced apart, randomly accepted, and capped by a per-area budget
scaled by the exported river-density knob — so source count per unit area is constant across world
sizes. Tracing is steepest descent across **six offset-row neighbours** with a deterministic
per-tile dither scaled to the world's mean slope; without the dither, near-flat terrain degenerates
into the row-parity bias of the neighbour layout (straight runs with staircase kinks). A stuck
trace calls `FillDepression`, which floods a bounded lake and overflows downstream.

Marking runs down to the waterline. Stopping above it severs every river from the sea across the
shore band — the long-standing "rivers end before the ocean" bug.

## Tiles

`TileType` is a byte enum; `TileTypeExtensions` holds every per-tile property as a switch:
base speed, discomfort, steering aversion, cover bonus, grazeability, spawnability,
terraformability, nutrition cap, ruggedness, clutter, and the wetter/drier/balanced shift chains
used by terraforming.

`GetClutter` is a general property; Reef is currently the only tile with any. It is resolved
against body radius in `TerrainProfile.Speed`, so a large body is obstructed and a small one is
not.

`IsSubmerged` (water **plus** Reef) is what element checks use; `IsWater` — which governs drowning
and spawn exclusion — deliberately excludes coral.

## World queries — `WorldManager`

The single door into the world. Notable entry points:

- `GetTile` / `SetTile` / `PaintTile` / `Terraform`
- `GetElevation` (barycentric-interpolated to match the rendered surface), `GetVertexElevation`,
  `GetVertexMoisture`, `GetVertexTemperature`
- `GetNutrition` / `ConsumeNutrition` / `AddNutrition`
- `GetMycelium` / `AddMycelium` / `DecayMycelium`
- `GetWaterFractionOnPath` — the across-water rejection test used by hunting and carrion
- `IsSpawnable` / `IsSpawnableForSpecies`, `HasWaterNearby`, `HasTerrainNear`
- `EdgeAversion` — a ramp to full aversion across the outer few tiles, maxed into every terrain
  steering sampler. Position clamping alone leaves creatures pressing into the border indefinitely;
  out of bounds `GetTile` answers deep water, which reads as "more open sea" to exactly the species
  that live in it
- `DeviationFromPristine` / `BuildPristineSamples` — how far the world has been terraformed from
  what generation produced; used by the invariant gate
- `DirtyChunks` — what the renderer must rebuild

## Diagnostics — `WorldSnapshot`

Written once at generation into `logs/`, all sharing a base name encoding seed and parameters:
biome PNG, elevation PNG, moisture PNG, temperature PNG, a spawn-point PNG written after initial
spawning, and a `.txt` carrying the full parameter set, per-tile-type distribution, a
river-connectivity line (with a warning when rivers are severed) and niche-coverage roll-ups.

The niche roll-ups are split so beach sand cannot be counted as desert.
