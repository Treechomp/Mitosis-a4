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

### #3 — Shroomer mortality + terrain dependence 🔲 PLANNED
With #1/#2 providing attrition, tighten the Shroomer engine so a bloom that loses its wet
substrate actually collapses:
- **Global-pressure gate on spore spread** — the clean consistency fix: make `SporeSystem` spread
  obey the same population-pressure ramp as `ReproductionSystem` (or a faction-share cap), so
  Shroomers can't convert the entire shared cap. *Apply only if #1/#2 don't bring them to heel on
  their own — re-measure first.*
- **Substrate dependence** — spore maturation should require the tile to *stay* wet; a spore on a
  tile dried below `SporeMoistureThreshold` (by Sectid nest-drying or Faeling neutralizing) should
  wither faster, so drying a territory genuinely kills the bloom rather than just slowing it. The
  wither path already exists (`SporeSystem` dry-tile branch) — the lever is tuning wither vs. gain
  and making sure faction terraforming actually crosses the threshold around blooms.
- **Real elder mortality** — consider a slow senescence or a crowding penalty on mature Shroomers
  so a fully-grown field doesn't sit immortal (0 age deaths over 17.5k ticks is the tell).

*Sequencing:* run a fresh sim after #1/#2 and read the log **before** building #3 — the numbers
will show how much of this is still needed once spores are being eaten.

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
  frozen (alive on low `HungerDecayScale` but not breeding) rather than cycling. Arctic Fox went
  extinct, Fox nearly. Systemic hunting + predator-reproduction issue.
- **Herbivore food ceiling / density-dependent reproduction.** Near-zero starvation, ~100% energy
  — herbivores plateau only because the shared cap fills. The long-standing "always booms to the
  cap" lever; belongs at the reproduction layer, not in the factions.
