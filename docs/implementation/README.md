# Implementation layer

*Last updated: 2026-09-10 · verified against `f3ea31c` on `claude/lod-override-testing-rhwq4f`*

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

| # | Divergence | Design says | Code does (at `afb5e99`) |
|---|---|---|---|
| V1 | **Pacing — in the faction layer, and in two directions at once.** Not every rate, which is what this row used to claim | median run resolves in 60–120 min ([`design/06-run-and-progression.md`](../design/06-run-and-progression.md)) | the creature layer already fits the target: read off `SpeciesRegistry` lifespans and hunger rates against `SurvivalSystems.HungerDecayScale`, the shorter-lived species turn over several generations inside a 90-minute run (table in [`../changelog.md`](../changelog.md)). The faction layer is what misses, and it misses both ways — faction combat resolves far too fast for the run (crystal siege ≈800 ticks, a heart dried out ≈5,280) while faction terraforming may be too slow to read at all (V7). One scale factor cannot correct both, which is why the uniform "retime, then reshape" plan was withdrawn. The prey base reaching its ceiling ≈4,000 ticks is early equilibrium, C2, not lifespan tuning |
| V1a | **Shroomer lifespan.** A mechanic decision inside V1, and now a settled one | lifespan follows achieved growth — an individual that reaches maturity lives long, one that stays stunted dies young ([`design/06-run-and-progression.md`](../design/06-run-and-progression.md)) | `MaxLifespan` is a flat per-species constant; nothing derives it from `Growth.CurrentScale`. The old fork in this row — raise the constant, or add ageing as a third limiter — is closed, and neither branch was taken |
| V2 | **The player is not a faction creature** | one creature of one faction, same rules, developing along a path | the player entity has no `Species` component, so it takes no terrain speed clamp (≈1 tile/tick against a creature cap of 0.25), no diet, no faction, no mortality, no verbs |
| V3 | **No influence channels** | anchors, signals and durable parameter writes — one mechanism, three targets | no signal field and no player-authored parameter writes. The medium the design writes into half exists: `SpeciesDefinition.StatVariation` gives every individual its own values across 34 fields, but none of it is heritable — `ReproductionSystem` spawns from the species definition, so a child is re-rolled around the species mean rather than around its parent, and nothing can be selected for or written into (D5, and C3 in [`design/08-open-questions.md`](../design/08-open-questions.md)) |
| V4 | **No goal conditions and no domain measure** | asymmetric triple; domain = terrain **and** anchors; the Faeling's domain is the wild it strengthened | nothing measures a per-faction domain. `WorldManager.DeviationAt` compares live moisture against `PristineMoisture` but returns `MathF.Abs(...)` — it discards the sign, which is exactly the attribution (wetter → Shroomer, drier → Sectid). Anchors are already counted |
| V5 | **Death flow** | respawn at an anchor, paid for out of the faction pool | `FactionLives` is created and consumed by nothing; anchors pay no cost |
| V6 | **No progression layer** | in-run character paths; between-run unlocks and species patterns that carry over | neither exists; there is no persistence of any kind |
| V7 | **Rivals' advance is not visible — unverified.** An open measurement, not an established divergence | one of the two clocks that make standing still bad | the mechanism is deliberately biased toward a front: `TerraformSystem` sends half its acts to the tile underfoot and half to a random tile in radius, and its own comment says the underfoot half is what lets a frontier advance exist at all rather than speckling a neighbourhood. Whether that reads as a front at world scale has never been looked at; `--snapshot-world` (`0b6c012`) is the instrument, and the reading is agreed in advance under R1 in [`design/08-open-questions.md`](../design/08-open-questions.md). Parameters as checked here: Shroomer wetter 0.10 / cooldown 4 / radius 2, Sectid drier 0.04 / 6 / radius 3 plus a burst per hatch and an ambient pass every `NestSystem.AmbientTerraformInterval` ticks, Faeling restore 0.25 / 8 / radius 4; `TerraformSystem.MoistureStep` 0.05, scaled by growth |

| V8 | **The engine decided herbivore composition, through one shared class ceiling — closed.** The prey base filled its class allowance in the opening fifth of a run and held it, the engine refusing herbivore births by the million while nothing went hungry, and inside the allowance the fastest breeder took what it could hold. Per-species carrying capacity read off the generated terrain replaced it: the class allowance is now untouched and the refusal count is zero. What remains open is not the mechanism but its level — see C5 in [`design/08-open-questions.md`](../design/08-open-questions.md). Figures in [`../changelog.md`](../changelog.md) | composition is a property of the species, not of the engine ([`design/07-simulation-contract.md`](../design/07-simulation-contract.md)) | resolved at the commit named in the changelog |

