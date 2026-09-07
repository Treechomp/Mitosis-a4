> ## ⚠ ARCHIVED — historical document
> **Accurate as of 2026-02-20. It is not a description of the current code or the current design.**
> Why it is here: A/B validation of a StatisticalSimSystem that no longer exists. The approach was replaced entirely by distance-based LOD - real entities everywhere, throttled by distance.
>
> Current documentation: [`docs/design/`](../design/), [`docs/implementation/`](../implementation/),
> [`docs/changelog.md`](../changelog.md). Everything below is preserved verbatim.

---

# Statistical Simulation Validation Report

**Date:** 2026-02-20
**Sessions analyzed:** 11-14
**Methodology:** Controlled A/B comparison — same seed, entity sim vs statistical sim

## Test Matrix

| Session | Seed | Mode | Ticks | Events | Start Pop | Final Pop |
|---------|------|------|-------|--------|-----------|-----------|
| S11 | 12 | Entity | 29,601 | 17,977 | 586 | ~1,985 |
| S12 | 12 | **Statistical** | 21,527 | 7,655 | 312 | ~2,000 |
| S13 | 32 | Entity | 22,912 | 9,961 | 565 | ~1,860 |
| S14 | 32 | **Statistical** | 27,187 | 9,171 | 291 | ~2,000 |

---

## CRITICAL FINDING: Event Logs Are Incomplete for Statistical Sim

`StatisticalSimSystem.cs` contains **zero calls** to the event logger. All births, deaths, predation, and starvation processed by the statistical sim in distant chunks (>100 tiles from player) happen silently. The event logs for S12 and S14 only capture:

1. **Entity-mode creatures near the player** (within 100 tiles)
2. **Faction species** (Shroomer, Sectid, Faeling) which are excluded from aggregation and run as full entities everywhere

This means **direct event-count comparisons between entity and statistical sims are misleading** — the statistical sim's events represent a small visible window, not the full simulation. Population CSV snapshots are the only reliable way to audit the statistical sim's actual behavior.

Despite this limitation, the data still reveals major divergences in outcomes, and the near-player event data provides a useful window into behavioral differences.

---

## 1. Starting Population Deficit

| Seed | Entity Start | Statistical Start | Deficit |
|------|-------------|-------------------|---------|
| 12 | 586 | 312 | **-47%** |
| 32 | 565 | 291 | **-48%** |

Both statistical sim runs start with roughly **half** the population of their entity counterparts. This is consistent across seeds, suggesting a systematic issue in initialization. Possible causes:

- Chunks distant from spawn are immediately aggregated, and aggregation skips some species or loses count
- The aggregation→materialization cycle loses creatures in the first tick
- Carrying capacity calculations in `StatisticalSimSystem` clamp populations below actual starting counts

**Recommendation:** Log the aggregation/materialization pipeline at tick 0 to identify where creatures are being lost.

---

## 2. Shroomer Dominance Explosion

### Population Share at Simulation End

| Seed | Entity Sim | Statistical Sim | Amplification |
|------|-----------|-----------------|---------------|
| 12 | 15% (290/1985) | **45%** (898/2000) | **3.0x** |
| 32 | 15% (275/1860) | **51%** (1022/2000) | **3.4x** |

### Root Cause: Asymmetric Simulation Rules

The code reveals the mechanism:
- **Shroomer is excluded from statistical aggregation** (faction species bypass via `ComponentFlags.Terraform` check)
- Shroomer stays as a live entity with full spore spreading, AoE growth, and entity-level reproduction **everywhere**
- Meanwhile, its predators and competitors in distant chunks are collapsed into population-level math

This creates a fundamental asymmetry: **Shroomer gets entity-level advantages while competing against statistically-averaged opponents.**

### Spore Production Comparison

| Metric | S11 (Entity) | S12 (Stat) | S13 (Entity) | S14 (Stat) |
|--------|-------------|-----------|-------------|-----------|
| spore_created | 1,419 | 1,557 | 700 | **1,812** |
| spore_matured | 1,137 | 1,204 | 586 | **1,455** |
| Maturation rate | 80.1% | 77.3% | 83.7% | 80.3% |

