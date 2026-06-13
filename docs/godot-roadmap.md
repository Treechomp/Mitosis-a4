# Mitosis — Development Roadmap & Status

> Engine: **Godot 4.6.3 + C#** · Renderer: **3D** · Last updated: **June 2026**
>
> For the full feature/system/species reference see
> **[FEATURES_AND_DESIGN.md](FEATURES_AND_DESIGN.md)**; for architecture see
> **[architecture.md](architecture.md)**. Superseded plans are in **[archive/](archive/)**.

---

## Current state (v0.7 — 3D world)

A self-contained ecosystem simulation runs end-to-end in 3D:

- **Engine/renderer**: Godot 4.6.3, C#, `Node3D` scene with an orthographic isometric
  `Camera3D`, `DirectionalLight3D`, triangulated 3D terrain meshes, and entity
  `MultiMeshInstance3D` batches (13 primitive shapes).
- **Simulation**: custom SoA ECS (16,384 capacity, 25 component types), **19 systems +
  EcosystemLogger** at a fixed 20 TPS.
- **World**: chunked terrain with per-vertex elevation, FastNoiseLite generation (elevation/
  moisture/temperature + domain warp), 20 tile types, 11 biomes, flow-based rivers/lakes,
  landmark features, and per-tile grazing nutrition.
- **Creatures**: 28 data-driven species (5 generalist, 20 biome-specific, 3 factions) with
  solo/pack/swarm/ambush hunting, fear responses, venom, flying/aquatic traits, and
  faction terraforming (spores / nests / crystals).
- **Scale**: distance-based **LOD** (5 tiers via `DueThisTick[]`, cell-cached distances)
  keeps distant simulation cheap — this replaced the removed statistical-sim experiment.
- **Tooling**: F3 per-system profiling + per-species overlay; `EcosystemLogger` CSV output.

> **Config note:** the code currently ships **DEBUG** defaults (9×9 chunks, 500 initial,
> 2000 cap) for lightweight, LOD-free species-balancing sessions. The intended default is
> **~18×18 chunks, ~1500 initial, ~10000 cap**; final production values will be set once all
> features are in and compute/render costs are known.

---

## Implemented

**Core ECS** — SoA `EntityManager` (16,384), `ComponentFlags` queries, 25 component types,
`DueThisTick[]` LOD gate, zero-alloc entity iteration.

**World generation** — chunked storage (32×32); elevation/moisture/temperature noise with
domain warping; temperature = noise + latitude + altitude cooling; 20 tile types; 11 biomes;
flow-based `RiverMapper` (hex-neighbor tracing, flow accumulation, depression lakes, wetland
banks); landmark pass (oases, clearings, permafrost, caves); per-tile nutrition with
depletion/regrowth; terraform shift chains.

**3D rendering** — per-chunk triangulated `MeshInstance3D` with elevation + smooth normals;
custom terrain shader (Lambert + slope-edge darkening + posterization); entity
`MultiMeshInstance3D` primitives placed on the terrain surface and facing velocity; dirty-chunk
mesh rebuilds; isometric follow camera with zoom. *(Mesh detail and entity-on-terrain
positioning are the current active polish area.)*

**Species** — `SpeciesDefinition` (100+ properties), `SpeciesRegistry` (28 species, name-hash
lookup); omnivore diet; flying & aquatic traits; venom DOT; mass-based hunt ratios; preferred
prey; per-species terrain speed/comfort modifiers; biome/tile spawn restrictions.

**Simulation systems** — LOD, Movement (terrain speed + uphill slope + 0.28 cliff block +
damping), SpatialHashUpdate, TerrainDiscomfort (+ drowning/suffocation), Hunger (+ energy regen
+ venom), Grazing, Wander (smooth turning, roaming, discomfort escape), Herding, Separation,
Collision, Hunting (solo/pack-flanking/swarm/ambush-pounce, stealth-aware), Fleeing (fear
responses, stealth-aware), Aging, Reproduction (density + global pressure), Terraform,
TileRegeneration (4-tick throttle), Nest, Spore (S-curve AoE + thorns), Crystal (power
inheritance + ranged).

**Population control** — hard cap in `EntityFactory`; reproduction global-pressure ramp +
local-density suppression.

