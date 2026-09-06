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

---

## The contract is the CSV. This file is the commentary.

`docs/lod-differential-expected.csv` records the verdict every (scenario, metric) pair currently
produces. **It is what the exit code is computed from.** The tables in this file explain WHY each
standing exception is accepted; they are prose for humans and are not read by anything.

Before this split the runner exited on the failure count. Dozens of metrics fail for the accepted
reasons documented below, so it exited 1 on every run and **had never once exited 0**. A gate that
is always red carries no signal: a new regression was indistinguishable from the standing ones
except by a human diffing a markdown table by eye — which is precisely the failure mode this test
was built to replace, since §6.1 stated the LOD rate rule in prose and it was broken three times
anyway.

Now the run compares itself to the recorded file and gates on the **difference**:

| | meaning | exit |
| --- | --- | --- |
| recorded FAIL, observed FAIL | a known, explained exception | silent, 0 |
| recorded passing, observed FAIL | **REGRESSION** | **non-zero** |
| recorded FAIL, observed passing | **IMPROVEMENT** — re-record | 0, printed loudly |
| present in only one of the two | **DRIFT** — a metric was added or removed | **non-zero** |

The comparison is binary: failing, or not. `ok`, `within_noise` and `below_min_count` are one
class, because a metric moving between them is not information — the last two both mean "this
metric had nothing to say", and which applies depends on where the dice fell. The exact verdict is
still recorded, because it is worth reading; it is just not gated on.

### Accepting an exception

```
godot --headless --path godot res://Scenes/LodDifferential.tscn -- --ticks=3000 --record
```

Recording is a deliberate act and never a side effect of a normal run — a run that quietly
re-recorded its own failures would accept every regression it found.

**Accepting an exception means writing down the reason.** Re-recording is half the act; the other
half is a paragraph in this file saying why that metric is allowed to diverge. A row in the CSV
with no matching explanation here is not an accepted exception, it is a bug that has been made
invisible — which is the exact thing the old always-red gate was doing to all forty at once.

The same applies in reverse. When an exception is FIXED the run prints an IMPROVEMENT and still
exits 0, because good news must not break the build — but it must be re-recorded promptly, since a
fixed exception left in the file will hide the next regression in that same metric.

If the expected file is missing, the run exits non-zero and tells you to record it. It never
silently passes.

---

**How to reproduce this table.**

```
godot --headless --path godot res://Scenes/LodDifferential.tscn -- --ticks=3000
```

3,000 ticks (150 s of game time at 20 TPS), tolerance 15%, min-count 5, 2 control runs per
scenario. All six runs take about a minute. Details of the harness and the CSV columns are in
[test-scenes.md](test-scenes.md).

> **Scenario count has grown.** The harness globs `TestScenarios/*.scenario.txt`, so the three
> faction-structure scenarios added with `SiegeSystem` (`crystal_siege`, `nest_raid`,
> `heart_drying`) are now in the sweep — nine scenarios, not six, and the per-scenario table below
> predates them. Siege damage was checked separately for LOD correctness and is sound: forcing
> `crystal_siege` to Full and to Minimal gives 138 vs 154 structure hits (the damage rate is right)
> with total destruction at t=801 vs t=1,154 — a 1.4× difference in *timing* from decision
> granularity, the same legitimate coarsening documented under exception 2 below, not a rate bug.
>
> **Numbers refreshed** after per-class population budgets replaced the global birth ramp
> (`FEATURES_AND_DESIGN.md` §9.1). That change altered the reproduction path these scenarios run
> through — a graded local-density brake instead of a hard cutoff, and a seed split derived from
> the class budgets — so every figure below moved. The *findings* did not: terraform is still
> within its noise floor, predation and spacing are still the standing exceptions.

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

| Scenario | metric | before the fix | current | noise floor |
| --- | --- | --- | --- | --- |
| shroomer_bloom | `terraform.nudges` | 872 vs 157 — **82% div, ratio 0.18** | 891 vs 818 — 8.2% div, ratio 0.92 | 7.6% |
| shroomer_bloom | `terraform.shifts` | 170 vs 39 — **77% div, ratio 0.23** | 175 vs 165 — 5.7% div, ratio 0.94 | 2.9% |
| faction_skirmish | `terraform.nudges` | 1408 vs 349 — **75% div, ratio 0.25** | 1547 vs 1388 — 10.3% div, ratio 0.90 | 8.8% |
| faction_skirmish | `terraform.shifts` | 411 vs 185 — **55% div, ratio 0.45** | 464 vs 431 — 7.1% div, ratio 0.93 | 3.2% |
| shroomer_bloom | `growth.shroomer_mean_scale` | — | 1.200 vs 1.199 — 0.03% div | 1.4% |