On seed 32, the statistical sim produced **2.6x more spores** than entity sim. This makes sense: in entity sim, Shroomer competes for space with individual creatures who graze and physically occupy tiles. In statistical sim, distant competitors exist only as numbers in a formula and don't physically obstruct spore spreading.

### Shroomer Mortality Comparison

| Cause | S11 (Entity) | S12 (Stat) | S13 (Entity) | S14 (Stat) |
|-------|-------------|-----------|-------------|-----------|
| Killed by predators | 668 | 213 | 217 | 83 |
| Starvation | 138 | 91 | 99 | 285 |
| Environment death | 126 | 85 | 60 | 137 |
| **Total deaths** | **932** | **389** | **376** | **505** |

Key pattern: In entity sim, predation is the primary Shroomer mortality source (668 and 217 kills). In statistical sim, predation drops dramatically (213 and 83 kills) because:
- Many predator species that hunt Shroomer go extinct earlier in stat sim
- The Lotka-Volterra predation formula may underestimate actual hunting pressure on Shroomer
- Fewer entity-mode predators exist near Shroomer colonies

In S14 (stat sim, seed 32), starvation (285) overtakes predation (83) as the primary Shroomer death cause — the population literally outgrows its food supply because predation can't keep it in check.

**Recommendation:** Either include Shroomer in statistical aggregation, or apply a statistical predation pressure to Shroomer in distant chunks proportional to the predator populations the stat sim is tracking.

---

## 3. Predator Ecosystem Collapse

### Hunting Activity (Events Logged)

| Metric | S11 (Entity) | S12 (Stat) | S13 (Entity) | S14 (Stat) |
|--------|-------------|-----------|-------------|-----------|
| hunt_start | 4,208 | 770 | 2,305 | 995 |
| kill | 2,710 | 526 | 1,182 | 480 |
| hunt_fail | 1,251 | 271 | 809 | 242 |

Note: The statistical sim numbers only reflect near-player entity-mode hunts. Distant predation happens via Lotka-Volterra math without logging.

### Predator Survival Comparison

**Seed 12:**

| Predator | S11 Entity (final pop) | S12 Stat (final pop) | S11 last event tick | S12 status |
|----------|----------------------|---------------------|--------------------|----|
| Polar Bear | 0 | 0 | 29,601 | Extinct |
| Bear | 67 | 1 | 29,575 | Nearly extinct |
| Jaguar | 29 | 7 | 29,586 | Remnant |
| Wolf | 18 | 23 | 29,399 | Similar |
| Boar | 102 | 0 | 29,574 | **Missing entirely** |
| Hawk | 0 | 0 | 17,985 (S11) | Extinct in both |
| Fox | 0 | 0 | 8,417 (S11) | Extinct in both |

**Seed 32:**

| Predator | S13 Entity (final pop) | S14 Stat (final pop) | S13 last event tick | S14 status |
|----------|----------------------|---------------------|--------------------|----|
| Crocodile | 40 | 0 | survived | **Extinct** |
| Jaguar | 44 | 22 | survived | Reduced |
| Polar Bear | 29 | 2 | survived | Nearly extinct |
| Bear | 24 | 0 | survived | **Extinct** |
| Boar | 148 | 0 | survived | **Missing entirely** |
| Wolf | 0 | 0 | 8,060 (S13) | Extinct in both |

### Extinction Timeline Comparison

**Seed 12 — species extinction tick (last event):**

| Species | S11 (Entity) | S12 (Statistical) |
|---------|-------------|-------------------|
| Shark | 1,118 | ~1,000 (tick range) |
| Sectid | 2,896 | ~3,100 (pop CSV) |
| Scorpion | 6,549 | ~6,600 (pop CSV) |
| Snake | 7,421 | ~7,400 (pop CSV) |
| Fox | 8,417 | ~6,100 (pop CSV) |
| Arctic Fox | 8,482 | ~6,600 (pop CSV) |
| Hawk | 17,985 | ~17,000 (pop CSV) |
| Boar | **survived** | **never existed** |
| Bear | **survived (67)** | **~1 remaining** |

