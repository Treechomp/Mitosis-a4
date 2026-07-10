# Faction Balance — Shroomer / Sectid / Faeling

Workstream to turn the three terraformer factions into a genuine three-way war instead of a
Shroomer monoculture. Opened after the first full run on the new terrain (run
`20260710_031029`, 36ch, seed 124154536).

## The problem (from run 20260710_031029)

Shroomers ran to **9,107 of ~11,700 total (78%)** and were still climbing when the run ended;
Sectids sat flat (~176), Faelings shrank (8 → 5). Diagnosis — the three factions have wildly
asymmetric reproduction economics:

- **Shroomer breeding is free and near-unstoppable.** Spores cost only hunger (regained by
  sitting on wet tiles), spore maturation ran **88%** (8,884 matured / 10,113 created), and
  `SporeSystem` spread is gated *only by the hard population cap*, **not** the global
  population-pressure ramp every other species obeys — so as total nears the cap, every sexual
  species is throttled toward zero while Shroomers keep converting the headroom. Grown Shroomers
  are near-immortal: 0 starvation / 0 age / 0 predation / 0 environment deaths, because their
  growth-scaled HP + thorns + AoE make them fortresses nothing eats.
- **Sectid breeding is expensive and was deadlocked.** A larva needs 18 food, Sectids can't
  graze, so they must *kill* — but they targeted the nearest random prey (Deer/Lizard/Rabbit/
  Boar, **0 Shroomers/spores**) and never funded a colony large enough to matter.
- **Faeling numbers are crystal-capped (~8) and were shrinking** because Shroomer AoE killed
  them faster than crystals respawned them, and they don't preferentially fight the thing that's
  winning.

Two ecosystem-wide issues surfaced in the same run but are **not** faction bugs (tracked
separately, see below): near-zero death pressure everywhere (herbivores never hit a food
ceiling) and a frozen predator guild (543 kills in 17.5k ticks, 89% hunt-failure).

## The four-part fix (user-approved direction)

Rock-paper-scissors goal: **Shroomers spread on wet ground → Sectids dry it out and eat immature
blooms → Faelings neutralize both extremes and suppress whoever's ahead.**

### #1 — Fungivore trait ✅ DONE (commit after 51fbdc0)
Herbivores flagged `IsFungivore` (Boar primary, + Rabbit/Lizard/Monkey) eat the nearest Shroomer
spore / immature Shroomer (`Growth.CurrentScale ≤ FungivoreMaxScale`, default 2.0) within
`FungivoreFeedRadius` each feed tick, via a grazing-adjacent path in `GrazingSystem` (no hunt/
rally/thorns). Gives herbivores a bloom-culling role that feeds them. Immature-Shroomer meals log
as kills for measurability. See FEATURES §6.5.

### #2 — Sectid retargeting ✅ DONE (same commit)
New `SpeciesDefinition.SporeHuntBias` (Sectid = 0.85) multiplies the hunt score of spores/immature
Shroomers by `1 − bias`, so a swarm eats a bloom out before it fortifies. The existing growth-
scaled mass gate already rejects grown Shroomers, so the bias can't make a swarm suicide on an
elder. See FEATURES §6.7.

> **Prerequisite fixed first (commit 51fbdc0):** `ExclusivePrey` leaked through the defensive
> rally path (Fish-only Penguins were hunting their own predators). Fixed so the specialist
> filter — the backbone of the fungivore design — is trustworthy.

### #3 — Shroomer mortality + terrain dependence ✅ DONE
Run `20260710_045611` confirmed #1/#2 alone were insufficient: fungivores held Shroomers at
~90–125 for the first ~3 000 ticks (the cull genuinely works while sparse — 338 immature Shroomers
eaten, maturation 88%→76%), but once blooms got dense they escaped exponentially to **61% of the
population and still climbing** at t24 100. Root cause: grown Shroomers had *no* mortality
(`MaxLifespan` 50 000 > run length → 0 age deaths). Implemented, all data-driven on
`SpeciesDefinition` (default off; only Shroomer opts in), in `SporeSystem`'s mature-Shroomer loop:
- **Crowding attrition** (`CrowdingRadius` 6, `CrowdingLimit` 8, `CrowdingDamage` 0.15) — a dense
  mat competes with itself; energy drains per neighbour over the limit, so blooms self-thin.
- **Drought death** (`DroughtDamage` 0.5) — a mature Shroomer on a tile below `SporeMoistureThreshold`
  starves and can't spread → **faction drying collapses a bloom** (the biological weapon the
  user wanted, over arbitral gating). Logs `environment_death:drought`/`crowding`.
- **Spread suppression** — local saturation (`CrowdingLimit → CrowdingSaturation` 18) zeroes spread
  where there's no open ground, plus a global population-pressure factor (mirrors ReproductionSystem)
  as a safety ceiling so Shroomers can never convert the whole cap.

