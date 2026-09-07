> ## ⚠ ARCHIVED — historical document
> **Accurate as of 2026-01-29. It is not a description of the current code or the current design.**
> Why it is here: The engine evaluation that chose Godot 4 + C# over Python/Arcade, Unity DOTS and Bevy. The decision was made and executed; kept as the rationale.
>
> Current documentation: [`docs/design/`](../design/), [`docs/implementation/`](../implementation/),
> [`docs/changelog.md`](../changelog.md). Everything below is preserved verbatim.

---

# Architecting large-scale ecosystem simulations with evolution mechanics

**Your Python/pygame prototype should migrate to Godot 4 with C# or Bevy (Rust)**—Python's ~100-entity ceiling makes it unsuitable for thousands of autonomous creatures. The optimal architecture combines archetype-based ECS for data layout, spatial hashing for neighbor queries, time-sliced AI with Level of Detail, and compact genetic encoding. Games like Factorio and Dwarf Fortress prove that **10,000+ entities at 60 FPS is achievable** with careful design, though most hit single-core CPU bottlenecks rather than memory limits.

## ECS architecture enables cache-efficient mass entity processing

The Entity Component System pattern underpins every successful massive-simulation game. Unlike traditional OOP where objects scatter through memory causing cache misses, ECS stores component data contiguously by type—enabling the CPU prefetcher to load upcoming data while processing current entities.

**Core architecture principles:**
- **Entities are just IDs** (32-64 bit integers with generation counters for recycling)
- **Components are Plain Old Data** (no methods, no inheritance)—`Position { float x, y }`, `Health { int current, max }`
- **Systems are functions** that query and process all entities with matching components

**Archetype-based storage** (used by Bevy, Unity DOTS, Flecs) groups entities with identical component sets into tables where each column is a contiguous array. A movement system iterating 10,000 entities with Position and Velocity components processes sequential memory addresses, achieving **40-60% performance gains** over scattered objects.

**Sparse set storage** (EnTT model) excels when components change frequently—O(1) add/remove versus archetype's expensive table migrations. The practical pattern: use archetypes for core simulation data (position, velocity, genetics) and sparse sets for transient tags (target-acquired, fleeing, mating).

For your ecosystem simulation, structure creature components by access frequency. Position, velocity, and AI state update every frame ("hot")—pack these tightly. Inventory, relationships, and detailed lineage data access rarely ("cold")—store separately. This **hot/cold splitting** keeps cache lines filled with relevant data during tight update loops.

## Data-Oriented Design maximizes throughput for thousands of entities

Structure of Arrays (SoA) layout stores each field in its own array rather than grouping fields per entity. When a system only needs positions, it reads contiguous floats without touching velocity, health, or genetics data that would pollute the cache:

```cpp
// Structure of Arrays - optimal for batch processing
struct Creatures {
    float x[10000], y[10000];      // Positions
    float vx[10000], vy[10000];    // Velocities  
    uint8_t ai_state[10000];       // AI state
    Genome genomes[10000];         // Genetics (accessed less often)
};
```

**Memory alignment** matters: align arrays to **64-byte cache line boundaries**. A 48-byte component struct wastes 16 bytes per entity due to padding—SoA eliminates this. Modern CPUs load 64 bytes at a time; sequential SoA access means each cache line fetch delivers 16 float positions (at 4 bytes each) instead of one scattered entity.

The **AoSoA hybrid** chunks SoA data into SIMD-width groups (4-8 entities), enabling vectorized operations while maintaining reasonable cache behavior. Factorio's optimization team found their simulation became **memory-bound rather than CPU-bound**—data layout decisions dominated performance more than algorithmic complexity.

## Spatial hashing outperforms trees for uniform entity distributions

For ecosystem simulations where creatures move frequently and query neighbors constantly (perception, flocking, predator detection), **spatial hashing delivers O(1) insertions and neighbor lookups**:

```python
def hash_position(x, y, cell_size=20, table_size=10000):
    xi, yi = int(x // cell_size), int(y // cell_size)
    return abs((xi * 92837111) ^ (yi * 689287499)) % table_size
```

Set cell size to **2× the typical entity perception radius**—this ensures neighbor queries check at most 9 cells. For 10,000 creatures with 30-unit perception range, 15-unit cells yield predictable ~100µs range queries.

