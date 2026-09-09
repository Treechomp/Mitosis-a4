# Species data

*Last updated: 2026-09-09 · verified against `c41212b`*

## Files

| File | Role |
|---|---|
| `Species/SpeciesDefinition.cs` | the data class every behaviour is configured from, plus `DietType` |
| `Species/SpeciesRegistry.cs` | every species, constructed and registered; **the authority for all tuning** |
| `Species/SpeciesToggle.cs` | per-species enable/disable for balance runs |
| `Species/TerrainProfile.cs` | the one resolver for how a species relates to a tile |

## `SpeciesDefinition`

A plain data class with a large number of fields grouped by concern: identity and diet; movement;
hunting and ambush; fleeing and fear; survival; reproduction; social and pack roles; terrain
relationship; trophic mass and nutrition; grazing and feed tiles; terraform; faction
spore/nest/crystal parameters; area attack and growth; venom; visuals; and `StatVariation`.

Systems read these fields. **Systems must not name species.** When a behaviour appears to need a
species name, the fix is to find the property that species has, add it to `SpeciesDefinition`, and
give every other species a neutral default. The audit that established this pattern is archived at
[`../archive/behavior-constants-audit-2026-06.md`](../archive/behavior-constants-audit-2026-06.md).

Two data-vs-logic distinctions worth knowing before adding a field:

- **Engine or world constants stay global** — tick rates, world bounds, hash cell sizes. Only
  things that express a species' character belong on the definition.
- **A "global behaviour constant" that varies by species in practice is a definition field that
  has not been lifted yet.** That is the antipattern the audit names.

## `SpeciesRegistry`

Constructs every `SpeciesDefinition` and registers it. Lookup is by name hash — `GetId(name)`,
`GetById`, `Get(name)` and `GetAllNames`. The hash function is the subject of the next section and
is emphatically not `string.GetHashCode()`.

This file is the authority for every creature tuning value in the game. Nothing in `docs/` restates
its numbers; read it, or dump it by reflection.

### Species ids are a deterministic hash (was D1, fixed)

`SpeciesRegistry.GetId` is FNV-1a over the species name, and `Register` keys `_speciesById` with
the same function. It must not be `string.GetHashCode()`: .NET randomises that per process, so
every species id differed from run to run, and anything whose result depended on iteration order
over a structure keyed by species id took a different branch each time. A fixed seed did not
produce a fixed run.

Reproduced 2026-08-29: `FactionCensus.DominantFactionId` iterates a `Dictionary<int,int>` keyed by
those ids and breaks a tie by whichever species the dictionary yields first. That answer drives the
keeper's siege mandate, so a tie falling the other way sends every keeper at a different faction
and the run diverges from there. Every scenario that diverged across processes had two or more
factions; every single-faction and no-faction scenario was stable. At short tick counts the same
scenarios are byte-identical, so this is a tiny divergence amplifying chaotically rather than gross
randomness.

Measured 2026-09-08 (`PopulationSoak`, seed 1234, 3,000 ticks, one binary, 18 consecutive runs):
before the fix the run was a **coin flip between exactly two trajectories, 9 and 9**. After it,
the same batch gives **one trajectory** — 18 times in the prototype and 10 times again on the
committed build. The `LodDifferential` gate, which reported different counts on two consecutive
runs of one binary, now reports identical ones.

The divergence is invisible until a population class saturates. Before the herbivore budget fills,
the two trajectories are byte-identical; the first differing sample is the tick the cap is reached,
because from then on a birth refused to one species is a slot handed to another. It peaks around a
third of the Sectid count near mid-run and then contracts as the global ceiling binds. Amplifying
chaos, bounded by the caps — not gross randomness.

Fixing it at the source was the only honest option: an id-order tie is not something a caller can
guard against. It does mean figures recorded before the change carry no guarantee of matching
figures after it — though in practice the five-seed prime-cut sweep reproduced exactly, because the
deterministic ids happen to select the trajectory those runs had already landed on.

Ids are still negative about half the time, and `GetById` still reserves 0 for "uninitialised", so
`GetId` never returns it.

## `SpeciesToggle`

Configured from the `DisabledSpecies` and `DisableFactionSpecies` exports before spawning. A
disabled species never spawns: `WorldSpawner` filters it out, crystal and nest seeding are skipped,
and `ReproductionSystem` refuses it as a runtime safety net. Unknown names are validated against the
registry and logged as warnings; the active set is printed at startup. A disabled faction's spawn
budget folds back into the creature budget, so a no-faction run still starts at full population.

## `TerrainProfile`

The single resolver for the species↔tile relationship, consulted by movement, wander, fleeing,
hunting and siege. It replaced three divergent water-handling code paths, and its whole value is
that there is now one answer to each question.

