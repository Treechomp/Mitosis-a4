# Species Stat & Behavior Attribution Audit

Working document for the attribution pass. Goal: stop the ecosystem collapsing into a few
dominant strategies by giving species distinct, viable niches. Based on the **no-faction run**
(`20260617_224800`, 28.2k ticks, Sectid/Shroomer/Faeling disabled).

Status legend for proposals: ☐ not started · ◐ in progress · ☑ done.
Authoritative tuning lives in `SpeciesRegistry.cs`; this doc is the design spec.

---

## 1. What the no-faction run showed

- **No top-down control.** Whole-run deaths: **age 1906**, starve 947, predation 527, env 233.
  Herbivores die of old age, not predators — the world grows to the population cap, not an
  ecological one. Deer 206→791, Elk 166→745, Monkey 119→425, Rabbit→491.
- **Predator guild collapses to two winners.** Kills made over the run:

  | Predator | Kills | End pop | Fate | | Predator | Kills | End pop | Fate |
  |---|---|---|---|---|---|---|---|---|
  | Crocodile | 234 | 64 | thriving | | Jaguar | 7 | 1 | dying |
  | Wolf | 168 | 48 | stable | | Bear | 5 | 1 | dying |
  | Arctic Fox | 59 | 16 | declining | | Fox | 4 | 0 | **extinct** |
  | Hawk | 46 | 0 | **extinct** | | Snake | 2 | 0 | **extinct** |
  | | | | | | Shark | 0 | 1 | dying *(fixed)* |
  | | | | | | Scorpion | 0 | 1 | dying |
  | | | | | | Polar Bear | 0 | 4 | dying |

  **Every losing predator dies of starvation (~0 predation).** They can't make enough kills.
- **Crocodile is over-dominant** — 234 kills across 8+ species (semi-aquatic ambush taxes
  everything near water).
- **Shark/aquatic hunting was broken** — fixed (`b53d94c`): aquatic predators were routing
  around their own element. Needs a re-run to confirm recovery.

---

## 2. Cross-cutting mechanics (new)

### M1 — Depth-aware water model  ☑
Tiles already distinguish `ShallowWater` / `River` / `Reef` vs `DeepWater`.
- **Hunting/pathing:** land creatures route around **deep** water only; **wade shallow/river**
  freely. True water-avoiders (insects — Sectid, Scorpion) get an explicit `AvoidsWater` and
  route around *all* water. Aquatic/semi-aquatic never avoid (already via `AvoidsOpenWater`).
- **Drowning (`TerrainSystems`):** keyed off depth — deep water drowns land creatures; shallow/
  river is safe or long-grace, so wading to fish isn't fatal.
- Enables Bears/others to fish at the water's edge; keeps wolves crossing rivers but not lakes.

### M2 — Prey flee stamina  ☑
Prey flee at full `FleeSpeedMultiplier` (burst), which **decays over sustained fleeing** toward
a tired floor (~walk pace) and **recovers at rest**. Stops deer/rabbits outrunning an endless
relay of predators. New `Prey` stamina fields + per-species `FleeBurst*/StaminaDecay/Recovery`
params. Primary effect on the fast grazers (Deer, Rabbit, Elk, Tapir, Monkey, Parrot, Lizard).

---

## 3. Per-species proposals

Current values (from `SpeciesRegistry.cs`): Tactic, BodyMass, HP(`MaxEnergy`), HuntSpd
(`BaseHuntSpeed`), Flee(`FleeSpeedMultiplier`), RepHT/MaxH (`ReproHungerThreshold`/`MaxHunger`,
shown as %), decay (`HungerDecayRate`), Elem (Aq/Semi/Fly).

### Predators

