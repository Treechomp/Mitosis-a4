# Whole-game invariants

**What this is.** A small set of properties that must hold for the game as a whole, asserted over a
20,000-tick run of the standard world and enforced by an exit code.

**Why it exists.** Three changes landed in sequence — LOD rate compensation (`93818ea`), per-class
population budgets (`14e9f08`), destructible faction structures (`31b689f`). Each met its own
acceptance criteria. Together they broke the game: within roughly a minute of world start, Faelings
began destroying every Sectid nest and mycelium heart on the map, essentially unopposed. A
6,000-tick run lost **44 of 46 nests and 21 colonies, the first at t=951**.

Nothing in the project could have caught it. The LOD differential compares a build against itself,
so a change that is uniformly wrong at every tier passes. The population soak measured composition,
and the composition looked fine — the factions were inside their budgets the whole time. Neither
asserted anything about the game *as a game*.

```
godot --headless --path godot res://Scenes/PopulationSoak.tscn -- --ticks=20000
```

Exit code 0 when every assertion holds, non-zero otherwise, and the failing assertions are named.
**This gate is binary.** Unlike the LOD differential, which carries 40 documented standing
exceptions and therefore always exits 1, this one has no exception list and must not grow one: if
an assertion needs an exception, the threshold is wrong and should be changed deliberately, with
the reasoning recorded here.

> **Status: GREEN as of 2026-08-29.** All eleven assertions hold on seeds 1234 and 999. The gate
> was red for exactly one commit — long enough to make the Sectid defect visible and get it fixed.
> The history is kept below, because "the gate went red, named a faction, and the cause turned out
> to be one struct field" is the argument for having it.

---

## The assertions

| # | Assertion | Threshold | What it is protecting |
|---|---|---|---|
| 1 | `structures_destroyed(t < 6000) == 0` | 6,000 ticks (5 min at 20 TPS) | A raid must be an **event a player can see coming and answer**. A faction that loses its infrastructure before it has built any has not been beaten, it has been deleted. |
| 2 | each faction holds an anchor **and** clears a population floor at t=20000 | 1 anchor each; Sectid 50, Shroomer 50, Faeling 6 | It is a **three-way** war. A two-way war is a different game, and losing a faction quietly is exactly what happened. |
| 2b | `sectid_kills_made > 0` over the run | 1 | The Sectid **colony economy has ignited at all**. Nests hatch on food carried home, so zero kills means zero births. |
| 3 | `0.005 < world_deviation < 0.35` at t=20000 | see below | The world is being **contested** — neither untouched nor converted. |
| 4 | no class exceeds its population ceiling | — | Kept from the previous change; cheap, and it has caught a real bug before. |

### On assertion 2's two halves

Assertion 2 was originally an anchor count alone, and it passed a run holding **46 nests and one
living Sectid** (recorded below). Structures outlive the faction that built them, so counting
buildings reports a dead faction as healthy. Each faction now has to clear both tests:

| faction | anchor | population floor |
|---|---|---|
| Sectid | ≥ 1 nest | ≥ 50 |
| Shroomer | ≥ 1 heart | ≥ 50 |
| Faeling | ≥ 1 crystal | ≥ 6 |

They are reported as two separate lines rather than one combined verdict, because *which* half
failed is the diagnostic: no anchors with a live population is a faction that has been evicted, a
population collapse with anchors standing is a faction that has starved.

Faeling's floor is 6, not 50, because Faelings are **few by design** — `FaelingCrystalCount = 12`
sets the whole faction's size, so 50 is unreachable and 6 is half the roster. Sectid and Shroomer
are open-ended populations that reach the thousands; 50 for those is not "healthy", it is
*"unambiguously still playing"*, which is what a floor is for. Sizing them as a share of the faction
budget would be the better shape and is deliberately not done yet: a share needs a measured healthy
distribution to be a share *of*, and the Sectid distribution is currently broken (below).

### On assertion 2b: why kills are asserted separately

Zero kills and a low population are not the same failure, and collapsing them into one assertion
would lose the more useful one. A Sectid nest hatches on food carried home by the swarm, so
`kills_made = 0` means `births = 0` and the colony dies of arithmetic — the population floor then
fails too, but several thousand ticks later and much further from the cause. The kill count fails
*at the cause*. Reading the two lines together separates "the Sectids were outfought" from "the
Sectid economy never started".

The counter is the run-cumulative `kills_made` for the Sectid species — the same quantity the
species-stats CSV reports per interval, keyed on the *predator* species.

### On assertion 3 and the deviation metric

`world_deviation` is the mean `|current moisture − pristine moisture|` over a fixed lattice
(`WorldManager.DeviationFromPristine`, stride 4 → 64 of every 1,024 tiles). Pristine is recomputed
by re-sampling the generator, so nothing is stored per tile; the sampled subset is cached on first
use, which also guarantees the **same tiles are compared on every call** — otherwise the number
would drift on its own and the invariant would fail for the wrong reason.