**Seed 32 — species extinction tick:**

| Species | S13 (Entity) | S14 (Statistical) |
|---------|-------------|-------------------|
| Hawk | 4,197 | 4,548 |
| Sectid | 5,013 | 4,405 |
| Arctic Fox | 5,536 | 2,649 |
| Fox | 6,737 | 2,359 |
| Scorpion | 6,821 | 4,899 |
| Wolf | 8,060 | 10,360 |
| Shark | 9,260 | 2,013 |
| Snake | 10,493 | 7,625 |
| Crocodile | **survived (40)** | 8,378 |
| Bear | **survived (24)** | **tick 0** (1 hunt_start, then gone) |
| Boar | **survived (148)** | **0 entire run** |

### Root Cause Analysis

The statistical sim's Lotka-Volterra predation model (`predationRate = totalPredators × killsPerPredatorPerTick × preyShareFraction`) has several problems for rare predators:

1. **No spatial refuge:** In entity sim, a Bear in a rich hunting ground can sustain itself. In stat sim, its hunger is averaged against all Bears in the chunk, potentially starving well-positioned individuals.

2. **No pack tactics:** Entity sim has Coordinated/Swarm/Ambush hunting tactics that boost success rates. The stat sim uses a flat kill rate based on `HungerDecayRate / EffectiveNutrition`.

3. **Predator capacity formula is too restrictive:** `predatorCapacity = totalPopulation × 0.15f` — only 1 predator per ~7 prey. This hard-caps predator populations regardless of hunting efficiency.

4. **Missing Boar entirely:** Boar appeared at 0 in both statistical runs. Boar is classified as a predator (it hunts) but also a litter species (2 offspring). The stat sim's predator capacity formula may be killing Boar before it can establish because it doesn't account for Boar's dual herbivore/predator nature.

**Recommendation:**
- Increase predator carrying capacity or make it species-specific
- Add hunting tactic bonuses to the Lotka-Volterra kill rate
- Special-case omnivores like Boar that can also graze
- Log statistical predation decisions for audit

---

## 4. Fish Population Instability

### Fish Timeline Comparison

| Metric | S11 (Entity) | S12 (Stat) | S13 (Entity) | S14 (Stat) |
|--------|-------------|-----------|-------------|-----------|
| Reproduce events | 79 | 68 | 109 | 39 |
| Total offspring | 158 | 136 | 218 | 78 |
| Age deaths | 101 | 102 | 50 | 63 |
| Starvation | 40 | 14 | 103 | 14 |
| Kill deaths | 0 | 0 | 9 | 1 |
| Final pop | ~49 | 20→0 | ~62 | 46→0 |
| Fish crash to 0? | No | **Yes (~tick 21K)** | No | **Yes (~tick 21K)** |

Fish crashes to 0 in **both** statistical sim runs around the same tick (~21,000), despite surviving to the end in both entity runs. The pattern:

1. Fish is never significantly hunted (aquatic isolation)
2. Fish reproduces in bursts with long gaps between them
3. In statistical sim, Fish reproduction events drop by ~50-64% compared to entity sim
4. Both stat sim Fish populations crash at nearly the same tick

The stat sim's reproduction formula likely fails for Fish because:
- `wellFedFraction` may be incorrect for aquatic species that feed differently
- The chunk-level `grazeable tiles` calculation may not count water tiles as food sources
- Fish breeding density suppression may be over-applied at population level

**Recommendation:** Audit the carrying capacity calculation for aquatic species. Fish feed on water tiles, but the stat sim's `grazeable_tiles` count may only look at land tile types.

---

## 5. Reproduction Rate Comparison

### Total Offspring Produced (Logged Events Only)