## Known defects

Open, reproduced, not fixed. Each is described in full in the document that owns it.

| # | Defect | Where |
|---|---|---|
| D2 | Predation rate falls sharply at coarse LOD tiers: one attack per due tick, plus engagement geometry that ignores the path travelled since the last decision. **Deferred by decision** until the systems it is measured over are in place | [lod-and-performance.md](lod-and-performance.md) |
| D3 | Nutrition consumed/regenerated diverges between LOD tiers in spatial granularity. **Deferred by decision**, with D2 | [lod-and-performance.md](lod-and-performance.md) |
| D4 | Faeling keepers have never been observed to damage a structure; cause not established, and the leading hypothesis was invalidated by a later change | [factions.md](factions.md) |
| D5 | `Species.Generation` is written at construction and read by nothing. (`StatVariation` was in this row and does not belong: it is read, and reaches 34 species fields at spawn — the gap is that none of that variation is inherited) | [species-data.md](species-data.md) |
| D6 | `Crystal.FAELING_AGGREGATED` is a dead sentinel from the removed statistical simulation | [factions.md](factions.md) |
| D7 | No automated tests below the whole-run gates — no unit coverage of individual systems | [tooling-and-tests.md](tooling-and-tests.md) |
| D9 | The "field feeding" of packmates described in earlier documentation is not implemented — no system feeds a groupmate | [factions.md](factions.md) |
| D10 | `SpeciesDefinition.ImmuneToStarvation` is set on one species and read by nothing; the immunity actually comes from zeroed hunger rates | [survival-and-population.md](survival-and-population.md) |
| D11 | The invariant gate's grace-window assertion is seed-dependent — it passes on one seed and fails on another *in the same build*, so a single-seed pass is not evidence the gate holds | [tooling-and-tests.md](tooling-and-tests.md) |
| D16 | The LOD contract is stale, and it is brittle in a way that makes staleness likely. Stale: the recorded verdicts have not been re-recorded since the change that last moved them, so the gate exits non-zero on an unmodified checkout. Brittle: a change to the food economy small enough to leave the population totals alone still flips a large block of verdicts, and a gentler version of the same change flips as many — so the count does not scale with the size of the behaviour change, and the gate cannot distinguish "LOD fidelity got worse" from "the trajectory moved". Figures in [`../changelog.md`](../changelog.md) | [tooling-and-tests.md](tooling-and-tests.md) |

| D17 | **Closed.** A grazer drew from the tile under it every due tick it stood on fertile ground, hungry or not, and the hunger gain was clamped, so most of what grazers took from the world was destroyed. It also made hunger a dead variable — animals sat near full for whole runs, so being displaced onto poor ground cost them almost nothing. A full animal now stops feeding, and what a mouthful is worth depends on how full the ground is; hunger spreads across species by the quality of the ground they hold, and animals starve where they cannot make a living. Figures in [`../changelog.md`](../changelog.md) | [survival-and-population.md](survival-and-population.md) |

| D18 | **Unexplained.** A one-line guard in `Genome.Inherit` — "a trait the parent does not express is not restored by regression", implemented as *skip the trait when the parent's value is exactly zero* — slowed the tick loop by about two orders of magnitude on the standard world: 200 ticks did not finish in 560 seconds against 400 ticks in 3. It is reproducible and it bisects cleanly to that line. No mechanism has been established, and the intended behaviour was reached another way instead (write grazing pressure only for a species that grazes), so the pathology is recorded rather than fixed. **Anything that generalises the "unexpressed trait" rule should reproduce this first** | [species-data.md](species-data.md) |

Ids are stable and are not reused, so the list has gaps. D8 — a kill created nutrition, because the
killer's share was never subtracted from the corpse pool — was fixed; the change and what it moved
are in [`../changelog.md`](../changelog.md). D1 — species ids from a per-process-randomised hash,
which made a fixed seed produce a coin flip between two runs — was fixed, and both gates are now
reproducible. D12 followed from it: with runs repeatable the LOD contract could be re-recorded, and
that gate now passes. D14 and D15 were found and fixed in the same session they were filed: every
spawn path now asks `EntityManager.HasRoomForEntity` before creating, and the headless runners
catch, report and quit non-zero rather than idling forever with the scene loaded. D13 was **withdrawn**: it recorded soak results as reproducible within a working copy
and not across copies, and wider sampling showed that was an artifact of too few runs. It was D1 all
along, and its evidence sits under the species registry.
