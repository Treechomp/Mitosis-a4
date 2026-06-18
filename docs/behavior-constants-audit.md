# Behavior Constants Audit — data ↔ logic separation

Goal (per request): keep a single species' stats and behavior *specifics* on the species
(`SpeciesDefinition`) rather than baked into shared system logic, so tuning one species can't
silently change every other species of the same type. The data↔logic split is already good
(systems read `SpeciesDefinition`); this audit targets the **leftover hardcoded knobs and
species-specific branches** still living inside systems.

Status: ☑ done · ◐ partial · ☐ proposed.

## Antipattern 1 — species-specific branches in shared systems
A system testing a concrete species/type and applying a magic number is the exact thing to
avoid: it couples that species to shared code.

| Location | Branch | Fix | Status |
|---|---|---|---|
| `CarrionSystem` | `isSectid ? SectidChopRate : MaxHunger×0.02` | → `SpeciesDefinition.CarrionChopRate` (Sectid sets 6) | ☑ done (proof-of-pattern) |
| `CarrionSystem` | `isSectid` gates corpse-vs-prey priority + full-radius seek | → a per-species `ScavengerProfile`/flags (forager vs hunter) | ☐ |
| `HuntingSystem` | `KillNutritionShare = 0.6` applied to all predators | → per-species `KillNutritionShare` (big predators gorge more) | ☐ |

## Antipattern 2 — global behavior consts that should express species character
These are applied identically to every predator/prey. They're fine as *defaults*, but a per-species
override lets a tenacious bear differ from a flighty fox without touching shared code.

| Const (system) | Current | Meaning | Recommend |
|---|---|---|---|
| `HuntReevalInterval` 150 (Hunting) | global | how long a predator persists before giving up | per-species (hunt tenacity) |
| `HuntSelfDamageBailFraction` 0.4 (Hunting) | global | self-damage % before bailing | per-species (bravery) |
| `HuntMinProgress` 5 / `HuntAvoidDuration` 600 (Hunting) | global | give-up sensitivity / blacklist time | per-species (low priority) |
| `SeekRadius` 18 (Carrion) | global, hunger-scaled | how far a scavenger ranges for carrion | per-species (scavenger vs pure hunter) |
| sated threshold `0.95` (Hunting/Carrion) | hardcoded | hunger % at which hunting/scavenging stops | per-species (low priority) |
| Rally* (`RallyAlertDuration`/`AllyThreshold`/`RangeMult`) | global | mob/defense triggers | per-species (pack vs swarm differ) |

## Keep global (engine / world constants — NOT per-species)
`MaxHuntCandidates` (perf cap), `SporeBodyMass`, Carrion `GraceTicks`/`RotTicks`/`ConditionFloor`/
`DecompositionEnrich`/`EatRange`/`FeedCommitTicks`/`MinCorpseNutrition`, Wander `TurnRate*`,
`HungerDecayScale` (deliberately a single global dial). These describe the world/engine, not a
species' character, so centralising them is correct.

## Recommendation
Migrate Antipattern-1 branches first (they're correctness/coupling smells), then the
Antipattern-2 consts on demand as balance needs arise — each becomes a `SpeciesDefinition` field
with the current constant as its default, so existing behavior is preserved until a species
overrides it. `CarrionChopRate` is implemented as the reference pattern.
