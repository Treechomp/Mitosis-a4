# Changelog

*Last updated: 2026-09-11 · current branch `claude/lod-override-testing-rhwq4f`, head `e0eb76a`*

What changed, when, in which commit, and what it measured. Newest first.

**Every measured figure in this project belongs here and nowhere else**, stamped with the seed and
commit it came from. A figure without provenance is not evidence — the project has already had one
headline result survive a change that invalidated it (noted below).

Entries are grouped by workstream because that is how the work happened; within a group they run
newest first. Commit subjects are quoted where they already say the result.

---

## 2026-09 — rendering cost, faction repair, the corpse economy

Fixing D8 removed food that a bug had been inventing, and the faction that eats corpses paid for
it. A kill used to yield the killer's cut **plus** an undiminished corpse; it now yields one body
split between the two. The Sectid colony economy converts corpse mass into larvae
(`NestSystem` spawns from `FoodStored`), so the change shows up as a birth-rate collapse rather
than as starvation: Sectid deaths went *down* while births fell by roughly two thirds. Whether to
compensate for that, and where, is an open balance decision — it has not been made.

| Commit | Date | Change | Result |
|---|---|---|---|
| — | 2026-09-12 | Measure LOD fidelity as a RATE of divergence instead of an end-state comparison, and record the first curve (D16, stage 1) | **the instrument repeats exactly, and the first curve plateaus rather than climbing.** predator_prey, seed 42, 3,000 ticks, Full against Minimal, sampled every 5 ticks, simulation at `bef5cb2`: onset at tick **5**, divergence rising to a plateau of **0.18–0.24** and ending at **0.178**, mean **0.180** over 601 samples. Two consecutive recordings on one build are byte-identical, streams and curve. The differential's verdicts are untouched: *known 173 / regression 31 / improvement 17 / drift 17*, and its summary CSV is byte-identical to the same run on an unmodified `bef5cb2`. Tables below |
| `f3ea31c` | 2026-09-10 | Give offspring their parents' traits, on a leash, and measure where selection takes them | **selection is real, it is measurable, and it points the way this project has twice had to undo.** Five seeds, 20,000 ticks: lineages reach 21 generations at the fast end and under 2 at the slow, and `ReproHungerThreshold` falls in 19 of 24 species — hardest where generations are highest (Fish −13.4% at gen 21, Penguin −9.8% at gen 13) and not at all where they are lowest. Invariants hold on 4 of 5 seeds, the fifth being D11. Tables below |
| `c41212b` | 2026-09-09 | Make what a mouthful is worth depend on how full the ground is, stop full animals feeding, and put hunger on the timescale of a run | **animals now starve where they cannot make a living, and no species is lost to it.** Seeds 1234 / 999, 20,000 ticks: starvation deaths **0 → 32 / 34**, average hunger spread **84–96% → 49–94%** and ranked by the quality of the ground each species holds, herbivores 3,177 / 2,776 on a plateau, all invariants holding on both seeds, class ceiling still never reached. Tables below |
| `4bca4f7` | 2026-09-09 | Give each species a carrying capacity read off the seed's terrain, and raise what species extract to match | **the engine stops deciding how many herbivores there are.** Seeds 1234 / 999, 20,000 ticks: the prey base reaches its class ceiling at **no point in either run** against t=2400 (12% of the run) before, herbivore birth refusals **1,813,206 → 0**, and Lizard — extinct in both controls — returns at 109 / 219. Herbivore total 5,036 / 5,040 → 3,958 / 3,779, held by thirteen species instead of one. Tables below |
| `6e4dd46` | 2026-09-09 | Add the per-species, per-tile forage yield, and ship it with no species using it | **the mechanism is neutral and the tables are not shipped.** Neutral, verified not assumed: seed 1234, 20,000 ticks, the soak reproduces `f735cea` species for species including its single starvation death, and the LOD differential returns *known 223 / reg 1 / imp 2 / drift 0* — the same verdicts the unmodified build returns. With tables on: the herbivore total does not move, nothing starves, composition moves 30–90%, and the LOD differential loses about twenty verdicts at **any** table strength. Tables below |
| — | 2026-09-08 | Compute what the grazing economy does, to settle C2 | **food is about a hundredfold from binding herbivore numbers, and is the only thing driving migration.** A Deer needs 2.1 grazeable tiles, the herbivore ceiling would eat from under 1% of the map, and a herd of six balances a thirteen-tile region — so it cannot deplete a meadow. Derived from the registry at `35c12ec`, not from a run. Table below |
| — | 2026-09-08 | Size the swarm kin-count loop that F3 proposed replacing, before building the replacement | **0.0155 ms/tick** — 310 ms over 20,000 ticks across 12.0M iterations, seed 999. That is 0.56% of `HuntingSystem` and **0.07% of the tick**, measured with the timestamp overhead included, so the true figure is lower. F3 is not worth a per-cell census |
| `23ee09d` | 2026-09-08 | Search outward from the predator instead of sweeping the whole tracking range (F2) | seeds 1234 / 999, 20,000 ticks: `HuntingSystem` mean **6.42 → 3.27** and **6.56 → 2.75** ms/tick (−49%, −58%); −59% and −68% at the dense end; whole tick **32.9 → 20.0** and **34.4 → 22.1** ms (−39%, −36%). Behaviour changes: failing metrics 44 → 43, and invariants hold on 3 of 5 seeds against 4 of 5 — both failures the D11 grace-window assertion |
| `45488fa` | 2026-09-08 | Ask before spawning; fail instead of hanging (D14, D15) | `--max-pop=20000 --initial=16000`, which threw during seeding, now seeds 14,506, declines the rest and exits 3. LOD gate 226/0/0/0 — the guards never fire at the shipped cap |
| `3c5e2fc` | 2026-09-08 | Order `Eligibility` cheapest-gate-first and resolve the species registry once per candidate | seed 1234 / 999, 20,000 ticks: mean `HuntingSystem` **6.42 → 5.68** and **6.56 → 5.95** ms/tick (−11.6%, −9.4%), about −12% at the dense end. Behaviour identical — same final composition to the creature, and the LOD gate reports 226/0/0/0. **F1 was expected to be the largest single term and is not**: the curve is still superlinear in Sectid count |
| `6a723a7` | 2026-09-08 | Time each system inside the soak | the instrument. Overhead below the wall clock's resolution: 3,000 ticks, profiled against not, 33s vs 33s and 34s vs 33s |
| — | 2026-09-08 | Read the creature layer against the 60–120 minute target, to scope V1 | **the creature layer is about right; V1 is a faction-layer divergence.** Derived by reading `SpeciesRegistry` against `SurvivalSystems.HungerDecayScale` at `946d46f` — an analysis, not a run, so it carries no seed. Table below |
| — | 2026-09-08 | Try D2's first mechanism on its own: spend the elapsed interval as attack credit | **worse, and reverted.** Failing metrics in the differential **44 → 48**; 14 regressions against 5 improvements, 8 of the 14 in `nest_raid` where cooldowns are shortest. Several predation metrics flipped from the coarse tier under-killing to over-killing: `kills.Sectid` full 6 / minimal 15, `deaths_predation.Rabbit` full 4 / minimal 13. Granting a window's blows at the sampling instant denies the prey the escape it gets at Full, so the rate returns as a burst. D2's two mechanisms are not separable |
| `186a0fd` | 2026-09-08 | Re-record the LOD contract and gate at the tick count it was recorded at (fixes D12) | **the gate exits 0, and had never done so before.** 226 verdicts recorded, 44 `FAIL`, all four documented categories and nothing outside them; two consecutive runs report `known 226 / regression 0 / improvement 0 / drift 0`. The stale comparison was partly a tick-count mismatch: `Ticks` defaulted to 2,000 while the contract, the documented command and the runner's usage line used 3,000 |
| `6b798c0` | 2026-09-08 | Give species ids a deterministic hash (fixes D1) | **a fixed seed now produces a fixed run.** Seed 1234, 3,000 ticks, one binary: 18 consecutive runs were a 9/9 coin flip between two trajectories before, and 10/10 one trajectory after. `LodDifferential` reported *known 174 / reg 12 / imp 33 / drift 4* then *172 / 14 / 36 / 5* on two consecutive runs of one binary before, and identical counts on two consecutive runs after. The invariant gate's five-seed results are unchanged, cell for cell |
| `0a0ae4d` | 2026-09-08 | Set `PrimeCutShare` from a five-seed sweep rather than by choosing a number | Sectid mean across five seeds **195 → 1,007**, which is 88% of the pre-fix 1,145; invariant gate **2/5 → 4/5 seeds passing**, against 3/5 for the pre-fix build. Table below |
| `0b6c012` | 2026-09-08 | Optionally snapshot the world at both ends of a soak run (`--snapshot-world`) | seed 1234, 2,000 ticks: verdict and exit code (2) identical with and without the flag; the flagged run wrote two labelled map sets (`_t0`, `_t2000`), the unflagged run wrote none |
| `e5f5597` | 2026-09-08 | Take the killer's prime cut out of the corpse rather than adding to it (D8) | 20,000 ticks. Seed 1234: Sectid **1,058 → 293**, nests **179 → 51**, Sectid births **1,584 → 574** while Sectid deaths fell (predation 614 → 429, starvation 70 → 52); Shroomer 2,892 → 3,658; total kills 9,830 → 9,274; world deviation 0.0175 → 0.0137. Seed 999: Sectid **1,531 → 229**, nests **283 → 54**. The grace-window invariant went pass → fail on seed 1234; on seed 999 it failed **before** this change too (see D11) |
| — | 2026-09-08 | Measured both gates at `946d46f` before changing anything, to have a control | invariant gate: seed 1234 **all 11 pass**, seed 999 **1 fails** (2 anchors lost before t=6000) — the assertion is seed-dependent (D11). LOD differential: **known 189, regression 10, improvement 21, drift 3**, exit 1 — the recorded contract no longer matches the code (D12) |
| `7a7fb28` | 2026-09-06 | Frustum-cull entities before rendering | instances written per frame **10,401 → 243** |
| `26f1a63` | 2026-09-06 | Let Shroomers answer a raider parked on their ground | — |
| `5aa4ba7` | 2026-09-06 | Choose the mycelium heart count with `MinHeartSpacing` instead of letting it emerge | hearts **153 → 26** (seed 1234), **131 → 22** (seed 999) |
| `d93d747` | 2026-09-06 | Delete keeper crystal-to-crystal travel | no measurable change — a mobility mechanic serving a raid that never happens (see D4) |
| `c553833` | 2026-09-06 | Fix `Siege` zero-initialisation: every Sectid was born besieging entity 0 | Sectid `kills_made` **0 → 1,487** (seed 1234), **0 → 1,820** (seed 999) |
| `72ea4ba` | 2026-09-06 | Gate the LOD differential on the difference from recorded verdicts | the gate can now exit 0; before this it never had |

