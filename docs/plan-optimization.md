# Optimization Plan: Rendering + Simulation LOD

## Goal
Scale from ~1,500 entities at acceptable FPS to 10,000+ entities at 60 FPS.

## Current Bottlenecks

**Rendering** (~15,000+ draw calls/frame):
- Terrain: Individual `DrawRect()` per tile in visible chunks (~1,000-4,000/frame)
- Entities: Individual `DrawCircle()`/`DrawPolygon()`/`DrawRect()` per entity with NO
  frustum culling - all entities drawn every frame even when off-screen
- No batching, no shaders, no MultiMesh

**Simulation** (14 of 17 systems ignore LOD):
- HuntingSystem (670+ lines, spatial queries), FleeingSystem, CollisionSystem,
  NestSystem, CrystalSystem, SporeSystem all process every entity every tick
- Only WanderSystem, SeparationSystem, HerdingSystem check LOD

---

## Track A: Rendering Optimization

### A1. Entity Frustum Culling (Quick Win)

**What**: Skip drawing entities outside the camera viewport.
**Where**: `GameManager.DrawEntities()`
**How**: Before drawing each entity, check if its screen position is within the
visible viewport bounds (already computed in `_Draw()` for chunk culling). Skip
the draw call if outside.

```
// Pseudocode - add to DrawEntities loop:
if (pos.X < minWorldX - margin || pos.X > maxWorldX + margin ||
    pos.Y < minWorldY - margin || pos.Y > maxWorldY + margin)
    continue;
```

**Impact**: At default zoom, maybe ~4 chunks visible out of 256 total. Roughly
95%+ of entities are off-screen and currently drawn for nothing. This alone
could cut entity draw calls from 1,500 to ~75-100.

**Risk**: None. Pure rendering optimization, no simulation impact.

### A2. Chunk Texture Caching

**What**: Pre-render each chunk's terrain to an `ImageTexture`, draw one textured
rect per visible chunk instead of 1,024 individual `DrawRect()` calls (32x32 tiles).
**Where**: New `ChunkRenderer` helper or inline in `GameManager`
**How**:
1. When a chunk is first loaded (or after terraform modifies it), render its 32x32
   tiles into an `Image` (CPU-side pixel buffer)
2. Upload to `ImageTexture`
3. In `_Draw()`: one `DrawTextureRect()` per visible chunk instead of 1,024 `DrawRect()`
4. Mark chunks dirty when TerraformSystem modifies a tile; re-render on next frame

**Impact**: Terrain draw calls drop from ~1,000-4,000 to ~4-16 (one per visible chunk).
Terraform changes are infrequent so re-rendering is rare.

**Risk**: Low. Small memory overhead for textures (~16 chunks visible * 32*32*4 bytes =
negligible). Need to invalidate on terraform.

### A3. Entity Batching with MultiMesh2D

**What**: Replace per-entity draw calls with Godot's `MultiMesh2D` instanced rendering.
One MultiMesh per shape type (circle, triangle, square), updated each frame.
**Where**: New rendering nodes added as children of GameManager or a RenderLayer node
**How**:
1. Create 3 `MultiMeshInstance2D` nodes (one per ShapeType), each with a small
   base mesh (unit circle, unit triangle, unit square)
2. Each frame, iterate visible entities (using A1 frustum culling), populate instance
   transforms (position, scale from Size) and colors
3. Set `InstanceCount` and update `MultiMesh` buffers
4. Godot draws all instances in one draw call per shape type

**Impact**: Entity rendering drops from N draw calls to 3 draw calls total (one per
shape type), regardless of entity count. This is the single biggest rendering win.

**Risk**: Medium. Requires restructuring the rendering approach. MultiMesh color
requires `UseColors = true` on the MultiMesh resource. Need to handle the case where
visible entity count changes frame-to-frame (resize buffers or pre-allocate to max).

**Alternative**: If MultiMesh proves awkward, a simpler approach is to collect all
same-shape entities and issue one `DrawPolygon()` with concatenated vertex arrays
(manual batching). Less efficient than MultiMesh but simpler to implement.

### Rendering Implementation Order

```
A1 (frustum culling) → A2 (chunk textures) → A3 (entity MultiMesh)
```

