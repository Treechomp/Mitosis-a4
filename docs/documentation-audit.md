# Documentation Audit — docs/ vs. source

> **Scope**: all 14 documentation files in the repo (12 in `docs/`, plus `README.md` and `CLAUDE.md`),
> 3,314 lines. **Verified against**: `dabca20` on branch `claude/game-test-scene-gen-2jkwur`.
>
> **Method**: full read of the eleven documents that describe current behaviour; every numeric claim
> checked against source. Tuning tables, trait flags and behaviour groupings were dumped from
> `SpeciesRegistry` and the component enums by reflection rather than read by eye.
>
> **Nothing in this audit has been fixed.** It is a findings list only — the corrections are a
> separate pass. See the *Fix order* section at the end for a suggested sequence.

---

## Summary

| Severity | Count | Meaning |
|----------|-------|---------|
| Critical | 10 | Would misdirect a tuning or design decision |
| High | 10 | Plainly wrong; low blast radius |
| Housekeeping | 11 | Structure, navigation, framing |
| **Total** | **31** | across 8 of 14 files |

Findings can span files, so the per-file column below sums past 31 — the counts family alone lands
on five documents at once. The pattern is consistent: the narrow, recently written files hold up;
every file that tries to summarise the whole project has drifted.

| File | Findings | Condition |
|------|---------:|-----------|
| `docs/FEATURES_AND_DESIGN.md` | 24 | Accurate in bulk; errors cluster in tuning tables and in the sections edited most recently |
| `docs/godot-roadmap.md` | 5 | Lists this branch's shipped work as future work |
| `docs/architecture.md` | 4 | Its canonical system-order list is missing a system; coordinate model describes removed code |
| `README.md` | 2 | Counts family; omits the test-scene harness |
| `docs/terrain-handling-audit.md` | 1 | Historical snapshot written in the present tense, linked as current reference |
| `docs/terrain-profile-design.md` | 1 | Carries a claim inverted by the element barrier |
| `docs/faction-balance-plan.md` | 1 | Paused on a branch that has since delivered |
| `docs/test-scenes.md` | 1 | Otherwise current throughout — one incomplete enumeration |
| `CLAUDE.md` · `3d-terrain-plan.md` · `terrain-roadmap.md` | 0 | Verified clean. The terrain docs correctly record `river_density 0.2`, contradicting FEATURES §3 |
| `behavior-arbitration.md` · `behavior-constants-audit.md` · `species-attribution-audit.md` | 0 | Sampled, not exhaustively verified — all three are historical working documents |

Not audited: `docs/archive/` (superseded by definition) and prose claims about intent rather than
implementation.

---

## A. Tuning constants that contradict the code

*7 findings · 5 critical.* These are the ones that matter for balance work. Each is a number a
designer would read and act on, and each is wrong by enough to change the decision.

### 01 — The global starvation dial is documented at twice its real value · **CRITICAL**

| | |
|---|---|
| **Doc says** | `HungerDecayScale` **(0.6)** — "the primary lever for predator carrying capacity" — `FEATURES_AND_DESIGN.md:638` |
| **Code says** | `private const float HungerDecayScale = 0.3f;` — `Systems/SurvivalSystems.cs:24` |

The doc names this as the single most important knob for predator survival, and every species'
starvation rate is multiplied by it. Anyone reasoning from 0.6 is modelling a world that starves
twice as fast as the one that runs.

### 02 — River density stated at double, contradicting two other documents · **CRITICAL**

| | |
|---|---|
| **Doc says** | `TerrainRiverDensity` scales taste **(default 0.4)** — `FEATURES_AND_DESIGN.md:201` |
| **Code says** | `RiverDensity = 0.2f` in `TerrainSettings` and on the `GameManager` export — `World/TerrainSettings.cs:46`, `GameManager.cs:62` |

The same file's own config table (line 1283) says 0.2, and `terrain-roadmap.md` records 0.2 as the
locked-in tuned default. Only the §3 prose is wrong — an isolated edit that was never propagated.

### 03 — Shroomer terraform rate understated by roughly 7× · **CRITICAL**

