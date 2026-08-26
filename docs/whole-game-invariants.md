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

---

## The assertions

| # | Assertion | Threshold | What it is protecting |
|---|---|---|---|
| 1 | `structures_destroyed(t < 6000) == 0` | 6,000 ticks (5 min at 20 TPS) | A raid must be an **event a player can see coming and answer**. A faction that loses its infrastructure before it has built any has not been beaten, it has been deleted. |
| 2 | `nests > 0 && hearts > 0 && crystals > 0` at t=20000 | all three | It is a **three-way** war. A two-way war is a different game, and losing a faction quietly is exactly what happened. |
| 3 | `0.005 < world_deviation < 0.35` at t=20000 | see below | The world is being **contested** — neither untouched nor converted. |
| 4 | no class exceeds its population ceiling | — | Kept from the previous change; cheap, and it has caught a real bug before. |

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
- **Assertion 2 is a floor of one.** "At least one anchor each" is survival, not health. A faction
  reduced to a single crystal has effectively lost, and a future version of this file should
  probably assert a share rather than existence.

## Known blind spot: assertion 2 passes vacuously

On the shipping build the gate exits 0 with this composition:

```
Shroomer 3946, Sectid 1, Faeling 12
anchors: nests 46, hearts 222, crystals 12
```

**The Sectid faction is one individual holding 46 empty nests, and assertion 2 reports it as
healthy** — because the assertion asks whether a nest still stands, and 46 do. This is the
floor-of-one weakness named above, caught in the wild on the first build it applies to.

The cause is not the Faeling wipe this change was about, and it is not keeper fire — gating the
keeper's ranged attacks on the same dominance mandate as its sieges changed nothing. From
`latest_species_stats.csv`:

| t | population | births/interval | deaths_predation | kills_made | avg hunger |
|---|---|---|---|---|---|
| 200 | 300 | 0 | 3 | **0** | 77% |
| 5,000 | 132 | 2 | 2 | **0** | 50% |
| 9,800 | 21 | 0 | 0 | **0** | 17% |
| 17,000 | 2 | 0 | 0 | **0** | 22% |

`kills_made` is zero for the entire run, including at t=200 when 300 Sectids are alive. A swarm
that catches nothing carries no food home, so nests never hatch, so births stay at zero while
predation and age take the founders. This is the fragility `faction-balance-plan.md` already
records — "their colony economy only ignites when the herbivore base surges … hostage to prey
density and needs its own scenario" — and it is species/prey balance, out of scope here.

**The recommended next tightening of this file** is therefore to replace assertion 2's existence
test with a share or a population floor, at the same time as the Sectid economy is addressed. Doing
it now would ship a red gate, which this file exists to prevent.

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
