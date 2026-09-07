# Species data

*Last updated: 2026-09-07 · verified against `7a7fb28`*

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

Constructs every `SpeciesDefinition` and registers it. Lookup is by name hash:
`GetId(name) => name.GetHashCode()`, with `GetById`, `GetByName` and `GetAllNames`.

This file is the authority for every creature tuning value in the game. Nothing in `docs/` restates
its numbers; read it, or dump it by reflection.

### D1 — species ids are not stable across processes

`string.GetHashCode()` is randomised per process in .NET, so every species id differs from run to
run. Anything whose result depends on iteration order over a structure keyed by species id is
therefore not reproducible across processes even with a fixed seed.

Reproduced 2026-08-29: `FactionCensus.DominantFactionId` iterates a `Dictionary<int,int>` keyed by
those ids and breaks a tie by whichever species the dictionary yields first. That answer drives the
keeper's siege mandate, so a tie falling the other way sends every keeper at a different faction
and the run diverges from there. Every scenario that diverged across processes had two or more
factions; every single-faction and no-faction scenario was stable. At short tick counts the same
scenarios are byte-identical, so this is a tiny divergence amplifying chaotically rather than gross
randomness.

The mechanism fits all the evidence and has not been formally proven to be the only one. A stable
id scheme (an explicit id, or a deterministic hash) fixes it at the source.

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

## D5 — unused fields

`SpeciesDefinition.StatVariation` and `Species.Generation` are populated and never read. They are
reserved for per-individual trait variation and inheritance (design question C3). Harmless, but they
make the definition look like it supports something it does not.