| | Radius | Strength | Cooldown |
|---|---|---|---|
| **Doc says** (`FEATURES_AND_DESIGN.md:1130`, §7.4 table) | 2.0 | **0.03** | **8** |
| **Code says** (`Species/SpeciesRegistry.cs`) | 2.0 | **0.1** | **4** |

Strength is a probability per cooldown roll, so the two errors compound: the documented figures
imply one nudge per ~267 ticks, the real ones about one per 40. The section's own footnote reasons
explicitly from the wrong number. Faeling (4.0 / 0.25 / 8) and the Sectid nest burst (14 × 0.08 at
radius 3) are both correct.

### 04 — Wetland and Bog are listed as non-grazeable; they are grazeable · **CRITICAL**

| | |
|---|---|
| **Doc says** | Wetland — Grazeable `–` · Bog — Grazeable `–` — `FEATURES_AND_DESIGN.md:307–308` (§4 tile table) |
| **Code says** | `IsGrazeable()` returns `true` for both; caps are Wetland **0.90**, Bog **0.70** — `World/TileType.cs` |

Wetland at 0.90 sits just under Grass. A designer placing herbivore niches from this table would
treat two of the most productive biomes as barren.

### 05 — Six thresholds in the biome classification bands are wrong, and one bucket no longer exists · **CRITICAL**

The Arctic, Cold temperate and Temperate bands are exact. The two hot bands are not, and the band
boundary between them is wrong, which shifts every rule inside it.

| Band / rule | Doc | Code |
|---|---|---|
| Warm-temperate / tropical boundary | 0.75 | **0.72** |
| Warm temperate → Shrubland | > 0.24 | **> 0.22** |
| Warm temperate → Dirt | > 0.13 | **> 0.14** |
| Tropical → Jungle | > 0.62 | **> 0.60** |
| Tropical → Savanna | > 0.40 | **> 0.42** |
| Tropical → Dirt | > 0.26 | **> 0.30** |
| Tropical → Sand | > 0.15 | **removed** |
| Sub-alpine ice cap | absent | **elev > 0.72 & temp < 0.26** |

The code comment beside the tropical band states the reason outright — "The old hot-dry Sand bucket
is gone (deserts are Arid, not beach)" — so the doc is describing a deliberately removed rule. The
sub-alpine ice cap is a real classification rule missing from the table entirely.
`World/TerrainGenerator.cs:417–506`.

### 06 — Five hunt speeds in the species roster are stale · **HIGH**

| Species | Doc | Registry |
|---|---|---|
| Wolf | 0.12 | **0.14** |
| Bear | 0.09 | **0.11** |
| Crocodile | 0.08 | **0.10** |
| Scorpion | 0.07 | **0.09** |
| Penguin | 0.10 | **0.13** |

Every body mass in the roster is correct, as are the other twenty-four hunt and wander speeds.
These five look like a tuning pass that landed in code and not in the table.
`FEATURES_AND_DESIGN.md:351–383`.

### 07 — River-source acceptance rate is inverted · **HIGH**

| | |
|---|---|
| **Doc says** | sources are "~65% randomly accepted" — `FEATURES_AND_DESIGN.md:199` |
| **Code says** | `if (rng.NextDouble() > 0.35) continue;` — **35%** accepted — `World/RiverMapper.cs:384` |

Reads like the complement was taken by mistake. Everything else in the RiverMapper description —
source band 0.68–0.80, spacing 18, flow thresholds 1 and 3, lake rise 0.04, lake cap 80 tiles,
marking to the 0.40 waterline — is exact.

---

## B. Behaviour that has since been removed or inverted

*5 findings · 3 critical.* Worse than a wrong number: a mechanism described as current that the code
no longer has, or now does the opposite of.

### 08 — The element barrier is described as deliberately *not* a movement wall — it now is one · **CRITICAL**

