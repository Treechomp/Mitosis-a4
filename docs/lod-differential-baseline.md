# LOD differential baseline

**What this measures.** LOD gates most systems with `if (!em.DueThisTick[entity]) continue;`.
A gated system that advances a per-tick quantity must also multiply it by
`SimulationLOD.EffectiveInterval`, or the quantity runs up to 20× slow for the majority of the
world — and *which* majority depends on where the player happens to be standing. On a profiled
36-chunk run at 12k creatures, 69% of entities sit at `Minimal` and 16% at `Low`.

The rule (`FEATURES_AND_DESIGN.md` §6.1) had been enforced only by prose, and had been broken at
least three times: Shroomer growth (10–20× fast at low tiers), attack-cooldown overshoot, and the
four systems §6.1 itself named. `Scripts/Testing/LodDifferentialRunner.cs` is the executable form:
one scenario, one seed, run twice — every entity pinned to `Full`, then every entity pinned to
`Minimal` — with end-state metrics compared.

**How to reproduce this table.**

```
godot --headless --path godot res://Scenes/LodDifferential.tscn -- --ticks=3000
```

3,000 ticks (150 s of game time at 20 TPS), tolerance 15%, min-count 5, 2 control runs per
scenario. All six runs take about a minute. Details of the harness and the CSV columns are in
[test-scenes.md](test-scenes.md).

---

## The noise floor, and why the table is not just a list of failures

These scenarios are small and chaotic. Run `predator_prey` at **Full tier** on seed 42 and again
on seed 43 — same code, same tier, different dice — and the kill count goes **17 → 40**, deaths by
predation among rabbits **4 → 31**. Nothing about LOD is involved. A flat 15% test on ecological
outcomes would therefore report failure everywhere regardless of whether the LOD code is correct,
and a report that always says FAIL carries no information.

So every scenario also runs two extra **Full-tier** simulations on shifted seeds. The worst
Full-vs-Full divergence a metric shows across those is its **noise floor**, and a metric is only
called a failure when the Full-vs-Minimal divergence beats both bars: over the tolerance *and*
over the floor. Verdicts in the CSVs are:

| verdict | meaning |
| --- | --- |
| `ok` | within tolerance |
| `within_noise` | over tolerance, but the scenario moves this metric at least as far on its own |
| `FAIL` | over tolerance **and** further than the dice moved it |
| `below_min_count` | fewer than `--min-count` events on both sides; reported, cannot fail |

**This is a floor, not a proof.** A metric can pass because the scenario is too noisy to measure
it. That is a statement about the scenario, not a clean bill of health, and it is the argument for
writing quieter, more mechanical scenarios (few creatures, no reproduction, one system under load)
rather than for loosening the tolerance. Where the test genuinely has teeth is on mechanical rates:
`terraform.nudges` has a noise floor of **4–10%** and `growth.shroomer_mean_scale` of **0.3–1.6%**,
so a real defect in either is unmissable.

---

## Result: before and after the Part C fixes

`terraform.nudges` is the clean case — a mechanical count, a low noise floor, and a defect eight
to eighteen times the size of that floor.

| Scenario | metric | before (Full vs Minimal) | after (Full vs Minimal) | noise floor |
| --- | --- | --- | --- | --- |
| shroomer_bloom | `terraform.nudges` | 872 vs 157 — **82% div, ratio 0.18** | 837 vs 787 — 6.0% div, ratio 0.94 | 9.6% |
| shroomer_bloom | `terraform.shifts` | 170 vs 39 — **77% div, ratio 0.23** | 165 vs 161 — 2.4% div, ratio 0.98 | 6.8% |
| faction_skirmish | `terraform.nudges` | 1408 vs 349 — **75% div, ratio 0.25** | 1528 vs 1423 — 6.9% div, ratio 0.93 | 4.1% |
| faction_skirmish | `terraform.shifts` | 411 vs 185 — **55% div, ratio 0.45** | 445 vs 440 — 1.1% div, ratio 0.99 | 8.3% |
| predator_prey | `spacing.Wolf` | 1.20 vs 6.34 — **81% div, ratio 5.3** | 1.20 vs 2.19 — 45% div, ratio 1.8 | 26% |

