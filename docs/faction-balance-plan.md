# Faction Balance — Shroomer / Sectid / Faeling

Workstream to turn the three terraformer factions into a genuine three-way war instead of a
Shroomer monoculture. Opened after the first full run on the new terrain (run
`20260710_031029`, 36ch, seed 124154536).

## STATUS (2026-07-13): #1–#4 implemented; workstream PAUSED pending the focused-testing branch

All four parts landed (fungivores, Sectid retargeting, Shroomer self-limits + fertility reliance,
Faeling keeper). The keeper's first full-world flight (run `20260713_035932`, seed 1269274668 —
same world as the strict-crowding run, so directly comparable) showed the remaining problems are
**not tunable from full-world runs**:

- **Keeper first flight:** the sense→prefer→siege machinery works, but fired only 6 kills (all
  Sectids, all before t12k) and then went silent while Shroomers tripled (107→317). Two causes:
  (1) *local-density blind spot* — Sectid colonies are always locally dense (8+/nest), so keepers
  read "Sectid dominance" beside nests of a faction that was globally collapsing (77→4) and piled
  onto the loser; **fixed** with a global-census guard (won't suppress a faction globally ≤2/3 of
  its rival). (2) *Coverage* — 8 keepers × 40-tile sense on a 1152² map: most blooms never meet a
  keeper. Siege efficacy is untested at this density (drought deaths flat, 51 vs ~54 pre-keeper);
  needs a staged bloom-vs-keeper scenario, not another full-world roll.
- **Sectids re-collapsed (77→4), and not because of Faelings:** 69 starvation vs 10 predation.
  Their colony economy only ignites when the herbivore base surges (the 504-peak run rode a
  1,160-Deer boom; this run's Deer stopped at ~507). Limited-resource-pool behaviour is working
  as designed — but their viability floor is hostage to prey density and needs its own scenario.
- **Predator collapse = starvation economics on a knife edge (the secondary question answered):**
  every collapsed predator starved (Wolf 63 starve/28 births, Hawk 25/8, Fox 46/24, Arctic Fox
  36/17; 834 starvations vs 469 kills guild-wide), with hunt VOLUME the bottleneck (1,723 attempts
  in 23k ticks; conversion 27% is fine). Decisive detail: on the SAME world, Hawk went 19→62 in
  one run and 22→0 in the next — sim RNG is unseeded (`new Random()` per system), so predator fate
  is dominated by early stochastic luck. This both explains "why predators diminished" and shows
  the tuning method has hit its limit.
- **Watch item:** Penguin 45→347 — free water-tile feeding (the "can't starve" model Shroomers
  used to have) with all three of its predators collapsed/tiny. Same structural pattern; candidate
  for the fertility-style treatment or predation pressure once predators function.

### Pre-branch control-run findings + adjustments (2026-07-13, second run on seed 1269274668)
A control run on the same build+seed confirmed spawn RNG alone reshuffles outcomes. Fixes and
tuning applied before branching:

- **Spore inflation in the population CSVs (answer to "do nests/crystals count?"):** nests and
  crystals carry no `Species` component and never counted; **spores DO** (they're tagged
  `Species(Shroomer)` for type checks) — every Shroomer population figure in prior CSVs was
  inflated by live spores, while the F3 overlay excluded them (hence CSV-vs-eye mismatches).
  Fixed: the logger now skips spores/nests/crystals in species counts and appends dedicated
  `spores,nests,crystals` columns; `total` = creatures only. *Interpretation caveat: earlier
  "Shroomer" trajectories in this doc (e.g. 107→317) include spores.*
- **Faeling budget is mostly notional (flagged, not changed):** `FaelingShare 0.04` → budget 80 →
  `80/10 = 8 crystals`, and each crystal sustains exactly ONE Faeling (1:1 in code, despite the
  "small group" comment) — so the faction is 8 creatures, not 80. Redesign (crystals hosting
  multiple Faelings, or budget-true crystal counts) belongs to the focused branch.
- **Shroomer AoE 25 → 14** (user call): 25 out-ranged every counter on the map. 14 keeps elders
  lethal up close but lets Faeling bolts (range 12) actually trade. Keeper siege standoff
  26 → 16 to match; elder-safety now only skips near-elders (scale ≳3.2).
- **Terraform rates were structurally invisible** (user observation confirmed in code):
  `TerraformStrength` is a *probability per cooldown roll* for ONE random tile, not an amount —
  Faeling 0.03 ≈ one nudge/267 ticks. Raised: Faeling strength 0.03→0.25, radius 3→4; Sectid
  nest burst 6×0.05@r1.5 → 14×0.08@r3 (the old footprint dried ~7-tile specks, leaving green
  between nests). Goal: terraforming becomes a real Shroomer counter so the crowding damage can
  eventually be relaxed from "artificial check" to backstop.
- **Arctic Fox buff** (dies out ~every run even beside the Penguin boom): the mechanical hole was
  that penguins raft on SHALLOW SEA to feed, and fox food-seek (`HuntTerrain`) only pointed at
  tundra/ice — it starved inland of its staple prey. `HuntTerrain` += ShallowWater (land hunters
  wade shallow safely), HuntRange 8→12 (spot rafts from shore), HungerDecayRate 0.04→0.03,
  SpawnWeight 0.8→1.1.

### Handoff → focused-testing branch (recommendations)
1. **Determinism first:** seed the per-system `Random` instances from `WorldSeed` (one line each)
   so identical setups reproduce — without this, A/B tuning of knife-edge systems (predators,
   keepers) is reading noise.
2. **Scenario harness:** small worlds + `DisabledSpecies` give most of it already; add tiny
   scripted setups — (a) one Shroomer bloom + N keepers (siege efficacy, drought kill-rate),
   (b) Sectid colony + fixed prey density sweep (find the colony-viability floor), (c) single
   predator species + prey at controlled density (hunt-volume economics, breeding threshold sweep).
3. **Metrics:** the events/species-stats CSVs already carry what's needed; per-scenario asserts
   ("bloom collapses within N ticks", "colony survives at density X") turn runs into pass/fail.

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
  starves and can't spread → **faction drying collapses a bloom** (the in-world weapon wanted
  here, over arbitral gating). Logs `environment_death:drought`/`crowding`.