| | |
|---|---|
| **Doc says** | `IsImpassable` is enforced as "very strong steering aversion + the 5% flop speed + drowning — **strong avoidance, not a movement wall**, so 'only accidental shoring/drowning' emerges without trapping entities." — `FEATURES_AND_DESIGN.md:570–572`, `terrain-profile-design.md:26–31` |
| **Code says** | `MovementSystem` **reflects** any step that would carry a creature out of its element, exactly as it reflects the world edge. Crossings *into* the wrong element are refused outright. — `Systems/MovementSystem.cs` |

This is a design-level inversion, and the same file documents the new barrier eighty lines earlier
in §6.1 — so `FEATURES_AND_DESIGN.md` now argues both sides. The claim also appears a third time in
`terrain-profile-design.md`, which is marked *Status: IMPLEMENTED* and therefore reads as
authoritative. Only `TerrainProfile.cs`'s own comments are still accurate.

### 09 — Two documents describe the half-tile row stagger as the current render model; it returns zero · **CRITICAL**

| | |
|---|---|
| **Doc says** | "odd grid rows are shifted half a tile (`SmoothRowOffset`) so terrain triangulates into an offset/hex-like mesh instead of axis-aligned squares" — `FEATURES_AND_DESIGN.md:94`, `architecture.md:86` |
| **Code says** | `SmoothRowOffset` **returns 0f** — "Staggered odd-row offset removed (Phase 1) — linear square mapping. Kept as a no-op for call-site stability." — `Utils/GridCoordinates.cs:29–33` |

**Worth a design decision, not just an edit.** `3d-terrain-plan.md` records the removal as Phase 1,
done, with the reason (grid-space movement rendered as a zig-zag). But `RiverMapper` still traces on
**6 offset-row hex neighbours** with even/odd variants, and FEATURES §3 correctly describes that. So
hydrology reasons about a hex adjacency the renderer stopped expressing — the two halves of the doc
are each right about their own half of a split the project has not yet resolved.

### 10 — The LOD section gives contradictory instructions on which interval to compensate by · **CRITICAL**

| | |
|---|---|
| **Line 531** | "Rate-sensitive systems multiply per-tick deltas by `SimulationLOD.TickInterval` so a throttled entity ages/starves at the correct rate." |
| **Line 466** | "**Compensate by `EffectiveInterval`, not `TickInterval`.** … otherwise every boundary the player walks past over- or under-counts hunger, ageing and growth." |

Both sentences are in §6.1, sixty lines apart, and the first is the one a reader hits when skimming
for the rule. The code follows the second.

A closely related stale line sits at 529: "the countdown is decrement-first, so newly spawned *or
re-tiered* entities process immediately at any tier" — true for upgrades only since phase staggering
landed (`TicksUntilUpdate = entity % TickInterval` on downgrade), which the same section documents
at line 454.

### 11 — Corpses are documented as a "dark Diamond"; they have their own shape · **HIGH**

| | |
|---|---|
| **Doc says** | a corpse entity is "`Carrion` component + a **dark Diamond** renderable" — `FEATURES_AND_DESIGN.md:972` |
| **Code says** | `new Renderable(…, ShapeType.Carcass)` — a dedicated flat splayed disc, added so corpses read as clearly not-a-creature — `Systems/CarrionSystem.cs:105` |

### 12 — The shape roster is short one entry — the same one · **HIGH**

| | |
|---|---|
| **Doc says** | "One `MultiMeshInstance3D` per shape (**13** `ShapeType` values)", then lists thirteen: Circle … Fangs — `FEATURES_AND_DESIGN.md:1171`, `architecture.md:72`, `godot-roadmap.md:19` |
| **Code says** | **14** values — `Carcass = 13` closes the enum — `Components/BehaviorComponents.cs:181–197` |

---

## C. The counts family

*1 finding · 5 files · high.* One finding rather than twenty, because it is one problem: the same
four inventory numbers are restated as prose in five documents with no shared source, so every
addition to the project needs five coordinated edits and never gets them.

### 13 — Species, systems, components and shapes are each wrong in every file that states them · **HIGH**

