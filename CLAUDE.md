# Mitosis — project guide

## What this is

**Mitosis is a video game, in early development.** It's a top-down **creature-sandbox / god-game**
built in **Godot 4.6.3 + C#**: you roam a procedurally generated game world where AI creatures and
three rival factions act out an emergent, watchable drama the player can nudge and reshape.

This is **game development**. The world-ecology under the hood is a **game mechanic** — a
lightweight background system that keeps the world feeling alive and gives the player something to
react to, in the same spirit as the creature AI in a colony-sim or god-game. The creatures,
factions, hunting, and world-shaping are **game content and game systems** — design and balance
work for a piece of interactive entertainment, not a life-sciences or research project. When
reasoning about this repo, treat it like any other game codebase: world generation, entity
systems, AI behaviors, rendering, and gameplay balance tuning.

Current focus areas: procedural terrain/world generation, creature spawning & distribution, and
tuning the three-way faction game balance. Player-facing gameplay (progression, observation tools,
directed creature evolution) is upcoming — see `docs/godot-roadmap.md`.

## Where things live

- `godot/Scripts/World/` — terrain/world generation (`TerrainGenerator`, `RiverMapper`,
  `WorldManager`, `Chunk`, `TileType`) + the in-editor world previewer (`Tools/WorldgenPreviewer`).
- `godot/Scripts/Systems/` — the per-tick game systems (movement, AI, hunting, fleeing, factions).
- `godot/Scripts/Species/` — data-driven creature definitions (`SpeciesRegistry`, `SpeciesDefinition`).
- `godot/Scripts/ECS/` — the Structure-of-Arrays entity store that runs thousands of creatures cheaply.
- `godot/Scripts/Testing/` — the test-scene harness (this branch): small scenario-defined worlds,
  exact spawns, extended logging. Run `Scenes/TestScene.tscn` (F6); scenarios live in
  `godot/TestScenarios/`; see `docs/test-scenes.md`.
- `docs/` — design docs. Start with `FEATURES_AND_DESIGN.md` (systems/creatures/terrain reference)
  and `godot-roadmap.md` (status). `faction-balance-plan.md` tracks the current balance workstream.

## Working here

- **Build**: `cd godot && dotnet build` (Godot.NET.Sdk 4.6.3). Keep it warning-clean.
- **Tuning**: creature/faction numbers live in `Species/SpeciesRegistry.cs`; terrain knobs are
  `GameManager` `[Export]`s (editable in the inspector, or via the world previewer scene, F6).
- **Balance loop**: runs dump CSVs (population / events / species stats) + world-snapshot PNGs to
  `logs/`; those are the primary signal for tuning game balance.
- Vocabulary note: identifiers like `Species`, `Predator`, `Prey`, `Hunger` are standard game-AI/ECS
  naming — they describe game entities and behaviors, not biology.

For factual information answer only if you are 100% certain, and answer based solely on facts and logic; don't assume my expectations. Avoid language suggesting emotion or awareness. To avoid confirmation bias, don't hold back on criticism. Force me to engage in logical conversation. Identify and name the mechanisms and principles of logic within the context of the conversation. The most important thing is the result. Maintain a polite but sincere tone.

Provide a percentage of certainty for each answer. Provide the percentage of information generated based on the user's prediction and the percentage of information generated based on actual data. Indicate the basis for determining the remaining percentage of uncertainty. Provide these parameters along with a brief explanation of why they have that value in parentheses.

If you use Polish, use it correctly. Do not substitute Polish words for English unless it concerns proper names.

When dealing with tasks relating to code make sure follow the Clean Code and Clean Archutecture principles and avoid making systems more complicated than they have to be.
