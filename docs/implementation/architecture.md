# Architecture

*Last updated: 2026-09-07 · verified against `7a7fb28`*

Engine: **Godot 4.6.3**, C# (`Godot.NET.Sdk`). Project root `godot/`, solution `Mitosis.sln`.
Build with `cd godot && dotnet build`; keep it warning-clean.

## Shape of the program

```
SpeciesRegistry / SpeciesDefinition      data: what each species is
        │
EntityManager (SoA) + Systems (ISystem)  simulation: 20 TPS fixed timestep
        │
WorldManager / Chunk / TerrainGenerator  world: tiles, elevation, nutrition, mycelium
        │
RenderingManager                         view: reads simulation state, writes nothing
```

`GameManager` (a `Node3D`) owns all four and runs the loop. Deleting `RenderingManager` would not
change the simulation.

## Entity storage — `Scripts/ECS/EntityManager.cs`

Structure-of-Arrays rather than Godot's scene tree. One flat array per component type indexed by
entity id, plus a `ComponentFlags` bitmask per entity for filtering. Capacity is a fixed constant
on `EntityManager`.

- `Query(ComponentFlags)` / `EntityQuery` / `AllEntityQuery` — zero-allocation iteration.
- `DueThisTick[]` — the LOD gate. One boolean per entity; a gated system skips with a single array
  read (see [lod-and-performance.md](lod-and-performance.md)).
- `OnEntityDying` — fires for every entity immediately before destruction, from any cause.
  `CarrionSystem.SpawnCorpse` is registered on it, which is why a corpse is produced by starvation,
  age, drowning and faction weapons and not only by predation. The hook is generic; the handler
  self-filters structures, spores and Faelings, so the ECS core carries no gameplay knowledge.
- Per-class population counts live in `PopulationBudget`; `EntityManager` charges and releases them
  on the same create/destroy/component paths that maintain its own counts, so the budget has no
  second bookkeeping path to fall out of step with.

Components are `[StructLayout(LayoutKind.Sequential)]` structs in four files:

| File | Holds |
|---|---|
| `Components/CoreComponents.cs` | `Position`, `Velocity`, `ChunkPosition` |
| `Components/CreatureComponents.cs` | `Species`, `Hunger`, `Energy`, `Age`, `Reproduction`, `SimulationLOD`, `TerrainDiscomfort`, `Fear`, `VenomEffect`, `Carrion` + `SpeciesType`, `LODLevel`, `FearResponse` |
| `Components/BehaviorComponents.cs` | `Predator`, `Prey`, `Wander`, `Renderable`, `Social`, `Terraform` + `HuntingTactic`, `PackRole`, `PackPhase`, `ShapeType`, `SocialType`, `TerraformDirection` |
| `Components/FactionComponents.cs` | `Nest`, `FoodCarrier`, `Spore`, `Growth`, `Crystal`, `FaelingPower`, `RangedAttack`, `Structure`, `Siege` + `StructureKind` |

Note `Structure` carries its own `Health`/`MaxHealth` and does **not** use `Energy`. Energy is
creature stamina — regenerating, drained by starvation — and giving it to a building meant every
system touching `Energy` had to be taught to skip structures by flag. One authority makes the skip
structural instead of remembered.

## The tick — `Scripts/GameManager.cs`, `Scripts/Systems/SimulationStack.cs`

`GameManager` runs a fixed-timestep accumulator at `TargetTPS` (exported, default 20), decoupled
from the render frame rate. `SimulationStack.Build` appends every system to the list **in run
order**; that method is the single authority for execution order.

Order as built, with the reasons that are load-bearing:

```
 1  LODSystem                first — populates DueThisTick[] for everything below
 2  MovementSystem           not gated: integration must run every tick
 3  SpatialHashUpdateSystem
 4  TerrainDiscomfortSystem
 5  HungerSystem
 6  GrazingSystem
 7  WanderSystem
 8  HerdingSystem
 9  SeparationSystem
10  CollisionSystem
11  SiegeSystem              before hunting: it decides objective-vs-meal, hunting skips whom it committed
12  HuntingSystem
13  FleeingSystem
14  CarrionSystem            after hunt/flee so it can steer idle hungry predators; before NestSystem
                             so a chopping Sectid is fed and held at the corpse first
15  AgingSystem
16  ReproductionSystem
17  TerraformSystem
18  TileRegenerationSystem
19  NestSystem
20  SporeSystem
21  CrystalSystem
22  MyceliumSystem           last of the faction systems: reads the world the others just changed
    EcosystemLogger          added by GameManager; implements ISystem but only observes
```

`SimulationStack.Build` must be called **after** `SimRandom.SetSeed` — several systems take a
random stream at construction and the streams are handed out in construction order.

`FactionCensus` is constructed inside `Build` and shared by `SiegeSystem` and `CrystalSystem`; two
copies would let a keeper besiege one faction while relocating toward another.

## Coordinate spaces — `Scripts/Utils/GridCoordinates.cs`

Two spaces, one bridge.

- **Grid space** — float `(x, y)` tile coordinates. Canonical. All gameplay runs here.
- **World space** — 3D. `VertexToWorld3D` maps grid `(x, y)` + elevation to a position: grid Y →
  world **−Z**, elevation → world **+Y**. `WorldToGrid` inverts it for input.

`SmoothRowOffset` exists for the half-row stagger that made the grid read as hex on screen. See
open question W1 in [`../design/08-open-questions.md`](../design/08-open-questions.md): hydrology
traces six offset-row neighbours while the renderer no longer staggers, and the two have not been
reconciled.

`PlayerController` converts input by computing the exact world-space target and converting back,
rather than approximating the Jacobian — the approximation has discretisation error where the
offset slope flips at a row boundary.

## Randomness — `Scripts/Utils/SimRandom.cs`

`SimRandom.SetSeed` derives every simulation random stream from the world seed; `SimRandom.Create`
hands out a stream. This is what makes a run reproducible from a seed. **D1** is the standing
exception: species ids are `string.GetHashCode()`, which .NET randomises per process, so anything
whose behaviour depends on iteration order keyed by species id is not reproducible across
processes — see [species-data.md](species-data.md).

## Spatial queries — `Scripts/Utils/SpatialHash.cs`

Grid-bucketed neighbour queries, owned by `WorldManager`, shared by hunting, fleeing, herding,
separation, collision, siege, carrion, the faction systems and LOD. `WorldManager.PredatorHash` is a second
hash holding only entities that register as threats, so prey scans do not walk the whole
population.

Never write an O(n²) all-entity loop; use the hash.

## Body size — `Scripts/Utils/BodyMetrics.cs`

Radius derived from `Renderable.Size` and growth. Used by `CollisionSystem` to hold bodies apart
and by attack tests to measure reach surface-to-surface. These two must agree: testing raw centre
distance while collision holds bodies apart by the sum of their radii makes sufficiently bulky
targets literally unhittable.

## Adding to the simulation

**A component:** add the struct to the appropriate `Components/*.cs`, add a flag to
`EntityManager.ComponentFlags`, add the backing array, and expose configuration on
`SpeciesDefinition` if it is species-driven.

**A system:** implement `ISystem.Process(EntityManager em)`, register it in `SimulationStack.Build`
at the correct point in the order, gate it with `if (!em.DueThisTick[entity]) continue;` unless it
must run every tick, and use the spatial hash for proximity. If it produces a resource another
system consumes, keep both at the same gate level. If it multiplies anything by an LOD interval,
read the rate rule in [lod-and-performance.md](lod-and-performance.md) first — the gate and the
multiplier are one mechanism and adding either without the other is a known class of bug.