| Inventory | Docs say | Actual | Missing / changed |
|---|---|---|---|
| Component types | 25 | **26** | `Carrion` absent from every component listing |
| Simulation systems | 19 | **20** | `CarrionSystem` |
| Species | 28 | **29** | Otter — *except* FEATURES §5, which says 29 |
| `ShapeType` values | 13 | **14** | `Carcass` |
| `SpeciesDefinition` properties | 100+ | **198** | True but long past useful |

Two consequences are worse than the arithmetic:

- **`architecture.md`'s system execution order — the list FEATURES links to as the authority — omits
  `CarrionSystem` entirely**, jumping from 12. Fleeing to 13. Aging. FEATURES' own one-line order
  (§2) includes it. The two docs disagree about what runs each tick.
- **FEATURES contradicts itself**: §5 says 29 species, its file map and footer say 28.

Where: `architecture.md:44, 50, 52, 72, 129–130, 162, 178` · `godot-roadmap.md:19, 20, 25, 40, 58` ·
`README.md:21, 63, 65, 67` · `FEATURES_AND_DESIGN.md:75, 1171, 1323, 1356, 1382`.

---

## D. Species lists that name the wrong species

*4 findings.* Trait and behaviour groupings, checked against the registry by reflection. Quick fixes,
but these are the lines a designer scans when deciding which species fills a niche.

### 14 — The semi-aquatic list names two species that aren't, and omits two that are · **HIGH**

| | |
|---|---|
| **Doc says** | Semi-aquatic = Crocodile, Turtle, Penguin, Polar Bear, **Tapir, Jaguar** — `FEATURES_AND_DESIGN.md:388` (and roster rows 382, 383) |
| **Registry says** | Crocodile, Turtle, Penguin, Polar Bear, **Frog, Otter** — Tapir and Jaguar set no aquatic flag at all |