Two things to read off that. The `Minimal` column came up to meet `Full` — ratios 0.90–0.94
against 0.18–0.45 before. And the `Full` column barely moved across the fix *or* the population
budgets that followed it (shroomer 872 → 837 → 891, all inside the Full-vs-Full spread), which is
what "brought Minimal up to Full rather than retuning the game" has to look like.

Per-scenario verdict counts after the fixes:

| Scenario | metrics | ok | within_noise | below_min_count | FAIL |
| --- | --- | --- | --- | --- | --- |
| aquatic_biome | 26 | 6 | 4 | 7 | 9 |
| faction_skirmish | 33 | 12 | 6 | 11 | 4 |
| freshwater_pond | 30 | 7 | 6 | 7 | 10 |
| grazing_depletion | 18 | 2 | 0 | 7 | 9 |
| predator_prey | 24 | 8 | 6 | 5 | 5 |
| shroomer_bloom | 18 | 10 | 0 | 5 | 3 |
| **total** | **149** | **45** | **22** | **42** | **40** |

**No scenario passes outright.** The 40 remaining failures break down as `pop` 9, `nutrition` 8,
`kills` 7, `births` 6, `spacing` 6, `deaths_predation` 4, and they have four causes between them,
each given a verdict below: two real defects that this change deliberately does not fix (predation
rate, grazing granularity), one accepted consequence of the LOD design (herd spacing), and one
family — `pop` and `births`, 15 of the 40 — that is purely downstream of the other three.

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

The measurement backs this up, and does so with a control built into the data. In `predator_prey`, `spacing.Rabbit` — a **solitary** species, so separation-only, no cohesion —
is 3.88 tiles at Full against 4.36 at Minimal: an 11% divergence, `ok`. Its herding neighbour
`spacing.Deer` in the same run is 2.18 against 5.66, a 2.6× spread. If the separation impulse were
under-applied at coarse tiers, rabbits would clump; they do not, and the species that spread are
the ones with cohesion and alignment. That was the diagnosis behind compensating the alignment
blend, which took `spacing.Wolf` from 1.20 vs 6.34 (81% divergence) down to within its own noise.

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
| freshwater_pond | `kills.Otter` | 32 | 2 | 0.06 | 41% |
| freshwater_pond | `kills.total` | 33 | 15 | 0.46 | 42% |
| aquatic_biome | `kills.Penguin` | 67 | 28 | 0.42 | 17% |
| aquatic_biome | `kills.total` | 87 | 39 | 0.45 | 17% |
| aquatic_biome | `deaths_predation.Fish` | 67 | 28 | 0.42 | 18% |
| predator_prey | `kills.total` | 36 | 22 | 0.61 | 23% |

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

| Scenario | metric | Full | Minimal | ratio | noise floor | verdict |
| --- | --- | --- | --- | --- | --- | --- |
| predator_prey | `spacing.Deer` | 2.18 | 5.66 | 2.6 | 9% | FAIL |
| grazing_depletion | `spacing.Deer` | 3.48 | 5.60 | 1.6 | 28% | FAIL |
| grazing_depletion | `spacing.Rabbit` | 4.54 | 5.40 | 1.19 | 6% | FAIL |
| aquatic_biome | `spacing.Fish` | 1.53 | 1.91 | 1.25 | 9% | FAIL |
| predator_prey | `spacing.Rabbit` | 3.88 | 4.36 | 1.12 | 15% | ok |
| predator_prey | `spacing.Wolf` | 5.08 | 7.88 | 1.55 | 75% | within_noise |

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
| shroomer_bloom | `nutrition.consumed` | 1334 | 822 | 0.62 | 24% | FAIL |
| shroomer_bloom | `nutrition.regenerated` | 927 | 581 | 0.63 | 25% | FAIL |
| faction_skirmish | `nutrition.consumed` | 3491 | 2447 | 0.70 | 26% | FAIL |
| grazing_depletion | `nutrition.consumed` | 935 | 624 | 0.67 | 20% | FAIL |
| freshwater_pond | `nutrition.consumed` | 2770 | 2189 | 0.79 | 14% | FAIL |

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

`shroomer_bloom` is the clean demonstration, because every confound is pinned: terraform nudges
within 8%, mean growth scale within **0.03%**, populations within their noise — identical
populations doing identical things — and consumption still 38% down at Minimal against a 24%
floor. Every scenario with depleted ground shows the same sign; where food is plentiful
(`predator_prey`, `aquatic_biome`) the metric is `ok`, which is what a saturation effect looks
like.

Where the divergence runs the *other* way it is downstream instead: more mouths alive at Minimal
because fewer of them were eaten (exception 1) means more grazing.