### The first LOD divergence curve — predator_prey, seed 42, 3,000 ticks

Two runs, one process each, pinned to `Full` and to `Minimal`, sampled every 5 ticks; the curve is
the comparison of the two recorded streams. The simulation is `bef5cb2` unchanged — the commit
carrying this entry adds the instrument and extracts a builder the two LOD harnesses now share, and
moves no simulation code.

Divergence averaged over each tenth of the run:

| through tick | 295 | 595 | 895 | 1195 | 1495 | 1795 | 2095 | 2395 | 2695 | 3000 |
|---|---|---|---|---|---|---|---|---|---|---|
| divergence | 0.081 | 0.161 | 0.166 | 0.166 | 0.184 | 0.204 | 0.211 | 0.222 | 0.219 | 0.184 |

It rises quickly and then holds. That shape is the reading: the cost of a coarser cadence on this
scenario is bounded rather than compounding. The largest value any single component reaches over the
whole run — centroid 0.694, hunger mean 0.276, count 0.179, dispersion 0.486, hunger spread 0.050,
age mean 0.014, age spread 0.007 — says where the two worlds actually differ: in where creatures
are and how hungry they are, hardly at all in how old they are.

Population over the run: both start at 45, the Full run ends at 40 and the Minimal run at 43. The
coarse tier keeps more prey alive, which is the direction D2 predicts.

**What the first recording found about the instrument.** Before centroid distance was bounded, the
scalar read mean 0.419 and final 0.278 on this same pair, with one tenth of the run averaging 0.9998
— because the centroid component reached 6.61 while every other component is at most 1. Combined by
an unweighted mean, an unbounded component does not contribute to the scalar, it becomes the scalar,
and the curve was a centroid plot wearing seven labels. Bounding it is what the figures above are
measured with.

**The verification.** Recorded twice on one build: both fingerprint streams and the curve are
byte-identical between passes. That is the check the instrument had to pass before its numbers meant
anything, and it passes because species ids became deterministic (D1) — the same test on the build
before that fix would not have.

**What this does not yet establish.** One scenario, one seed, one tier pair. No tolerance is set and
nothing gates on the curve; the exit code is still the differential's. Whether the plateau height is
acceptable, and what a worsening looks like, needs the curve recorded across scenarios and seeds.

### HuntingSystem cost against Sectid count — seeds 1234 and 999, 20,000 ticks, default cap

`MaxPopulation` 12,000. Two runs in parallel on a four-core machine in both the before and the
after set, so the contention is the same on both sides; ms/tick is the mean over each 200-tick
sample interval.

| | | before | | after `3c5e2fc` | |
|---|---|---|---|---|---|
| tick | sectids | hunting ms | share of tick | hunting ms | share of tick |
| 4,000 | 385 | 1.70 | 9.4% | 1.69 | 9.1% |
| 8,000 | 669 | 4.47 | 17.0% | 4.36 | 15.8% |
| 12,000 | 1,017 | 8.87 | 21.6% | 7.90 | 19.8% |
| 16,000 | 1,020 | 10.16 | 21.5% | 8.91 | 20.0% |
| 20,000 | 866 | 10.40 | 21.7% | 9.14 | 19.9% |

Seed 1234 above; seed 999 runs 1.53 → 13.18 ms across the same span and ends at 1,445 Sectids.

The shape is the finding. Between t=4,000 and t=20,000 the creature count rises 1.6× and hunting
cost rises **6.1×**; cost *per Sectid* rises from 4.4 to 12.0 µs over the same span, so it is
superlinear in the population that drives it and not merely in the world's. At ~1,020 Sectids
hunting is 8.9–11.6 ms/tick, which reproduces the ~10 ms reported from the interactive build at
~1,100 Sectids.

### D18 named: a structural zero, a NaN, and a loop with only float exits

Reproduced on the standard world, seed 1234, by reinstating the pathological guard behind nothing
but a local edit and instrumenting the two readings that could falsify the hypothesis. The result
confirms half of it and refutes the other half cleanly.

