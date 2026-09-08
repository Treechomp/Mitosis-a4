# Implementation layer

*Last updated: 2026-09-08 · verified against `186a0fd` on `claude/lod-override-testing-rhwq4f`*

What the code does today and where it lives. Secondary to [`../design/`](../design/): if these two
disagree about intent, design is right and the code has drifted; if they disagree about behaviour,
the code is right and this folder has drifted.

## The rule that keeps this folder true

**Name the authority; do not copy it.**

Every tuning value in the project lives in exactly one place — a field on `SpeciesDefinition`, an
`[Export]` on `GameManager`, a constant in the system that uses it. These documents tell you *which*
field holds a thing. They do not restate the value.

```
✅  "Uphill resistance and the hard cliff block are in MovementSystem; the block's threshold is
     a constant there."
❌  "Movement blocks an elevation difference greater than 0.28."
```

The second sentence is faster to read and is the exact mechanism that put 31 contradictions into
the previous documentation set. A field name breaks loudly when it is renamed; a copied number goes
wrong in silence.

Counts are the same category. There is no line here saying how many species or systems exist —
`SpeciesRegistry` and `SimulationStack.Build` are the answer, and they are one `grep` away.

## Contents

| Document | Covers |
|---|---|
| [architecture.md](architecture.md) | ECS, the tick, system order, coordinate spaces, how to add things |
| [world-generation.md](world-generation.md) | terrain, hydrology, climate coupling, chunks, tiles |
| [species-data.md](species-data.md) | how a species is defined, registered, toggled, and related to terrain |
| [movement-and-terrain.md](movement-and-terrain.md) | movement, the terrain profile, discomfort, terraform, regeneration |
| [survival-and-population.md](survival-and-population.md) | hunger, grazing, ageing, carrion, reproduction, population budgets |
| [hunting-and-fleeing.md](hunting-and-fleeing.md) | hunting tactics, target selection, fleeing, herding, spatial systems |
| [factions.md](factions.md) | spores, nests, crystals, structures, siege, mycelium, census, lives |
| [lod-and-performance.md](lod-and-performance.md) | LOD tiers, decision cadence, the rate rule, profiling |
| [rendering-and-input.md](rendering-and-input.md) | 3D rendering, camera, player input, observation tools, overlay |
| [tooling-and-tests.md](tooling-and-tests.md) | test scenes, scenario runner, gates, logging, worldgen preview |

Change history — what was done, when, in which commit, and what it measured — is in
[`../changelog.md`](../changelog.md), not here.

## Divergences from design

Places where the code does not do what [`../design/`](../design/) describes. These are **not**
defects: the code is not broken, it has not been built yet or was built to an earlier intent. This
table is what the design layer is *for* — it is the backlog that falls out of having a target
picture to compare against, and it is the first thing to re-read when planning work.

Figures here are stamped like changelog entries, because a gap measured against one build is
evidence about that build.

| # | Divergence | Design says | Code does (at `0b6c012`) |
|---|---|---|---|
| V1 | **Pacing.** Every rate is set from balance-run length rather than play length | median run resolves in 60–120 min ([`design/06-run-and-progression.md`](../design/06-run-and-progression.md)) | rates are tuned around a ~20,000-tick (~17 min) horizon; faction-scale events resolve in the first few percent of a target run — crystal siege ≈800 ticks, prey base at ceiling ≈4,000, a heart dried out ≈5,280 |
| V1a | **Shroomer lifespan.** A mechanic decision inside V1 | either age never checks a bloom, or ageing becomes a third limiter — deliberately | `MaxLifespan` 50,000 ticks was chosen to *exceed* a run and no longer does; every species now dies of age inside a target run |
| V2 | **The player is not a faction creature** | one creature of one faction, same rules, developing along a path | the player entity has no `Species` component, so it takes no terrain speed clamp (≈1 tile/tick against a creature cap of 0.25), no diet, no faction, no mortality, no verbs |
| V3 | **No influence channels** | anchors, signals and durable parameter writes — one mechanism, three targets | no signal field, no player-authored parameter writes; `SpeciesDefinition.StatVariation`, the medium the design writes into, is read by nothing (D5) |
| V4 | **No goal conditions and no domain measure** | asymmetric triple; domain = terrain **and** anchors; the Faeling's domain is the wild it strengthened | nothing measures a per-faction domain. `WorldManager.DeviationAt` compares live moisture against `PristineMoisture` but returns `MathF.Abs(...)` — it discards the sign, which is exactly the attribution (wetter → Shroomer, drier → Sectid). Anchors are already counted |
| V5 | **Death flow** | respawn at an anchor, paid for out of the faction pool | `FactionLives` is created and consumed by nothing; anchors pay no cost |
| V6 | **No progression layer** | in-run character paths; between-run unlocks and species patterns that carry over | neither exists; there is no persistence of any kind |
| V7 | **Rivals' advance is not visible** | one of the two clocks that make standing still bad | terraforming is a probability per cooldown roll applied to one tile at a time; nothing reads as a front. See R1 in [`design/08-open-questions.md`](../design/08-open-questions.md) |

## Known defects

Open, reproduced, not fixed. Each is described in full in the document that owns it.

| # | Defect | Where |
|---|---|---|
| D2 | Predation rate falls sharply at coarse LOD tiers: one attack per due tick, plus engagement geometry that ignores the path travelled since the last decision | [lod-and-performance.md](lod-and-performance.md) |
| D3 | Nutrition consumed/regenerated diverges between LOD tiers in spatial granularity | [lod-and-performance.md](lod-and-performance.md) |
| D4 | Faeling keepers have never been observed to damage a structure; cause not established, and the leading hypothesis was invalidated by a later change | [factions.md](factions.md) |
| D5 | `StatVariation` and `Species.Generation` exist and are never read | [species-data.md](species-data.md) |
| D6 | `Crystal.FAELING_AGGREGATED` is a dead sentinel from the removed statistical simulation | [factions.md](factions.md) |
| D7 | No automated tests below the whole-run gates — no unit coverage of individual systems | [tooling-and-tests.md](tooling-and-tests.md) |
| D9 | The "field feeding" of packmates described in earlier documentation is not implemented — no system feeds a groupmate | [factions.md](factions.md) |
| D10 | `SpeciesDefinition.ImmuneToStarvation` is set on one species and read by nothing; the immunity actually comes from zeroed hunger rates | [survival-and-population.md](survival-and-population.md) |
| D11 | The invariant gate's grace-window assertion is seed-dependent — it passes on one seed and fails on another *in the same build*, so a single-seed pass is not evidence the gate holds | [tooling-and-tests.md](tooling-and-tests.md) |

Ids are stable and are not reused, so the list has gaps. D8 — a kill created nutrition, because the
killer's share was never subtracted from the corpse pool — was fixed; the change and what it moved
are in [`../changelog.md`](../changelog.md). D1 — species ids from a per-process-randomised hash,
which made a fixed seed produce a coin flip between two runs — was fixed, and both gates are now
reproducible. D12 followed from it: with runs repeatable the LOD contract could be re-recorded, and
that gate now passes. D13 was **withdrawn**: it recorded soak results as reproducible within a working copy
and not across copies, and wider sampling showed that was an artifact of too few runs. It was D1 all
along, and its evidence sits under the species registry.