The roster rows repeat the error in prose ("Jaguar — Semi-aquatic; jungle stealth", "Tapir —
Tropical; semi-aquatic; panics"). The Flying, Aquatic, Venom, Ambush, PackCoordinated and Swarm
groupings are all correct.

### 15 — All three fear-response groups are missing members, and one names the wrong species · **HIGH**

| Response | Doc | Registry |
|---|---|---|
| Panic | Rabbit, Frog, **Tapir** | Rabbit, Frog, **Fish** |
| Freeze | Turtle | Turtle, **Shroomer** |
| Defensive | Boar, Musk Ox | Boar, Musk Ox, **Faeling** |

Tapir is a plain Flee. Fish panicking is behaviourally significant — it is the species most often
being chased — and it is absent from the table. `FEATURES_AND_DESIGN.md:939–941`.

### 16 — Otter is missing from the omnivore list and the Solo tactic list · **HOUSEKEEPING**

| | |
|---|---|
| **Doc says** | Omnivore = Boar, Penguin · Solo = Hawk, Shark, Polar Bear, Arctic Fox, Penguin — `FEATURES_AND_DESIGN.md:388, 697` |
| **Registry says** | Omnivore = Boar, Penguin, **Otter** · Solo predators also include **Otter** |

The same file's roster row for Otter correctly labels it **Omnivore** — the species was added to the
table but not to the summary lines below it. A representative case of the general pattern.

### 17 — The nutrition footnote gives different numbers than the nutrition table · **HOUSEKEEPING**

| | |
|---|---|
| **§4 footnote** (`:316`) | "Arid **(0.15)** and Tundra **(0.2)** are grazeable at reduced starting nutrition" |
| **§3 table** (`:265`) **and code** | "Tundra **0.25**, Arid **0.2**" — matching `NutritionCap()` |

---

## E. Measured figures with no provenance

*2 findings · 1 critical.* The docs quote a lot of measurements, which is a strength — it is how a
design decision stays arguable later. But none records the seed or commit it came from, so there is
no way to tell a live figure from an expired one without re-measuring.

### 18 — The headline predator-targeting result is from a run that predates deterministic RNG · **CRITICAL**

| | |
|---|---|
| **Doc says** | "bears went from **39% to 73%** big-game targeting" — `FEATURES_AND_DESIGN.md:765` |
| **Re-measured** | **61% → 79%** pooled over 3 seeds with the payoff term on/off, under `SimRandom` (headless regression harness, `dabca20`) |

The original figure was taken before per-system RNG was seeded from `WorldSeed` — the exact problem
`godot-roadmap.md` raises as the reason full-world balance runs stopped being trustworthy. The effect
is real and the direction holds; the magnitude was overstated. Any other single-run number in these
docs from before `SimRandom` deserves the same suspicion.

### 19 — §6.1 carries two whole-tick cost baselines forty-five lines apart · **HOUSEKEEPING**

| | |
|---|---|
| **Line 451** | "the whole tick got cheaper, **12.5 → 11.4 ms**, with Movement itself at 1.5 ms" |
| **Line 496** | "Cost: whole-tick mean **12.3 → 13.1 ms** on 5,000 creatures" |

Both were true when written, of different commits, and neither says so. Current: whole tick 13.1 ms,
Movement 1.88 ms on the same 5,000-creature profile. Two numbers claiming to describe the same thing
is worse than one stale one — a reader cannot tell which is current.

---

## F. Status drift

*4 findings · 1 critical.* The most consequential category for planning the next branch, because the
roadmap is the document that decides what the next branch is.

### 20 — The roadmap lists this branch's delivered work as unstarted future work · **CRITICAL**

| | |
|---|---|
| **Roadmap says** | ☐ **Focused system testing (next branch)** — "seed the sim RNGs from `WorldSeed` for reproducibility, then build small scripted scenarios per system … with pass/fail metrics from the existing CSV logging". And: ☐ "No automated tests for systems/ecosystem balance yet." — `godot-roadmap.md:80–87, 106` |
| **Shipped** | `Utils/SimRandom.cs` seeds every stream from `WorldSeed`; `Scripts/Testing/` holds the harness (4 files); **6 scenarios** in `godot/TestScenarios/`; three extended log streams; `docs/test-scenes.md` documents all of it |

This is the document you would plan the next branch from, and its near-term list still points at the
branch you are leaving. The whole Testing subsystem, terrain paint mode, the observation/inspection
tools and the worldgen previewer appear nowhere in "Implemented".

### 21 — The "Implemented" list predates several shipped systems · **HIGH**

Absent from `godot-roadmap.md`'s current-state and implemented sections, all present in code:

- `CarrionSystem` and the corpse economy — a whole simulation system
- Otter, and the freshwater predation loop built around it
- The water-fertility (plankton) economy and fertility-coupled breeding
- Reef as an element for non-swimmers, and size-scaled reef clutter
- The test-scene harness, observation tools, terrain paint mode, worldgen previewer

Also: `TileRegeneration` is described as a "4-tick throttle", but a full sweep is
`RegenInterval 4 × RegenSweepPasses 8` = every 32 ticks.

### 22 — The faction workstream is paused on a branch that has since delivered · **HOUSEKEEPING**

`faction-balance-plan.md`: "*#1–#4 implemented; workstream PAUSED pending the focused-testing
branch*". The focused-testing branch exists and the scenarios it asked for (`faction_skirmish`,
`shroomer_bloom`) shipped. Its handoff section is the natural starting point for the next branch and
should be re-opened rather than re-derived.

### 23 — Every long document carries two "last updated" stamps that disagree · **HOUSEKEEPING**

| File | Header | Footer | Actually edited |
|---|---|---|---|
| `FEATURES_AND_DESIGN.md` | July 2026 | June 2026 | **Aug 2026** |
| `godot-roadmap.md` | July 2026 | June 2026 | **Aug 2026** |
| `architecture.md` | — | June 2026 | **Aug 2026** |

Two stamps per file means neither gets maintained: the header gets bumped on a big pass, the footer
never does. One stamp, or none — a git log is more honest than either.

---

## G. Structure and navigation

*7 findings · 1 high.* None of these is factually wrong. They are the reasons the factual errors
above survived — material filed where nobody rereads it, and stale documents linked as current.

### 24 — A prose paragraph is spliced into the middle of the species roster, splitting the table in two · **HOUSEKEEPING**