**Proposed fix**, deliberately not made here (`GrazingSystem` is outside Part C's scope, and this
changes feeding balance): spend the batch along the path actually travelled since the last
decision rather than at a point — the same principle the terraform fix preserves ("a compensated
batch spreads the same way a sequence of Full-tier rolls would") and the same one
`DecisionCadence.Horizon` already applies to terrain sampling. Sample a handful of points along
`(prevPos → pos)` and draw the batch across them.

### 4. Population and birth counts — downstream of 1, 2 and 3

`pop.*` and `births.*` are 15 of the 40 remaining failures (9 and 6) and sit at the end of every
causal chain above: fewer kills leave more prey (`predator_prey` ends at 157 creatures at Minimal
against 128 at Full), underfed grazers on depleted ground breed less (`grazing_depletion` ends at
81 against 111), and looser herds change encounter rates.

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
- **~~Runs are exactly reproducible.~~ THEY ARE NOT — see below.** This claim was here, and
  testing it while building the expected-verdict gate showed it to be false for any scenario with
  two or more factions in it.

---

## BLOCKING: the multi-faction scenarios are not reproducible across processes

**Found 2026-08-29 while recording the expected verdicts.** Recording twice on the same build
produced materially different verdicts, so the stability check that the gate depends on was run
directly. Same seed, same binary, same single scenario, two separate processes:

| `nest_raid`, 3,000 ticks | process A | process B |
| --- | --- | --- |
| `kills.Sectid` | 41 vs 12 (FAIL) | not emitted at all |
| `deaths_predation.Rabbit` | 29 vs 7 (FAIL) | 11 vs 8 (within_noise) |
| `deaths_predation.Sectid` | 0 vs 0 (below_min_count) | 7 vs 4 (FAIL) |
| `births.Faeling` | 14 vs 11 (FAIL) | 13 vs 8 (within_noise) |

At **800** ticks the same scenario is byte-identical across processes, so this is a tiny
divergence amplifying chaotically, not gross randomness.

**Which scenarios.** Every scenario that diverged has **two or more factions**
(`crystal_siege`, `nest_raid`, `faction_skirmish`). Every single-faction and no-faction scenario
(`shroomer_bloom`, `heart_drying`, `predator_prey`, `aquatic_biome`, `freshwater_pond`,
`grazing_depletion`) was stable across every run compared.

**The mechanism.** `SpeciesRegistry.GetId(name) => name.GetHashCode()`, and .NET randomises string
hashing **per process**. Verified directly — the same string, two processes:

```
"Sectid".GetHashCode() = 1807738839
"Sectid".GetHashCode() = -811547173
```

So every species id differs from run to run. `FactionCensus.DominantFactionId` then iterates
`Dictionary<int, int> _global`, keyed by exactly those ids, and picks the leader with `n > lead` —
**a tie is resolved by whichever species the dictionary happens to yield first**, which is a
function of the process's hash seed. That answer drives `StructureTargetsDominantOnly`, the Faeling
keeper's siege mandate: a tie falling the other way sends every keeper against a different faction,
and the run diverges from there. A scenario with one faction never ties, which is exactly the
observed split.

This is the identified mechanism and it fits all the evidence; it has not been proven to be the
*only* source of cross-process divergence.

**Consequence for the gate.** On the multi-faction scenarios the recorded verdicts are not a
contract — three successive recordings of the same build gave 45, then a different set, then 59
FAIL. **The gate is trustworthy today only on the single-faction and no-faction scenarios**, and
will raise spurious REGRESSION and IMPROVEMENT on the other three. That is a defect in the
simulation, not in the gate, and the gate is what exposed it.

The sharpest demonstration is a full run against a freshly recorded file, on an unchanged build:

```
[LodDiff] 4 REGRESSION(S)
      faction_skirmish   deaths_predation.Rabbit          within_noise -> FAIL
      faction_skirmish   kills.Sectid                     within_noise -> FAIL
      faction_skirmish   kills.total                      within_noise -> FAIL
      nest_raid          deaths_predation.Sectid          below_min_count -> FAIL
[LodDiff] 16 IMPROVEMENT(S)      (crystal_siege 5, nest_raid 11)
[LodDiff] known 203  regression 4  improvement 16  drift 0
```

**All twenty differences fall in `crystal_siege`, `nest_raid` and `faction_skirmish`. The other
seven scenarios produce zero.** Nothing changed between recording and running but the process.

Until this is fixed, a run filtered to the stable scenarios is the meaningful gate:

```
godot --headless --path godot res://Scenes/LodDifferential.tscn -- --ticks=3000 \
    --scenario=shroomer_bloom,predator_prey,aquatic_biome,freshwater_pond,grazing_depletion,heart_drying
```

**Not fixed here** — this change was scoped to make the existing verdicts checkable and to touch no
simulation code. The fix is to make species ids stable across processes (a deterministic hash, or
an assigned ordinal at registration) and to give `DominantFactionId` an explicit, id-independent
tie-break. Both are small; both change simulation behaviour and need their own change.

Note the trap this fell into is the one the task anticipated in the other direction: the
instruction was that unstable verdicts mean "more control runs, not a looser tolerance". More
control runs would not help here. The instability is not in the noise floor — it is in the runs
being different simulations.