**What the instrument said.** Per sample: entities with a non-finite position, velocity or hunger;
the fullest spatial-hash cell; the mean size of a neighbour query.

| | healthy build | pathological build |
|---|---|---|
| non-finite entities | 0 through t=600 | **first at t=25** |
| fullest cell | 12–16 | **16 at the stall** |
| mean query size | 3.5–4.4 | **4.1 at the stall** |
| progress | 600 ticks in 3s | tick 26 unfinished after 280s |

**The spatial-hash half of the hypothesis is refuted.** Buckets and query sizes are normal at the
moment everything stops. Nothing degrades gradually; one tick simply never ends.

**The chain, each link measured rather than argued.** A watchdog that printed each system before
entering it named `WanderSystem` as the one the tick enters and never leaves. The first bad entity
is a **generation-1 Boar with NaN velocity and entirely finite hunger** — which also refutes the
hunger route the hypothesis proposed. Logging which traits the guard zeroed named the source:

1. A solitary-spawned parent carries zeroes in the four social traits, because the factory writes
   them that way. The guard read "the parent's value is zero" as "the parent does not express this"
   and copied the zeroes into a child assembled as a herd animal, which does have a `Social`
   component — so `PreferredGroupSize` became 0.
2. `HerdingSystem` computes `1 − (groupSize − preferred) / (preferred × 0.5)`. With both zero that
   is `0/0`, and `MathF.Max` propagates the NaN rather than discarding it.
3. The NaN cohesion factor reaches velocity.
4. `DecisionCadence.Horizon` reads velocity: `sqrt(NaN)` is NaN, `speed <= 0f` is false for NaN, and
   `MathF.Max`/`Math.Clamp` propagate — so the wander look-ahead is NaN.
5. `WanderSystem.WorstAversionAlong` was `for (float d = step; ; d += step)` with exits
   `worst >= 1f` and `d >= lookAhead`. Every comparison against NaN is false, so neither ever fires.

That is the two orders of magnitude: not a slowdown at all, but a single tick that does not
terminate. It also explains why the cost was the whole tick rather than births alone, and why it
bisected to a line that is correct in isolation.

**Three fixes, in order of how much they matter.** The presence mask makes the ambiguity
unwritable — absence is a bit, and there is no value-based test left to reach for. The look-ahead
loop is now step-bounded, so a bad value upstream produces a wrong steering decision rather than a
hang. The herding divide is guarded. A fourth, `SpatialHash.QueryRadius`, now refuses a non-finite
or absurd radius loudly instead of sweeping billions of cells — it was not this bug's path, but it
is the same class of failure one call away.

**And the ambiguity was live in shipped code, not only in the pathological guard.** `Genome.Blend`
mixed slot by slot, and `SpawnSectid` creates no `Reproduction` component while the factory that
seeds the founder Sectids does. So every delivery from a nest-born courier blended a zero into six
reproduction traits of the nest template and dragged them toward zero for as long as the colony ran.
It was inert — nest-born larvae have no `Reproduction` component for `ApplyTo` to write into — but it
was wrong, and it is exactly what the mask prevents. `SpawnShroomer` is missing `Age` and
`Reproduction` the same way.

**Verification of the fix.** Five seeds, 20,000 ticks: invariants hold on 4 of 5, the fifth being
the D11 grace-window assertion on a different seed than last time — which is what D11 says to
expect. The drift signal is unchanged in kind and magnitude: seed 1234 still reaches 21 generations
of Fish with `ReproHungerThreshold` down 8.9%. Health at full population is clean — no non-finite
entities, fullest cell 42, mean query 10.4.

The LOD differential moves, and that is a finding rather than a failure: *known 173 / regression 31
/ improvement 17 / drift 17* against 178 / 30 / 16 / 20 before. The mask is not purely structural
because one path really did change. Some founders are spawned force-solitary, so their genomes
carried four zeroed social traits; the old code regressed those zeroes toward the species value and
wrote them into herd-born children, which therefore started at about a tenth of their species'
cohesion. With the mask those traits are simply absent and the child keeps the values the factory
rolled for it. That is the intended behaviour and it was never intended to be inherited.

### Where selection takes a trait when descent is switched on

Seeds 1234, 999, 4242, 31337 and 8675309, 20,000 ticks each, standard world. Drift is the change in
a species' population mean between t=0 and the end, as a fraction of that species' own value, so
traits measured in ticks and in tiles read on one scale. Full tables per seed in
`logs/population_soak/trait_drift_seed*.csv`.

**Lineages are real and their depth varies by an order of magnitude.** Mean generations reached:

| | Fish | Penguin | Lizard | Parrot | Rabbit | Wolf | Sectid | Shroomer | Deer | Musk Ox | Polar Bear |
|---|---|---|---|---|---|---|---|---|---|---|---|
| generations | 21.1 | 13.2 | 10.0 | 6.9 | 6.0 | 3.8 | 3.8 | 2.5 | 2.9 | 1.9 | 1.7 |

**The clearest signal is the one this project has removed twice.** `ReproHungerThreshold` — how fed
an animal must be before it may breed — falls in 19 of 24 species, mean −3.1%, and it is the
tightest non-social trait in the set (spread 4.4 against 18–20 for the social ones). It scales with
generation count, which is what separates selection from noise:

| species | generations | ReproHungerThreshold | MaturityAge |
|---|---|---|---|
| Fish | 21.1 | **−13.4%** | −4.2% |
| Penguin | 13.2 | **−9.8%** | −3.7% |
| Arctic Fox | 4.6 | −9.1% | −2.9% |
| Fox | 4.6 | −7.1% | +0.9% |
| Rabbit | 6.0 | −5.6% | −4.8% |
| Wolf | 3.8 | −4.1% | −1.1% |
| Sectid | 3.8 | +0.2% | −0.5% |
| Shroomer | 2.5 | +0.1% | −1.6% |
| Polar Bear | 1.7 | +2.4% | −0.3% |

Breed sooner, at a lower bar. The leash bounds how far it can go — no lineage may leave a band
around its species value — but the direction is unambiguous and it is what the trade-off matrix has
to be designed against. The two faction species that breed through a nest and a spore rather than
by pairing barely move, which is consistent: their selection acts on the larder and the substrate,
not on a breeding threshold.

**Metabolism drifts, and the capacity model does not know.** `HungerDecayRate` runs −13.5% to
+12.8% across species and `MaxHunger` −6.6% to +12.4%. `HabitatCapacity` prices an animal at
`GrazeConsumeRate × BreakEvenFullness`, both species constants and neither heritable, so the model
is not wrong today — but the payout an animal actually receives is computed from its own decay rate,
which is drifting. Whether capacity should read a live mean is now a question with numbers attached.

**The social traits are noise at this length.** `PreferredGroupSize`, `GroupAffinity`,
`CohesionStrength` and `AlignmentStrength` all show means near +5% with a spread near 20 — the mean
is a handful of small populations, not a signal. Read them again over longer runs before touching a
rate.

**Two artefacts and one pathology, all found by measuring.** A trait must be counted only over
animals carrying the component that holds it, or an absent value averages in as zero and reads as a
45% collapse. A grower's body size belongs to `GrowthSystem`, so reading it as a trait had a spore
inherit its parent's *grown* size. And a shoal's descendants acquired **89% of a grazing pressure
their founders never had** over 19 generations, purely from regression pulling an unexpressed trait
toward a species value — fixed by writing grazing pressure only for a species that grazes. The
general form of that fix cost two orders of magnitude of tick rate for reasons not established, and
is filed as D18.