*Tuning caveat:* damage values are a first pass (can't run here). If a test run shows Shroomers
collapsing to extinction, lower `CrowdingDamage`/`DroughtDamage`; if still booming, raise them or
lower `CrowdingSaturation`.

### #4 — Faeling "keeper" redesign 🔲 PLANNED (design: option ①, "anti-dominance balancer")
Faelings become self-correcting keepers of order rather than just another combatant:
- **Focus fire the locally-dominant faction.** Today they attack Sectids/Shroomers indiscriminately
  at range. Instead, sample the local faction mix (spatial-hash query around the Faeling / its
  patrol target) and prioritize ranged attacks on whichever faction is over-represented nearby —
  which is currently always Shroomers, but stays correct if the balance ever flips.
- **Neutralize terrain extremes.** Lean their balanced-terraform (toward Grass) into actively
  converting the most moisture-distorted tiles back to neutral — removing Shroomer **wet**
  substrate *and* Sectid **dry** substrate, denying both factions their breeding ground. This is
  the "order" mechanic: they patrol toward the biggest terrain distortion and undo it.
- **Survivability to matter.** They're starvation-immune and crystal-respawned but AoE-killable;
  if #1–#3 haven't blunted Shroomer AoE enough, give Faelings a small edge (out-range the AoE,
  which ranged already allows; or a modest founding-population/crystal bump) so keepers persist
  long enough to compound power from kills/balanced terraforming.

This is the largest single piece and benefits most from #1–#3 being observable first.

## No-faction baseline (run 20260710_211700, factions disabled, 36 000 ticks) — REFRAMES the above

A `DisableFactionSpecies` run is the control. It changes the diagnosis:

- **The world self-limits at ~4 000 total** and holds there flat from t8 000 to t36 000 — nowhere
  near the 12 000 cap, or even the 6 000 (50%) mark where the global population-pressure ramp
  begins. **Food (grazing capacity), not the cap, is the binding constraint.** Herbivores die of
  age + predation (Fish 832 age/364 predation, Rabbit 448/386, Deer 370/164) — a functioning food
  web. Consequence: the 12 000 cap and its pressure ramp are nearly inert in a healthy world, so
  the global-pressure *factor* I added to Shroomer spread (#3) barely engages until Shroomers alone
  are already a monoculture — **the biological crowding/drought levers must carry #3, not the gate.**
- **Without factions the predator guild is HEALTHY:** at t36 000 Hawk 69, Wolf 56, Bear 54,
  Jaguar 48, Polar Bear 50, Shark 16, Crocodile 26, Fox 16 — all stable. In the faction run these
  same species collapsed to 0–1. **So the severe predator collapse is largely faction-driven (the
  Shroomer monoculture), NOT an independent hunting-mechanic failure** — I over-attributed it
  earlier. Fixing Shroomers (#3) should relieve most of it; re-measure before any predator work.
- **Genuinely world/mechanic-limited extinctions persist even with no factions:** Arctic Fox
  (54 starvations — cold-prey-limited), Scorpion (30 — broken sit-and-wait ambush, terrain-roadmap
  §5), Snake (19 — same class). These match the species-attribution-audit "known-hard" list and are
  the *real* residual predator work, much smaller than it looked.
- **Design implication — how factions should disrupt.** The no-faction world is *placid*:
  populations flatline into a static equilibrium after t8 000. Factions exist to disrupt that, but
  the disruption must be **local, cyclic, and bounded**, not additive: today Shroomers pile
  population *on top* of the ~4 000 baseline (faction run hit 7 792) and only ever grow, so the
  "disruption" is a one-way ramp to monoculture whose main damage is invisibly stealing the shared
  reproduction/space budget. Target: a healthy faction presence is **hundreds, not thousands** — a
  bloom degrades a region, provokes a response (fungivores/Sectids/Faelings), collapses, the region
  recovers, repeat. #3's crowding/drought should aim for a Shroomer equilibrium in the low hundreds;
  #4's Faelings should accelerate the *collapse* half of that cycle.

## Parked (re-evaluate after #3)
- **Predator/hunting deep-dive.** Reframed by the baseline above: mostly a symptom of the Shroomer
  monoculture, not a standalone failure. After #3 lands and Shroomers are bounded, re-run and check
  whether Hawk/Wolf/Bear/Fox recover on their own. Residual true problems are the known world-limited
  specialists (Arctic Fox, Scorpion, Snake) — a smaller, species-specific job.
- **Herbivore food ceiling / density-dependent reproduction.** Near-zero starvation, ~100% energy
  — herbivores plateau only because the shared cap fills. The long-standing "always booms to the
  cap" lever; belongs at the reproduction layer, not in the factions.