| Species Type | S11 (Entity) | S12 (Stat) | S13 (Entity) | S14 (Stat) |
|-------------|-------------|-----------|-------------|-----------|
| Shroomer | 1,137 | 1,204 | 586 | 1,455 |
| Top herbivores | ~2,500 | ~800 | ~1,700 | ~1,000 |
| Predators | ~250 | ~70 | ~140 | ~75 |
| **Total logged** | **~5,700** | **~2,900** | **~4,400** | **~3,400** |

The statistical sim logs far fewer herbivore and predator reproduction events because those happen silently in distant chunks. But Shroomer reproduction is fully logged (and higher) because it runs as a live entity everywhere.

This creates a visible audit gap: **we can see Shroomer growing but cannot see the stat sim's compensating births for other species**. The population CSVs show other species growing, but we can't verify the birth/death math is correct without event logging.

---

## 6. Near-Player Predator Behavior Shift

Since only near-player hunts are logged in statistical sim, we can compare hunting behavior in the "visible zone":

### Seed 12 — Top Predator by Kill Count

| Rank | S11 (Entity) | S12 (Statistical) |
|------|-------------|-------------------|
| 1 | Polar Bear: 760 kills | **Wolf: 267 kills** |
| 2 | Bear: 643 kills | Hawk: 106 kills |
| 3 | Jaguar: 512 kills | Shark: 55 kills |
| 4 | Wolf: 311 kills | Shroomer: 46 kills |
| 5 | Hawk: 231 kills | Bear: 23 kills |

### Seed 32 — Top Predator by Kill Count

| Rank | S13 (Entity) | S14 (Statistical) |
|------|-------------|-------------------|
| 1 | Crocodile: 269 kills | **Jaguar: 365 kills** |
| 2 | Polar Bear: 242 kills | Polar Bear: 46 kills |
| 3 | Jaguar: 219 kills | Wolf: 29 kills |
| 4 | Boar: 190 kills | Shroomer: 16 kills |
| 5 | Bear: 156 kills | Hawk: 8 kills |

In statistical sim, the predator kill distribution is much more concentrated in 1-2 species. The diverse predator guild of entity sim (5+ active predators) collapses to 1-2 dominant hunters. This is because most predator species go extinct in distant chunks, leaving only the few that happen to be near the player.

---

## 7. Death Cause Distribution

### Seed 12

| Cause | S11 Entity | % | S12 Stat (logged) | % |
|-------|-----------|---|-------------------|---|
| Kill (predation) | 2,710 | 58% | 526 | 44% |
| Starvation | 1,160 | 25% | 303 | 25% |
| Age death | 554 | 12% | 255 | 21% |
| Environment death | 232 | 5% | 123 | 10% |
| **Total** | **4,656** | | **1,207** | |

### Seed 32

| Cause | S13 Entity | % | S14 Stat (logged) | % |
|-------|-----------|---|-------------------|---|
| Kill (predation) | 1,182 | 49% | 480 | 31% |
| Starvation | 693 | 29% | 448 | 29% |
| Age death | 380 | 16% | 462 | 30% |
| Environment death | 160 | 6% | 161 | 10% |
| **Total** | **2,415** | | **1,551** | |

In statistical sim, predation as a share of deaths drops significantly (58%→44%, 49%→31%) while age death share increases (12%→21%, 16%→30%). This confirms that predation pressure is weaker in statistical sim, letting more creatures live long enough to die of old age.

---

## 8. Population Dynamics

### Growth Curves

**Entity sim** reaches population equilibrium naturally — S13 never even hit the 2,000 cap (maxed at 1,860). Predator-prey dynamics create natural limits.

**Statistical sim** always hits the hard 2,000 cap and relies on the cap itself to prevent further growth. The stat sim's Lotka-Volterra equations don't produce sufficient top-down pressure to limit populations organically.

### Late-Game Composition (at cap)

