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