**Quadtrees** suit variable-density scenarios (clustered populations, large empty spaces) but incur O(log n) update costs per moving entity. Spatial hashing's constant-time updates make it preferable for uniformly-distributed ecosystems where everything moves every frame.

**Chunk-based persistence** layers over spatial partitioning for large worlds. Minecraft-style 64×64 tile chunks enable streaming distant areas to disk while maintaining detailed simulation near the player. The transition protocol: when leaving an area, compress detailed creature state into population statistics; when returning, spawn creatures matching those statistics.

## Time-sliced AI with Level of Detail preserves emergent behavior at scale

Running full behavior trees for 10,000 creatures every frame is computationally impossible. The **LOD Trader algorithm** (Game AI Pro) treats AI fidelity as an optimization problem: maximize perceived realism given a fixed compute budget.

**Practical LOD tiers for ecosystem simulation:**

| Distance | Update Rate | AI Complexity | Pathfinding |
|----------|-------------|---------------|-------------|
| 0-20m | Every frame | Full behavior tree, sensing | Individual A* |
| 20-50m | Every 4 frames | Simplified rules | Flow fields |
| 50-150m | Every 16 frames | State machine | Group waypoints |
| 150m+ | Every 60 frames | Statistical | None |
| Off-screen | On demand | Population aggregates | None |

**Hysteresis bands** prevent "popping"—upgrade LOD at 50m but downgrade at 60m. For seamless transitions, interpolate between behavior outputs over 0.5-2 seconds rather than switching instantly.

**Time-slicing** distributes AI updates across frames. With 10,000 creatures and a 5ms AI budget at 60 FPS, process ~1,000 creatures per frame in priority order:

```python
priority = (1 / distance²) × visibility × time_since_update × importance
```

Creatures near the player, recently interacted with, or exhibiting interesting behaviors (hunting, mating) get priority. Distant herbivores grazing contentedly can update once per second without visible degradation.

## Compact genetic encoding enables efficient evolution across populations

Encode genomes as fixed-size byte arrays for cache-friendly storage and fast crossover:

```python
class Genome:  # ~128 bytes total
    # Physical traits: 32 × 16-bit values (64 bytes)
    body_size: uint16      # 0-65535 mapped to trait range
    speed: uint16
    perception_range: uint16
    metabolism: uint16
    # ... 28 more traits
    
    # Neural weights: 16 × 16-bit (32 bytes)
    aggression_weight: uint16
    flee_threshold: uint16
    # ...
    
    # Metadata (32 bytes)
    species_id: uint32
    parent_ids: (uint32, uint32)
    mutation_count: uint16
```

**Mutation and crossover** at this scale cost negligible CPU—under 0.1ms per reproduction event. For 100 births per second (ambitious for ecosystem dynamics), genetics consumes <1% of frame budget.

**Lineage tracking** uses a generational database: store full data for the 3 most recent generations, compress older ancestors to founder markers plus significant mutations, and prune extinct lineages (no living descendants) periodically. This prevents unbounded memory growth across thousands of generations.

**NEAT (NeuroEvolution of Augmenting Topologies)** suits creature AI evolution—it starts with minimal neural networks and adds complexity through mutation, using "innovation numbers" to enable meaningful crossover between different network topologies. Real-time NEAT replaces individuals continuously rather than generationally, fitting naturally into ecosystem simulation where creatures reproduce asynchronously.

## Factorio and Dwarf Fortress demonstrate proven optimization patterns

**Factorio's transport belt breakthrough** revolutionized entity optimization: instead of tracking absolute item positions, store **inter-item distances**. A belt with 200 items requires updating only 2 integers (the gap sizes at each end) rather than 200 positions. This achieved **50-100× speedup** on item movement.

The insight generalizes: **store deltas, not absolutes** wherever entities maintain stable relative relationships. Creature formations, herds, and family groups could track positions relative to a leader rather than absolute world coordinates.

**Dwarf Fortress's connected components** enable O(1) pathfinding reachability checks. Flood-fill assigns each walkable tile a component ID; creatures query "can I reach target?" by comparing component IDs without running A*. Tarn Adams notes this handles dynamic maps (water cutting paths) efficiently through incremental updates.

