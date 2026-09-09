# Survival & population

*Last updated: 2026-09-09 · verified against `35c12ec`*

## `HungerSystem` — `Systems/SurvivalSystems.cs` · gated

Decays hunger by the LOD interval and a single global scale factor; drains energy while starving;
zero energy is death. Skips structures, nests and crystals — and nothing else. Regenerates energy when not
starving and off combat cooldown. Applies active venom damage-over-time. Reads
`FoodCarrier.IsHibernating` and drops metabolism sharply for dormant Sectids.

The global hunger scale is the primary lever for **predator carrying capacity**: predators die
almost entirely of starvation between kills and gain the most runway from it, while continuously
grazing herbivores sit near full regardless.

Faeling death passes part of its accumulated power to its crystal — implemented here and in
`AgingSystem`, both of which can be the cause of death.

**D10 — `SpeciesDefinition.ImmuneToStarvation` is dead.** It is set on exactly one species and read
by nothing. Faeling starvation immunity is actually achieved by setting its hunger decay and
starvation damage to zero. The property is a trap: it reads as the mechanism and is not.

## `GrazingSystem` — `Systems/SurvivalSystems.cs` · gated

Herbivores on grazeable tiles consume tile nutrition and gain food **scaled by what remained**, so
a stripped tile pays nothing. The consumed/requested ratio is guarded against a zero tick interval,
which would otherwise be `0/0`.

`FeedTiles` with a `FeedConsumeRate` behave exactly like grazing on water: fertility is stripped
and food paid in proportion. Without a consume rate the tile is an inexhaustible subsistence floor —
the right model for a species the water does not have to carry, wrong for one it does. Everything
downstream keys off the same question, so depleted water pushes a shoal out the way bare pasture
pushes a herd.

**Fungivory** — a species flagged `IsFungivore` also consumes the nearest Shroomer spore or
immature Shroomer within a radius, below a maximum growth scale that keeps it under the danger
band. This is grazing-adjacent, not hunting: no attack, no thorn damage, no rally. Consumption is a
discrete meal and is deliberately **not** LOD-scaled. Eaten immature Shroomers log as kills so
bloom control shows up in the events CSV; spores do not, being too numerous. Grown Shroomers are
immune — surviving to maturity is what earns their defences.

## `AgingSystem` — `Systems/SurvivalSystems.cs` · gated

Age increments by the LOD interval; death at the species' maximum lifespan. Skips structures.

## `CarrionSystem` — `Systems/CarrionSystem.cs`

Corpse creation is bound to the *event of dying*: `EntityManager.OnEntityDying` fires for every
entity from any cause and the registered handler calls `SpawnCorpse`, which self-filters
structures, spores and Faelings. That is why starvation, age, drowning and faction weapons all
leave bodies without any system knowing about corpses.

A corpse is an entity with a `Carrion` component and its own renderable shape, holding an edible
pool scaled by body size, by condition at death (hunger ratio, with a floor) and by growth scale.
The condition factor guards a non-finite hunger ratio so a `NaN` cannot propagate into corpse
nutrition. Pack sharing of the corpse is emergent. Note that the corpse pool is computed from the prey's own
nutrition and condition and is **not** reduced by what the killer already ate — see D8 in
[hunting-and-fleeing.md](hunting-and-fleeing.md).

Each tick the system:

1. **Decays** corpses — a fresh grace period, then nutrition rots away and the renderable shrinks;
   depleted corpses are removed. `IsDepleted` is written as `!(Nutrition > 0)` so a `NaN` corpse
   counts as depleted; a `NaN` corpse previously became an immortal scavenger magnet that stopped
   predators hunting entirely. Rotting also returns a fraction of each tick's decay to the tile's
   nutrition.
2. **Feeds scavengers** — a hungry predator, omnivore or Sectid with no live target moves to the
   nearest corpse and eats at a rate set by its maximum hunger. The seek radius **scales with
   hunger** for dedicated hunters, so a fed predator does not abandon a hunt for a distant carcass.
   Land and insect foragers will not path to a corpse across water. Sectids use the full radius
   always and weigh a corpse against live prey by distance alone, chopping into their carrier sacks;
   `NestSystem` holds one at the carcass until full and then ferries the load home, so a large kill
   takes repeated trips.