| Species | S12 Stat % | S14 Stat % | "Healthy" range (Entity avg) |
|---------|-----------|-----------|------------------------------|
| Shroomer | **45%** | **51%** | 15% |
| Parrot | 8% | 9% | 10% |
| Elk | 8% | 7% | 8% |
| Deer | 6% | 7% | 6% |
| Monkey | 6% | 7% | 10% |
| Snake | 2% | 0% | 0% |
| All predators combined | **<3%** | **<2%** | **~15%** |

The statistical sim produces a **Shroomer monoculture** with a thin herbivore layer and virtually no predators.

---

## Summary of Issues (Priority Order)

### P0 — Critical

1. **Shroomer asymmetry:** Faction species run as full entities while competitors are aggregated. Shroomer grows 3-3.4x more dominant than in entity sim. Either aggregate Shroomer too, or apply statistical predation/competition pressure to it.

2. **Starting population deficit (~48%):** Nearly half the population is lost when statistical sim initializes. This cascading deficit affects all downstream dynamics.

3. **No event logging in StatisticalSimSystem:** All births, deaths, predation, and starvation in distant chunks are invisible. Cannot audit or debug the population math.

### P1 — High

4. **Predator carrying capacity too low:** `totalPopulation × 0.15f` kills off predator diversity. Entity sim sustains 5+ predator species; stat sim sustains 1-2.

5. **Omnivore/dual-role species not modeled:** Boar (predator + litter breeder + grazer) is 0 in both stat sim runs but 100-148 in entity sim.

6. **Fish/aquatic species reproduction broken:** Fish crashes to 0 in both stat sim runs. Likely a grazeable-tiles or carrying-capacity bug for water-dwelling species.

### P2 — Medium

7. **Lotka-Volterra kill rate underestimates hunting pressure:** No modeling of pack tactics, ambush bonuses, or species-specific hunt efficiency. Flat `HungerDecayRate / EffectiveNutrition` formula.

8. **No terrain/biome death in statistical sim:** Entity sim has 160-232 environment deaths per run. Stat sim doesn't model drowning/suffocation for distant populations.

9. **Population cap reliance:** Stat sim always hits 2,000 hard cap. Entity sim sometimes doesn't (1,860 on seed 32). Stat sim lacks the natural top-down pressure that keeps entity populations in check.

---

## Recommended Next Steps

1. **Add event logging to StatisticalSimSystem** — log aggregated births, deaths (by cause), and predation events per tick interval. This is prerequisite for all further debugging.

2. **Fix starting population** — audit the tick-0 aggregation pipeline to find where ~48% of creatures are lost.

3. **Address Shroomer asymmetry** — options:
   - Include Shroomer in statistical aggregation (simplest)
   - Apply a statistical predation multiplier to Shroomer based on distant predator counts
   - Cap Shroomer growth rate in distant chunks to match entity-sim observed rates

4. **Species-specific carrying capacity** — replace the flat `0.15f` predator ratio with per-species capacity based on diet, hunting efficiency, and prey availability.

5. **Aquatic species audit** — verify that Fish carrying capacity counts water tiles correctly.

---

# Sessions 15-18: Post-Fix Validation (Feb 21)

**Date:** 2026-02-21
**Sessions analyzed:** 15-18
**Purpose:** Validate whether fixes from commits `da95d74`, `c1ea121`, `294be57` resolved the issues found in Sessions 11-14

## Test Matrix

| Session | Timestamp | Mode | Ticks | Events | Start Pop | Final Pop | Shroomer % |
|---------|-----------|------|-------|--------|-----------|-----------|------------|
| S15 | 073456 | Entity | 39,432 | 22,924 | 574 | ~1,996 | **35%** |
| S16 | 081050 | Entity | 38,525 | 20,855 | 589 | ~2,000 | **37.5%** |
| S17 | 084402 | **Statistical** | 31,721 | 31,087 | 128 | ~2,000 | **96%** |
| S18 | 091049 | **Statistical** | 24,943 | 11,977 | 132 | ~1,938 | **94%** |