The Full-tier column barely moved (shroomer 872 → 837, faction 1408 → 1528, both inside the
Full-vs-Full spread of 785–926 and 1389–1520). That is the point: the fixes brought `Minimal` up
to `Full`, they did not retune the game.

Per-scenario verdict counts after the fixes:

| Scenario | metrics | ok | within_noise | below_min_count | FAIL |
| --- | --- | --- | --- | --- | --- |
| aquatic_biome | 26 | 9 | 1 | 7 | 9 |
| faction_skirmish | 33 | 8 | 6 | 12 | 7 |
| freshwater_pond | 31 | 7 | 8 | 7 | 9 |
| grazing_depletion | 18 | 4 | 0 | 7 | 7 |
| predator_prey | 24 | 7 | 6 | 6 | 5 |
| shroomer_bloom | 19 | 9 | 2 | 6 | 2 |
| **total** | **151** | **44** | **23** | **45** | **39** |

**No scenario passes outright.** The 39 remaining failures break down as `pop` 13, `births` 7,
`spacing` 7, `nutrition` 6, `kills` 4, `deaths_predation` 2, and they have four causes between
them, each given a verdict below: two real defects that this change deliberately does not fix
(predation rate, grazing granularity), one accepted consequence of the LOD design (herd spacing),
and one family — `pop` and `births`, 20 of the 39 — that is purely downstream of the other
three.

---

## Part C: what was changed, and the rate-like / state-like call for each