Both games are **single-core CPU-bound**—multithreading simulation logic where "everything depends on what happened previously" proves extraordinarily difficult. Songs of Syx simulates 10,000+ citizens on one core; Banished's developer achieved 20× pathfinding speedups through algorithmic improvements, not parallelization.

**RimWorld's job system** uses hierarchical priorities: work types contain work givers contain job drivers contain "toils" (atomic actions). Pawns don't decide—their next action is predetermined by the highest-priority available job. This deterministic scheduling enables reproducible AI behavior crucial for debugging and save/load consistency.

## Engine recommendation: migrate from Python to Godot 4 with C#

Your Python/pygame prototype faces a hard ceiling around **100 entities** with complex per-frame logic. Even aggressive optimization (Numba JIT compilation, Cython for hot paths) only extends this to ~500-1000—insufficient for ecosystem simulation with thousands of creatures.

**Godot 4 with C#** offers the optimal migration path:
- GDScript resembles Python for rapid iteration on non-critical code
- C# provides **10× performance over GDScript** for math-heavy simulation
- Full .NET ecosystem access for libraries
- Entity ceiling: **5,000-10,000** autonomous creatures with good architecture
- GDExtension/C++ available for isolated hotspots if needed

**Unity DOTS** achieves higher entity counts (**50,000+**) but imposes a steep learning curve and fundamentally different programming paradigm. The Burst Compiler + Jobs system approaches C++ performance from C# code. Consider this if you have Unity experience or need absolute maximum scale. Note: the 2023 runtime fee controversy was cancelled in 2024, returning to subscription pricing.

**Bevy (Rust)** matches Unity DOTS performance with cleaner ECS design—it's the **fastest Rust ECS by significant margin** according to benchmarks. The catch: learning Rust represents substantial investment. If your team can handle it, Bevy offers excellent long-term architectural benefits (memory safety, no garbage collection pauses) and improving rapidly despite current documentation gaps.

**Development velocity vs. performance tradeoff:**

| Approach | Dev Speed | Performance | Practical Entity Ceiling |
|----------|-----------|-------------|--------------------------|
| Python/Pygame | ★★★★★ | ★ | ~100 |
| Godot GDScript | ★★★★ | ★★ | ~2,000 |
| Godot C# | ★★★ | ★★★ | ~10,000 |
| Unity DOTS | ★★ | ★★★★★ | ~50,000+ |
| Bevy (Rust) | ★★ | ★★★★★ | ~50,000+ |

## Implementation roadmap for your ecosystem simulation

**Phase 1: Validate design in Python** (current)
Keep prototyping game mechanics, evolution rules, and ecosystem dynamics. Use Numba for numerical loops. Target ~500 creatures maximum. Focus on proving the design is fun before optimizing.

**Phase 2: Migrate core simulation to Godot C#**
Port creature logic, genetics, and AI to C# classes following ECS patterns (even without a formal ECS library—Godot's node system can approximate this). Implement spatial hashing in C# for neighbor queries. Target 2,000-5,000 creatures.

**Phase 3: Add LOD and background simulation**
Implement time-sliced AI updates with priority queues. Add distance-based LOD tiers. Create statistical population models for off-screen areas. Target 10,000+ total creatures with ~2,000 in full detail.

**Phase 4: Optimize hotspots**
Profile to identify actual bottlenecks. Move verified hot paths to GDExtension/C++ if needed. Consider flow fields for mass pathfinding. Implement Factorio-style "sleep/wake" for inactive entities.

**Key architectural decisions to make early:**
- **Genome format**: Define your 128-byte structure now; changing later breaks save compatibility
- **Spatial partition cell size**: 2× largest perception radius
- **Component granularity**: Split creatures into 5-8 components by update frequency
- **LOD thresholds**: Match your visual style and gameplay requirements

The games that successfully simulate thousands of entities share one pattern: **ruthless prioritization of what matters**. Dwarf Fortress models individual dwarf memories and relationships because that creates emergent stories. Factorio models exact item positions on belts because that enables complex factory logic. Your ecosystem simulation should model genetics and evolution in detail because that's your core mechanic—everything else (rendering, pathfinding, peripheral behaviors) should be optimized aggressively to free CPU cycles for what makes your game unique.