**Key change from S11-S14:** Event logging is now active in StatisticalSimSystem (fix `da95d74`), enabling direct audit of stat sim birth/death decisions. S17 and S18 event logs contain `stat_birth`, `stat_age_death`, `stat_kill`, and `stat_starvation` events.

---

## CRITICAL: Shroomer Monoculture is Worse, Not Better

The fixes improved some aspects (event visibility, Fish/omnivore bugs) but the core Shroomer dominance problem has **intensified**:

| Metric | S11-14 (pre-fix) | S15-18 (post-fix) | Trend |
|--------|:-:|:-:|:-:|
| Shroomer % (entity sim) | 15% | 35-37.5% | Worsened (2.3x) |
| Shroomer % (stat sim) | 45-51% | **94-96%** | **Worsened (1.9x)** |
| Shroomer amplification (stat/entity ratio) | 3.0-3.4x | **2.5-2.7x** | Slightly improved |
| Species extinct (entity sim) | 7-9/28 | 8-11/28 | Similar |
| Species extinct (stat sim) | N/A (no data) | **13-20/28** | Severe |

The stat-to-entity amplification ratio is slightly lower (2.5-2.7x vs 3.0-3.4x), but both baselines are much higher, likely due to different world seeds or configuration changes.

---

## 9. Event-Level Confirmation of Stat Sim Behavior

### 9.1 Event Type Distribution (Stat Sim Sessions)

S17 and S18 now log stat sim events. The event composition confirms the two-tier architecture:

**Session 17 (31,087 total events):**

| Event Type | Count | % |
|---|---:|---:|
| stat_age_death | 5,988 | 19.3% |
| stat_birth | 5,842 | 18.8% |
| stat_kill | 4,665 | 15.0% |
| spore_created | 4,438 | 14.3% |
| reproduce | 3,985 | 12.8% |
| spore_matured | 3,415 | 11.0% |
| starvation | 1,202 | 3.9% |
| stat_starvation | 498 | 1.6% |
| environment_death | 331 | 1.1% |
| hunt_start | 290 | 0.9% |
| kill | 246 | 0.8% |
| hunt_fail | 118 | 0.4% |
| age_death | 69 | 0.2% |

Stat events account for 54.7% of S17 events. No `aggregate` or `deaggregate` events exist.

**Session 18 (11,977 total events):**

| Event Type | Count | % |
|---|---:|---:|
| spore_created | 2,888 | 24.1% |
| reproduce | 2,653 | 22.2% |
| spore_matured | 2,290 | 19.1% |
| stat_age_death | 1,309 | 10.9% |
| stat_birth | 1,060 | 8.9% |
| stat_kill | 607 | 5.1% |
| stat_starvation | 474 | 4.0% |
| starvation | 293 | 2.4% |
| environment_death | 223 | 1.9% |
| hunt_fail | 80 | 0.7% |
| hunt_start | 48 | 0.4% |
| kill | 43 | 0.4% |
| age_death | 9 | 0.1% |

### 9.2 Shroomer Has Zero Stat Events

Confirmed in both S17 and S18: **every Shroomer event is a real entity event**. Zero `stat_birth`, `stat_death`, `stat_kill`, or `stat_starvation` for Shroomer. This is the designed behavior (terraformers excluded from aggregation) but it's the root cause of the monoculture.

### 9.3 Stat Sim Species Breakdown (S17)

Species can be categorized by their stat-simulation percentage:

| % Stat-Simulated | Species |
|:-:|---|
| 100% | Fish, Parrot, Snake, Tapir |
| 93-98% | Deer, Elk, Turtle, Fox |
| 78-88% | Arctic Fox, Camel, Lizard, Shark, Boar, Polar Bear, Rabbit |
| 50-75% | Bear, Frog, Jaguar, Monkey, Hawk, Musk Ox, Scorpion |
| 37-46% | Penguin, Crocodile, Wolf |
| **0%** | **Shroomer, Sectid, Faeling** |

---

## 10. The Predation Collapse (Confirmed with Event Data)

### Shroomer Predation Comparison