### Making hunger track the ground

Seeds 1234 and 999, 20,000 ticks, against the `4bca4f7` build on the same seeds.

**What was wrong, in one number.** A deer's payout was flat for any tile holding more than 0.11
nutrition, because it scaled by `consumed ÷ requested` rather than by how full the tile was. Its
`MinAcceptableNutrition` is 0.28 and the move-on pressure starts at 0.30, so it was steered off
ground long before the payout could fall. The same held for every species. Ground quality decided
where animals went and never what it was worth to be there.

| | `4bca4f7` | after |
|---|---|---|
| starvation deaths | 0 / 0 | **32 / 34** |
| average hunger, across species | 84–96% | **49–94%** |
| species lost | none | none |
| herbivores at t=20,000 | 3,958 / 3,779 | 3,177 / 2,776 |
| world nutrition fill | 92.0% | 93.8% / 94.6% |
| invariants | 10 of 11 (D11) · 11 of 11 | **11 of 11 · 11 of 11** |
| class ceiling | never reached | never reached |

**Hunger now ranks species by the ground they hold, and the ranking reproduces.** Worst fed on both
seeds are Tapir (49% / 57%), Turtle (64% / 59%) and Fish (64% / 51%) — the species on scarce or
contested habitat. Best fed are Camel (93% / 92%), Musk Ox (88% / 94%) and Boar (91% / 90%), which
hold ground nothing else wants. That ordering is the design working: a species thrives where its
ground supports it.

**The calibration took three passes, and the first two are the useful record.** With the food model
alone, populations declined all run: hunger had become live but `ReproHungerThreshold` was still set
at 83–100% of maximum, a threshold that only made sense when every animal sat near full, so almost
nothing bred. Lowering it to 65% turned the decline into a plateau. The second pass lost Parrot
entirely on seed 1234 — a flat ×5 on hunger drain gave the smallest-bellied species a 1,444-tick
starvation time, faster than it could find new ground. Banding every ground feeder into 2,500–8,000
ticks brought it back at 231.

**Total grazing pressure did not rise, and cannot be raised this way.** Consumption per animal is
0.0166/tick against 0.0174 before, and the world sits at 94% full either way. At equilibrium an
animal's draw is its hunger drain divided by how efficiently it converts ground into food, so
tripling what it strips per feeding tick cuts the time it spends feeding by the same factor. The
knob that does move it is efficiency — `BreakEvenFullness`, the ground fullness a species needs to
hold condition. Raising it wears the world harder and starves more animals; the two are the same
trade, and it sits with C5's density as one decision rather than two.

### What the terrain-derived capacity does

Seeds 1234 and 999, 20,000 ticks, `--chunks=36 --max-pop=12000 --initial=2000 --sample=200`,
against the `f735cea` control on the same seeds. Three builds: the control, the capacity brake
alone, and the brake with forage tables and rebalanced extraction rates.

| | prey base reached its class ceiling | herbivore birth refusals | herbivore total | invariants |
|---|---|---|---|---|
| control `f735cea` | t=2400 — 12% of the run | 1,813,206 | 5,036 / 5,040 | 11 of 11 · 10 of 11 (D11) |
| brake only, rates unchanged | t=2400 | 1,813,206 | 5,038 | 11 of 11 |
| **brake, tables, rebalanced rates** | **never — ecology bound first** | **0** | 3,958 / 3,779 | 10 of 11 (D11) · 11 of 11 |

**The refusal count is the result.** 1,813,206 refused herbivore births in a 20,000-tick run is the
engine choosing the world's contents about ninety times a tick, and it is now zero. The prey base
rises to roughly 4,100 by t=6000 and holds between 3,900 and 4,200 for the rest of the run, against
a class allowance of 5,040 that is never touched.

**Composition stops being one species.** Fish held 74% and 66% of the herbivore class in the two
controls and holds 30% and 29% now. Lizard was extinct in both controls — 0 individuals at t=20,000
— and ends at 109 and 219. Every rare species recovers on both seeds: seed 1234 gives Rabbit
4 → 501, Parrot 2 → 70, Monkey 26 → 246, Frog 21 → 227, Camel 41 → 272, and seed 999 the same
directions. Deer and Fish, the two that were over their computed capacity, fall to it: Deer 471 → 153
against a capacity of 165, Fish 3,737 → 1,179 against 1,195.

**The direction reproduces across seeds**, which the forage tables alone never did. The mechanism is
a limit rather than a reweighting, so the same species win and lose on both.

**The brake alone does almost nothing**, which is the control that matters: with rates unchanged the
capacities are about five times the class allowance, so the ceiling is still reached at t=2400 and
the refusal count is unchanged. It is the rebalanced extraction that moves the binding constraint;
the brake is what catches it when it moves.

**Nothing starves, and the world is still nearly full of food.** Starvation deaths are 0 or 1 in
every run. `--nutrition-log=500` on seed 1234 to t=8000: the world holds 92.0% of its total
nutrition capacity at equilibrium, consuming 71.2/tick against 70.0/tick regenerated. So the
population is limited by the capacity model, not by running out of food — the model is a design
choice about how densely a niche should be packed, and it says so.

**The gap the density number is hiding.** The capacity model prices an animal at the rate it strips
while feeding. Measured, ~4,100 herbivores consume 71.2/tick, which is 0.017 each against a nominal
rate of 0.065 — animals are actually feeding about a quarter of the time. So the occupancy fraction
absorbs a duty cycle as well as a margin, and is part ecology and part fudge until D17 is fixed.

**What D17 costs, in numbers.** Grazers draw from the ground whether or not they are hungry. What
the same population *needs* to stay fed is about 8/tick against the 71.2/tick it takes, so about
seven eighths of everything grazers strip from the world is destroyed rather than eaten. That is
also why the earlier "food is a hundredfold from binding" figure was wrong: it computed the 8, and
the world is drawn down by the 71.

**The LOD differential cannot adjudicate this change.** It returns known 178 / regression 24 /
improvement 14 / drift 10, against 223 / 1 / 2 / 0 for the control — the same order of movement the
forage tables alone produced at any strength, which is D16. The contract has not been re-recorded:
re-recording would bury 24 regressions that nothing has established are harmless, and leaving it red
loses nothing, since it was already red at `f735cea`.

### What the forage yield does when it is switched on

Seeds 1234 and 999, 20,000 ticks, `--chunks=36 --max-pop=12000 --initial=2000 --sample=200`,
against the `f735cea` control on the same seeds. Three table variants were built and measured; none
of them ships.

| | herbivore total | starvation deaths | invariant gate | LOD differential |
|---|---|---|---|---|
| control `f735cea` | 5036 / 5040 | 1 / 0 | 11 of 11 · 10 of 11 (D11) | known 223, reg 1, imp 2 |
| mechanism, no tables | 5036 | 1 | 11 of 11 | known 223, reg 1, imp 2 |
| tables, yields to 0.2 | 5039 / 5034 | 0 / 0 | 11 of 11 · 10 of 11 (D11) | not run |
| tables, yields to 0.45 | 5040 / 5039 | 1 / 0 | 11 of 11 · 11 of 11 | known 188, **reg 23**, imp 13, drift 4 |
| the same, threshold on raw fertility | 5039 / 5038 | 0 / 0 | 10 of 11 (D11) · 11 of 11 | not run |
| tables, yields to 0.75 | not run | — | — | known 193, **reg 22**, imp 9, drift 4 |