A short feed-commit keeps the killer on its kill instead of immediately re-hunting.

## `ReproductionSystem` — `Systems/ReproductionSystem.cs` · gated

Non-faction reproduction. Requires: under the hard cap, off cooldown, mature, hunger and energy
above the species' thresholds, **class budget not full**, a valid spawn tile, and a local-density
roll. Deducts hunger and energy, resets cooldown, and spawns offspring near the parent at reduced
starting hunger. Both the spawn loop and `EntityFactory.SpawnCreature` re-check the hard cap and
the budget, and offspring of a disabled species are refused here too.

**Local density is the primary brake** and is graded rather than a cliff: free below half the
limit, falling linearly to zero at it. The limit is derived from the species' preferred group size
and counted within a multiple of its social radius — per species and per place, which is what makes
it usable as the main throttle.

**It gates a birth, not a footstep.** The check runs in `ReproductionSystem` and refuses to spawn;
nothing anywhere refuses movement into ground that is already crowded. So a large number of animals
can stand in a small area despite the brake, and observing that is not evidence the brake is
failing — it is evidence of what the brake is.

**Breeding grounds**: a species with `BreedingTiles` may only breed while the **parent** stands on
one, and `WanderSystem.IsBreedingReady` gives a fed mature adult that is off them a roam target on
the nearest patch, overriding the roam cooldown the way lethal ground does.

**Guard order is part of the design.** Every pure array read and cheap dice roll runs above the
spatial density query, which is the most expensive guard and is therefore last. This was once
violated and the system spent an eighth of a tick building neighbour lists for creatures about to
be refused anyway.

## `PopulationBudget` — `ECS/PopulationBudget.cs`

Three hard per-class ceilings taken as shares of the maximum population — prey base, hunters,
factions — with the remainder left as headroom for spores, structures and transients. Shares are
exported on `GameManager`; `PopClass`, `Classify`, `ClassOf`, `BudgetFor`, `CountFor`, `CanSpawn`,
`Track`/`Untrack`, `LogRefusal`, `RefusalsFor` and `SplitSeed` are the API.

Classification is by `SpeciesDefinition`: faction first (a Sectid carries the predator flag and
would otherwise read as a hunter), then hunters, then the prey base. The omnivores are resolved
explicitly, because the class decides which ceiling a population competes for.

Why this replaced a single global ramp is a design matter — see contract §2 in
[`../design/07-simulation-contract.md`](../design/07-simulation-contract.md). Two implementation
consequences:

- **All five spawn paths consult it** — `EntityFactory.SpawnCreature`, `ReproductionSystem`,
  `NestSystem` (larva hatch and nest founding), `SporeSystem` (spore transform), `CrystalSystem`
  (Faeling respawn). One unbudgeted path recreates the ratchet the budgets exist to remove.
- **Refusals are counted and emitted**, throttled to one row per species per snapshot interval,
  with counts in the population CSV and on the debug overlay. Frequent refusals mean the budget is
  wrong or the ecology is not binding.

The counts live in `PopulationBudget`, and `EntityManager` charges and releases them on the same
create/destroy/component paths that maintain its own entity counts — so there is no second
bookkeeping path.

Expect the prey base to sit on its ceiling: at current regrowth against consumption, food does not
bind. That is design question C2, not a defect.

## Initial population — `WorldSpawner.cs`

`PopulationBudget.SplitSeed` divides the starting population between the classes in the same
proportions as their ceilings, so a world starts in the shape its budgets will let it hold. Within
a class, species are weighted by `SpawnWeight`. Creatures spawn in groups — an alpha at the centre
with higher leadership — herbivores wide, predators near prey. Sectids seed as nests plus starter
swarms; Shroomers seed blooms, and `SpawnMyceliumHearts` then places hearts subject to
`MinHeartSpacing`, so the faction has something that can be taken from it from the first tick.

**Faelings are not seeded by share.** A crystal holds exactly one Faeling, so crystal count *is*
the Faeling ceiling, and it is now a designed constant (`FaelingCrystalCount`) rather than a value
derived from sensing coverage. The derivation is what produced a hundred-plus immortal raiders; see
[factions.md](factions.md).
