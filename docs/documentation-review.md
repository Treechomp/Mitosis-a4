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

> **Status**: All 22 issues have been **resolved**. See resolution notes on each item.

---

## Critical Issues

### C1. README.md describes the archived Python version, not the active Godot project — RESOLVED

The root `README.md` still references the **archived** Python/Arcade implementation:
- Tech stack lists Arcade, Esper, OpenSimplex, NumPy (should be Godot 4.6, C#)
- Quick Start shows `pip install` / `python run.py` (should be Godot instructions)
- Project structure shows the Python `src/` layout, not `godot/Scripts/`
- Documentation link points to `development-plan.md` (superseded)

**Resolution**: README.md fully rewritten with Godot tech stack, quick start, project
structure, and documentation links.

### C2. "Snow" tile type listed but does not exist in code — RESOLVED

`FEATURES_AND_DESIGN.md` listed `Snow` in the terrain types table. The `TileType` enum
has no Snow variant.

**Resolution**: Terrain types table completely rebuilt from code. Now lists all 20 tile
types with correct values for all properties (walkability, speed, discomfort, avoidance,
cover, spawnable, grazeable).

### C3. Terrain speed modifiers table has wrong values — RESOLVED

Every non-Grass speed modifier in the FEATURES_AND_DESIGN terrain table was incorrect.

**Resolution**: All values now match `TileType.cs:GetSpeedMultiplier()` exactly, including
all 11 new tile types added in Phase 2.4.

### C4. Walkability claims are wrong — RESOLVED

The doc marked DeepWater and ShallowWater as "No*" (not walkable). In code,
`TileType.IsWalkable()` returns `true` for everything except Mountain and Lava.

**Resolution**: Terrain table now correctly shows water as walkable with `Yes*` footnote
explaining it's slow and uncomfortable. Mountain and Lava correctly shown as `No`.

### C5. Terrain generation thresholds and algorithm details are wrong — RESOLVED

The generation algorithm section had multiple errors (thresholds, noise type, octaves).

**Resolution**: Generation algorithm section completely rewritten with correct
FastNoiseLite parameters, temperature-based biome selection, domain warping details,
and elevation/moisture/temperature thresholds matching `TerrainGenerator.cs`.

### C6. ISystem interface signature is wrong — RESOLVED

Doc said systems implement `Update(EntityManager, WorldManager, ...)`.
Code: `Process(EntityManager em)` — single parameter, different method name.

**Resolution**: All references updated to `Process(EntityManager em)`. Constructor
injection pattern for additional dependencies (WorldManager, SpatialHash) documented.

---

## Major Issues

### M1. Rendering description is completely outdated — RESOLVED

Doc described `_Draw()` calls using DrawRect, DrawCircle, DrawTriangle.

**Resolution**: Rendering section rewritten to describe MultiMeshInstance2D batching
(3 draw calls for all entities), chunk texture caching, and frustum culling.

### M2. LOD-aware systems list is incomplete — RESOLVED

Doc only listed WanderSystem, SeparationSystem, HerdingSystem as LOD-aware.

**Resolution**: Full LOD-aware systems table added with all 7 gated systems
(Wander, Separation, Herding, Collision, TerrainDiscomfort, Terraform, Reproduction)
and their gate levels.

### M3. Sectid species values are wrong in multiple places — RESOLVED

Attack damage, hunger decay, max lifespan, carrying speed, and feeding claims were
all incorrect.

**Resolution**: Sectid section rewritten with correct values from SpeciesRegistry.cs.
Feeding claim corrected to document that Sectids cannot graze and must hunt to survive.

### M4. Rabbit reproduction values are wrong — RESOLVED

Reproduction cooldown (300→600), hunger cost (25→45), energy cost (20→30) were wrong.

**Resolution**: Rabbit section updated with correct reproduction values from
SpeciesRegistry.cs.

### M5. Energy regeneration system is undocumented — RESOLVED

HungerSystem includes energy regeneration logic that was not documented.

**Resolution**: Energy regeneration documented in Section 5.4 (Hunger System) including
RegenCooldown mechanic and per-species EnergyRegenRate.

### M6. Velocity clamping is undocumented — RESOLVED

MovementSystem clamps creature velocity to 0.25 tiles/tick, was not documented.

**Resolution**: Velocity clamping documented in Section 5.2 (Movement System) with
explanation of why it exists (prevents unbounded velocity accumulation).

### M7. Local density suppression in reproduction is undocumented — RESOLVED

ReproductionSystem suppresses reproduction when nearby same-species count exceeds
2× PreferredGroupSize, was not documented.

**Resolution**: Local density check documented in Section 5.13 (Reproduction System)
as requirement #7 with radius and threshold details.

### M8. Biome types are wrong in godot-roadmap.md — RESOLVED

Doc said "6 biome types" with wrong names. Code has 11 BiomeType values.

**Resolution**: Roadmap updated with correct 11 biome types (Ocean, Coast, Grassland,
Forest, Desert, Mountain, Wetland, River, Arctic, Tropical, Volcanic).

---

## Minor Issues

### m1. GrazingSystem file location inconsistency — RESOLVED

Execution order incorrectly listed `[TerrainSystems.cs]` for GrazingSystem.

**Resolution**: Corrected to `[SurvivalSystems.cs]` in execution order.

### m2. ReproductionSystem file location wrong — RESOLVED

Section 5.13 incorrectly said `SurvivalSystems.cs`.

**Resolution**: Corrected to `ReproductionSystem.cs`.

### m3. File map says "80+ properties" — RESOLVED

Inconsistency between "80+" and "90+" in different locations.

**Resolution**: Standardized to "90+" throughout (actual count is ~95).

### m4. Reproduction spawn doesn't retry — RESOLVED

Doc claimed "up to 10 attempts" for spawn location but code does a single attempt.

**Resolution**: Documentation updated to describe single-attempt behavior.

### m5. plan-optimization.md is partially stale — RESOLVED

Listed rendering optimizations as planned when they were already implemented.

**Resolution**: Updated to reflect completed optimization status.

### m6. `CreaturesPerChunk` export parameter undocumented — RESOLVED

GameManager.cs has `CreaturesPerChunk = 2f` not listed in Configuration Reference.

**Resolution**: Added to Configuration Reference (Section 8) in FEATURES_AND_DESIGN.md.

### m7. Player entity visual not in entity visuals table — RESOLVED

Player rendered as yellow circle (size 12) but not in the Entity Visuals table.

**Resolution**: Added Player row to Entity Visuals table in Section 7.

### m8. Sectid food delivery self-feed percentage discrepancy — RESOLVED

Doc claimed 30% self-feed on delivery needed verification.

**Resolution**: Verified and documented correctly in Sectid/Nest System section.

---

## Audit History

| Date | Action | Issues |
|------|--------|--------|
| Feb 2026 | Initial audit | 22 issues identified (6 critical, 8 major, 8 minor) |
| Feb 2026 | Full fix pass | All 22 issues resolved |
| Feb 2026 | Post-Phase 2.4 update | Docs updated for 11 new tile types, world size, seed changes |
