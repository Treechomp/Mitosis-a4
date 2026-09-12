# LOD & performance

*Last updated: 2026-09-09 · verified against `c41212b`*

## `LODSystem` — `Systems/LODSystem.cs` · runs first, never gated

Computes a distance to the player **per spatial cell** (cell caching avoids a per-entity square
root), assigns one of five tiers with hysteresis at the boundaries, and writes a single boolean per
entity into `EntityManager.DueThisTick[]`. Every gated system then skips with one array read.

`SetPlayerEntity`, `SetVisibleRadius`, `ApplyTier`. `SetLevelOverride` forces every entity to a
chosen tier regardless of distance — this is what makes the coarse tiers testable on a small world
(see [tooling-and-tests.md](tooling-and-tests.md)).

Tier intervals are declared on `LODLevel` / `SimulationLOD`. `TickInterval` starts at one and is
never zero; `LODSystem` repairs any non-positive interval even when the tier does not change,
because a zero interval feeds a divide-by-zero into rate maths — it once produced `NaN` hunger that
spread through the food web.

**Phases are staggered** on downgrade (`TicksUntilUpdate = entity % TickInterval`); upgrades fire
immediately so an entity entering view is responsive at once. Without staggering the population
falls into lockstep — everything spawned together shares a phase, and a tier change resets the
countdown, re-synchronising every entity in a cell as the player moves — so cost arrives in waves
rather than flat.

## The rate rule

**LOD gates decisions, not motion.** Movement is not gated; see
[movement-and-terrain.md](movement-and-terrain.md) and contract §1 in
[`../design/07-simulation-contract.md`](../design/07-simulation-contract.md).

For everything that *is* gated, two rules, and both have been violated in shipped code:

1. **The gate and the multiplier are one thing.** A system that multiplies a rate by the LOD
   interval **must** also skip non-due ticks. Compensation pays back ticks that were actually
   skipped; applying it every tick is multiplication, not compensation. If you add an effective
   interval to a loop, add `if (!em.DueThisTick[entity]) continue;` in the same edit.
2. **Compensate by the *effective* interval, not the nominal one.** `TickInterval` is only the
   nominal interval of the current tier; when an entity changes tier the countdown resets, so the
   real gap since its last update differs. `DecisionCadence.Elapsed` is the correct quantity.

Rate-like and state-like quantities compensate differently:

- **Rate-like** (accrual per tick: hunger, ageing, growth, damage over time, terraform rolls) —
  multiply by elapsed ticks, or for a probability use `1 − (1−p)^elapsed`, never `p × elapsed`.
- **State-like** (a force, a spacing, a steering blend: separation, collision, herd cohesion) —
  do **not** scale. They express a target configuration, not an accumulation, and scaling them
  overshoots.

`Systems/DecisionCadence.cs` is the shared helper: `Interval`, `Elapsed`, `Horizon` (how far the
entity will travel before it thinks again — used to scale look-ahead), `TurnAgility`, `BlendRate`,
`BlendVelocity`. Use it rather than reimplementing the convention, so the convention is a function
and not a habit.

## Verification

The LOD differential test runs the same scenario at Full and at a forced coarse tier and compares
outcome metrics against recorded verdicts. It is a gate, not a report: the pass/fail condition is
the difference from the recorded verdict set, so a change that alters LOD behaviour must
deliberately re-record. See [tooling-and-tests.md](tooling-and-tests.md).

### The rate of divergence, measured apart from the outcome

The differential compares two runs at the end of three thousand ticks, by which point they have
separated chaotically. That is D16: the verdict count moves with any change to the systems the
metrics are measured on, and it does not move with the SIZE of that change, so the gate cannot say
whether fidelity got worse or the trajectory moved. No tolerance repairs that, because it is the
shape of the measurement and not its thresholds.

