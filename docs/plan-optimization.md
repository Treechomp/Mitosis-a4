# Optimization Plan: Rendering + Simulation LOD

## Goal
Scale from ~1,500 entities at acceptable FPS to 10,000+ entities at 60 FPS.

## Current Bottlenecks

**Rendering** — RESOLVED (steps A1-A3 all implemented):
- ~~Terrain: Individual `DrawRect()` per tile~~ → Chunk texture caching (A2)
- ~~Entities: Individual draw calls with no frustum culling~~ → MultiMesh batching + frustum culling (A1+A3)
- Current: 3 draw calls for entities + 4-16 for terrain chunks

**Simulation** (LOD mostly implemented):
- HuntingSystem, FleeingSystem, NestSystem, CrystalSystem, SporeSystem: LOD gates at Aggregate
- WanderSystem, SeparationSystem, HerdingSystem: LOD gates via ShouldUpdate()
- CollisionSystem, TerrainDiscomfortSystem: LOD gates at Reduced
- TerraformSystem: LOD gate at Statistical
- ReproductionSystem: LOD gate at Aggregate
- Remaining bottleneck: spatial hash queries in HuntingSystem/FleeingSystem at scale

---

## Track A: Rendering Optimization

### A1. Entity Frustum Culling — DONE

**Implemented in**: `RenderingManager.UpdateEntityMultiMeshes()`

Per-entity viewport bounds check skips off-screen entities before populating
MultiMesh buffers. Uses camera position, zoom, and a size-based margin.

### A2. Chunk Texture Caching — DONE

**Implemented in**: `RenderingManager.DrawTerrain()` + `RenderChunkTexture()`

Each chunk is rendered to a cached `ImageTexture` at 4 pixels per tile. Textures
are rebuilt only when `WorldManager.DirtyChunks` flags a terraform modification.
One `DrawTextureRect()` per visible chunk. Terrain draw calls: ~4-16 per frame.

### A3. Entity Batching with MultiMesh2D — DONE

**Implemented in**: `RenderingManager.UpdateEntityMultiMeshes()` + `CreateMultiMeshInstances()`

Three `MultiMeshInstance2D` nodes (circle, triangle, square) batch-render all
entities. Per-frame: iterate visible entities, set instance transforms (position,
scale) and colors, set `VisibleInstanceCount`. Buffers pre-grow to entity count.
Entity draw calls: 3 total regardless of count.

### Rendering Summary

All three rendering optimizations (A1, A2, A3) are implemented in
`RenderingManager.cs`. The rendering pipeline is no longer a bottleneck.

---

## Track B: Simulation LOD Expansion

### LOD Levels Recap

| Level | Distance | Interval | Description |
|-------|----------|----------|-------------|
| Full | < 50 tiles | Every tick | Full AI |
| Reduced | < 100 tiles | Every 5 ticks | Simplified |
| Statistical | < 200 tiles | Every 30 ticks | Minimal |
| Aggregate | 200+ tiles | Every 60 ticks | Population-level |

### B1. Systems to LOD-Gate

**High-impact systems** (expensive, safe to throttle):

| System | LOD Gate | Rationale |
|--------|----------|-----------|
| HuntingSystem | ~~Skip at Statistical+~~ **Done: Aggregate** | Spatial queries are the most expensive per-entity operation. Currently gated at Aggregate; could tighten to Statistical. |
| FleeingSystem | ~~Skip at Statistical+~~ **Done: Aggregate** | Currently gated at Aggregate; could tighten to Statistical. |
| CollisionSystem | ~~Skip at Reduced+~~ **DONE: Reduced** | Off-screen overlaps are invisible. When entities come on-screen, one tick of collision resolution fixes any overlap. |
| CrystalSystem (ranged attack) | Skip at Reduced+ | Ranged target scanning is expensive. Crystal spawning/death still works (checked in HungerSystem/AgingSystem). Only skip the attack loop. |
| NestSystem (spawning loop) | Skip larvae at Reduced+ | Nest food storage and Sectid food delivery still work. Only throttle the larvae spawn timer processing. |
| SporeSystem (AoE attack) | Skip AoE at Reduced+ | Shroomer growth still works (simple increment). Only skip the expensive AoE spatial query. Spore moisture still accumulates. |

**Low-cost systems** (cheap but easy to gate, small cumulative benefit):

