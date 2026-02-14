# Documentation Review — Code vs Docs Audit

> **Date**: February 2026
> **Scope**: All active docs (`README.md`, `architecture.md`, `FEATURES_AND_DESIGN.md`,
> `godot-roadmap.md`, `plan-optimization.md`) compared against the Godot C# codebase.
> Excluded: `roadmap-old.md` (deprecated).

---

## Summary

After a line-by-line comparison of every source file under `godot/Scripts/` against the
documentation, this audit identified **22 issues** grouped by severity:

- **Critical** (6): Factually wrong information that would mislead a contributor
- **Major** (8): Significant omissions or incorrect values
- **Minor** (8): Stale details, inconsistencies within docs, or cosmetic issues

---

## Critical Issues

### C1. README.md describes the archived Python version, not the active Godot project

The root `README.md` still references the **archived** Python/Arcade implementation:
- Tech stack lists Arcade, Esper, OpenSimplex, NumPy (should be Godot 4.6, C#)
- Quick Start shows `pip install` / `python run.py` (should be Godot instructions)
- Project structure shows the Python `src/` layout, not `godot/Scripts/`
- Documentation link points to `development-plan.md` (superseded)

**Impact**: New contributors will try to run the wrong project.

### C2. "Snow" tile type listed but does not exist in code

`FEATURES_AND_DESIGN.md` line 143 lists `Snow | No | No | No | 0.5x` in the terrain
types table. The `TileType` enum (`TileType.cs`) has **no Snow variant**. The nine actual
tile types are: DeepWater, ShallowWater, Sand, Grass, Forest, Mountain, **River**,
Wetland, Arid.

Additionally, the **River** tile type — which does exist and has specific speed (0.35x),
discomfort (8.0), and avoidance (0.8) values — is **missing from the terrain types table**.

### C3. Terrain speed modifiers table has wrong values

Every non-Grass speed modifier in the FEATURES_AND_DESIGN terrain table is incorrect:

| Tile | Doc Value | Actual Code Value | Delta |
|------|-----------|-------------------|-------|
| DeepWater | 0.3x | **0.25x** | -0.05 |
| ShallowWater | 0.5x | **0.4x** | -0.10 |
| Sand | 0.9x | **0.7x** | -0.20 |
| Forest | 0.8x | **0.85x** | +0.05 |
| Wetland | 0.7x | **0.75x** | +0.05 |
| Arid | 0.95x | **0.8x** | -0.15 |
| Mountain | 0.4x | **0.05x** | -0.35 |
| River | *(missing)* | **0.35x** | n/a |

Source: `TileType.cs:GetSpeedMultiplier()`

### C4. Walkability claims are wrong

The doc marks DeepWater and ShallowWater as "No*" (not walkable). In code,
`TileType.IsWalkable()` returns `true` for **everything except Mountain**. Water is
walkable — just slow and uncomfortable. The asterisk footnote about "aquatic species"
treating water as walkable is misleading; **all species** can walk on water.

Source: `TileType.cs:IsWalkable()` — `return tile != TileType.Mountain;`

### C5. Terrain generation thresholds and algorithm details are wrong

The generation algorithm section has multiple errors:

| Detail | Doc Says | Code Says |
|--------|----------|-----------|
| High elevation | >= 0.85 → Snow | > 0.8 → Mountain (no Snow) |
| Mountain range | 0.70–0.85 | > 0.8 only |
| Deep water | < 0.20 | < 0.3 |
| Shallow water | 0.20–0.30 | 0.3–0.4 |
| Sand (elevation) | *(not mentioned)* | 0.4–0.45 |
| Noise type | "OpenSimplex" | FastNoiseLite (SimplexSmooth) |
| Octaves | "4 octaves each" | Elevation=4, Moisture=3, River=2 |
| Detail noise | Listed as a layer | Does not exist in code |

Source: `TerrainGenerator.cs`

### C6. ISystem interface signature is wrong

Doc says systems implement `Update(EntityManager, WorldManager, ...)`.
Code: `Process(EntityManager em)` — single parameter, different method name.

Source: `ISystem.cs` — `void Process(EntityManager em);`

---

## Major Issues

### M1. Rendering description is completely outdated

Doc (§7): "Godot `_Draw()` calls using DrawRect, DrawCircle, DrawTriangle"

Actual implementation (`RenderingManager.cs`):
- **Entities**: Three `MultiMeshInstance2D` nodes batch-render circle, triangle, and
  square shapes. Per-entity frustum culling skips off-screen entities.
- **Terrain**: Chunk textures are cached as `ImageTexture` (4 pixels per tile).
  Dirty chunk tracking (`WorldManager.DirtyChunks`) triggers re-render only when
  terraforming modifies tiles.

This is a significant performance optimization that was completed but not documented.

### M2. LOD-aware systems list is incomplete

Doc says: "LOD-aware systems: WanderSystem, SeparationSystem, HerdingSystem.
All other systems process every entity every tick regardless of LOD."

Actually LOD-gated in code:

| System | LOD Gate Level | Source |
|--------|---------------|--------|
| WanderSystem | ShouldUpdate() | WanderSystem.cs:46 |
| SeparationSystem | ShouldUpdate() | SpatialSystems.cs:43 |
| HerdingSystem | ShouldUpdate() | HerdingSystem.cs:69 |
| **CollisionSystem** | **Reduced** | SpatialSystems.cs:133 |
| **TerrainDiscomfortSystem** | **Reduced** | TerrainSystems.cs:33 |
| **TerraformSystem** | **Statistical** | TerrainSystems.cs:106 |
| **ReproductionSystem** | **Aggregate** | ReproductionSystem.cs:49 |

The last four are undocumented.

### M3. Sectid species values are wrong in multiple places

| Property | Doc Value | Code Value | Source |
|----------|-----------|------------|--------|
| Attack damage | 8 | **12** | SpeciesRegistry.cs:685 |
| Hunger decay | 0.07/tick | **0.05f** | SpeciesRegistry.cs:705 |
| Max lifespan | 20000 | **25000** | SpeciesRegistry.cs:706 |
| Carrying speed | 0.08 | **0.11f** | SpeciesRegistry.cs:734 |
| Feeding claim | "On Arid and Sand tiles, 0.35 nutrition" | **No tile feeding** (`CanGraze = false`, no `FeedTiles` set) | SpeciesRegistry.cs:782 |

The feeding claim is entirely wrong — Sectids must hunt to survive as stated by the code
comment: "Sectids do NOT graze; they must hunt to survive."

### M4. Rabbit reproduction values are wrong

| Property | Doc Value | Code Value |
|----------|-----------|------------|
| Reproduction cooldown | 300 ticks | **600** ticks |
| Hunger cost | 25 | **45f** |
| Energy cost | 20 | **30f** |

Source: `SpeciesRegistry.cs:216,247-248`

### M5. Energy regeneration system is undocumented

`HungerSystem` (SurvivalSystems.cs:47-60) includes energy regeneration logic:
- When not starving and out of combat: `energy += EnergyRegenRate`
- Has `RegenCooldown` on the `Energy` component to prevent instant regen after combat
- Each species has a configurable `EnergyRegenRate` in `SpeciesDefinition`

This mechanic is not mentioned in Section 5.4 (Hunger System) or anywhere else.

### M6. Velocity clamping is undocumented

`MovementSystem.cs:41-51` clamps creature velocity to a hard max of **0.25 tiles/tick**.
This applies to all entities with the Species component (not the player). It prevents
unbounded velocity accumulation from additive systems like hunting, fleeing, and herding.

This is not documented in Section 5.2 (Movement System).

### M7. Local density suppression in reproduction is undocumented

`ReproductionSystem.cs:93-112` suppresses reproduction when nearby same-species count
exceeds `2× PreferredGroupSize`. Checked within a radius of `1.5× SocialRadius`.
This is a major population control mechanic preventing exponential growth in
well-fed areas.

Not documented in Section 5.13 (Reproduction System). The doc says the process simply
checks thresholds and spawns — the density check is a critical balancing mechanism.

### M8. Biome types are wrong in godot-roadmap.md

Doc says: "6 biome types (Arctic, Temperate, Tropical, Desert, Highland, Wetland)"

Code `BiomeType` enum (TileType.cs:23-33): **8 types** — Ocean, Coast, Grassland,
Forest, Desert, Mountain, Wetland, River. Completely different names and count.

---

## Minor Issues

### m1. GrazingSystem file location inconsistency within FEATURES_AND_DESIGN.md

- Execution order (line 108) says `[TerrainSystems.cs]`
- Section 5.5 (line 430) correctly says `SurvivalSystems.cs`
- Code confirms: GrazingSystem is in `SurvivalSystems.cs`

### m2. ReproductionSystem file location wrong in FEATURES_AND_DESIGN.md

- Section 5.13 (line 688) says: "File: `Scripts/Systems/SurvivalSystems.cs`"
- Code: `ReproductionSystem.cs` (its own dedicated file)
- File map section (line 936) correctly shows it as a separate file

### m3. File map says "80+ properties" but docs text elsewhere says "90+"

- File map (line 946): "80+ property data class"
- Section 4 (line 200): "90+ configurable properties"
- `architecture.md` also says "90+"
- Actual code has ~95 properties, so "90+" is more accurate

### m4. Reproduction spawn doesn't retry (doc says "up to 10 attempts")

Doc (line 702): "Find walkable spawn location within SpawnRadius (up to 10 attempts)"
Code: Picks one random offset, checks walkability, skips if not walkable. No retry loop.

Source: `ReproductionSystem.cs:115-120`

### m5. plan-optimization.md is partially stale

- Says rendering still uses "_Draw() calls" (MultiMesh batching is implemented)
- Says several systems lack LOD gates (code shows they have them)
- Optimization tracks A1/A2/A3 appear implemented but doc still lists them as planned

### m6. `CreaturesPerChunk` export parameter undocumented

`GameManager.cs:29` has `[Export] public float CreaturesPerChunk = 2f;` — a configurable
parameter not listed in the Configuration Reference (Section 8).

### m7. Player entity visual not in entity visuals table

The player is rendered as a yellow circle (size 12) via `EntityFactory.CreatePlayer()`
but is not listed in the Entity Visuals table in Section 7.

### m8. Sectid food delivery self-feed percentage discrepancy

Doc (line 758): "On delivery: add food to nest, self-feed 30%"
This claim should be verified against NestSystem.cs for the exact percentage.

---

## Recommended Priority

1. **Fix README.md** (C1) — highest user-facing impact
2. **Fix terrain types table** (C2, C3, C4) — foundational reference data
3. **Fix generation algorithm** (C5) — misleading for anyone extending terrain
4. **Fix ISystem signature** (C6) — wrong API documentation
5. **Update rendering section** (M1) — describes a non-existent implementation
6. **Update LOD gates** (M2) — affects performance understanding
7. **Fix species values** (M3, M4) — wrong tuning reference
8. **Document missing mechanics** (M5, M6, M7) — critical but undocumented systems