The *Thorn defence* paragraph sits between the Shark row and the Otter row
(`FEATURES_AND_DESIGN.md:356–364`). In any Markdown renderer the roster becomes two tables, and the
second — twenty-two of the twenty-nine species — loses its header row. The paragraph belongs in §7.1
with the rest of the Shroomer defences.

### 25 — §6.7 "Hunting" has absorbed about ninety lines that are not about hunting · **HOUSEKEEPING**

Reef-counts-as-water, size-scaled reef clutter, inverse-mass collision push weights and
surface-to-surface combat reach all live under Hunting because that is where the work happened to be
done, not where the topic belongs. Reef material belongs in §4/§6.x (Terrain Profile), collision in
§6.10.

Related: the reef-clutter footnote in §4 points to "*Reef clutter* under **§6.6**" — it is in §6.7.

### 26 — A section is numbered "§6.x" and cross-referenced as though that were a number · **HOUSEKEEPING**

Terrain Profile sits between §6.2 and §6.3 as **§6.x**, and two other sections point at "§6.x" and
"§6.x tolerance". It also opens "**Three dimensions:**" and then lists five bullets (the source
comment says four). Give it a real number and correct the count.

### 27 — A historical audit is written in the present tense and linked as a current reference · **HIGH**

`terrain-handling-audit.md` is explicitly a "snapshot as of the aquatic-speed overhaul", but it reads
as live: an inventory table with a *Status* column, and a section headed "How each dimension resolves
**today**". Two of its entries are now false:

- `GetCoverBonus` marked "read by **nobody** — DEAD"; it is now the per-tile concealment floor used
  in both detection directions.
- `TerrainSpeedModifiers` marked "half-live — water tiles only (land entries inert)";
  `TerrainProfile.Speed` made all tiles live.

And `FEATURES_AND_DESIGN.md:597` sends the reader there for detail. Either move it to
`docs/archive/` or put a superseded banner at the top; the fix it describes is already documented in
`terrain-profile-design.md`.

### 28 — Both file maps omit roughly a dozen real source files · **HOUSEKEEPING**

Missing from `FEATURES_AND_DESIGN.md` §10 and/or `architecture.md`: `Systems/CarrionSystem.cs`,
`Systems/DecisionCadence.cs`, all four `Scripts/Testing/` files, both `Scripts/Tools/` files,
`Utils/BodyMetrics.cs`, `Utils/SimRandom.cs`, `World/IChunkGenerator.cs`, `ScenarioTileParams.cs`,
`TerrainPalette.cs`, `TerrainSettings.cs`, `Species/TerrainProfile.cs` and `SpeciesToggle.cs`
(architecture only), `Scenes/TestScene.tscn`, `Scenes/WorldgenPreview.tscn`, and
`godot/TestScenarios/`.

### 29 — README doesn't mention the test-scene harness at all · **HOUSEKEEPING**

The Documentation section lists FEATURES, architecture, roadmap and archive, but not
`docs/test-scenes.md` — the entry point for the workflow the project now runs its balance work
through. Project Structure likewise omits `Testing/` and `Tools/`.

### 30 — The roam-reason enumeration in test-scenes.md is missing three of seven · **HOUSEKEEPING**

| | |
|---|---|
| **Doc says** | `roam_start` reason = seek_food / restore_terrain / keeper_siege / random — `test-scenes.md:134` |
| **Code emits** | also **escape_substrate**, **escape_terrain**, **seek_breeding_ground** — `Systems/WanderSystem.cs:193–276` |

The only finding in `test-scenes.md`, which is otherwise accurate throughout — including the default
scenario path, all six shipped scenarios, the control tables and the determinism claim.

---

## H. An undocumented consequence worth a design decision

### 31 — Motion was ungated from LOD; the spatial hash was not · **HIGH**

| | |
|---|---|
| **Documented** | §6.1 "Known limits" lists uneven rate compensation in Carrion, Herding, Separation/Collision and Wander. The spatial hash is not mentioned. — `FEATURES_AND_DESIGN.md:503–508` |
| **In code** | `SpatialHashUpdateSystem` is **LOD-gated**; `MovementSystem` is not. A Minimal-tier creature moves every tick and refreshes its hash entry every twentieth. — `Systems/SpatialHashUpdateSystem.cs:33` |