`LodDivergenceRunner` measures the same pair of runs as a rate. Each run writes a fingerprint
stream; the two streams are compared afterwards into **onset** — the first sample tick at which the
two worlds are not identical — and **growth**, one bounded scalar per sample tick. The curve is the
artefact. A gameplay change moves both runs alike and leaves the rate where it was; a fidelity
change bends it. What it records and how the scalar is built is in
[tooling-and-tests.md](tooling-and-tests.md); the recorded curve is `docs/lod-divergence-curve.csv`
and the figures behind it are in [`../changelog.md`](../changelog.md).

**A flat curve is not the target, and a gate built on this file must never ask for one.** A coarser
decision cadence changes behaviour by design: a creature that re-steers every twentieth tick arrives
somewhere else, and it is meant to. The curve says what that costs. The gate this is built towards
asserts that the curve has not WORSENED beyond a stated tolerance — a tolerance that has not been
set, because setting it needs more than one scenario and one seed. Until then the exit code is still
the differential's, and this instrument reports.

This sits against contract §1, which promises that reduced detail changes *nothing but cost*. That
promise is about outcomes and it already admits documented exceptions — herd spacing is one, below.
Measuring the rate turns the size of those exceptions from a category into a number. Whether the
contract should say so is a design question and not an implementation one; it is not decided here.

## Only two of the tiers are tested

`LodDifferentialRunner` runs each scenario at `LODLevel.Full` and at `LODLevel.Minimal`, with its
control runs at Full. The tiers between them are never compared against anything. Every rate that
must scale is therefore checked at one end of the ladder and assumed in the middle, in a codebase
where the rate rule has been broken three times.

That is worth knowing on its own, and it is also the shape the design layer is aiming at: the
contract wants two levels, full and one compressed, and the gate already tests exactly that pair.
Collapsing the ladder would delete the untested middle rather than test it.

## Deferred, deliberately

**D2 and D3 are parked, and this is a decision rather than an omission.** They are equivalence
defects: the work is to make a compressed tier produce the world the full tier produces. Two things
make that premature.

The player is not implemented (V2, V3, V5 in [README.md](README.md)). Player agency is what the
recent design work added, and it lands directly on the predation and nutrition economies these two
defects live in. Tuning tier equivalence against the current balance would be fitting to a
configuration that is known to be leaving.

Equivalence is also not separately measurable from balance. The differential asks whether two tiers
agree, not whether either is right; a change can close the gap between them while moving the game.
D2's first mechanism was tried on exactly that basis and inverted several metrics rather than
shrinking them — see the note under D2 and [`../changelog.md`](../changelog.md).

So: the systems the equivalence is measured over should be in place and working first, and the LOD
pass comes after.

**The gate is red, and it went red exactly as predicted.** It exits non-zero on an unmodified
checkout and the mismatch is large — the counts, with their commit, are in
[`../changelog.md`](../changelog.md). Nothing about that is an LOD regression in the sense the gate
means: the food economy was rebuilt underneath it (per-species carrying capacity, forage yields,
hunger that tracks the ground), so the populations every metric is measured on are different
populations. The contract has deliberately **not** been re-recorded, because re-recording across a
behaviour change buries whatever real fidelity loss might be inside it.

That is D16 in [README.md](README.md): the contract cannot separate "LOD fidelity got worse" from
"the trajectory moved", so it fails any change to the systems it measures without saying which
happened. Measured, the count does not even scale with the size of the change — a forage change
gentle enough to barely move the population flipped as many verdicts as one that moved it a third.
The replacement measurement is the divergence curve above; it does not yet gate anything.

**What that means for the LOD pass.** Re-recording is the first step of the work and not a
housekeeping task before it: the contract has to be re-recorded against a balance that is going to
hold, and read once with the knowledge that its baseline moved. Until then the gate's exit code
carries no signal, and the per-metric output still does.

## Standing divergences