It is logged with a **wetter/drier split**, which is the scoreboard for the three-way war: one
number saying "the world is 1.4% altered" cannot tell a swamp advancing from a desert advancing
from the two cancelling out. Restoration is the only direction that pulls both components down.

**The floor was moved from 0.02 to 0.005, and the threshold was wrong rather than the game.**
Measured on the standard 36-chunk world:

| state | deviation |
|---|---|
| t=8,000, factions still small | 0.0044 |
| t=20,000, Shroomers at 2,850 and expanding | 0.0139 |
| t=20,000, shipping build | 0.0168 |

0.02 asks for the equivalent of 2% of a 1.33-million-tile world fully displaced. The faction
populations that exist at 20,000 ticks cannot produce that: ~3,400 faction members each working a
radius-2 disc reach on the order of 1–2% of the map, and not all of it to full displacement. The
number the game actually reaches while visibly contested is 0.0139, and the number it sits at when
almost nothing is happening is 0.0044. **0.005 separates those two states**, which is what the
floor is for; 0.02 separated "healthy" from "healthy". The ceiling of 0.35 is untested from below —
nothing has come close — and is left as a guard against a future monoculture.

---

## These numbers are a starting position

Every threshold here is provisional and should be expected to move as the game changes. In
particular:

- **6,000 ticks** is a statement about pacing, not a measurement. If faction combat is later meant
  to open earlier, this moves — deliberately, in a commit that says so.
- **0.005 / 0.35** are calibrated against one world size (36 chunks) and one seed. Deviation is a
  whole-world mean, so a smaller world will read higher for the same amount of fighting. If the
  standard world size changes, re-measure both.
- **50 / 50 / 6 are a starting position, not a measurement.** They were chosen as numbers no
  healthy faction could plausibly sit below, not read off a healthy run — because there is no
  healthy Sectid run to read them off yet. Once the Sectid economy works, re-measure all three and
  consider replacing them with a share of the faction budget, which would scale with world size and
  cap instead of being pinned to one configuration.
- **The anchor half is still a floor of one.** A faction reduced to a single crystal has
  effectively lost. The population floor is what now carries the weight of assertion 2; the anchor
  test remains because holding zero sites is a distinct failure from holding zero members.

## RESOLVED: the red gate found a struct-initialisation bug

**The failures below are fixed.** `Siege.TargetStructure` was zero-initialised rather than -1, so
every Sectid was born besieging entity 0 — the first Faeling crystal — and `HuntingSystem`'s siege
guard skipped it forever; the hunt path never executed once. Diagnosed in `ff4248f`, fixed by
making the constructor argument required and adding an explicit parameterless constructor.

| | before | after (seed 1234) | after (seed 999) |
| --- | --- | --- | --- |
| Sectid population at t=20,000 | **1** / 10 | **1,067** | **1,712** |
| Sectid `kills_made` over the run | **0** | **1,487** | **1,820** |
| Sectid nests | 46 → 46 | 46 → **186** | 46 → **264** |
| Shroomer | 3,947 | 2,886 | 2,238 |
| Faeling | 12 | **7** | 10 |
| crystals standing | 12 | **11** | **10** |
| gate | 2 FAIL | **ALL HOLD** | **ALL HOLD** |

Two things worth carrying forward. **Faeling is now the fragile faction**: 12 → 7 on seed 1234
against a floor of 6, with a crystal lost, because Sectids can finally besiege something real. The
next assertion likely to fire is the one that was never in danger before. And on seed 999 the
ratchet test reports Shroomer `rose 16x, FELL 0x` after the faction class filled — the runner's own
monoculture signature — while Sectid fell 17x. The ceiling is genuinely contested now; whether it
stays that way is the next measurement's question, not this one's.

The original red-gate record follows.

## The gate was RED, deliberately

Assertions 2 and 2b **fail on the shipping build**, and that is the correct reading of it. Run of
2026-08-29, 20,000 ticks, seed 1234, exit code 2:

```
  PASS  Sectid: holds at least one nest            [Sectid nests=46, floor=1]
  FAIL  Sectid: population at or above 50          [Sectid population=1, floor=50]
  PASS  Shroomer: holds at least one heart         [Shroomer hearts=293, floor=1]
  PASS  Shroomer: population at or above 50        [Shroomer population=3947, floor=50]
  PASS  Faeling: holds at least one crystal        [Faeling crystals=12, floor=1]
  PASS  Faeling: population at or above 6          [Faeling population=12, floor=6]
  FAIL  Sectid: kills_made above zero over the run [Sectid kills_made=0, floor=1]
```

**The Sectid faction is one individual holding 46 empty nests.** Before the floors existed the gate
exited 0 on this same composition, because the only question it asked was whether a nest still
stood, and 46 do. A third of the game was missing and the gate was green.