At hunting speed that is up to ~5–6 tiles of positional staleness in every neighbour query —
targeting, fleeing, herding, collision. Cell size is 32, so most of the time the entity is still
found in the right cell and nothing visible breaks; but it is precisely the class of coupling the
section's own "the gate and the multiplier are one thing" rule exists to catch, and it is currently
invisible. Whether to fix it or accept it is a design call. Either way it should be written down.

---

## Why this keeps happening

The error rate is not high for 3,300 lines of hand-maintained reference. The pattern in *which*
claims went stale is more useful than the count.

**Counts live in prose, five times over.** Species, systems, components and shapes are restated in
five documents with no shared source. Adding `CarrionSystem` and Otter required nine coordinated
edits and got two. This is the one category that can be fixed permanently rather than repeatedly.

**Sections grow where the work happened, not where the topic lives.** Reef handling is documented
under Hunting, collision under Hunting, thorn defence inside the species roster. Material filed away
from its topic is material nobody rereads when that topic changes — which is exactly how §6.x kept
saying "not a movement wall" while §6.1, eighty lines up, documented the wall.

**Measurements are recorded without provenance.** The docs quote a lot of numbers, which is a real
strength — it keeps old decisions arguable. But none carries the seed or commit it came from, so an
expired figure is indistinguishable from a live one. The 39%→73% bear result survived a change that
invalidated it.

**Historical documents are written in the present tense.** `terrain-handling-audit.md` was accurate
the day it was written and is now a trap, because nothing about it says "was". Audits and snapshots
need a status banner at birth, not at archive time.

---

## Fix order

Sequenced by what the next branch needs first.

1. **Correct the eight balance-critical numbers.** Findings 01–07 and 17. `HungerDecayScale`,
   `TerrainRiverDensity`, the Shroomer terraform row, Wetland/Bog grazeability, the hot-band
   classification thresholds, the five hunt speeds, the river acceptance rate, the Arid/Tundra
   footnote. Roughly an hour, and it is the set that would otherwise misdirect the next tuning pass.

2. **Reconcile the three self-contradictions in FEATURES §6.1 and §6.x.** Findings 08 and 10. Delete
   the `TickInterval` compensation sentence, fix the re-tiered-entities line, and rewrite the "not a
   movement wall" paragraph in both `FEATURES_AND_DESIGN.md` and `terrain-profile-design.md`. A doc
   that argues both sides of a design question is worse than one that is silent.

3. **Rewrite the roadmap against reality before planning off it.** Findings 20–22. Move focused
   system testing to Implemented with what actually shipped, add CarrionSystem / Otter / water
   fertility / reef / tooling, and re-open `faction-balance-plan.md`'s handoff section as the
   candidate next workstream.

4. **Make the counts self-checking instead of hand-maintained.** Finding 13. The regression harness
   already reflects over `SpeciesRegistry` and the enums — a check that asserts the numbers appearing
   in `docs/` and `README.md` match the registry, the `ComponentFlags` enum, the `ISystem`
   implementations and `ShapeType` would end this category permanently. Failing that, delete the
   counts from prose and state them once.

5. **Settle the hex-versus-square question.** Finding 09. Hydrology traces on hex neighbours; the
   renderer stopped staggering rows. Decide which is authoritative, then correct §2 and
   `architecture.md` to match — this is a design decision the docs are currently papering over rather
   than a wording fix.

6. **Re-home the misfiled material and banner the stale audit.** Findings 24–29. Move thorn defence
   out of the roster table, reef and collision out of §6.7, number §6.x, add a superseded banner to
   `terrain-handling-audit.md`, and refresh both file maps and the README's documentation list.

7. **Adopt two conventions going forward.** One timestamp per file, in the header. Every quoted
   measurement stamped with its seed and commit, or deleted — findings 18, 19 and 23 are all the same
   missing habit.

---

*Audited 2026-08-25 against `dabca20`. This document records findings only; no documentation was
changed as part of it.*
