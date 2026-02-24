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

## Session 24 Review (20260224_222216)

**Config**: World 32, Seed 69, Initial 1500, Cap 10000. Herbivore 0.82, Sectid 0.08.

### Population Analysis

Population grew steadily from 1,695 (tick 100) to cap ~9,969 by tick 19,800 then stabilized.

**Winners — Dominant species at equilibrium:**
- **Shroomer**: Exploded from 162 → 7,307 (73% of total). Spore reproduction dominates the world.
- **Elk**: 124 → 469. Steady climber, never declined.
- **Deer**: 158 → 387. Solid herbivore throughout.
- **Musk Ox**: 85 → 198. Consistent arctic herbivore.
- **Camel**: 55 → 169. Stable arid specialist.
- **Parrot**: 52 → ~200 peak then settled ~182. Solid mid-game.

**Losers — Extinctions and collapses:**
- **Snake**: 17 → 0 by tick ~8000. Carnivore, couldn't find enough prey.
- **Sectid**: 120 → 0 by tick ~4100. Faction species — terraform/nest loop couldn't keep up.
- **Faeling**: Never spawned beyond 6. Crystal faction failed to establish.
- **Wolf**: 40 → 0 by tick ~5400. Pack hunter, collapsed with other predators.
- **Scorpion**: 15 → 0 by tick ~7500. Small predator outcompeted.
- **Arctic Fox**: 22 → 2. Near extinction — outcompeted in arctic niche.
- **Bear**: 8 → 2. Barely hanging on.
- **Hawk**: 20 → 0 by tick ~6200. Aerial predator collapsed.
- **Shark**: 11 → 0 by tick ~9000. Aquatic niche unsustainable.
- **Crocodile**: 13 → 1. Near extinction.
- **Jaguar**: 11 → 1. Near extinction.

**Key events breakdown (27,500 ticks):**
| Event | Count | Notes |
|-------|-------|-------|
| spore_created | 16,536 | Shroomer spore reproduction engine |
| reproduce | 14,455 | Steady reproduction across all species |
| spore_matured | 10,970 | ~66% spore survival rate |
| starvation | 5,300 | Primary death cause (3,731 Shroomer alone despite growth) |
| age_death | 1,159 | Natural lifespan expiry |
| hunt_start | 351 | Very few hunts — predators collapsed early |
| environment_death | 276 | Drowning/suffocation |
| kill | 154 | Only 44% hunt success rate |
| hunt_fail | 151 | Prey escaping / discomfort abandons |

**Shroomer dominance via spore reproduction.** Despite 3,731 starvation deaths, Shroomer's
spore mechanism (16,536 created, 10,970 matured) massively outpaced losses. Predators were
unable to control the population — all carnivores went extinct. Hunting was negligible as
population control (only 154 kills total). This is a Shroomer-dominated equilibrium where
starvation is the only real population check.

### Session 25 Comparison (20260224_231045, same parameters)

| Milestone | Session 24 | Session 25 |
|-----------|-----------|-----------|
| Pop at tick 5K | 2,307 | 2,942 |
| Pop at tick 10K | 3,743 | 4,079 |
| Cap reached | tick 19,800 | tick 19,500 |
| Shroomer at end | 7,307 (73%) | 6,086 (61%) |
| Deer at end | 387 | 657 |
| Elk at end | 469 | 407 |

Same macro pattern reproduced: Shroomer dominance, total predator extinction, Sectid
collapse by ~4K-5K ticks. S25 grew slightly faster and had more species diversity at
equilibrium (Deer higher, Shroomer slightly less dominant). Differences are within
normal stochastic variation — LOD changes have not distorted simulation outcomes.

### Performance Observations (from user report)

1. **TileRegenerationSystem: ~16ms per tick consistently**
   - World size 32 chunks × 32 tiles = 1,024 chunks
   - Each chunk iterates 32×32 = 1,024 tiles
   - Total: 1,048,576 tile checks per tick
   - `IsGrazeable()` check + float comparison + `MathF.Min` addition per tile
   - This is a flat O(chunks × tiles²) cost, independent of population
   - **NOT LOD-gated** — it shouldn't be, because regeneration is a world-level process, not entity-level

2. **HuntingSystem: heaviest entity system, increasing with population**
   - Each predator does 2-4 `QueryRadius()` calls per tick:
     - Pack member count (line 254)
     - Target acquisition scan (line 316)
     - Tracking scan when hungry (line 475)
     - Pack share on kill (line 663)
     - Isolation check (line 807)
     - Flanker position check (line 1162)
   - Even in this session with few predators, the *prey iteration* inside queries scales with local density
   - At 10K entities with spatial hash cell size 32: high-density cells contain dozens of entities

3. **Distance checking is the core cost driver**
   - LODSystem: `MathUtils.Distance()` (sqrt) for every LOD entity, every tick
   - HuntingSystem: `MathUtils.DistanceSquared()` per candidate inside QueryRadius
   - SeparationSystem: `MathF.Sqrt(distSq)` per neighbor pair
   - CollisionSystem: `MathF.Sqrt(distSq)` per overlapping pair

---

## Track C: Next-Phase Performance Optimizations

### C1. Tile Regeneration Optimization (Target: 16ms → <2ms)

