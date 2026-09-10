# Faction systems

*Last updated: 2026-09-10 · verified against `f3ea31c`*

| File | Role |
|---|---|
| `Systems/SporeSystem.cs` | Shroomer growth, spread, area attack, self-limiting |
| `Systems/NestSystem.cs` | Sectid nest economy, larvae, food carrying, hibernation, nest terraforming |
| `Systems/CrystalSystem.cs` | Faeling crystals, respawn, power, ranged attack, keeper role |
| `Systems/MyceliumSystem.cs` | Shroomer territory field and hearts |
| `Systems/SiegeSystem.cs` | objective targeting, structure damage, destruction consequences (`Structures`) |
| `ECS/FactionCensus.cs` | global per-faction totals and a coarse per-chunk presence grid |
| `ECS/FactionLives.cs` | the player's remaining continuations |

## `SporeSystem` — Shroomer

**Feeding — in `GrazingSystem` (`Systems/SurvivalSystems.cs`), not here.** Growth fuel is tile
nutrition, consumed on grazeable tiles at a rate several times a
grazer's, paying out scaled by richness. Combined with wetter terraforming this converts fertile
ground into barren swamp and forces the bloom to advance. Wet-tile feeding is only a **subsistence
floor** roughly equal to hunger decay: a Shroomer survives on its own swamp but stays too hungry to
keep spreading there. Setting that floor high enough to prevent starvation removed all fertility
reliance and was the root of the historical monoculture.

**Spread.** Mature, well-fed Shroomers on wet tiles occasionally emit spores at a hunger cost; the
probability is LOD-compensated as `1 − (1−p)^interval`, not multiplied. Spores accrue moisture on
wet ground and wither on dry, transforming at a threshold.

**Growth** accumulates linearly toward a maximum scale. What the smoothstep shapes is the *effects*
of that scale — `SpeciesDefinition.GetGrowthScalingFactor` is what area reach, area damage and the
thorn factor read:

- **Area attack** — radius and damage grow from near-harmless to devastating; a slow passive pulse
  plus a faster combat pulse when attacked. Targets rival factions.
- **Thorn defence** — melee attackers take counter-damage scaled by the **attacker's** body mass
  (`ThornDamageBase`, `ThornMassReference`, square-root of the mass ratio, clamped). Flat thorns
  punish exactly the wrong attacker: damage-for-damage they cost a swarm most, since many small
  mouths pay the full toll on every one of their many bites, while a heavy predator paid the same
  toll for a far larger blow.

**Self-limiting.** Lifespan is longer than a run, so age never checks a Shroomer. Each due
Shroomer counts its same-species neighbours once, and that count drives three effects:

- **Crowding attrition** above a neighbour limit, LOD-compensated, so a dense mat self-thins into
  advancing fronts.
- **Drought death** for a mature Shroomer on ground drier than its moisture threshold, which also
  blocks spreading. This is what makes rival drying an actual weapon.
- **Spread suppression** as local saturation climbs, plus a global population-pressure factor as a
  safety ceiling.

Both attrition deaths log as `environment_death` with a detail distinguishing drought from
crowding.

## `NestSystem` — Sectid

Sectids cannot graze; the whole faction is one conversion of prey into brood.

A killer chops into its `FoodCarrier` sack and carries the load to the nearest nest at a carrying
speed, self-feeding on delivery. Chopping at a carcass (`CarrionSystem`) both fills the carrier and
feeds the chopper.

**D9 — the documented "field feeding" does not exist.** Previous documentation described packmates
being fed directly in the field on a kill, hunger only and no carrier fill. No system feeds a
groupmate: every hunger-increasing write in `Systems/` feeds the entity it is iterating. Either the
behaviour was removed and the documentation was not, or it was never implemented as described.
Whether the swarm needs it is a balance question, not a documentation one.

A nest accumulates food toward a larva timer; hatching produces a Sectid. Nests have staged growth,
each stage adding a larva slot; at maximum stage with surplus a nest founds nearby nests or a
distant colony. `MaxCarryFood` is set below the food-per-larva figure deliberately, so a large kill
is worth repeated trips rather than being wasted at the carry cap.

**Nest-based terraforming.** Sectids do not reshape terrain while roaming — they move too fast to
leave an imprint. `TerraformSystem` skips nest-breeders entirely; instead the *stationary nest*
applies a concentrated burst of drying nudges around itself on each successful **hatch**, so the
colony's influence accumulates in one place and is tied to its success. Abandoned nests do not
terraform.

**Hibernation.** A hungry Sectid that detects no huntable prey within a radius for a sustained
period retreats to its nest and goes dormant: `HungerSystem` drops its metabolism sharply,
`WanderSystem` and `HuntingSystem` skip it, and `FleeingSystem` excludes it from threat scans. It
wakes when huntable prey strays close or when it picks up food. A bite also wakes it.