| System | quantity | call | change |
| --- | --- | --- | --- |
| `TerraformSystem` | acts per unit of world time | **rate-like** | batch the owed rolls, carry the remainder |
| `WanderSystem` | `RoamCooldown` (a countdown in ticks) | **rate-like** | `-= Elapsed`, clamped at 0 |
| `HerdingSystem` | `LeaderLostTicks` (a stopwatch in ticks) | **rate-like** | `+= Elapsed` |
| `HerdingSystem` | alignment (exponential approach to the leader's heading) | **rate-like** | `DecisionCadence.BlendRate` |
| `HerdingSystem` | cohesion (pull proportional to the current gap) | **state-like** | none — see below |
| `SeparationSystem` | separation impulse | **state-like** | none — see below |
| `CollisionSystem` | overlap resolution | **state-like** | none — see below |
| `CarrionSystem` | `CarrionChopRate` (nutrition per tick) | **rate-like** | `× Elapsed`, capped by appetite |
| `CarrionSystem` | corpse rot and decomposition | **neither** | none — the loop is ungated and corpses carry no LOD |

### Why Separation, Collision and Herding-cohesion are state-like

All three add an impulse (or a correction) to a velocity that `MovementSystem` damps **on the same
decision cadence** — `0.85` per due tick, gated identically. Impulse and decay share a clock, so
the steady state `force / (1 - damping)` is the same at every tier, while movement integrates that
velocity every tick regardless of tier. Multiplying by `EffectiveInterval` here would not
compensate for anything: it would put twenty times the force on a distant creature and fire it out
of its own herd.

The measurement backs this up, and does so with a control built into the data. In
`predator_prey`, `spacing.Rabbit` — a **solitary** species, so separation-only, no cohesion — is
3.82 tiles at Full against 3.87 at Minimal: a 1.2% divergence, `ok`, against a 10% noise floor. If
the separation impulse were under-applied at coarse tiers, rabbits would clump; they do not. What
*did* diverge was the **social** species (`spacing.Wolf` 1.20 → 6.34), which is cohesion and
alignment, not separation. Compensating the alignment blend cut that to 2.19.

`FEATURES_AND_DESIGN.md` §6.1 listed "separation impulses" among the uncompensated quantities.
That was the wrong diagnosis of a real symptom: distant herds *are* looser, but because of
alignment and heading commitment, not because the separation force is weaker.

### Why corpse decay needed nothing

§6.1 also listed corpse decay. `CarrionSystem.DecayCorpses` has no `DueThisTick` gate, and a
corpse is spawned with no `SimulationLOD` component at all — it has no tier and no interval, and
LODSystem marks it due every tick. Rot already advances once per tick everywhere in the world.
Both docs have been corrected.

---

## Standing exceptions: metrics that still diverge

### 1. Predation rate — a real defect, out of scope here

| Scenario | metric | Full | Minimal | ratio | noise floor |
| --- | --- | --- | --- | --- | --- |
| freshwater_pond | `kills.Otter` | 32 | 1 | 0.03 | 28% |
| freshwater_pond | `kills.total` | 39 | 6 | 0.15 | 41% |
| aquatic_biome | `kills.Penguin` | 91 | 30 | 0.33 | 27% |
| aquatic_biome | `kills.total` | 114 | 53 | 0.47 | 14% |
| aquatic_biome | `deaths_predation.Fish` | 91 | 33 | 0.36 | 25% |

**Not acceptable — but not what this change fixes.** Part C's scope is `TerraformSystem` plus the
four systems §6.1 names; `HuntingSystem` is neither, and it is the one system where a
"compensation" would directly retune combat balance. Two mechanisms are visible:

1. **One attack per due tick.** `HuntingSystem` fires at most one attack per due tick even though
   the attack cooldown is correctly decremented by `EffectiveInterval`. Shipped `AttackCooldown`
   values run 14–35 ticks (default 20), so at `Minimal` (interval 20) a fast attacker loses up to
   ~30% of its damage output to the cap alone. This is the same shape as the terraform defect and
   would take the same fix — spend the elapsed ticks as credit, act as many times as the window
   allows.
2. **Engagement geometry**, which is larger and harder. A predator must be within reach *at the
   moment of a due tick*. At `Minimal` it commits to a heading for 20 ticks and covers several
   tiles blind, so a chase that would connect at Full often passes straight through the strike
   window. Batching attacks does not fix this; it needs the strike test to consider the path
   travelled since the last decision, the way `DecisionCadence.Horizon` already does for terrain.

Fixing (1) is a small, contained change; (2) is a design question. Both change predator/prey
balance and belong in their own change with a balance pass behind them.

### 2. Herd and pack spacing — accepted, inherent to gating decisions

| Scenario | metric | Full | Minimal | ratio | noise floor |
| --- | --- | --- | --- | --- | --- |
| predator_prey | `spacing.Deer` | 1.94 | 7.00 | 3.6 | 48% |
| predator_prey | `spacing.Wolf` | 1.20 | 2.19 | 1.8 | 26% |
| grazing_depletion | `spacing.Deer` | 2.48 | 4.67 | 1.9 | 34% |
| aquatic_biome | `spacing.Penguin` | 2.77 | 4.33 | 1.6 | 17% |

**Accepted.** After the alignment fix, what remains is the cost of the design decision §6.1 makes
deliberately: LOD gates decisions, not motion. A `Minimal`-tier creature integrates its velocity
every tick but re-steers only every 20th, so it travels roughly 5 tiles committed to one heading.
A herd whose members each fly 5 tiles blind between corrections cannot hold a 2-tile formation, no
matter how correct the per-decision steering rates are. The steering *authority* per unit of world
time is compensated (`DecisionCadence.BlendRate`); the *granularity* of correction is not, and
cannot be without giving distant creatures per-tick decisions — which is the cost LOD exists to
avoid.

Note the direction of the error, because it rules out the alternative explanation: distant herds
are too **loose**, never too tight. An under-applied separation force would show as clumping.

### 3. Nutrition consumed / regenerated — a second real defect, in spatial granularity

| Scenario | metric | Full | Minimal | ratio | noise floor | verdict |
| --- | --- | --- | --- | --- | --- | --- |
| shroomer_bloom | `nutrition.consumed` | 1181 | 830 | 0.70 | 13% | FAIL |
| shroomer_bloom | `nutrition.regenerated` | 855 | 575 | 0.67 | 17% | FAIL |
| faction_skirmish | `nutrition.consumed` | 3706 | 2338 | 0.63 | 28% | FAIL |
| predator_prey | `nutrition.consumed` | 2769 | 3492 | **1.26** | 2% | FAIL |

**A real defect, and not the missing-multiplier kind.** `GrazingSystem` already multiplies every
consume rate by `EffectiveInterval` — the multiplier is present and correct. What is wrong is
*where* the batch is spent:

```csharp
float requested = herbDef.GrazeConsumeRate * tickMult;
float consumed  = _worldManager.ConsumeNutrition(pos.X, pos.Y, requested);   // ONE tile
float richness  = consumed / requested;                                      // pays out pro rata
```

At Full tier a creature takes twenty small bites over twenty ticks and walks while it does so,
drawing from up to twenty different tiles and collecting the regrowth that arrives between bites.
At Minimal it asks one tile for the whole twenty ticks' worth at once, and a tile can only give
what it is holding. On rich ground the two are equivalent; on depleted ground — which is precisely
the ground these scenarios are built to create — the batch is capped by a single tile's stock, and
`richness` then underfeeds the creature as well.

`shroomer_bloom` is the clean demonstration, because every confound is pinned: Deer 20 vs 20,
Shroomers 27 vs 30, terraform nudges within 6%, mean growth scale within 0.9% — identical
populations doing identical things — and consumption still 30% down at Minimal against a 13%
floor.

`predator_prey` diverges the other way (Minimal consumes **more**) and that one *is* downstream:
its Minimal run ends with 183 creatures against 143, including 42 deer against 32 and 126 rabbits
against 98, because fewer of them were eaten (see exception 1). More mouths, more grazing. Its 2%
noise floor confirms the signal is real; it is just signal about how many animals are alive.

**Proposed fix**, deliberately not made here (`GrazingSystem` is outside Part C's scope, and this
changes feeding balance): spend the batch along the path actually travelled since the last
decision rather than at a point — the same principle the terraform fix preserves ("a compensated
batch spreads the same way a sequence of Full-tier rolls would") and the same one
`DecisionCadence.Horizon` already applies to terrain sampling. Sample a handful of points along
`(prevPos → pos)` and draw the batch across them.

### 4. Population and birth counts — downstream of 1, 2 and 3

`pop.*` and `births.*` are 20 of the 39 remaining failures (13 and 7) and sit at the end of every
causal chain above: fewer kills leave more prey (`predator_prey` ends at 183 creatures at Minimal
against 143 at Full), underfed grazers on depleted ground breed less (`grazing_depletion` ends at
71 against 103), and looser herds change encounter rates.

**Accepted as downstream.** They are reported because population is the outcome a player would
actually notice, not because they are independently diagnosable — nothing in this family should be
chased until predation and grazing granularity are fixed, at which point it should be re-measured
rather than re-argued.

---

## Known limits of this test

- **One seed pair per scenario.** The noise floor comes from two control runs. That is enough to
  stop the obvious false positives and not enough to characterise a distribution. More control
  runs cost about a Full-tier run each (1–8 s).
- **`Full` vs `Minimal` only.** The intermediate tiers are reachable via `--scenario` plus a
  scenario-file `lod_override`, but the automated comparison does not sweep them. A defect that
  only bites at `Medium` would be missed.
- **No scenario is quiet enough for the ecological metrics.** Every existing scenario has
  reproduction enabled and 30–650 creatures, which is what makes the noise floor 25–48% on kills
  and births. A purpose-built scenario — a handful of creatures, reproduction off, one system
  under load — would give those metrics floors like the ones terraform enjoys. That is the highest
  value follow-up to this work.
- **The metrics are end-state, not integrated.** Two runs that reach the same end state by
  different paths compare equal. Population is sampled once at the final tick, while births,
  deaths, kills, terraform and nutrition are whole-run totals and so are more robust.
- **`spacing.*` is confounded by density.** Fewer animals in the same space sit further apart
  whatever the steering code does, so a spacing divergence has to be read next to the population
  divergence — `grazing_depletion`'s `spacing.Rabbit` (5.09 → 9.25) comes with `pop.Rabbit`
  (50 → 32) and is mostly that. The metric is only emitted where every run compared has at least
  `--min-count` individuals of the species, so it is never a statement about two crocodiles.
- **Runs are exactly reproducible.** Two invocations of the command above produce identical
  metrics for every scenario (`SimRandom` fixes each system's stream to the seed before the stack
  is built). A number that moves between runs of the same build is a bug in the harness, not
  variance.