The 16ms TileRegenerationSystem cost is entirely from iterating 1M+ tiles per tick.
Several approaches, in order of effort vs impact:

**C1a. Skip-tick throttle (easiest, ~8ms saving)**
Regenerate only every N ticks (e.g., every 4 ticks) and multiply the regeneration rate:
```csharp
// In TileRegenerationSystem.Process()
_tickCounter++;
if (_tickCounter % 4 != 0) return;
// RegenerationRate becomes 0.002f (4× base) inside chunk
```
Nutrition resolution: 0.002 per 4 ticks vs 0.0005 per tick = identical result.
At `RegenerationRate = 0.0005f`, tiles take 2000 ticks to fully recover — a 4-tick gap is invisible.

**C1b. Dirty-chunk tracking (medium effort, near-zero cost when stable)**
Only iterate chunks that have been grazed since last full regeneration:
```csharp
// In ConsumeNutrition(): mark chunk dirty
// In RegenerateNutrition(): if all tiles are at max, mark clean and skip next time
```
At equilibrium with 10K herbivores, maybe 200-400 chunks are actively grazed.
Reduces iteration from 1024 chunks to ~200-400 = 60-80% reduction.

**C1c. SIMD/vectorized nutrition update (advanced)**
The inner loop (`if grazeable && < max: add rate, clamp`) is SIMD-friendly.
Could use `System.Numerics.Vector<float>` to process 8 tiles per cycle.
Requires restructuring nutrition storage from `float[,]` to `float[]`.

**Recommendation**: C1a first (trivial change, halves the cost), then C1b if still showing up.

### C2. Chunk-Based LOD for Distance-Heavy Systems

Currently LODSystem computes `MathUtils.Distance()` (which includes `MathF.Sqrt()`) for
every entity with SimulationLOD, every tick. At 10K entities that's 10K sqrt calls per tick.

**Proposal: Compute LOD per spatial-hash cell, not per entity.**

The spatial hash cell size is 32 tiles. Entities in the same cell are at most ~45 tiles
apart (diagonal). LOD boundaries are at multiples of `visibleRadius` (60+ tiles).
The error from using cell-center distance instead of entity distance is at most ~22 tiles
— well within the hysteresis buffer already in place (10% of boundary distance).

```
Implementation sketch:
1. Each tick, compute LOD level for each occupied spatial hash cell
   (distance from cell center to player → LOD level)
2. When an entity's cell LOD changes, update the entity's LOD component
3. Skip per-entity distance computation entirely
4. Cost: ~200-400 cell distance checks vs 10,000 entity checks = 25-50× reduction
```

This also benefits HuntingSystem indirectly: fewer entities are DueThisTick,
so fewer spatial hash queries fire.

### C3. Hunting System Distance Reduction

HuntingSystem is the second heaviest and **scales with population**. Key optimizations:

**C3a. Early-out on spatial hash queries using squared distances only**
Several places compute `MathF.Sqrt()` when squared distance would suffice:
- Line 546: `float dist = MathF.Sqrt(dx * dx + dy * dy)` — only needed for
  pack tactics (hold distance, approach speed). For attack range check (line 598),
  `distSq < attackRangeSq` already works. Split the sqrt to only happen when
  entering pack tactic movement.
- Separation system (line 73): `float dist = MathF.Sqrt(distSq)` — needed for
  normalization. Could use fast inverse sqrt approximation instead.

**C3b. Reduce redundant QueryRadius calls per predator**
A single predator in a pack currently triggers:
1. Pack member count → QueryRadius (line 254)
2. Target scan → QueryRadius (line 316) — same radius, overlapping results
3. Tracking scan → QueryRadius (line 475) — wider radius
4. Isolation check → QueryRadius per target (line 807)

Optimization: Cache the first query result and reuse it for pack counting + target scan
since they use overlapping radii. The isolation check could also be cached per-target
(multiple predators targeting the same prey re-check isolation independently).

**C3c. Predator-only spatial hash**
Currently all entities share one spatial hash. HuntingSystem queries return ALL entities
in range, then filters for Prey flag. At 10K entities (mostly herbivores), a query might
return 50 entities but only 10 are valid targets.

A separate predator-indexed structure (or component flag filter on the hash) would cut
iteration counts significantly for prey-searching queries.

### C4. Priority Order

| Priority | Task | Expected Impact | Effort |
|----------|------|-----------------|--------|
| 1 | C1a: Tile regen skip-tick | 16ms → ~4ms | Trivial (5 lines) |
| 2 | C2: Chunk-based LOD | 10K sqrt/tick → ~300 | Medium |
| 3 | C3b: Cache QueryRadius in hunting | ~30-40% reduction in spatial queries | Medium |
| 4 | C1b: Dirty-chunk regen | 4ms → <1ms at equilibrium | Low-medium |
| 5 | C3a: Eliminate unnecessary sqrt | ~10-15% per-entity hunt cost | Low |
| 6 | C3c: Predator spatial hash | Reduces false positives in queries | Higher effort |

---

## What This Plan Does NOT Cover (Future Work)

- Spatial hash consolidation (multiple systems rebuild independently)
- Statistical simulation for Aggregate LOD (chunk-level population math)
- Entity materialization/dematerialization at LOD boundaries
- Rendering LOD (smaller/simpler shapes for distant entities)
- Thread-based system parallelization
