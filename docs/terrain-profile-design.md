# Unified Species Terrain Profile — Design Spec

Target design for consolidating terrain handling. Decisions captured from the
2026-06-23 discussion; see `terrain-handling-audit.md` for the pre-redesign state and the
failure observations motivating it.

**Status: IMPLEMENTED (2026-06-23).** `TerrainProfile` (Scripts/Species/TerrainProfile.cs)
is the single resolver; MovementSystem/WanderSystem/FleeingSystem/HuntingSystem consult it.

## What was built vs. this spec
- **Speed = REPLACE**, all tiles — done (MovementSystem via `TerrainProfile.Speed`). The
  previously-inert land speed modifiers are now LIVE; swimmer water values were retuned to
  absolute tile speeds.
- **Concealment** — done, and wired BOTH detection directions (prey harder to target in
  cover; concealed predators harder for prey to notice). `GetCoverBonus` is now the per-tile
  floor (no longer dead). 12 species got camouflage entries.
- **Species-aware avoidance** — done for Movement/Wander/Fleeing (fixes fish foraging/
  wandering/fleeing onto land). HuntingSystem/CarrionSystem keep their existing water-path
  rejection (`AvoidsOpenWater` + `GetWaterFractionOnPath`), which already works; not yet
  folded into the resolver.
- **Comfort vs hard-avoid — DIVERGENCE FROM THE "single-axis" musing.** Implemented as the
  *separation* model (the user's first statement), not one collapsed axis:
  * **Hard avoid** = element membership via `TerrainProfile.IsImpassable` (aquatic↔land,
    insect↔water). Concise and exact — avoids enumerating every land tile per fish.
  * **Soft comfort** = `TerrainComfortModifiers` + tile `GetDiscomfortRate` in
    TerrainDiscomfortSystem (the hunger/fear-overridable "leave uncomfortable ground" urge).
  Note: hard avoidance is enforced as a very strong *steering* aversion (1.0) + the 0.05
  beach/flop speed penalty + drowning/suffocation, NOT a hard movement wall — so "only
  accidental shoring/drowning" is achieved by strong avoidance, not an impassable barrier
  (keeps entities from getting trapped).

## Goal
Replace the scattered, inconsistently-applied terrain knobs (tile-intrinsic +
per-species speed/comfort/avoidance/cover, resolved three different ways across
Movement/Wander/Hunting/Fleeing/Carrion) with **one per-species, per-tile profile**
that every system reads through a single resolver.

## The profile (per species, per tile)
A species defines a sparse table of overrides; everything unspecified falls back to a
tile-intrinsic default. Resolved values:

| Field | Meaning | Resolution rule |
|---|---|---|
| **Speed** | movement multiplier on this tile | **REPLACE**: species value if defined, else `tile.GetSpeedMultiplier()` (tile base kept for grip differences: sand/snow/grass). NOT multiplied. |
| **Comfort** | affinity / dislike, single continuous axis | species value if defined, else tile default. Drives both soft-avoid and hard-avoid (see below). |
| **Concealment** | how hidden the species is here (cover) | species value if defined, else `tile.GetCoverBonus()` as a generic floor. Reduces detectability by predators. |

Spawn eligibility (`AllowedSpawnTiles` / `PreferredBiomes`) and the pure-element flags
(`IsAquatic` / `SemiAquatic` / `AvoidsWater` / `IsFlying`) stay as-is for now; drowning /
suffocation continues to derive from them. (Open: fold the "hard-avoid" comfort extreme
and these element flags together so there's one source of truth for "lethal element".)

## Comfort axis — one field, soft and hard regions
Decision: keep **both** soft-comfort and hard-avoidance behaviour, but (proposed) express
them on a single comfort scale with a threshold, so the extreme is structurally
un-overridable instead of a second parallel field.

```
comfort >= 0            : at home. No avoidance; discomfort decays.
SOFT_AVOID < comfort < 0: disliked. Avoided while wandering/grazing, BUT fear or deep
                          hunger can override and push the creature in.
                          (Rabbit won't cross a river to graze, but will to escape a
                          predator or when its grass is fallow.)
comfort <= HARD_AVOID   : barrier. Never voluntarily entered even when fleeing/starving.
                          Pathing treats it as blocked; only accidental entry (knockback,
                          pathing slip) puts a creature here → drowning/suffocation.
                          (Sectid↔water, Fish/Shark↔land.)
```
The override logic (fear/hunger raising tolerance) applies only in the SOFT band; below
`HARD_AVOID` no drive can suppress it. This is what fixes:
- fish/sharks fleeing or chasing onto land (hard-avoid land),
- Sectids swimming to prey/carcasses (hard-avoid water),
- while still letting pressed rabbits cross rivers (soft band).

## One shared resolver, used everywhere
A single function (e.g. `SpeciesTerrainProfile.Resolve(speciesId, tile)`) returns the
three values. Then:
- **MovementSystem**: speed = resolved Speed × slope resistance (element beach penalty
  becomes just "land is hard-avoid for aquatics", unified).
- **Wander / Hunting / Fleeing / Carrion**: all use the *same* comfort-based avoidance —
  soft band steers around (modulated by fear/hunger urgency), hard band is impassable.
  Eliminates the three divergent water-avoidance code paths and the
  FleeingSystem species-unaware bug.
- **Detection (Hunting target acquisition + Fleeing predator detection)**: effective
  detection range/score scaled down by the prey's resolved Concealment. (Rabbit in forest,
  Scorpion in desert, Arctic Fox in snow are harder to spot → better ambush, better escape.)

## Not in this profile (tracked separately)
- **Terrain-aware food/habitat seeking.** A separate but related gap: nothing pulls a
  specialist toward its food/biome across terrain (penguins → coast, sharks → fish-rich
  water). Wander is random; hunting only reacts to in-range prey. Needs a "seek comfortable
  habitat / known food terrain" drive. Listed here because the redesign should make it cheap
  (the comfort field already encodes where a species wants to be — gradient-follow it when
  hungry/uncomfortable).

## Migration notes
- `TerrainSpeedModifiers` → profile Speed (semantics change multiply→replace; existing
  values need re-reading under the new rule).
- `TerrainComfortModifiers` → profile Comfort (re-scale to the soft/hard band thresholds).
- `GetCoverBonus` (tile) → generic Concealment floor; add per-species concealment entries.
- `GetAvoidanceWeight` (tile), `AvoidsOpenWater` (derived) → subsumed by the comfort axis
  (hard band) + element flags; likely removable after migration.
- Currently dead: land half of `TerrainSpeedModifiers`, `GetCoverBonus` — both become live.