These four categories are what the recorded contract holds as expected `FAIL` rows: every metric
failing in the current recording is a predation count, a spacing, a nutrition total, or a
population or birth count downstream of those. A failing row outside them is not covered by
anything written down and is a bug — that is the whole point of recording the set rather than the
count. The scenarios are small, so a handful of individuals is tens of per cent; the gate takes a
measured noise floor from two same-tier control runs and fails only when the tier difference clears
both that floor and a fixed tolerance.

The contract was last recorded once species ids became deterministic. It could not honestly be
recorded before that: recording twice on one build produced materially different verdicts, which
the previous baseline recorded as a blocking problem against itself.

### D2 — predation rate falls at coarse tiers

Two mechanisms, measured across the aquatic, freshwater and predator/prey scenarios:

1. **One attack per due tick.** `HuntingSystem` fires at most one attack per due tick even though
   the cooldown is correctly decremented by the effective interval. With shipped cooldowns in the
   low tens of ticks, a coarse tier loses a share of a fast attacker's damage output to the cap
   alone — and much more during an ambush burst, which quarters the cooldown.
2. **Engagement geometry.** A predator must be in reach *at the moment of a due tick*. At a coarse
   tier it commits to a heading for many ticks and covers several tiles blind, so a chase that
   would connect at Full passes straight through the strike window. The strike test has to consider
   the path travelled since the last decision, the way `DecisionCadence.Horizon` already does for
   terrain.

**The two are not separable, and this was established the hard way.** (1) was described here as the
small contained half and was written: the elapsed ticks spent as credit, as many blows as the
window bought, floored so no credit banks across targets, thorns answering only the blows that
actually landed. It made the differential worse. More metrics failed after it than before, and the
new failures concentrated in the swarm scenarios, where cooldowns are shortest — several flipped
from the coarse tier under-killing to over-killing it.

The reason is the flaw in (1) taken alone: being in reach *at the sampling instant* is not the same
as having been in reach for the interval. At Full the prey can leave between blows; granting the
window's blows at once takes that away, so a rate is restored as a burst. The blow count is only
correct once something knows how long the predator was actually in reach — which is (2). Figures in
[`../changelog.md`](../changelog.md); the attempt is not in the history, only its measurement.

A note for whoever writes (2): `EntityManager.PrevPositions` does not help. It is snapshotted every
tick for render interpolation, so for an entity due every twentieth tick it holds last tick's
position, not the position at its last decision. The path over the elapsed window needs storing
when the decision is taken.

Both change predator/prey balance and belong in their own change with a balance pass behind it.
(2) is also design question C2-adjacent — see
[`../design/08-open-questions.md`](../design/08-open-questions.md).

### D3 — nutrition consumed/regenerated diverges in spatial granularity

Real, and distinct from a rate error: the totals differ because *where* feeding happens differs when
decisions are coarser.

### Accepted, not defects

- **Herd and pack spacing** diverge between tiers. This is inherent to gating decisions: a herd
  whose members re-decide less often sits at a different equilibrium spacing. Accepted.
- **Population and birth counts** diverge downstream of the above. Reported, not separately
  actioned.

## Other performance notes

- `SpatialHashUpdateSystem` refreshes hash positions and is gated. Motion was ungated from LOD and
  the hash was not, which means a coarse-tier entity's *position in the hash* lags its real
  position. This is a known consequence and has not been separately resolved.
- `WorldManager.PredatorHash` is a second, smaller hash carrying only entities that register as
  threats, so prey scans do not walk the whole population.
- The across-water path test in `HuntingSystem` is deferred to only the candidate that would become
  the new best, and candidate counts are capped per scan.
- `TileRegenerationSystem` batches passes and skips fully-regenerated chunks.
- Entities are frustum-culled before rendering; on a profiled world this cut per-frame instance
  writes by more than an order of magnitude (see [`../changelog.md`](../changelog.md), `7a7fb28`).
- The debug overlay (F3) reports sorted per-system timings; that is the measurement of record for
  "which system is expensive".