**The herbivore total never moves.** It sits on its class ceiling in all ten runs, under a change
that makes forage strictly harder to get. That is C2's finding surviving a direct attempt to break
it, and it is the strongest single result here.

**Nothing starves,** in any run, under any setting. The largest starvation count in the whole set is
one animal.

**Composition moves a great deal, and mostly not reproducibly.** At yields down to 0.45, seed 1234
gives Deer −38%, Musk Ox −35%, Boar −31%; seed 999 gives Deer −41%, Elk +41%, Musk Ox +38%,
Tapir −49%. Only Deer moves the same way on both seeds. At yields down to 0.2 the same shape gives
Boar 127 → 10, Turtle 32 → 10 and Frog 27 → **0** — a species lost on one seed, which is what ruled
that strength out.

**The route is reproduction, not hunger.** Average hunger falls for the species that lose (Deer
85.7% → 80.8%, Rabbit 97.0% → 85.7% on seed 1234) and `ReproHungerThreshold` is high — a Deer must
be near-full to breed — so a species that eats slightly worse breeds much less and loses share of an
allowance it competes for. None of that is visible as death.

**One species already holds most of the herbivore class**: 74% and 66% of the herbivore total on the
two control seeds, 80% and 63% with tables on. Any forage change is therefore mostly a re-division
of one species' share, which is why the composition numbers above are large and unstable, and why
the tables cannot be judged on them (V8 in [`implementation/README.md`](implementation/README.md)).

**`MinAcceptableNutrition` reads as worth, not depletion.** Comparing it against raw fertility
instead of the yielded value was built and measured: it made the composition swing worse on one seed
(Deer −47% against −38%) and left species standing on ground that does not feed them. The shipped
code compares the effective value.

**The LOD cost does not scale with the size of the change.** Yields down to 0.45 cost 23
regressions; yields down to 0.75 — a range of 1.33× between a species' best and worst ground, small
enough to barely move the numbers — cost 22. The regressions cluster on `nutrition.consumed`,
`nutrition.regenerated`, and on Deer's population, births and spacing. Filed as D16: the contract
cannot separate "LOD fidelity got worse" from "the trajectory moved", so it fails any change to the
food economy without saying whether the change was harmful.

### The LOD contract, measured at each step

3,000 ticks, the documented gating command, on an unmodified checkout each time.

| build | known | regression | improvement | drift |
|---|---|---|---|---|
| `f735cea` — before any of this session's work | 223 | 1 | 2 | 0 |
| `6e4dd46` — forage mechanism, no tables | 223 | 1 | 2 | 0 |
| forage tables at either strength (not shipped) | 188 / 193 | 23 / 22 | 13 / 9 | 4 / 4 |
| `4bca4f7` — terrain-derived capacity | 178 | 24 | 14 | 10 |
| `c41212b` — hunger tracks the ground | **176** | **32** | **14** | **20** |
| `f3ea31c` — heredity | 178 | 30 | 16 | 20 |

The contract has not been re-recorded at any point. Drift is metrics entering or leaving the
minimum-count filter as species populations move, which is most of what the last rows are. None of
it is evidence about LOD fidelity either way, which is the whole of D16.

Heredity moved two verdicts, which is the expected answer: the differential compares two tiers of
one build, and descent applies identically at both. Stochastic drift should make it noisier over
time, and that is worth knowing before it is later mistaken for a regression.

### The LOD contract was already stale at `f735cea`

Measured as a control before anything was changed: an unmodified checkout returns *known 223,
regression 1, improvement 2, drift 0* and exits 1. The three moved verdicts are all in
`freshwater_pond` (`spacing.Deer` regressed, `pop.Fish` and `pop.total` improved) and date from
`23ee09d`, which changed behaviour and was never re-recorded. A measurement that reads a non-zero
LOD result as caused by the change under test is reading this instead.

### The grazing economy, read from the registry at `35c12ec`

An analysis, not a run, so there is no seed to stamp. It exists because C2 asked whether food should
bind herbivore numbers and the question had never been answered arithmetically.

Hunger drains at `HungerDecayRate × SurvivalSystems.HungerDecayScale` (0.3). Grazing draws
`GrazeConsumeRate` from the tile and pays `GrazeNutrition` in hunger, so the exchange rate is the
ratio of the two. A tile regrows at `Chunk.RegenerationRate` 0.0005 per tick, and there is no hidden
loss in the sweep: `TileRegenerationSystem` visits each chunk every `RegenInterval × RegenSweepPasses`
= 32 ticks and regenerates 32 ticks' worth.

| | Deer | Rabbit |
|---|---|---|
| hunger drain per tick | 0.05 × 0.3 = 0.015 | 0.08 × 0.3 = 0.024 |
| hunger per unit of nutrition | 0.5 / 0.035 = 14.3 | 0.3 / 0.012 = 25 |
| sustained demand, nutrition per tick | **0.00105** | **0.00096** |
| grazeable tiles to sustain one animal | **2.1** | **1.9** |

**As a population limiter it is roughly a hundredfold from binding.** The herbivore ceiling of 5,040
at the shipped cap would be fed by about 10,600 tiles at the Deer rate, against 1,152² = 1,327,104
tiles in a 36-chunk world — **0.8% of the map** — and `IsGrazeable` covers eleven terrain types,
Wetland and Bog among them.

**As a driver of migration it is the only thing the economy does, and the numbers say why it does it
badly.** On a lush tile (`NutritionCap` 1.0 — caps run 0.2 to 1.0 by biome) a grazing Deer strips
the tile in 1.0 / 0.035 ≈ 29 ticks and the tile takes 1.0 / 0.0005 = 2,000 ticks to come back. But a
herd of six sustains itself on 0.0063 nutrition per tick, which balances about thirteen tiles. A
herd therefore cannot deplete a meadow: it strips what it stands on, steps a few tiles, and
everything has regrown before it returns. That is why herd movement reads as shuffling rather than
as a response to the land.

At twenty times the demand a herd of six would balance ~250 tiles and a herd of sixty ~2,500, which
is a meadow. That factor is a computed target and nobody has played it.

Three corrections to the analysis as it was handed over, found while checking it against source:

- `BreedingTiles` is set on **two** species, Otter and Penguin, not one. `BreedingNutritionSensitivity`
  is indeed set on exactly one, Fish, at 1.0.
- Wolf `SpawnWeight` is 1.5 against 0.3–0.4 for the apex predators, so it is seeded **3.75× to 5×**
  denser, not four to five.
- The strip and recovery times above are per biome, not global: on Arid or Tundra soil
  (`NutritionCap` 0.2) the same Deer strips a tile in about six ticks and it returns in 400.

Fish has both the shortest `ReproCooldown` (300) and the shortest `MaturityAge` (600) in the roster,
which is the competitive-exclusion claim the design layer now rests on.

### Where HuntingSystem's cost actually was

Three candidate terms were read out of the code before any of them was changed. Measured, they are
not close to equal.

| term | what it was | measured |
|---|---|---|
| F1 — expensive gate first | a world tile resolved per candidate before the flag test that rejects most | −10% of hunting |
| F2 — an unbounded tracking sweep | 80 tiles queried and fully scanned to keep one entity, with a cap that counted the wrong thing | **−49% to −58%** |
| F3 — the per-entity kin count | every swarm member walking nearly the list its neighbours walk | **0.07% of the tick** |