| Species | Now | Issue (run) | Proposed |
|---|---|---|---|
| **Crocodile** | Ambush, m8, hunt .08, RepHT 80%, Semi, decay .03 | 234 kills / 8+ species — apex monopoly | Narrow to near-water prey; trim hunt/track range so it can't tax the whole map. |
| **Wolf** | PackCoord, m3.5, hunt .12, RepHT 93% | Healthy (168 kills, breeds) | Keep. Reference for a working predator. |
| **Fox** | Solo, m2.0, hunt .11, RepHT 100%(!), decay .035, pack .1 | 4 kills, extinct | **→ Ambush + scavenger.** Add stealth/pounce; lean on carrion. Drop stray `PackHunterChance`. Lower RepHT (180/180 = 100% is unreachable). |
| **Bear** | Solo, m12, hunt .09, RepHT 86%, decay .04 | 5 kills, dying | **Burst speed** (pounce-style high burst); **shallow-water fishing** (M1). Lower RepHT. |
| **Polar Bear** | Solo, m14, hunt .10, RepHT 87%, Semi | 0 kills | Prey access (arctic Penguin/Fish); shallow-water fishing; burst; lower RepHT. |
| **Hawk** | Solo, m1.5, hunt .15, RepHT 87%, **decay .05 (highest)**, Fly | 46 kills yet extinct | Highest decay starves it despite kills → **lower decay** to ~.035; raise founding pop. |
| **Jaguar** | Ambush, m7, hunt .12, RepHT 88% | 7 kills, dying | Lower RepHT; verify ambush stealth lands kills; prey access (tropical). |
| **Snake** | Ambush, m1.2, hunt .10, RepHT 86%, venom | 2 kills, extinct | Lower RepHT; venom should secure kills — check it applies; founding pop. |
| **Scorpion** | Ambush, m0.8, hunt .07, RepHT 85%, venom | 0 kills | Desert prey access (Lizard/Camel young); `AvoidsWater` (M1); lower RepHT. |
| **Shark** | Solo, m10, hunt .14, RepHT 89%, Aq, decay .025 | 0 kills → **fixed** | Re-run to confirm; if still low, check Fish flee speed in water. |
| **Arctic Fox** | Solo, m1.8, hunt .12, RepHT 88%, decay .04 | 59 kills, slowly declining | Closest to viable; small RepHT cut + M2 (catch tiring prey) likely tips it positive. |

**Cross-predator theme:** reproduction thresholds sit at **86–100% of MaxHunger** — a predator
must be nearly gorged to breed. With infrequent kills, solo hunters never get there. Propose
lowering predator `ReproHungerThreshold` to ~**70–75%** so a good meal or two enables breeding.

### Herbivores / prey

| Species | Now | Note | Proposed |
|---|---|---|---|
| Deer, Elk, Tapir | Flee 2.0–2.2, large herds | Outrun predators indefinitely; boom | **M2 stamina.** |
| Rabbit | Flee 2.4, frail (HP40), fast-breed | Working as prey | M2 (shorter burst, quick tire). |
| Monkey, Parrot, Lizard, Frog | Flee 2.3–2.6 | Fast, hard to catch | M2; small-prey for ambushers. |
| Fish | Flee 2.5, Aq | Shark food | Verify in-water flee isn't uncatchable (Shark dependency). |
| Camel, Musk Ox, Turtle | Flee 1.3–1.8, tanky | Stable, slow | Minor; low priority. |
| Penguin | Flee 2.0, Semi | Arctic prey base | Ensure co-located with Polar Bear/Arctic Fox. |
| Boar | Omnivore, PackCoord, defensive | Stable | Keep. |

---

## 4. Open balance items (beyond stat attribution)

- **Herbivore over-reproduction / no ecological ceiling.** Even a healthy predator guild may not
  fully check the grazer boom. Candidate (separate from this pass): density-dependent
  reproduction — suppress births as local crowding / grazing depletion rises.
- **Biome prey co-location.** Polar Bear & Scorpion logging 0 kills suggests their biome prey
  isn't spawned near them. May need minimum founding populations or co-located spawns.

---

## 5. Proposed implementation order (incremental)

1. **M1 — depth-aware water model** (wade/drown by depth; `AvoidsWater` for insects). Unblocks
   Bear fishing + confirms Shark. ☑ Done — land creatures wade shallow/river and only drown in
   deep water; Sectid/Scorpion (`AvoidsWater`) route around and drown in any water; hunting,
   scavenging and drowning are all depth-aware. (Also landed: Rabbit repro energy budget fix and
   `HungerDecayScale` → 0.3.)
2. **Fox → ambush/scavenger; Bear → burst + shallow fishing** (depends on M1). ☑ Done — Fox is
   now an `Ambush` predator (quick low-commitment pounce) that leans on carrion, with RepHT
   180→120 and the stray `PackHunterChance` removed; Bear is an `Ambush` charger (high
   `PounceSpeedMult` burst), fishes shallows (Fish added to preferred prey, wading via M1), RepHT
   300→245.
3. **M2 — prey flee stamina.** ☑ Done — `Prey.Stamina` (0..1) drains while fleeing
   (`FleeStaminaDrain`), recovers at rest (`FleeStaminaRecovery`), and the flee burst fades toward
   `FleeTiredSpeedFloor` of `FleeSpeedMultiplier` as it empties. Applied to Flee + Panic responses.
4. **Predator viability audit fixes** (RepHT cuts, Hawk decay, Crocodile nerf, biome prey).

Each lands as its own commit for testing between steps.
