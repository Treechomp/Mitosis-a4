# Behavior Arbitration — design & roadmap

How a creature prioritizes competing **drives** — hunger (forage/hunt), fear (flee), social
(herd/pack cohesion), terrain discomfort (escape). Target *selection* and *stats* are already
granular per species; the rigid layer is **arbitration**: the thresholds deciding *which drive
wins* were hardcoded constants scattered across systems. This doc inventories them and tracks
the incremental lift into per-species params (Option A), with the full utility-AI rewrite kept
as a future option (Option B).

## Current arbitration (priority order, per system)
There is no central arbiter — each system decides whether to act via early-`continue` guards, and
system *order* (in `GameManager`) resolves conflicts (later systems overwrite velocity):

- **HerdingSystem** yields to, in order: terrain-escape → fleeing → hunting/foraging (hungry herd
  members leave the huddle). Runs before Hunting/Fleeing.
- **HuntingSystem** acts when hunger < `HuntThreshold`, stops when ≥ `SatedHunger`; tracks distant
  prey below `TrackingHungerThreshold`; gives way to fear because **FleeingSystem runs after it**.
- **FleeingSystem** (runs last of the behavior systems) commits to a response when a threat is in
  range or fear > `FleeFearThreshold`; can blend terrain-escape into the flee vector.
- **WanderSystem** migrates toward food when hunger < `ForageHungerThreshold` and no food/prey is
  underfoot; escapes uncomfortable terrain via discomfort hysteresis.

## Inventory of arbitration thresholds
`✅` lifted to a per-species param this pass · `•` still hardcoded (candidate for later).

| Threshold (was) | Location | Meaning | Status |
|---|---|---|---|
| `0.7` migrate-to-food hunger | Wander `ShouldStartRoaming` (predator + grazer), roam-dir trigger | forage/migration urgency | ✅ `ForageHungerThreshold` |
| `0.4` / `0.6` herd-release hunger | Herding priority-3 | hungry herd member leaves huddle to forage | ✅ `ForageHungerThreshold` (aligned so herd-release never fights the forage roam) |
| `0.5` flee-response trigger | Fleeing | fear ratio at which prey commits to flee/panic/freeze | ✅ `FleeFearThreshold` |
| `0.95` sated | Hunting | hunger ratio at which a predator stops hunting | ✅ `SatedHunger` |
| `HuntThreshold` / `TrackingHungerThreshold` | Hunting | start hunting / long-range track | (already per-species) |
| `0.7` flee speed-boost fear | Fleeing | extra speed when very afraid | • |
| `0.8` terrain-escape-vs-fear | Fleeing | blend terrain escape into flee only below this fear | • |
| `0.8` / `0.9` mob-commit fear | Fleeing | defensive rally / hold-ground | • |
| discomfort hysteresis `0.6`/`0.1` | Wander | enter/exit terrain escape | • |
| `RallyAllyThreshold` (2), `RallyRangeMult` | Hunting | defensive-rally trigger | • (per-species candidate) |

## Option A — implemented (this pass)
Three per-species params, **defaults equal to the old constants** (zero behavior change unless a
species overrides):

- **`ForageHungerThreshold`** (default 0.7) — hunger ratio below which a creature migrates toward
  food, and a herd predator leaves the huddle. Wired in Wander (3 sites) + Herding (2 sites,
  aligned so the herd-release and the food-seeking roam trigger at the *same* point — this was the
  penguin deadlock: forage wanted 0.7, herd held until 0.4).
  - **Penguin = 0.8** — disperses/forages early to stay coupled to offshore fish.
- **`FleeFearThreshold`** (default 0.5) — fear ratio to commit to a flee response. Wired in Fleeing.
  - **Rabbit = 0.35** (frail, jumpy), **Musk Ox = 0.7** (bold defensive herd).
- **`SatedHunger`** (default 0.95) — hunger ratio at which a predator stops hunting. Wired in Hunting.

### Remaining Option-A candidates (lift on demand, same pattern)
Flee speed-boost fear, terrain-escape-vs-fear blend, mob-commit fear, discomfort hysteresis,
rally trigger. Each is a `SpeciesDefinition` field defaulting to today's constant; lift when a
species needs it rather than pre-emptively.

## Option B — utility-AI rewrite (future, kept in mind)
Replace the scattered `continue`-guard arbitration with one per-creature arbiter that scores every
drive each tick (hunger→forage/hunt, fear→flee, social→cohere, discomfort→escape) from per-species
weight/curve params and executes the highest-utility action. Collapses behavior *selection* out of
Hunting/Fleeing/Herding/Wander into a single system; those become pure *executors* of the chosen
action.

- **Pros:** maximally granular and composable; new behaviors are new scoring terms; no more
  threshold-ordering bugs like the penguin deadlock.
- **Cons:** weeks of work; re-opens every balance decision (all the per-species tuning) at once;
  needs a re-derivation of curves that currently live implicitly in the thresholds above.
- **Trigger to reconsider:** when the Option-A per-species knobs start *fighting each other*
  (adjusting one to fix a species breaks another), that's the signal the arbitration genuinely
  needs to be unified rather than tuned.