| System | LOD Gate | Rationale |
|--------|----------|-----------|
| TerraformSystem | ~~Skip at Statistical+~~ **DONE: Statistical** | Terraforming is probabilistic and slow. Skipping distant terraform is unnoticeable. |
| GrazingSystem | Skip at Statistical+ | Distant entities won't feed but also won't starve instantly (hunger decay is slow). Resume on LOD upgrade. |
| ReproductionSystem | ~~Skip at Statistical+~~ **DONE: Aggregate** | Distant entities won't reproduce. Population pressure still maintained by nearby entities. |
| TerrainDiscomfort | ~~Skip at Reduced+~~ **DONE: Reduced** | Accumulation is cosmetic for distant entities. Resets naturally via decay. |

**Systems that MUST NOT be LOD-gated**:

| System | Reason |
|--------|--------|
| MovementSystem | Entities must move every tick or they freeze and pile up |
| HungerSystem | Starvation is a core population control. Skipping = immortal distant entities |
| AgingSystem | Natural death is core population control. Skipping = population explosion |
| LODSystem | Must run every tick to update LOD levels |

### B2. Implementation Approach

For each system, the pattern is the same - add a check near the top of the
per-entity loop:

```csharp
// For systems that should skip at Statistical and above:
if (entityManager.HasComponent(entity, ComponentFlags.SimulationLOD))
{
    ref var lod = ref entityManager.SimulationLODs[entity];
    if (lod.Level >= LODLevel.Statistical) continue;
}

// For systems that should skip at Reduced and above:
if (entityManager.HasComponent(entity, ComponentFlags.SimulationLOD))
{
    ref var lod = ref entityManager.SimulationLODs[entity];
    if (lod.Level >= LODLevel.Reduced) continue;
}
```

This is a minimal, low-risk change per system. The LOD level is already computed
by LODSystem each tick. We're just reading it.

### B3. Compensation for LOD Gaps

When systems skip ticks for distant entities, some values drift. To compensate:

- **HungerSystem** (runs every tick): Already handles this - hunger decays
  continuously so entities still feel hunger pressure
- **Hunting/Fleeing skip**: Predators still wander and starve. When LOD upgrades
  as player approaches, they resume hunting immediately. Brief pause is unnoticeable.
- **Collision skip**: Minor overlaps accumulate off-screen. One tick of collision
  resolution on LOD upgrade fixes them. Player never sees the overlap.
- **Terraform skip**: Tiles change slowly anyway. No compensation needed.

### LOD Implementation Order

Most LOD gates are now implemented. Remaining:
```
B1 (GrazingSystem) → B1 (faction system attacks)
```

---

## Combined Implementation Order

| Step | Track | Change | Status |
|------|-------|--------|--------|
| 1 | A1 | Entity frustum culling | **DONE** — in RenderingManager |
| 2 | B1 | LOD-gate HuntingSystem | **DONE** — Aggregate |
| 3 | B1 | LOD-gate FleeingSystem | **DONE** — Aggregate |
| 4 | B1 | LOD-gate CollisionSystem | **DONE** — Reduced |
| 5 | A2 | Chunk texture caching | **DONE** — in RenderingManager |
| 6 | B1 | LOD-gate TerraformSystem | **DONE** — Statistical |
| 7 | B1 | LOD-gate TerrainDiscomfort | **DONE** — Reduced |
| 8 | B1 | LOD-gate ReproductionSystem | **DONE** — Aggregate |
| 9 | A3 | Entity MultiMesh batching | **DONE** — in RenderingManager |
| 10 | B1 | LOD-gate GrazingSystem | Pending |
| 11 | B1 | LOD-gate faction systems (Crystal, Nest, Spore attacks) | Pending |

**Remaining work**: LOD-gate GrazingSystem and faction system attack loops. Profile
at higher entity counts (5K, 10K) to identify remaining bottlenecks (likely spatial
hash queries in HuntingSystem/FleeingSystem).

---

## What This Plan Does NOT Cover (Future Work)

- Spatial hash consolidation (multiple systems rebuild independently)
- Statistical simulation for Aggregate LOD (chunk-level population math)
- Entity materialization/dematerialization at LOD boundaries
- Rendering LOD (smaller/simpler shapes for distant entities)
- Thread-based system parallelization