## `CrystalSystem` — Faeling

Crystals are `Structure` entities with finite health, each linked to exactly one Faeling. When its
Faeling dies the crystal waits a delay and spawns a replacement carrying inherited power. Faelings
gain power from kills and from balanced terraforming, which boosts growth and ranged damage; part
of the power passes to the crystal on death, so a lineage compounds. They are immune to starvation,
terrain discomfort and predation, and attack rivals at range.

**Keeper role (anti-dominance).** On an interval, a Faeling asks `FactionCensus` which rival
faction is over-dominant, subject to a minimum presence and a lead margin. While a reading is
active:

- ranged attacks prefer the dominant faction's members;
- **elder safety** — a keeper never duels a Shroomer whose growth-scaled area reach rivals its own
  attack range; grown Shroomers are left to the siege and to drying;
- **siege patrol** — the keeper roams to a standoff ring outside a full-grown Shroomer's reach
  around the dominant faction's sensed hotspot and holds station, restoring the substrate the
  winner depends on. Implemented in `WanderSystem` (`KeeperSiegeStandoff`), not here — only elder
  safety and attack preference live in `CrystalSystem`.

With no reading, keepers fall back to patrolling toward damaged terrain and restoring it.

**Crystal count is a designed constant** (`FaelingCrystalCount`), not derived. It was previously
computed from a target sensing coverage, which produced over a hundred crystals and therefore over
a hundred immortal raiders — perception bought with presence, and presence is force. `FactionCensus`
now supplies perception separately. Crystal-to-crystal keeper travel was implemented and then
deleted: keepers that relocate assemble, and assembled keepers sweep.

## `MyceliumSystem` — Shroomer territory

Mycelium is a per-tile float on `Chunk`, modelled directly on the nutrition field including its
skip flag. It thickens under living Shroomers scaled by growth and tapering with distance
(`GrowTerritory`) and thins on its own. Entities per colonised tile would have been tens of
thousands of entities carrying no behaviour.

A **heart** is a `Structure` that survives while, within its radius, both the fraction of tiles
above the fungal moisture threshold exceeds a floor **and** living Shroomers exceed a floor
Fail either and it drains; hold both and it recovers.

Two call paths, easily confused: `UpdateHearts` runs the survival and drain test using
`MoistTileFraction` and `CountFactionCreatures`; `TerritorySupportsHeart` is the separate founding
and worldgen-seeding predicate and uses `MoistFraction` and `CountFaction`. That gives rivals two different attacks on one target: dry the ground
from outside and never trade a blow, or go in and kill the bodies.

The survival test reads **moisture**, not the mycelium field, on purpose: moisture is what
terraform attacks, so the drying route acts on the heart directly rather than through a derived
quantity. The field is the visible territory and the founding requirement.

A mature Shroomer on sufficiently claimed ground founds a heart (`FoundHearts`, `SpawnHeart`), at
most one per `MinHeartSpacing` (`HeartWithinSpacing`), and `WorldSpawner.SpawnMyceliumHearts` seeds
them at generation under the same spacing rule. A bloom that loses its heart founds another once it
has regrown.

## `SiegeSystem` and `Structures`

**Why it exists.** Structures used to be unattackable by construction: prey eligibility requires a
`Prey` flag that a nest did not carry, and a crystal's health was a sentinel. Nothing in the game
could damage a nest or a crystal, so the only thing factions could contest was individual
creatures — which makes faction strength a function of population, the one contest an elite faction
can never win.

**`Structure` component** carries `Health`, `MaxHealth`, `FactionSpeciesId`, `Kind`,
`UnderAttackTicks` and `LastAttacker`. Not `Energy` — see [architecture.md](architecture.md).

**Objective targeting is a separate path from prey targeting.** `SiegeSystem` runs *before*
`HuntingSystem` and a creature it commits is skipped by hunting for that tick — one guard, the
entire coupling. Structures deliberately do not satisfy prey eligibility: a building has no body
mass to gate against, no nutrition payoff to score, never flees and must not drop a carcass, so
routing it through the prey path would mean an "is this actually a building" guard at roughly
fifteen points inside a very large loop.

**Weighting is data.** `StructureAggression` is a *distance ratio*: a structure is preferred while
`dist(structure) ≤ dist(prey) × aggression`. That lets the siege path weigh a building against
whatever the creature can see without reimplementing prey scoring.

**Sieging with nothing to hunt is opt-in.** The ratio measures a structure against the meal being
passed up; with no meal there is nothing to measure, and any stand-in distance silently becomes the
real siege rule. `StructureIdleSeekRadius` defaults to zero, meaning "do not divert at all"; the
Faeling opts in with a wide radius, since it does not eat and is therefore always idle by this test.