| Method | Question | Semantics |
|---|---|---|
| `Speed` | how fast here | **REPLACE**: the species' own per-tile value overrides the tile's intrinsic grip; unlisted tiles fall back to `BaseSpeed`. Reef inherits the species' shallow-water speed when unlisted, then has clutter applied against body radius |
| `IsImpassable` | is this the wrong element | hard barrier — enforced as very strong steering aversion plus a token movement speed plus drowning/suffocation, so accidental shoring emerges without trapping entities |
| `SteerAversion` | would I rather be elsewhere | `[0..1]`, consulted on **every** tile including water, which is how preference *within* an element is expressed |
| `DiscomfortRate` | how long can I stand it | element-aware: wrong element maximal, home element free, everything else the tile baseline plus the species' comfort modifiers, floored at zero |
| `Concealment` | how well does this hide me | per-species override, else the tile's cover bonus; used by both hunting and fleeing |

**Aversion and discomfort are deliberately different.** Aversion is a pull; discomfort is an
intolerance. Habitat preference belongs in aversion. Routing it through discomfort is what stranded
sharks and fish — the tile table rates open water as punishing, negative comfort modifiers only
partly cancelled it, and the residue accumulated until both species were permanently "escaping" in
their own feeding grounds, with hunting disabled the whole time.

## `TerrainForageModifiers` — the per-species forage yield

`TerrainProfile.ForageYield` is the fifth dimension of the species↔tile resolver: the multiplier
between a tile's fertility and the food this species gets out of it, backed by
`SpeciesDefinition.TerrainForageModifiers`. An unlisted tile is worth face value, and every grazer
now sets a table: a specialist reaches face value only on its own ground and the roster's generalist
reaches it nowhere, which is what a generalist costs.

**It scales the payout, not the draw.** A grazer strips ground at `GrazeConsumeRate` wherever it
stands; poor forage means less hunger back for the same mouthful. So the yield *is* the exchange
rate between fertility and food, which is what decides how much ground a species needs — scaling
both sides would only make a species eat faster, not make the ground worth more.

`TerrainProfile.EffectiveNutrition` is the tile's fertility as this species values it, and it is
what every "is this worth eating" threshold now compares against:

| Site | Question | Reads |
|---|---|---|
| `SurvivalSystems` grazing | is there anything here, and what do I get | effective nutrition for the guard, yield on the payout |
| `SurvivalSystems` `FeedTiles` | the same, for a species that feeds off water | yield on `FeedNutrition` |
| `WanderSystem.HasFoodAt` | is this still worth standing on | effective nutrition vs `MinAcceptableNutrition` |
| `WanderSystem.GetFoodScore` | which way is better ground | effective nutrition as the score |
| `TerrainSystems` grazing pressure | has this ground finished | effective nutrition vs the depletion band |

The threshold sites read the effective value rather than raw fertility because
`MinAcceptableNutrition` is documented as the point where a species stops treating ground as *worth*
feeding on. The alternative — reading it as a pure depletion test — was built and measured, and it
left species standing on ground that does not feed them instead of moving to ground that does.

**The tables are load-bearing twice.** In the moment they decide what a mouthful is worth and which
way a hungry animal walks. Over a run they decide how the world's food flow is divided:
`HabitatCapacity` splits each tile type's supply among the species that feed on it in proportion to
their yields there, so a table is what turns a smaller pool into a *different* pool
([survival-and-population.md](survival-and-population.md)).

Measured on their own, before that second use existed, the tables only moved composition around and
could not settle it, because the shared class ceiling settled it instead. The figures for both
states are in [`../changelog.md`](../changelog.md).

## `BreakEvenFullness` — the one number that prices ground

The ground fullness at which a species just holds its condition while feeding. Below it an animal
loses hunger and must move or die; above it, it gains and can eventually breed. Lower is hardier.

It replaced `GrazeNutrition`, which is deleted. A payout authored independently of hunger drain
drifts away from it, and had: every grazer was paid enough per mouthful to stay full on ground at a
tenth of its cap, so being driven onto exhausted land cost nothing. What a full tile pays is now
`hunger drain ÷ BreakEvenFullness`, computed where it is used, so the two cannot separate again.

It is stated as fullness **while feeding**, not averaged over a run. An animal is only standing on
ground it can feed from part of the time, so the fullness it actually needs is higher than the
number — which is the same factor `HabitatCapacity.OccupancyFraction` carries, seen from the other
side.

`HabitatCapacity` reads it too: a species' demand is `consume rate × BreakEvenFullness`, the draw at
equilibrium. That is why raising the consume rate and lowering the break-even leaves the population
where it was and changes only how hard the ground is worn while an animal is on it.

## D5 — unused fields

`SpeciesDefinition.StatVariation` and `Species.Generation` are populated and never read. They are
reserved for per-individual trait variation and inheritance (design question C3). Harmless, but they
make the definition look like it supports something it does not.
