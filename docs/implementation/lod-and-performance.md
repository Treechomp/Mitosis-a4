# LOD & performance

*Last updated: 2026-09-07 · verified against `7a7fb28`*

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

## Standing divergences

### D2 — predation rate falls at coarse tiers

Two mechanisms, measured across the aquatic, freshwater and predator/prey scenarios:

1. **One attack per due tick.** `HuntingSystem` fires at most one attack per due tick even though
   the cooldown is correctly decremented by the effective interval. With shipped cooldowns in the
   low tens of ticks, a coarse tier loses a substantial share of a fast attacker's damage output to
   the cap alone. Same shape as the terraform defect, same fix: spend the elapsed ticks as credit
   and act as many times as the window allows. Small and contained.
2. **Engagement geometry.** A predator must be in reach *at the moment of a due tick*. At a coarse
   tier it commits to a heading for many ticks and covers several tiles blind, so a chase that
   would connect at Full passes straight through the strike window. Batching attacks does not fix
   this; the strike test has to consider the path travelled since the last decision, the way
   `DecisionCadence.Horizon` already does for terrain.

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