| | S15 (entity) | S16 (entity) | S17 (stat) | S18 (stat) |
|---|---:|---:|---:|---:|
| Shroomer killed by predators | 845 | 773 | **79** | **10** |
| Primary predator | Wolf (452) | Polar Bear (447) | Wolf (78) | Sectid (7) |
| Predator species hunting Shroomer | 7 | 6 | **1** (Wolf only) | **2** (Sectid, Faeling) |

Predation on Shroomer drops **90-99%** when stat sim is ON. This is because predator species go extinct in statistical chunks via `stat_starvation` within the first 3,000-5,000 ticks.

### Predator Extinction Timeline (S17)

12 of 13 extinct species were eliminated by `stat_starvation`:

| Tick | Species | Deaths | Cause |
|---:|---|---:|---|
| 900 | Parrot | 9 | stat_starvation |
| 1,290 | Rabbit | 29 | stat_starvation (25) + stat_kill (4) |
| 1,290 | Jaguar | 4 | stat_starvation |
| 1,350 | Lizard | 21 | stat_starvation (12) + stat_kill (9) |
| 1,380 | Hawk | 6 | stat_starvation |
| 1,590 | Bear | 3 | stat_starvation |
| 1,800 | Shark | 4 | stat_starvation |
| 2,490 | Snake | 4 | stat_starvation |
| 2,640 | Arctic Fox | 7 | stat_starvation |
| 3,359 | Sectid | 30 | kill by Shroomer (22) + starvation |
| 3,510 | Crocodile | 3 | stat_starvation |
| 4,814 | Scorpion | 4 | stat_starvation |
| 21,063 | Faeling | 41 | All killed by Shroomer |

**All predators extinct by tick 4,814.** Only Wolf (partially real at 46% stat) survives long enough to hunt Shroomers (78 kills), but even Wolf stops hunting after tick 25,000.

Compare to entity sim S15: Jaguar (last tick 38,900+), Polar Bear (38,900+), Bear (38,900+), Wolf (38,900+), Boar (38,900+) — all survive to session end.

---

## 11. The Death Spiral Mechanism (Confirmed)

The event data confirms the four-phase mechanism:

### Phase 1: Stat Starvation Mass Extinction (ticks 0-5,000)

The stat sim's carrying capacity formula calculates that most predator populations are unsustainable at the low starting population (~128 entities). `stat_starvation` events eliminate 11-12 species in the first 5,000 ticks. This happens because:
- Starting population is split across many chunks → very few entities per chunk
- Predator carrying capacity (`herbivoreCount * 0.2`) rounds to near-zero in sparse chunks
- The stat sim expresses this as immediate death events with no migration or adaptation

### Phase 2: Unopposed Shroomer Growth (ticks 5,000-10,000)

With predators gone, Shroomer's birth-to-death ratio climbs to 10-13:1. Spore maturation rate holds steady at ~77-80%. The only mortality is starvation and drowning.

### Phase 3: Exponential Shroomer Explosion (ticks 10,000-25,000)

Peak net growth: +787 Shroomers per 5,000 ticks (S17) and +458 per 2,500 ticks (S18). Shroomer terraforming converts terrain to Wetland, expanding its own habitat while shrinking habitat for other species.

### Phase 4: Carrying Capacity Plateau (ticks 25,000+)

Starvation (74%) and drowning (21%) become the primary death causes. Net growth drops to near-zero. Shroomer fills 94-96% of the 2,000 population cap.

---

## 12. Entity Sim Comparison: Self-Regulating Ecosystem

### S15 (Entity Sim) — Healthy Dynamics

| Metric | Value |
|---|---|
| Shroomer births | 2,274 |
| Shroomer deaths (predation) | 845 (37.1% of births) |
| Shroomer deaths (starvation) | 636 |
| Shroomer deaths (environment) | 222 |
| Shroomer net growth | +569 |
| Top predator: Wolf→Shroomer | 452 kills |
| Predator species surviving | 7 (Wolf, Polar Bear, Jaguar, Bear, Boar, Crocodile, Faeling) |
| Shroomer growth self-regulated? | **Yes** — net growth oscillates around zero after tick 20,000 |