**The approach is steering, not assignment.** `SiegeSystem` blends velocity through
`DecisionCadence.BlendVelocity` at the species' mass-derived turn rate, LOD-compensated, exactly as
the hunt path does. Writing velocity outright turned a besieger instantly, ignored its mass, was
not LOD-compensated, and discarded wander, herding and habitat steering for that tick.

**`StructureDefenseRadius` is the call to arms**: a struck structure points its faction's nearby
creatures at the **attacking creature**, never at the building, by setting the same `LastAttacker`
pair a bitten creature gets — so defence runs through the existing rally rather than a second
mechanism.

**Destruction consequences** live in `Structures.Damage`, shared so every attacker produces the
same outcome: `structure_damaged` → `structure_destroyed`, plus `nest_destroyed` with the brood
lost in its larva slots, `colony_destroyed` when it was the colony's last nest, `crystal_destroyed`
which unlinks its Faeling (it fights on but never respawns), and `heart_destroyed`.

## `FactionCensus`

One pass over all entities on a slow cadence, producing global totals and a coarse per-chunk
presence grid, keyed by **species id** rather than by a hand-written faction list — so a new faction
is counted, ranked and targeted without any system naming it. Bodies only: spores, structures,
nests and crystals are excluded.

It exists to separate perception from presence. Knowing where the problem is costs one entity pass;
getting there is a mobility question, and neither needs more bodies.

Shared by `SiegeSystem` and `CrystalSystem` — see [architecture.md](architecture.md).

**D1 affects this file.** `DominantFactionId` iterates a dictionary keyed by species id and breaks
ties by iteration order, which is process-dependent. See [species-data.md](species-data.md).

**D6**: `Crystal.FAELING_AGGREGATED` is a dead sentinel from the removed statistical simulation.

## `FactionLives`

`Total`, `Remaining`, `Spent`, `CanRespawn`, `Consume`. Created with the run from
`FactionLivesPerRun`.

Deliberately **not** derived from the number of surviving anchors: anchor counts are a worldgen
consequence and get retuned, and a coverage fix must not silently be a difficulty change. The fail
state has two independent conditions — out of continuations, or no structure left to anchor one —
which is the other reason they cannot be the same number.

**Nothing consumes it yet.** Player control, input and the death flow are a later change; the
counter exists so that flow has a home to spend from.

## Descent, per faction

Each faction assembles its own creatures rather than going through `EntityFactory`, so each applies
its own inheritance — one rule per faction's allocation verb, not three mechanisms
([`../design/03-creatures.md`](../design/03-creatures.md)).

| System | Holds the template | Applies it |
|---|---|---|
| `SporeSystem` | `_sporeLineage`, keyed by spore entity, because a spore carries no components worth inheriting | `SpawnShroomer`, on transform |
| `NestSystem` | `_nestBrood`, keyed by nest entity: one `Genome.Blend` per delivery at the courier's load against the larder it joined | `SpawnSectid`, on hatch |
| `CrystalSystem` | `_crystalMemory`, refreshed from the living keeper each tick | `SpawnFaeling`, on respawn |

Three notes on why each is shaped that way. The nest needs no contributor list: one weighted blend
per delivery is O(1), and because the weight is the larder itself, influence is consumed as larvae
are grown. The crystal reads its keeper while it lives rather than recording it at death, because a
Faeling can die in four different systems and a memory written at every one is a memory that will be
missed at the fifth. And each store is keyed by entity id, so `SpawnNest` clears its entry on
creation — otherwise a reused id would inherit the brood of the nest that held it before.

Faeling fidelity is a flat copy. Making it purchasable is a crystal-economy decision, recorded in
[`../design/04-factions.md`](../design/04-factions.md) and deliberately not built.

## D4 — keepers have never damaged a structure

Measured 2026-08-29 across four 20,000-tick runs (seeds 1234 and 999, with and without keeper
travel): **zero** `structure_damaged` and zero `structure_destroyed` events attributed to a Faeling,
in 80,000 keeper-ticks. Every structure lost in those runs died to Sectids or to the environment.

Ruled out: `FactionCensus.Refresh` does run on its interval, it counts only terraformer species, and
`DominantFactionId` returns a real answer on those populations. The leading hypothesis was that
`StructureTargetsDominantOnly` restricts a keeper to the dominant faction's structures while the
dominant faction — Shroomer — had none for most of a run, because hearts were not seeded at
generation. **Heart seeding has since changed**, so that hypothesis needs re-testing before it is
reasoned about further.

Diagnosing it properly needs the treatment the Sectid hunt funnel got: instrument
`SiegeSystem.Acquire` per keeper and find which gate returns nothing.