F1 was expected to be the largest single term and was worth a tenth. F3 was expected to be worth
replacing with a per-cell census and is worth 0.0155 ms/tick — the census would have cost a build
pass, a query per hunter and a deliberate approximation feeding `Pow(packSize, exponent)` into the
mass gate, to buy back seven hundredths of one per cent. The whole of it was in F2, and the reason
is not density but a bound that never bound: the cap counted successive nearest-so-far records
rather than candidates examined, and records grow logarithmically.

### `MaxPopulation` above about 14,000 cannot run — an entity-array ceiling, not a cost ceiling

`EntityManager.MaxEntities` is 16,384 and is a hard array bound. At `MaxPopulation` 20,000 the class
budgets alone come to 8,400 + 2,600 + 6,600 = **17,600 creatures**, before a single corpse, spore or
structure. `CarrionSystem.SpawnCorpse` is the only spawn path that checks the ceiling; `NestSystem`
and the spore and crystal paths call `CreateEntity` unguarded, so the run throws
`InvalidOperationException: Maximum entity count reached` (D14).

Measured, not inferred: two soaks at `--max-pop=20000` threw at t≈17,000 and t≈19,800 on seeds 1234
and 999. Both then **hung rather than failing** — `Run()` throws out of `_Ready()`, so
`GetTree().Quit()` never runs and the process idles at 0% CPU indefinitely (D15). They sat for forty
minutes before being killed.

This bears on how the ~13,000-creature "usable ceiling" is read. 16,384 entities less the corpses,
spores and structures a running world carries leaves creature headroom of roughly that size, so the
observed ceiling is at least partly the array bound rather than the cost curve. The cost curve is
real and is measured above; the two explanations are not exclusive, and the ceiling should not be
attributed to cost without raising `MaxEntities` and re-measuring.

### The creature layer against a 90-minute run — read from the registry at `946d46f`

Derived by reading `SpeciesRegistry` against `SurvivalSystems.HungerDecayScale`, not measured from a
run, so there is no seed to stamp. It exists because V1 asserted that *every* rate is set for
balance-run length, and that is not true of this layer.

| species | full → starving | lifespan | feeding cycles in 90 min | generations in 90 min |
|---|---|---|---|---|
| Rabbit | 6.2 min | 12.5 min | 14.4 | 7.2 |
| Fish | 5.6 min | 8.3 min | 16.2 | 10.8 |
| Sectid | 9.2 min | 20.8 min | 9.8 | 4.3 |
| Deer | 13.3 min | 25.0 min | 6.8 | 3.6 |
| Wolf | 14.6 min | 20.0 min | 6.2 | 4.5 |
| Shroomer | 14.6 min | 41.7 min | 6.2 | 2.2 |
| Bear | 24.3 min | 33.3 min | 3.7 | 2.7 |
| Faeling | — | 50.0 min | — | 1.8 |

Several generations of the shorter-lived species inside a target-length run is what the progression
layer asks for, and it is what these numbers give. The mis-tuning V1 describes is in the faction
layer, which misses in two opposite directions at once — combat far too fast, terraforming possibly
too slow to read.

One incidental finding, kept because it is the reason to distrust the archived reference rather
than because the number matters: the hunger scale is `0.3` in `SurvivalSystems`, not the `0.6` the
archived documentation claimed. That was audit finding 01, and it is still the clearest single
argument for reading the code rather than the archive.

### The prime-cut sweep — 20 runs, 20,000 ticks each, seeds 1234 / 999 / 4242 / 31337 / 8675309

Final Sectid population, and whether the whole-game invariant gate passed. `PrimeCutShare` is the
fraction of a body the killer eats on the kill; the corpse holds the rest, and the Sectid colony
economy is what converts that rest into larvae.

| seed | pre-fix `946d46f` | 0.60 | 0.30 | **0.15** |
|---|---|---|---|---|
| 1234 | 1,058 pass | 293 fail | 735 pass | 866 pass |
| 999 | 1,531 fail | 229 fail | 531 pass | 1,445 pass |
| 4242 | 1,073 pass | 237 pass | 589 pass | 1,033 pass |
| 31337 | 1,050 pass | 105 fail | 561 pass | 740 pass |
| 8675309 | 1,011 fail | 110 pass | 680 fail | 951 fail |
| **gate** | **3/5** | **2/5** | **4/5** | **4/5** |
| **mean Sectid** | **1,145** | **195** | **619** | **1,007** |

Two things the sweep settles beyond the value itself.

**Each cell was one run, and at the time one run was a coin flip (D1).** Re-running a single binary
on seed 1234 eighteen times produced exactly two trajectories, nine times each — at seed 1234 that
is Sectid 866 against 933, about 8%. That defect is now fixed, and re-running the whole 0.15 column
against the deterministic build reproduced **every cell exactly**: 866, 1,445, 1,033, 740, 951, the
same anchor counts and the same 4/5 pass rate. The ids happen to select the trajectory those runs
had already landed on, so the column stands as measured rather than merely surviving its error bar.

An earlier draft of this entry blamed the working copy and recorded it as D13. That was wrong: the
first samples happened to land the same way several times in a row, which looked like per-checkout
determinism and was not. D13 is withdrawn and the evidence sits under the species registry.

The pass/fail column is still a count over seeds and not a verdict — D11 is untouched by any of
this, and the assertion still disagrees between seeds of one build.

**The grace-window assertion does not respond monotonically to this parameter** (D11). Seed 8675309
fails at 0.15, 0.30 and pre-fix but passes at 0.60; seed 1234 does the reverse. A single seed
flipping across a change is therefore not evidence about that change, and the pass counts above
are the only honest way to read this gate.

---

## 2026-08 — structures, budgets, gates

The three changes marked ⚠ each met their own acceptance criteria and together broke the game:
within about a minute of world start, Faelings destroyed essentially every Sectid nest and mycelium
heart on the map (**44 of 46 nests and 21 colonies in 6,000 ticks, first loss at t=951**). Nothing
in the project could have caught it, which is why the whole-game invariant gate exists.

| Commit | Date | Change | Result |
|---|---|---|---|
| `ff4248f` | 2026-08-29 | Diagnose Sectid zero-kills: every Sectid born besieging entity 0 | measurement that produced `c553833` |
| `405ad48` | 2026-08-29 | Add per-faction population floors to the invariant gate | — |
| `8ef94ce` | 2026-08-28 | Make `SiegeSystem` steer rather than assign velocity; make idle sieging opt-in | fixed besiegers drifting straight through terrain they should avoid |
| `b10e992` | 2026-08-26 | Add a whole-game invariant gate, and fix what it caught | gate went red, named a faction, cause was one struct field |
| `31b689f` ⚠ | 2026-08-26 | Make faction structures destructible objectives | before this, **no entity in the game could damage a nest or a crystal** |
| `14e9f08` ⚠ | 2026-08-26 | Split the population cap into per-class budgets | 20,000 ticks, seed 1234: composition **89.7% herbivore / 4.1% predator → 45.4% / 14.1% / 35.6% faction**; herbivore:predator **21.8:1 → 3.2:1**; Faelings **8 → 99–128**; no class exceeded its ceiling at any sample; factions traded places inside a shared ceiling instead of one ratcheting upward |
| `93818ea` ⚠ | 2026-08-25 | Enforce the LOD rate rule with a test, and fix what it found | — |
| `a35639d` | 2026-08-25 | Add documentation audit | **31 doc-vs-code discrepancies**: 10 critical, 10 high, 11 housekeeping, across 8 of 14 files |