### S16 (Entity Sim) — Similar Pattern

| Metric | Value |
|---|---|
| Shroomer births | 2,782 |
| Shroomer deaths (predation) | 773 (27.8% of births) |
| Shroomer deaths (starvation) | 1,067 |
| Shroomer net growth | +672 |
| Top predator: Polar Bear→Shroomer | 447 kills |
| Shroomer growth self-regulated? | **Yes** — net negative in late game (-91 in final third) |

**Key insight:** In entity sim, Shroomer growth follows a textbook logistic curve and self-limits. Predation removes 28-37% of Shroomer births. Multiple predator species coexist and provide redundant population control. The ecosystem produces a diverse community of 17-20 species.

### Comparison: Wolf→Shroomer Kill Rate

| Session | Wolf→Shroomer Kills | Shroomer Births | Kill/Birth Ratio |
|---|---:|---:|---:|
| S15 (entity) | 452 | 2,274 | **19.9%** |
| S16 (entity) | 223 | 2,782 | **8.0%** |
| S17 (stat) | 78 | 3,415 | **2.3%** |
| S18 (stat) | 0 | 2,290 | **0%** |

Wolf removes 8-20% of Shroomer births in entity sim but only 0-2.3% in stat sim. This single predator-prey relationship explains much of the divergence.

---

## 13. Faeling: The Control Experiment

Faeling is uniquely informative because it's also excluded from aggregation (0% stat-simulated) like Shroomer. In S17:

- **41 births, 41 deaths** — perfect equilibrium for 21,000 ticks
- **100% of deaths caused by Shroomer** AoE attacks
- Every crystal respawn was answered by Shroomer territorial killing
- Went extinct at tick 21,063 when Shroomer reached peak saturation

This demonstrates that even real-entity species cannot coexist with Shroomer at scale. Faeling's balanced terraform (shifting tiles toward Grass) cannot counteract Shroomer's wet terraform fast enough, and Faeling's ranged attacks (12 damage, 20 tick cooldown) are insufficient against Shroomer's S-curve AoE (2.4→30 damage, 2→25 tile radius).

---

## 14. Updated Priority List

### P0 — Critical (Unchanged but Confirmed)

1. **Shroomer asymmetry** — now confirmed with full event data. The 90-99% predation drop is the primary driver. **Recommended fix: Freeze-on-aggregate for terraformers** (roadmap Section 5.5). When chunk goes distant: snapshot faction population, pause all activity, restore exactly on materialization. No growth, no death, just stasis. This eliminates the asymmetry.

2. **Starting population deficit** — still ~78% reduction (574→128). The prior ~48% deficit was for different seeds; the magnitude varies but the problem persists.

### P1 — High (Updated with New Evidence)

3. **Predator carrying capacity** — `stat_starvation` kills 11-12 species in the first 5,000 ticks. The formula needs:
   - A minimum population floor (prevent extinction to zero from carrying capacity alone)
   - Multi-chunk predator range (predators hunt across chunk boundaries)
   - Slower starvation ramp (entity sim predators survive 7,000-22,000+ ticks; stat sim kills them in 900-3,500 ticks)

4. **Shroomer-specific population pressure** — even with entity sim, Shroomer reaches 35-42%. Consider:
   - Density-dependent spore suppression (fewer spores when many nearby Shroomers)
   - Per-species population cap for terraformers (max 30% of total)
   - Increased predator prey preference for Shroomer

### P2 — Medium (New)

5. **stat_starvation death rate too aggressive** — entity sim equivalent species survive 5-20x longer than their stat sim counterparts. The stat model should dampen starvation mortality or model inter-chunk migration before killing.

6. **Fish stat sim cycling** — Fish has enormous stat sim throughput (S17: 21,067 stat births, 18,626 stat deaths) but remains trapped in the statistical layer. Verify this represents actual ecosystem behavior and not a numeric oscillation.