The decline is monotonic from the first sample — this is not a late collapse, it is a faction that
never starts:

| t | Sectids | Shroomers | nests |
|---|---|---|---|
| 200 | 300 | 374 | 46 |
| 2,000 | 223 | 495 | 46 |
| 6,000 | 88 | 808 | 46 |
| 8,000 | **46** — crosses the floor | 1,036 | 46 |
| 10,000 | 31 | 1,427 | 46 |
| 14,000 | 7 | 2,674 | 46 |
| 20,000 | **1** | 3,947 | 46 |

The nest count never moves. Structures are not the thing dying.

The cause is not the Faeling wipe the gate was originally built for, and it is not keeper fire —
gating the keeper's ranged attacks on the same dominance mandate as its sieges changed nothing.
`kills_made` is **zero for the entire run**, including at t=200 when 300 Sectids are alive. A swarm
that catches nothing carries no food home, so nests never hatch, so births stay at zero while
predation and age take the founders. From `latest_species_stats.csv` on an earlier run of the same
build:

| t | population | births/interval | deaths_predation | kills_made | avg hunger |
|---|---|---|---|---|---|
| 200 | 300 | 0 | 3 | **0** | 77% |
| 5,000 | 132 | 2 | 2 | **0** | 50% |
| 9,800 | 21 | 0 | 0 | **0** | 17% |
| 17,000 | 2 | 0 | 0 | **0** | 22% |

This is the fragility `faction-balance-plan.md` already records — "their colony economy only
ignites when the herbivore base surges … hostage to prey density and needs its own scenario" — and
it is species/prey balance, deliberately **not** fixed by the change that added these floors.

### Why ship a red gate

An earlier revision of this file argued the opposite: that tightening assertion 2 should wait for
the Sectid fix, because "doing it now would ship a red gate, which this file exists to prevent."
That was wrong, and the reasoning that replaces it is:

- The gate does not exist to be green. It exists to make the state of the game legible. A green
  gate over a run with one living Sectid is a **false negative**, and a false negative in a gate is
  worse than a red one — it actively certifies the defect.
- The two failures are as loud as the defect deserves, and no louder. They name a faction, a
  metric, a value and a floor, and they point at a known, documented, scoped problem.
- Waiting couples two changes that do not need to be coupled. The floors are correct whether or not
  the Sectid economy is fixed today; holding them back only means the next regression in *another*
  faction also goes uncaught.

The rule this file states — no standing exception list — is unchanged and is what makes the red
meaningful. **Nothing here may be excused; the failures close when the Sectid economy works.**

A second, smaller blind spot: **worldgen currently seeds zero mycelium hearts** (`0 of 376
Shroomers`), because no natural site meets the moisture floor at t=0 — a heart needs 35% of its
territory above the fungal threshold and untouched grassland is below it. Blooms found their own
once they reach `MyceliumFoundScale`, which at a growth rate of 8e-5 takes ~12,500 ticks, and the
20,000-tick run ends with 222. So assertion 2 holds, but the Shroomer faction has no anchor for the
first ~12,500 ticks — a gap that matters for the respawn design and not for the invariant. Giving
worldgen a few deliberate starter blooms (a cluster of Shroomers on wet ground WITH a heart, rather
than scattered individuals) is the obvious fix and belongs with the respawn work.

## What the gate has already caught

Both of these were live bugs found by running the assertions, not by reading code:

1. **Keepers were dismantling the losing faction.** `SiegeSystem` picked the nearest enemy
   structure and never consulted the world census, so twelve Faelings spent a run destroying Sectid
   nests while the Sectids were collapsing (216 → 142) and the Shroomers ran away with the map
   (376 → 2,850). Fixed by `StructureTargetsDominantOnly`: a keeper besieges the faction the census
   says is winning, or nobody.
2. **A hungry creature would lay siege instead of eating.** The siege aggression ratio compares a
   structure's distance against the current PREY TARGET's, and a creature with no prey target
   scored that as infinite — so "nothing to eat in sight" resolved as "definitely go break a
   building". Once crystals were toughened, a hungry swarm would commit 600+ ticks to hammering
   one instead of foraging, and the Sectid faction went 223 → 1 and 212 → 0 across two seeds while
   holding 47 empty nests. Fixed twice over: hungry creatures forage unless their own home is
   being broken, and "no prey in sight" is now scored as the creature's own seek radius rather
   than infinity.
3. **Worldgen was seeding hearts that could not live.** `WorldSpawner.SpawnMyceliumHearts` placed a
   mycelium heart on every scattered Shroomer, and 76 of 85 bled out by t=819 with cause
   `environment` — a heart needs 35% of its territory above the fungal moisture threshold and more
   than three Shroomers inside it, and natural grassland is below the first. From the outside this
   was indistinguishable from the Shroomer faction being destroyed. Fixed by sharing one
   `TerritorySupportsHeart` test between worldgen seeding and organic founding.