**Measured 2026-08-29, four 20,000-tick runs, seeds 1234 and 999** (with and without keeper travel):
**zero** structure damage and zero structure destruction attributed to any Faeling, in 80,000
keeper-ticks. Every structure lost died to Sectids or to the environment. Cause not established;
see D4 in [implementation/factions.md](implementation/factions.md).

---

## 2026-08 (earlier) — LOD correctness, the food web, observation

| Commit | Date | Change | Result |
|---|---|---|---|
| `dabca20` | 2026-08-13 | Keep creatures in their element at every LOD tier; size-aware reef | reef speed now scales with body radius: small species pass freely, large ones drop toward a floor — a reef is partial refuge, not a wall |
| `3486b40` | 2026-08-13 | Fix LOD growth multiplication; report population against what the cap limits | Shroomer growth had been running **10× faster at Low tier, 20× at Minimal** — compensation applied without gating |
| `1a78b2b` | 2026-08-12 | **LOD gates decisions, not motion** | 5,000 creatures, 512-tile world: whole tick **12.5 → 11.4 ms** with Movement at 1.5 ms while running for every entity every tick. Before: a Low-tier creature covered **13%** of the ground a Full-tier one did over the same wall clock, Minimal **6%** |
| — | — | Update phase staggering | peak-to-median tick cost **2.30× → 1.53×** |
| `fc0a784` | 2026-08-12 | Reef counts as water for non-swimmers; mass decides who yields in a collision | before: a fox shunted a turtle as easily as the reverse, and nests were pushed across the map |
| `14ef4e2` | 2026-08-12 | Fix unhittable bulky targets, the Sectid water leak, and thorn scaling | mature bloom (scale 2.5): a 4-wolf pack goes from **52 ticks at 35% HP loss** to **671 ticks at 45%**; lone bears, jaguars and boar trios break off; an 8-Sectid swarm still kills it in **40 ticks** |
| `f68049c` | 2026-08-12 | Terrain escape heads for the nearest shore; runs become reproducible | before, all eight escape rays tied in open water and the tie broke by iteration order — every animal in a lake fled due east |
| `bd2102a` | 2026-08-12 | Score prey by payoff, so predators pick meals not neighbours | bears **39% → 73%** big-game targeting on a stacked test. ⚠ **This figure predates deterministic RNG and has not been re-measured since; treat it as indicative only.** |
| `a8441cb` | 2026-08-12 | Unify prey eligibility across hunting and tracking; otter survivability | ended the fox-pile-up on unkillable turtles |
| `84a75a9` | 2026-08-11 | Food-web pass: unreachable prey, prey tiers, water fertility, otters | water carries fertility; deep water becomes a genuine refuge |
| `71a48ca` | 2026-08-11 | Fix aquatic biome: element-aware discomfort, edge barrier, penguin haul-out | ended sharks and fish permanently "escaping" their own feeding grounds |
| `fb01b34` | 2026-08-10 | Fix locked-in escape, missing newborn components, pack leadership cycles | — |
| `6099c50` | 2026-08-10 | Observation tools: click-to-inspect, species highlight/jump, free camera | — |
| `16abb9d` | 2026-08-09 | Shroomer defence, nutrition economy, penguin predation | **54% faster tick** |
| `8660a1f` | 2026-08-07 | Faction tuning: habitat steering, Sectid colony shape, mass-scaled kill food | — |
| `14f0a0b` | 2026-08-06 | Fix spatial-hash radius queries returning whole cells | found by the test-scene harness |

---

## 2026-07 — test harness, faction balance, worldgen defaults

| Commit | Date | Change | Result |
|---|---|---|---|
| `a28a2c1` | 2026-07-13 | Test-scene harness: scenario-defined worlds, exact spawns, extended logging | the answer to "full-world balance runs have hit their limit" |
| `0a70801` | 2026-07-13 | Pre-branch fixes: spore-true logging, area attack reach reduced, real terraform rates | faction terraforming had been set so low it barely marked the map — the strength value is a probability per roll, not an amount |
| `fa45f3d` · `27f6030` | 2026-07-13 | Faeling keeper redesign — anti-dominance balancer | the keeper's current role |
| `80bb6f5` | 2026-07-13 | Reframe the project as a game in development, not ecosystem-sim research | — |
| `9ff6e29` | 2026-07-10 | Shroomers rely on and impact land fertility (user design directive) | removed the "cannot starve on wet tiles" rule that was the root of the historical monoculture |
| `d6126e6` | 2026-07-10 | Relax Shroomer crowding so blooms can form (Sectids had starved) | — |
| `a2af072` | 2026-07-10 | Record a no-faction baseline | reframed predator collapse as **faction-driven**, not an intrinsic predator problem |
| `494386f` | 2026-07-10 | Shroomer self-limiting: crowding, drought, spread caps | drying a bloom's ground now kills it |
| `2f95302` | 2026-07-10 | Anti-bloom pass: fungivore trait + Sectid retargeting | — |
| `51fbdc0` | 2026-07-10 | Fix `ExclusivePrey` leaking through the defensive counter-attack path | a fish-only specialist had been hunting down its own predators |
| `8da9d5c` · `d9c335a` · `e3de145` | 2026-07-08/09 | Live worldgen preview tool over the real pipeline; tuned defaults locked in | preview calls the same per-tile function generation loops over, so it cannot drift |
| `8dc9c59` | 2026-07-07 | Moisture field in scale with the world: exported frequency + contrast | the Arid/Bog extremes raw FBM starves now actually occur |
| `87a10fd` | 2026-07-07 | Snapshot set: elevation / moisture / temperature maps + spawn-point map | — |
| `4a1dba4` | 2026-07-07 | Biome-aware shores, river meanders, sharper cliffs | shores became a distance post-pass, so beaches stay narrow on flat worlds |
| `b15e862` | 2026-07-06 | **Two-way biome generation**: hydrology and relief feed back into climate | riparian corridors, delta fans, slope drainage |
| `0a079c8` | 2026-07-06 | Terrain variety: ridged mountain ranges + terraced cliff regions | — |
| `f536ef1` | 2026-07-06 | Fix rivers dying at the shore: mark hydrology down to the waterline | ended the long-standing "rivers end before the ocean" bug |
| `339c162` · `a1dc25b` | 2026-07-06 | Penguin: feed from water tiles; root cause was terrain discomfort driving them out of the water they feed in | — |
| `b02045d` | 2026-07-06 | Behaviour arbitration: lift hardcoded drive-priority thresholds to per-species parameters | — |
| `d578aa1` | 2026-07-05 | Fix drowning/suffocation (beached sharks were near-immortal at coarse LOD); keep hunters in their element | — |

**Scenario results recorded in this period** (each reproducible from its scenario file):

- `heart_drying`, 8,000 ticks — a ring of drying nests takes the wet fraction **0.46 → 0.44 → 0.38
  → 0.36 → 0.33**, crossing the floor at t≈4,800; the heart dies at **t=5,280**, every hit
  attributed to `environment`, while the Shroomer count floor is never the one that gives way
  (6 → 15 against a floor of 3). An earlier draft with the dryers *inside* the bloom lost outright:
  14 Shroomers wetted ground faster than five nests could dry it.
