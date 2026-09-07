# Movement & terrain response

*Last updated: 2026-09-07 · verified against `7a7fb28`*

## `MovementSystem` — `Systems/MovementSystem.cs` · **not LOD-gated**

Runs for every entity every tick. Integration is not a decision, and gating it made the world
advance at different speeds in different places (see
[lod-and-performance.md](lod-and-performance.md)).

Per tick, in order: clamp creature velocity to a maximum (the player is excluded); resolve tile
speed via `TerrainProfile.Speed`; apply uphill slope resistance; apply the hard cliff block for
non-flying creatures; attempt diagonal then axis-aligned sliding; clamp to world bounds; update
`ChunkPosition`; damp creature velocity and zero micro-drift.

Cost is kept down by two things rather than by skipping:

- the terrain speed multiplier (species lookup, `TerrainProfile` probe, slope elevation sample) is
  resolved on the entity's **due** tick and cached in `Velocity.CachedSpeedMult`;
- the cliff test samples elevation only when a step actually leaves the current tile. At normal
  creature speeds almost every step stays inside one, and cliffs are boundaries *between* tiles.

**Wrong element**: a creature on terrain its `TerrainProfile` reports impassable moves at a token
fraction of speed — it flounders in place and drowns or suffocates rather than chasing prey across
ground it cannot use.

**World edge**: a step that would cross the border has that velocity component reflected inward,
and `WorldManager.EdgeAversion` is maxed into every terrain steering sampler. Both halves are
required; see [world-generation.md](world-generation.md).

Flying creatures bypass slope and cliff checks. The player bypasses the clamp and the damping.

## `TerrainDiscomfortSystem` — `Systems/TerrainSystems.cs` · gated

Discomfort **settles toward a level** rather than accumulating without bound: each tile has an
equilibrium derived from the species' rate for it, capped relative to the species' threshold,
approached by a fraction of the remaining gap per tick and shed at the species' decay rate on
better ground.

Under pure accumulation, *any* net-positive rate reaches the ceiling eventually, so "mildly
disliked" and "lethal" differed only in how many ticks they took — which is the bug behind wolves
at absurd discomfort levels and sharks fleeing their own sea. Both also disabled hunting for the
duration.

The rate itself comes from `TerrainProfile.DiscomfortRate`, so aquatic species feel nothing in
water. Flying creatures ignore discomfort entirely.

**Grazing pressure**: a hungry herbivore on non-grazeable *or stripped* ground accrues extra
discomfort, scaled by hunger, at full strength once the tile is bare. A dead pasture is as useless
as bare rock. This is the soft terrain preference and is overridable by hunger and fear.

**Drowning and suffocation** are tracked here. Depth-aware: land creatures wade shallow water and
drown in deep, insects drown in any water, aquatic species suffocate on land, aquatic and
semi-aquatic species never drown. Damage begins after a per-species grace period. Both the grace
counter and the damage are LOD-compensated, without which a coarse-tier beached aquatic roamed the
land near-immortally. Health barely resists it — there is a damage floor, because you cannot
out-HP a lack of air.

Predators additionally stay in their element while hunting via `HuntingSystem.SteerForHabitat`, so
the drowning consequence only bites when a creature genuinely strays.

## `TerraformSystem` — `Systems/TerrainSystems.cs` · gated

Applies faction moisture nudges from the *moving creature*, for factions that terraform that way.
It **skips nest-breeders**: Sectid drying comes from the nest on each hatch instead (see
[factions.md](factions.md)).

`TerraformStrength` is a **probability per cooldown roll** — one random tile in radius receives a
moisture nudge on success — not an amount. Reading it as an amount is how faction terraforming was
once set so low it never marked the map.

A tile change re-derives the tile type from its parameters and marks the chunk dirty so the renderer
rebuilds it.

## `TileRegenerationSystem` — `Systems/TerrainSystems.cs`

Regrows tile nutrition. Throttled and batched across ticks, and fully-regenerated chunks are
skipped — this was a 16 ms per-tick bottleneck before both.

## Terrain in the steering systems

Three helpers in `WanderSystem` exist because naive steering has three specific failure modes:

- `SteerWithoutReversing` — plain `direction + avoidance × k` flips the heading whenever the ground
  ahead scores above the midpoint, which in uniform bad ground is every tick in every direction. A
  creature crossing water had its heading inverted on the spot each tick and oscillated a few tiles
  from where it started. Keeping the forward component and applying only the sideways part means
  avoidance routes *around* an obstacle, and when there is no way around, the answer is to keep
  going.
- `FindEscapeDirection` / `TryFindComfortableTarget` — escaping bad ground is a **roam to a
  specific tile**, not a nudge. Rays are walked outward and scored by how soon the ground improves,
  starting from a random compass point so genuine ties scatter instead of every animal in a lake
  picking the same direction from iteration order.
- `IsLethalSubstrate` / `TryFindSafeSubstrateTarget` — lethal ground overrides the roam cooldown.

An in-flight roam whose target is not comfortable ground is abandoned when escape begins; without
that, a creature that walked into a lake mid-roam kept swimming toward the target it had picked on
dry land.
