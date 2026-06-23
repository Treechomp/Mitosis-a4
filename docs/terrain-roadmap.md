# Terrain Workstream — Roadmap

Active workstream after the terrain-profile redesign. Tracks the terrain-coupled problems the
runs keep surfacing and the order we attack them. See `terrain-handling-audit.md` (current
mechanisms) and `terrain-profile-design.md` (the resolver that shipped).

## Where the last run landed (run 20260623_215555, 35k ticks, 9-world)
Kill effectiveness (kills / hunt_start):

| Predator | Conv% | Note |
|---|---|---|
| Jaguar 56, Bear 50, Fox 50 | high | ambush/solo specialists convert well |
| Hawk 39 | — | **apex fish predator** (281 fish) — out-fishes the Shark |
| Arctic Fox 33, Snake 33 | ok | but too few absolute kills → starve |
| Shark 21 | **improved** | 7→103 kills, no longer starving (redesign win) |
| Wolf 18, Polar Bear 14 | low | survive on *volume*; Wolf fails 2793/2987 hunts |
| Scorpion 0 | **broken** | 0 kills, extinct — dormant ambush can't catch fast prey |

- **Redesign wins:** sharks feed now; ongoing land-stranding stopped (env deaths drop to ~0
  after t10k — the species-aware fleeing/wander fix worked).
- **Penguin regressed to 0 fish kills → extinct.** Root cause = reachability: penguins have no
  FeedTiles/graze, so WanderSystem's food-seeking scores 0 for them and nothing steers them to
  water. They wander randomly, never meet fish, starve. THE motivating case for food-seeking.
- **Polar Bear** abandoned the aquatic niche entirely (hunts Boar/Elk on land).
- **Env deaths are front-loaded** (61/79 before t6k, ~0 after t10k) = improper spawn placement,
  not ongoing behaviour. Confirms the spawn-niche concern.
- REPLACE speed did **not** break land balance — core ecosystem healthy (total 3147→5284).

## Focus items (in attack order)

### 1. World-snapshot tooling — DONE (this commit)
On world generation, dump `world_<ts>_seed…_…ch_ef…_df…_rf….png` (biome map) + a `.txt` with the
worldgen parameters AND a per-tile-type biome distribution histogram + niche-coverage flags
(water %, cold %, desert %, grazeable %). Lets us SEE what a parameter set produces and judge
whether each niche has enough habitat — uploadable for discussion. (Param text is in the
filename + sidecar for now; baked-on-image text is a possible v2.)

### 2. Spawn-niche placement validation
Symptoms: front-loaded environment deaths (fish spawned off-element), and specialists with no
habitat. Goals:
- Spawn each species only on tiles that match its niche (CanSpawnOnTile already exists — verify
  it's honoured for aquatics/cold specialists, and that a fish never spawns on land).
- Penguins must spawn in cold *coastal* terrain (snow adjacent to water), not snow on a mountain
  top with no fish for miles. Needs the biome-distribution data from (1) to know if such terrain
  even exists for a given seed.
- Fall back gracefully when a niche is too small (don't dump a species into hostile terrain).

### 3. Terrain-aware food-seeking drive — DONE
The biggest behavioural gap. Wander was random and hunting only reacted to in-range prey, so a
specialist in a marginal/disconnected biome starved regardless of stats (penguins, sharks, crocs,
arctic foxes). **Fixed:** new `SpeciesDefinition.HuntTerrain` (tiles where a predator's prey
concentrate); a hungry predator with no prey in `HuntRange` now roams toward the nearest
HuntTerrain tile via the same `TryFindFoodTarget`/`GetFoodScore` path herbivores use. Set for
Penguin/Shark/Polar Bear/Crocodile (water), Scorpion/Snake (desert/scrub), Arctic Fox (tundra/ice).
*Caveat:* doesn't rescue a Shark stranded in a fishless pond (can't cross land) — that needs the
connected-seas worldgen item (#4 "still open"). The win is amphibious/land specialists reaching
their food (inland penguins migrating to the coast).

### 4. Biome-distribution / worldgen validation (depends on 1)
Use the snapshot histograms across seeds/params to decide which worldgen parameters to tweak or
redesign so every niche gets viable, *connected* habitat (enough water bodies, a real cold belt,
deserts, etc.). Possible later: a live/quick worldgen preview to iterate parameters faster.

**Done so far** (from snapshot `seed1720373941`, which showed Arid 0.0% and 15% beach-Sand):
- **A — real deserts:** `DetermineTileType` hot zone (temp>0.72, widened from 0.75) now maps low
  moisture (≤0.30) to **Arid** with a thin Dirt fringe; the old hot-dry Sand bucket removed.
  Deserts were effectively absent (Arid only formed at moisture<0.15). *Re-snapshot to verify Arid
  climbs from 0% to several %.*
- **B — thinner beaches:** Sand elevation band narrowed 0.40–0.46 → 0.40–0.43; the inner shore now
  falls through to its climate biome (recovers land, less fragmentation).
- Snapshot niche roll-up fixed to separate Arid (true desert) from Sand (beach) and DeepWater
  (shark sea) from total water, so a future "desert fine" reading can't be faked by beaches.

**Still open:** connected seas for the Shark niche (water is "elevation<0.40" → many small ponds,
DeepWater only 5.2% and scattered). Needs an ocean-basin/continent-mask redesign, not a threshold
tweak — its own discussion.

### 5. Scorpion ambush rework (terrain-adjacent)
Dormant sit-and-wait converts 0% against fast small prey. Not a tuning issue — the mechanic needs
rethinking (e.g. a short active lunge phase, or concealment-assisted strike when prey strays
close). Parked until the above land.