- `crystal_siege` — both crystals down by **t≈800**.
- `nest_raid` — `nest_destroyed` and `colony_destroyed` both fire, plus the two-way counter-siege.

---

## 2026-06 — terrain profile, species attribution, carrion, the 3D docs rewrite

| Commit | Date | Change |
|---|---|---|
| `6d3531c` | 2026-06-23 | Concealment tone-down + niche-aware spawn placement |
| `436af1b` | 2026-06-23 | Terrain-aware food-seeking for predators |
| `6152547` · `dcca479` | 2026-06-23 | Real deserts, thinner beaches, niche roll-ups split so beach sand cannot fake a desert |
| `3835d05` · `99b911c` | 2026-06-23 | **Unified `TerrainProfile` resolver** + species-aware movement; concealment wired | replaced three divergent water-handling code paths |
| `8f58f39` · `dbacfab` | 2026-06-20 | Aquatic speed overhaul; Penguin becomes a fish specialist |
| `c932a7f` | 2026-06-19 | Fix predators unable to attack at reduced LOD — the "invulnerable prey" / prolonged-push bug | cooldown decrement overshot zero into a stuck negative |
| `e4d3e60` | 2026-06-19 | Aquatic creatures avoid *land* instead of water |
| `3934e86` | 2026-06-19 | Biome-differentiated grazing nutrition |
| `a37fce2` | 2026-06-18 | Spatial-query fleeing; skip fully-regenerated chunks in tile regeneration |
| `71cc1ab` | 2026-06-18 | Prey flee stamina: burst → tire → recover |
| `5971de1` | 2026-06-18 | Fox → ambush/scavenger, Bear → ambush charger + fishing |
| `2b0f268` | 2026-06-17 | Depth-aware water model: wade shallow, drown in deep |
| `b53d94c` | 2026-06-17 | Fix Shark starvation: aquatic predators no longer avoid their own element |
| `07af682` | 2026-06-16 | Per-species enable/disable toggle for balance runs |
| `498c095` · `b1cc4e1` | 2026-06-16 | Stop predators abandoning hunts mid-approach — **the real collapse cause**; stop pack flankers aborting before the kill |
| `147c03b` | 2026-06-16 | Hunting performance: defer the across-water test, cap candidates |
| `4371f0a` | 2026-06-16 | Feed predators on a kill so they can reach reproduction |
| `849888f` | 2026-06-16 | Wrong-element movement, hunger-scaled scavenging, nest-based terraforming |
| `112c8db` · `35e6efc` | 2026-06-15 | Prevent immortal `NaN` corpses trapping every predator as a scavenger; fix the `NaN` hunger cascade from an uninitialised tick interval |
| `b8f0cde` | 2026-06-15 | Fix CSV corruption from locale-dependent float formatting |
| `15a16d6` · `b6a4d54` | 2026-06-15 | **Carrion**: corpse-on-death hook for every cause; decomposition enriches soil |
| `4666700` · `f7dce3b` · `47e9da0` | 2026-06-15 | Defensive rally: attacked packs and swarms mob the attacker; proactive threat detection; committed mobbers do not flee their target |
| `c55f099` | 2026-06-15 | Differentiate per-species energy — 14 species had shared the default |
| `1feb16b` | 2026-06-14 | Predator target-viability re-evaluation |
| `77cddb8` · `443cb41` · `3f4ce8f` · `e6ae9f6` | 2026-06-14/15 | Make Sectids viable: cheaper nest economy, hibernation food floor, swarm kills, field feeding |
| `4074253` | 2026-06-14 | Directed foraging: hungry grazers seek food instead of starving in place |
| `ca57670` … `a177835` | 2026-06-14 | 3D terrain plan phases 1–3: world-space mapping, per-vertex parameters, continuous palette, surface detail, flat depth-coloured sea, biome-aware roughness |
| `6b2c5b4` | 2026-06-14 | Interpolate entity positions between simulation ticks |
| `d4e39dd` · `1efda83` | 2026-06-13 | Documentation overhaul for the 3D codebase; superseded docs archived |

---

## 2026-02 to 2026-03 — the 2D→3D migration and the LOD era

| Period | Work |
|---|---|
| 2026-03 | Elevation shading iterations (altitude shading → contour bands → manual Lambert), screen-space slope edge darkening, frustum-culling pop fixes, player movement precision on the row offset |
| 2026-02 (late) | **Phase 5B: true 3D** (`66c80f9`) — `Node3D`, `Camera3D`, `MeshInstance3D`. Triangle-grid plan phases 1–6: per-vertex elevation, `GridCoordinates`, triangle mesh renderer replacing chunk textures, row offset applied to entities and camera, faux-isometric lift, hex alignment + slope resistance + flat spawn filter. `c7c41b2`: **tile-type impassability removed**, replaced by elevation-difference cliff detection |
| 2026-02 (mid) | LOD replaces the statistical simulation (`874eeec`), `DueThisTick` array and cached intervals (`543ab35`), tier hysteresis (`a615857`), tile regen throttle + chunk LOD distance caching (`41cc8ab` — **tile regeneration 16 ms → ~4 ms**), smooth turning momentum |
| 2026-02 (mid) | **Statistical simulation** built, validated and eventually removed: chunk-level population maths for distant areas, inter-chunk migration, cross-chunk predation, terraformer statistics. Superseded entirely by LOD |
| 2026-02 (early) | Species diversity: 20 biome species, 13 procedural shapes, omnivore/flying/venom traits, hunting-tactic enum, area-attack S-curve; World Generation v2: new tile types and biomes, domain warping, temperature and latitude, tile nutrition, landmarks, flow-based rivers replacing noise rivers; drowning and suffocation; per-system profiling overlay |
| 2026-02 (early) | Faction species implemented (`0369a8e`): Sectid nests, Shroomer spores, Faeling crystals. Coordinated flanking, ambush hunting with stealth, velocity damping and jitter fixes, `BehaviorSystems`/`GameManager` split into single-responsibility files |
| 2026-02-14 | First docs-vs-code audit: 22 issues found and fixed (`3b11586`, `b9e1a53`) |

---

## 2026-01 — Python prototype and the migration decision

Original prototype in **Python / Arcade + Esper ECS**, built out over about ten days: chunked
world generation, grazing, hunting, fleeing, reproduction, ageing, herding and pack behaviour,
terrain discomfort, separation and collision, stat variation, a data-driven species definition
system, and a Fear component.

It hit a performance ceiling around **~500 entities** despite batched rendering, vectorised
terrain, threaded and then multiprocessed chunk generation, and predictive loading. The migration
evaluation (`b26e419`, archived) compared Godot 4 + C#, Unity DOTS and Bevy and chose Godot; the
port began at `b827b3f` (2026-01-29) and the Python source was archived at `ecb045f` (2026-02-06).

Two commits from **2024-09** predate the project proper and carry only a README and an upload.

---

## Conventions for adding to this file

- One row per commit that changed behaviour. Pure refactors and doc-only commits are summarised,
  not listed.
- A measured figure needs the **seed**, the **tick count** and the **commit** it was taken at. If
  you cannot supply all three, do not record the figure.
- When a later change invalidates an earlier measurement, mark the earlier one rather than deleting
  it — the fact that it expired is itself information.
- Design decisions do not live here. If a change embodies a decision, the decision goes in
  [`design/`](design/) and this file records that the change landed.
