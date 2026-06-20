# Terrain Handling Audit

A reference map of every mechanism that governs how a species relates to the terrain
it stands on, and how those mechanisms currently resolve. Written to ground the design
discussion on unifying terrain handling. Snapshot as of the aquatic-speed overhaul
(water-only `TerrainSpeedModifiers` wiring).

## Two halves: tile-intrinsic vs per-species

Terrain behaviour is driven by **tile-intrinsic** properties (identical for all species)
and **per-species** knobs. Each system combines them differently — or reads only one
side — which is the source of the inconsistency.

### Inventory (who reads what)

| Knob | Side | Read by | Status |
|---|---|---|---|
| `GetSpeedMultiplier` | tile | MovementSystem | live (all species) |
| `TerrainSpeedModifiers` / `GetTerrainSpeedModifier` | species | MovementSystem | **half-live — water tiles only** (land entries inert) |
| `GetAvoidanceWeight` | tile | WanderSystem, HuntingSystem, FleeingSystem | live |
| `TerrainComfortModifiers` / `GetTerrainComfortModifier` | species | TerrainDiscomfortSystem, HuntingSystem | live |
| `GetDiscomfortRate` | tile | TerrainDiscomfortSystem | live |
| `DiscomfortThreshold` / `DiscomfortDecayRate` | species | (component init) → WanderSystem escape | live |
| `GrazingPressure` | species | TerrainDiscomfortSystem | live |
| `AvoidsOpenWater` (derived) | species | HuntingSystem, CarrionSystem | live |
| `AvoidsWater` | species | HuntingSystem, TerrainDiscomfortSystem (drown), CarrionSystem | live |
| `IsAquatic` | species | MovementSystem, WanderSystem, TerrainDiscomfortSystem, spawn | live |
| `SemiAquatic` | species | WanderSystem, TerrainDiscomfortSystem | live |
| `IsFlying` | species | MovementSystem, TerrainDiscomfortSystem | live |
| `AllowedSpawnTiles` / `PreferredBiomes` / `CanSpawnOnTile` | species | ReproductionSystem, WorldSpawner | live |
| `FeedTiles` / `FeedNutrition` | species | GrazingSystem, WanderSystem | live |
| `WaterStealthBonus` | species | HuntingSystem (ambush stealth) | live |
| `GetCoverBonus` | tile | **nobody** | **DEAD** |
| `GetRuggedness` | tile | TerrainGenerator (visual noise only) | gen-only |

## How each dimension resolves today

### 1. Movement speed — MovementSystem, multiplicative
```
speedMult = tile.GetSpeedMultiplier()                       // intrinsic, all species
          × (IsFlying        ? force 1.0 : …)
          × (IsAquatic && !water ? 0.05 : 1)                // beached penalty
          × (water           ? species.GetTerrainSpeedModifier(tile) : 1)  // WATER ONLY
          × slopeResistance(elevationRise)
moveDelta = velocity × speedMult                            // velocity already carries
                                                            // BaseHuntSpeed / WanderSpeed / flee mult
```
Velocity is clamped to an absolute 0.25 before `speedMult`.

- **Two authorities** for "speed on this tile": tile-intrinsic `GetSpeedMultiplier` and
  per-species `TerrainSpeedModifiers`. They multiply, so a species "fast in jungle (1.3)"
  still pays jungle's 0.6 intrinsic base (net 0.78).
- The per-species half is **only wired on water tiles**. The authored land affinities
  (Monkey jungle 1.3, Lizard sand 1.3, Crocodile-slow-on-land 0.3, etc.) are still inert.

### 2. Avoidance / pathing — THREE systems, inconsistent species-awareness
This is the most fragmented dimension. "Where will a creature refuse to go" is answered
three different ways:

- **WanderSystem** — species-aware via `Weight(tile, isAquatic, semiAquatic)`:
  aquatic invert (avoid land), semi-aquatic treat water as home, land avoid water.
  Drives proactive wander steering + discomfort escape direction.
- **HuntingSystem** — species-aware a *different* way: `AvoidsOpenWater` gate +
  `GetWaterFractionOnPath` (won't path across water to prey) + `SteerAroundWater` +
  per-prey target penalty (`GetAvoidanceWeight × 50` + `GetTerrainComfortModifier × 20`).
- **FleeingSystem** — **NOT species-aware**: uses raw `tile.GetAvoidanceWeight()` for
  everyone. A fleeing penguin or fish steers *away* from water like a land animal.
  → Latent bug for every aquatic/semi-aquatic species; undercuts "penguins flee to sea".
- **CarrionSystem** — `AvoidsOpenWater` + `GetWaterFractionOnPath` (mirrors Hunting).

### 3. Comfort / habitat dislike — overlaps with avoidance
- `TerrainComfortModifiers` (per-species) feeds **both**:
  - TerrainDiscomfortSystem: added to `tile.GetDiscomfortRate()`; negative = comfortable
    (decays discomfort), positive = accumulates. Past `DiscomfortThreshold` the creature
    enters WanderSystem escape.
  - HuntingSystem target scoring: only the positive (uncomfortable) side, to avoid
    hunting prey standing on hostile tiles.
- This is a **parallel** "dislikes this terrain" concept to `GetAvoidanceWeight`. Two
  systems expressing nearly the same idea (soft dislike vs hard route-around).

### 4. Spawn / habitat — comparatively clean
`CanSpawnOnTile` = `AllowedSpawnTiles` → else aquatic⇒water → else `IsSpawnable`.
`CanSpawnInBiome` = `PreferredBiomes`. Read by ReproductionSystem + WorldSpawner.

### 5. Survival / element — depth-aware, clean
TerrainDiscomfortSystem drowning/suffocation:
- aquatic/semi-aquatic: never drown; aquatic suffocate out of water.
- `AvoidsWater` (insects): drown in any water.
- ordinary land: drown only in deep water (wade shallow/river safely).
Scaled by `WrongElementGraceTicks` / `WrongElementDamageRate`.

## Redundancy / multiple-authority summary
- **"How fast on this tile"**: `tile.GetSpeedMultiplier` + `species.TerrainSpeedModifiers` (2).
- **"How much avoid this tile"**: `tile.GetAvoidanceWeight` + `species.TerrainComfortModifiers`
  + `AvoidsOpenWater/AvoidsWater` flags — resolved inconsistently across 3 systems.
- **"Comfort/discomfort"**: `tile.GetDiscomfortRate` + `species.TerrainComfortModifiers`.

## Dead / partial config
- `GetCoverBonus` — fully dead (no consumer). Intended for ambush/stealth/flee concealment;
  ambush currently uses only `WaterStealthBonus`.
- Land half of `TerrainSpeedModifiers` — inert (water-only wiring).
- `GetRuggedness` — terrain-generation visual only (fine, not behavioural).

## Open design questions
1. **One terrain profile, or many knobs?** Replace the scattered speed/avoid/comfort/spawn
   knobs with a single per-species, per-tile resolved profile that every movement/AI system
   reads?
2. **Multiplier vs override.** Are per-species modifiers relative to the tile-intrinsic value
   (current: multiply) or should strong affinity override the intrinsic base?
3. **Unify avoidance + make it species-aware everywhere.** Fix FleeingSystem; collapse the
   three pathing code paths into one shared "terrain passability/cost" function.
4. **Land-speed wiring.** Finish enabling `TerrainSpeedModifiers` on land with a balance pass,
   or keep water-only.
5. **Comfort vs avoidance.** Merge the two "dislikes terrain" systems, or keep as soft
   (discomfort) vs hard (route-around)?
6. **`GetCoverBonus`** — wire into ambush/stealth/flee, or delete.