**Logging/debug** — `EcosystemLogger` (per-event + per-population CSVs, `latest_*` copies);
debug overlay with LOD/category/per-species counts and per-system timings.

---

## Active / near-term work

- [ ] **Terrain mesh & entity positioning** — ongoing polish of the 3D terrain mesh and how
      entities sit on the surface (the current focus; see code, not yet final).
- [ ] **Settle production config** — move off the DEBUG world/population values to the intended
      defaults (~18×18 / 1500 / 10000) once performance at scale is measured.
- [ ] **Ecosystem balancing** — use the DEBUG config + `EcosystemLogger` CSVs to tune species
      so populations neither collapse nor monoculture (historically Shroomers tended to
      dominate; predators tended to collapse early).
- [ ] **Performance at scale** — profile at 5K/10K entities; likely hotspots are spatial-hash
      queries in Hunting/Fleeing and tile regeneration.

### Known issues / tech debt

- [ ] `StatVariation` and the `Species.Generation` counter exist but are unused (reserved for
      trait variation — see Future).
- [ ] `Crystal.FAELING_AGGREGATED` is a vestigial sentinel left over from the removed
      statistical sim; harmless but dead.
- [ ] `TerrainSpeedModifiers` per species — verify all paths use it consistently.
- [ ] No automated tests for systems/ecosystem balance yet.

---

## Future roadmap (designed, not yet implemented)

These are forward-looking design directions; detailed design notes for several of them live in
the archived roadmap. Order is indicative, not committed.

1. **Inner-species variety** — per-entity trait multipliers at birth (speed/size/metabolism/
   senses/fertility/resilience) from `StatVariation`; trait inheritance with mutation; faction
   morphs/castes/aspects; natural-selection tracking.
2. **Game systems & UI** — main menu; HUD (minimap, population bars, selected-entity panel);
   entity selection/inspection; time controls (pause / speed / step); camera modes; settings;
   debug console.
3. **Persistence** — save/load full world + entity state (seed, tick, chunks, entities).
4. **Player progression & directed mutation** — gather resources by observing creatures; a
   bestiary that unlocks species knowledge; spend resources to nudge species' trait evolution;
   resource-gated player abilities.
5. **Evolution & speciation** — long-run trait drift, speciation events, extinction tracking and
   ecological-cascade analysis.

---

## Removed / superseded (history)

- **Statistical simulation** (`StatisticalSimSystem`, `ChunkPopulationData`) — an attempt to
  cut CPU cost for distant chunks via population math. **Removed** and replaced by the LOD
  approach (real entities everywhere, throttled by distance). The old design/validation docs
  are in `archive/`.
- **2D rendering** (`MultiMeshInstance2D`, chunk textures) — replaced by the 3D mesh renderer.
  The migration plan is in `archive/TRIANGLE_GRID_PLAN.md`.
- **Tile-type walkability** (`TileType.IsWalkable`) — replaced by elevation-based slope/cliff
  rules; all tiles are now traversable.

---

## Version history

| Version | Focus |
|---------|-------|
| v0.1–v0.3 | Python→Godot 2D migration; core ECS, species, fear, terrain discomfort, faction species (spore/nest/crystal), pack tactics, mass-based hunting |
| v0.4 | Code reorganization; jitter fixes (velocity damping, mass-based blending, hysteresis escape); coordinated wolf flanking; crocodile ambush; stealth-aware prey |
| v0.5 | World Generation v2: new tile types & biomes, domain warping, temperature/latitude, tile nutrition, landmarks, flow-based rivers |
| v0.6 | Species Diversity: 20 biome species, omnivore/flying/venom, hunting-tactic refactor (Solo/Pack/Swarm/Ambush), S-curve AoE + thorns |
| **v0.7** | **3D world**: triangulated terrain meshes with elevation + lighting, 3D entity primitives, isometric camera, elevation-based passability (slope + cliffs); statistical sim removed in favor of LOD; project on Godot 4.6 |

> Detailed Python-era and 2D-era roadmaps/plans are preserved in
> [archive/](archive/) (`roadmap-old.md`, `development-plan.md`, `plan-optimization.md`,
> `TRIANGLE_GRID_PLAN.md`, `statistical_sim_validation_report.md`).

---

*Last updated: June 2026 · Godot 4.6.3 + C# · 3D renderer*
