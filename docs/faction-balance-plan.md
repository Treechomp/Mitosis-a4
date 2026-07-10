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

## Parked (high priority, separate workstreams)
- **Predator/hunting deep-dive.** 543 kills / 17.5k ticks, 89% hunt-failure; most predators are
  frozen (alive on low `HungerDecayScale` but not breeding) rather than cycling. **Worsening:** by
  run `20260710_045611` (t24 100) Hawk, Arctic Fox, Fox, Snake, Scorpion were extinct and
  Wolf/Bear down to 1 — and the `ExclusivePrey` fix ruled out Penguin predation as the cause, so
  this is squarely the systemic hunting + predator-reproduction issue. High priority once factions
  settle; it may be dragging the whole ecosystem.
- **Herbivore food ceiling / density-dependent reproduction.** Near-zero starvation, ~100% energy
  — herbivores plateau only because the shared cap fills. The long-standing "always booms to the
  cap" lever; belongs at the reproduction layer, not in the factions.