- **Spread suppression** — local saturation (`CrowdingLimit → CrowdingSaturation` 18) zeroes spread
  where there's no open ground, plus a global population-pressure factor (mirrors ReproductionSystem)
  as a safety ceiling so Shroomers can never convert the whole cap.

*Tuning caveat:* damage values are a first pass (can't run here). If a test run shows Shroomers
collapsing to extinction, lower `CrowdingDamage`/`DroughtDamage`; if still booming, raise them or
lower `CrowdingSaturation`.

**Retune 2 (after run `20260710_221127`, same seed as the boom run).** The first pass worked TOO
well: Shroomers held a flat ~90 for the whole run (322 crowding + 112 drought + 186 fungivore
deaths, maturation 88%→43%) and the predator collapse was relieved (Wolf/Hawk/Bear/Fox/Jaguar all
alive, vs 0–1 in the boom run — confirming the baseline reframe). BUT the Sectids starved to
extinction (87→0, 96 starvations): `CrowdingLimit 8` triggered at any 8-in-radius cluster, so
**blooms could never form** — Shroomers existed only as scattered individuals, spore creation
collapsed 6 414→1 177, and the Sectids (who need the spore/immature-Shroomer food base to hold
swarm-kill-mass) lost their food and death-spiralled. Relaxed to let blooms build and cycle:
`CrowdingLimit 8→20`, `CrowdingSaturation 18→40`, `CrowdingDamage 0.15→0.08`, `DroughtDamage
0.5→0.35`. Goal: Shroomers cycle in the hundreds with visible blooms that feed Sectids, drought +
fungivores + Sectid predation still capping the total. **Watch next run:** Shroomer equilibrium
(want hundreds, cyclic — not ~90 flat, not thousands) and whether Sectids survive.

### #4 — Faeling "keeper" redesign ✅ DONE (option ①, "anti-dominance balancer")
Implemented (see FEATURES §7.3 for the mechanics): periodic dominance sense
(`KeeperSenseRadius` 40, `KeeperMinPresence` 5, 1.5× margin, spores/structures excluded) →
ranged-attack preference for the locally winning faction; **elder-safety filter** (never
bolt-duel a Shroomer whose growth-scaled AoE reach rivals the 12-tile bolt range — the
pre-keeper suicide that bled Faelings 8→5); **siege patrol** to a 26-tile standoff ring around
the dominant hotspot where balanced terraform dries/restores the winner's substrate (drought
from #3 kills the elders bolts can't). Falls back to the old damaged-terrain restoration when
surroundings are balanced. **Watch next run:** Faeling kill counts/power growth, whether
sieges measurably shorten bloom lifetimes (environment_death:drought near hotspots), and
whether 8 keepers are enough to matter (if not, `FaelingShare` is the dial).

Original design sketch (for reference):
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

### #3b — Shroomers rely on & impact land fertility 🔲 NEW (test pending)
User design directive (the intended faction identities): if unchecked, **Shroomers** should
dominate by turning the map to swamp/bog/wetland, and **Sectids** by turning it to desert then
going map-wide dormant from prey exhaustion — the key difference being *Sectids have a limited
resource pool (must hunt) while Shroomers create their own (wet substrate)*. So Shroomers should
also **rely on and impact land fertility, more than herbivores** — grounding their self-limit in
ecology rather than only the arbitrary crowding of #3.

Root cause found in the registry: Shroomer `FeedNutrition = 0.5` on Wetland/Forest, commented
"can't starve on wet tiles" — free food on the swamp they create, zero fertility reliance.
Implemented (defaults off, only Shroomer opts in): `FertilityConsumeRate` 0.06 (3× a grazer's
depletion) + `FertilityFeedNutrition` 0.8 drive growth from tile nutrition; wet-tile `FeedNutrition`
cut 0.5→0.06 (subsistence floor ≈ hunger decay). Effect: a bloom strips fertile ground, terraforms
it to barren swamp, and must advance into fresh land — an ecological self-limiting front that also
destroys herbivore pasture (their disruption). Crowding/drought (#3, relaxed) kept as secondary.
**Watch next run:** whether Shroomers now form advancing blooms bounded by fertility (want cyclic,
hundreds), whether Sectids get enough Shroomer/spore food to persist, and whether herbivores near
blooms feel the pasture loss. NOTE — Sectid non-Shroomer-reliance is a separate open item.

## No-faction baseline (run 20260710_211700, factions disabled, 36 000 ticks) — REFRAMES the above

A `DisableFactionSpecies` run is the control. It changes the diagnosis:

- **The world self-limits at ~4 000 total** and holds there flat from t8 000 to t36 000 — nowhere
  near the 12 000 cap, or even the 6 000 (50%) mark where the global population-pressure ramp
  begins. **Food (grazing capacity), not the cap, is the binding constraint.** Herbivores die of
  age + predation (Fish 832 age/364 predation, Rabbit 448/386, Deer 370/164) — a functioning food
  web. Consequence: the 12 000 cap and its pressure ramp are nearly inert in a healthy world, so
  the global-pressure *factor* I added to Shroomer spread (#3) barely engages until Shroomers alone
  are already a monoculture — **the in-world crowding/drought levers must carry #3, not the gate.**
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