A1 is a 10-line change with immediate impact. A2 is moderate effort with big
terrain savings. A3 is the largest change but provides the scaling solution.
Each step is independently valuable - we can stop after any step and have improvement.

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
| HuntingSystem | Skip at Statistical+ | Spatial queries are the most expensive per-entity operation. Distant predators still move (wander) and starve (hunger) but don't actively hunt. |
| FleeingSystem | Skip at Statistical+ | Distant prey don't need fear updates. They still move via wander. If a predator approaches, LOD will upgrade them to Full before they're in danger. |
| CollisionSystem | Skip at Reduced+ | Off-screen overlaps are invisible. When entities come on-screen, one tick of collision resolution fixes any overlap. |
| CrystalSystem (ranged attack) | Skip at Reduced+ | Ranged target scanning is expensive. Crystal spawning/death still works (checked in HungerSystem/AgingSystem). Only skip the attack loop. |
| NestSystem (spawning loop) | Skip larvae at Reduced+ | Nest food storage and Sectid food delivery still work. Only throttle the larvae spawn timer processing. |
| SporeSystem (AoE attack) | Skip AoE at Reduced+ | Shroomer growth still works (simple increment). Only skip the expensive AoE spatial query. Spore moisture still accumulates. |

**Low-cost systems** (cheap but easy to gate, small cumulative benefit):

| System | LOD Gate | Rationale |
|--------|----------|-----------|
| TerraformSystem | Skip at Statistical+ | Terraforming is probabilistic and slow. Skipping distant terraform is unnoticeable. |
| GrazingSystem | Skip at Statistical+ | Distant entities won't feed but also won't starve instantly (hunger decay is slow). Resume on LOD upgrade. |
| ReproductionSystem | Skip at Statistical+ | Distant entities won't reproduce. Population pressure still maintained by nearby entities. |
| TerrainDiscomfort | Skip at Reduced+ | Accumulation is cosmetic for distant entities. Resets naturally via decay. |

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

```
B1 (HuntingSystem) → B1 (FleeingSystem) → B1 (CollisionSystem)
→ B1 (faction systems) → B1 (low-cost systems)
```

Start with the three highest-CPU systems. Each is an independent change.

---

## Combined Implementation Order

| Step | Track | Change | Effort | Expected Impact |
|------|-------|--------|--------|-----------------|
| 1 | A1 | Entity frustum culling | Small (~10 lines) | ~95% fewer entity draw calls |
| 2 | B1 | LOD-gate HuntingSystem | Small (~5 lines) | Skip expensive spatial queries for distant predators |
| 3 | B1 | LOD-gate FleeingSystem | Small (~5 lines) | Skip predator scanning for distant prey |
| 4 | B1 | LOD-gate CollisionSystem | Small (~5 lines) | Skip 2-pass collision for distant entities |
| 5 | A2 | Chunk texture caching | Medium (~60-80 lines) | Terrain draw calls: ~4,000 → ~4-16 |
| 6 | B1 | LOD-gate faction systems (Crystal, Nest, Spore attacks) | Small (~15 lines total) | Throttle faction spatial queries |
| 7 | B1 | LOD-gate remaining (Terraform, Grazing, Reproduction, Discomfort) | Small (~20 lines total) | Cumulative CPU savings |
| 8 | A3 | Entity MultiMesh batching | Large (~150-200 lines) | Entity draw calls: N → 3 regardless of count |

Steps 1-4 are quick wins that should immediately improve the 1,500-entity experience.
Step 5 handles terrain. Steps 6-7 round out LOD coverage. Step 8 is the big rendering
rewrite that enables 10,000+ entities.

**After steps 1-4**, we should profile again to see where the remaining bottlenecks are
before committing to steps 5-8. The actual bottleneck distribution may surprise us.

---

## What This Plan Does NOT Cover (Future Work)

- Spatial hash consolidation (multiple systems rebuild independently)
- Statistical simulation for Aggregate LOD (chunk-level population math)
- Entity materialization/dematerialization at LOD boundaries
- Rendering LOD (smaller/simpler shapes for distant entities)
- Thread-based system parallelization
