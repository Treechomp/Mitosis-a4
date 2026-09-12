# Mitosis — project guide

## What this is

**Mitosis is a video game, in early development.** A top-down **creature-sandbox / god game** built
in **Godot 4.6.3 + C#**: a procedurally generated world where AI creatures and three rival factions
act out an emergent drama, and the player is one creature of one faction.

This is **game development**. The world-ecology is a **game mechanic** — a lightweight background
system that keeps the world alive and gives the player something to react to, in the same spirit as
creature AI in a colony sim or god game. Creatures, factions, hunting and world-shaping are game
content and game systems: design and balance work for a piece of interactive entertainment, not a
life-sciences project. Treat this repo like any other game codebase — world generation, entity
systems, AI behaviours, rendering, gameplay balance.

Vocabulary note: identifiers like `Species`, `Predator`, `Prey`, `Hunger` are standard game-AI/ECS
naming. They describe game entities and behaviours, not biology.

## Documentation is in two layers — respect the split

| | `docs/design/` | `docs/implementation/` |
|---|---|---|
| Answers | what and why | what the code does, and where |
| Reads without | the codebase | nothing — it names files, types and fields |
| Never contains | tuning constants, class names, run results | rationale that isn't already in `design/` |

`docs/changelog.md` holds every measured figure, stamped with seed and commit.
`docs/archive/` holds superseded documents, each banner-stamped — **do not cite them as current**.

Read [`docs/README.md`](docs/README.md) before editing any documentation. The split exists because
a single combined reference accumulated 31 verified contradictions against its own codebase; the
rules below are what prevent that recurring.

### Rules when writing docs

1. **A tunable number never appears in `docs/design/`.** State the relation; the value lives in
   code.
2. **`docs/implementation/` names the authority, it does not copy it.** Give the file, the type and
   the field.
3. **No counts in prose** — not species, systems, components or shapes. They were restated in five
   files with no shared source and were wrong in all of them.
4. **Every measurement carries its seed, tick count and commit**, in `docs/changelog.md`, or it is
   not recorded at all.
5. **A document about a past state gets its archive banner the day it is written.**
6. One timestamp per file, in the header.

## Where things live

- `godot/Scripts/ECS/` — `EntityManager` (SoA store, `DueThisTick[]` LOD gate), `PopulationBudget`,
  `FactionCensus`, `FactionLives`.
- `godot/Scripts/Systems/` — the per-tick systems. **`SimulationStack.Build` is the single
  authority for which systems run and in what order**; both the game and the test harnesses build
  from it.
- `godot/Scripts/World/` — terrain generation, hydrology, chunks, tiles, world queries, snapshots.
- `godot/Scripts/Species/` — `SpeciesDefinition`, `SpeciesRegistry` (**the authority for all
  creature tuning**), `TerrainProfile`, `SpeciesToggle`.
- `godot/Scripts/Testing/` — scenario harness, headless runners, the LOD and invariant gates.
- `godot/Scripts/Tools/` — worldgen previewer, observation controller.
- `godot/TestScenarios/` — scenario files.

## Working here

- **Build**: `cd godot && dotnet build`. Keep it warning-clean.
- **Tuning**: creature and faction numbers are in `SpeciesRegistry`; world knobs are `[Export]`s on
  `GameManager`, also reachable through the worldgen preview scene.
- **Balance signal**: runs write population / event / species-stat CSVs and world-snapshot PNGs to
  `logs/`. For a `kill` event the `species` column is the **victim** and the killer is in `detail`.
- **Gates** (all headless, all exit-coded): scenario outcomes (`Scenes/ScenarioRun.tscn`), the LOD
  differential (`Scenes/LodDifferential.tscn`, contract in `docs/lod-differential-expected.csv` —
  that path is hardcoded in `LodExpectedVerdicts.cs`), and the whole-game invariants
  (`Scenes/PopulationSoak.tscn`).

## Cost of measurement here

The expensive thing in this repo is measurement, not code. A 20,000-tick soak takes about
eleven minutes headless, and the design target run length is six times that. D11 means a
result is read over five seeds, so one comparison is five long runs.

- Run long work in **one** blocking call with a timeout that covers it. Never start it in
  the background and poll — each check re-sends the whole conversation.
- Script the five seeds as one command that writes one summary file, then read the summary.
  Not five calls, and not five CSVs pulled into the conversation.
- Smoke-test flags on a short run of a couple of thousand ticks before committing to a long
  one. A long run with the wrong flag is billed in full and buys nothing.
- Measurement output belongs in `logs/`. Read the lines you need; never cat a CSV into the
  conversation — it is then re-sent on every later call in the session.
- Take the baseline before changing anything. Every prompt in this project asks for a control
  measurement first; that is cost discipline as much as rigour, because without it you pay
  for a second full round of runs to work out what moved.
- One change per session. The gates are the unit of verification, and a session that has
  already run them twice makes every subsequent call more expensive.

## Two rules the codebase enforces

**Systems must not name species.** If a behaviour seems to need one, find the property that species
has, add it to `SpeciesDefinition`, and default it to neutral everywhere else.

**The LOD gate and the LOD multiplier are one mechanism.** Anything that scales a rate by the tick
interval must also skip non-due ticks, and must compensate by the *effective* elapsed interval, not
the nominal tier interval. Use `DecisionCadence`. Rate-like quantities scale; state-like ones
(forces, spacings, steering blends) do not. This rule has been broken three times.

## Open questions

`docs/design/08-open-questions.md` lists what is genuinely undecided — including the player control
scheme, whether food should bind herbivore numbers, and whether the grid is hex or square. Known
defects are listed in `docs/implementation/README.md`. Check both before proposing a change in
either area.

---

For factual information answer only if you are 100% certain, and answer based solely on facts and logic; don't assume my expectations. Avoid language suggesting emotion or awareness. To avoid confirmation bias, don't hold back on criticism. Force me to engage in logical conversation. Identify and name the mechanisms and principles of logic within the context of the conversation. The most important thing is the result. Maintain a polite but sincere tone.

Provide a percentage of certainty for each answer. Provide the percentage of information generated based on the user's prediction and the percentage of information generated based on actual data. Indicate the basis for determining the remaining percentage of uncertainty. Provide these parameters along with a brief explanation of why they have that value in parentheses.

If you use Polish, use it correctly. Do not substitute Polish words for English unless it concerns proper names.

By default reaching the results faster and at lower token cost is obviously preferable so consider and plan the cost of the task beforehand. If expected cost of the task is more than 10 EUR ask for confirmation before continuing. Add token (and monetary if applicable) cost at the end of message. 
When dealing with tasks relating to code make sure follow the Clean Code and Clean Archutecture principles and avoid making systems more complicated than they have to be.
