# Terrain Workstream — Roadmap

Active workstream after the terrain-profile redesign. Tracks the terrain-coupled problems the
runs keep surfacing and the order we attack them. See `terrain-handling-audit.md` (current
mechanisms) and `terrain-profile-design.md` (the resolver that shipped).

## Reopened (2026-07-06) — worldgen variety & two-way biome pass
Focus shifted from species-coupling to the generator itself (game-prep). Landed:

- **River connectivity FIXED.** Rivers were traced to the ocean but only *marked* above the old
  LandLevel (0.45) — the Sand shore band (0.40–0.45) accumulated flow yet was never marked, so
  every river visibly died at the beach. Marking now runs to the waterline (0.40), and chunk
  overrides apply to any dry land tile (incl. Sand/Ice — arctic rivers stay continuous) instead
  of only `IsSpawnable()`. The world snapshot now reports river tiles / sea-outlet tiles and
  warns if rivers are severed.
- **Two-way biome generation.** Classification still runs on elevation/temperature/moisture, but
  hydrology and relief now feed back into the moisture *before* classification: riparian halos
  around rivers/lakes (wetland margins in wet climates, green corridors + oases through dry
  ones), ~13-tile marshy delta fans where rivers meet the sea (plus a tidal-marsh rule in the
  beach band), and slope drainage so swamps/bogs settle into flat basins while hillsides dry.
- **Ridged mountain ranges** (base elevation: classify/cool/shed rivers; orogeny-belt gated so
  they form a few connected chains) and **terraced cliff regions** (stored elevation only:
  mesas/bluffs with steep risers movement actually feels). `SampleBaseElevation` is now the
  single elevation authority shared by chunks and the RiverMapper (which caches the full-world
  map that chunk gen reads back).
- **Standard test config is now the code default**: 36×36 chunks / 2000 initial / 12000 cap
  (was the stale 9×9 debug setup); river source budget scales with world size.

Follow-ups from the first 36ch snapshot review (seed 1956076603, ef 0.004):
- **Drainage pivot bug fixed** — a hardcoded "typical slope" made flat low-frequency worlds
  uniformly wetter (Arid 0.1%, Wetland 10.3%); the pivot is now the world's *measured* mean
  land slope, so the moisture budget balances at any elevation frequency.
- **Shores decoupled from elevation** — beaches are now a distance-to-ocean post-pass (≤ 2
  tiles), typed by climate: Sand normally, Wetland when very wet (deltas/mangroves), nothing on
  frozen coasts, biome-kept rocky shoreline on steep coasts. Flat worlds no longer grow huge
  beach rings; the old 0.40–0.43 Sand band is gone from classification.
- **River meander** — descent choice among true-downhill neighbours is dithered by a
  deterministic per-tile hash scaled to mean slope, killing the staircase/horizontal artifact on
  flat terrain (also fixed: rivers reaching the west/north world edge ended in a fake depression
  instead of flowing off the map).
- **Cliffs sharpened** — riser concentrated into the top 15% of each terrace band, stronger
  detail damping on treads, defaults step 0.08 / strength 1.0 (ridges confirmed expressing in
  3D; cliffs were too soft to read).
- **Snapshot set extended** — `_elev`/`_moist`/`_temp` parameter maps + a post-spawn
  `_spawns` map (species-coloured dots) alongside the biome map and report.
- **Preview-driven fixes:** `TerrainOrogenyFrequency` exported (was settings-only, missing from
  the inspector); all frequency exports carry explicit 0.0001-step Range hints (Godot's default
  0.001 step silently rounded 0.0025 → 0.003 on save — masking the best-looking moisture value);
  `TerrainRiverDensity` export (default 0.4, 1 = old density) — the meandering/lake-chain look
  was right but the network was far too dense.
- **Live worldgen preview tool** (`Scenes/WorldgenPreview.tscn`, run with F6) — sliders for
  every terrain parameter with instant climate-only regeneration + a full-detail mode running
  the exact pipeline (rivers/deltas/shores). Built on the `TerrainGenerator.SampleTile`
  refactor (chunk gen and the landmark pass folded into one per-tile function), so the
  preview and the game share one source of truth. Ends the change-number→relaunch loop that
  made moisture-frequency tuning (0.002 vs 0.003 = Arid 1.3% vs 2.6% on the same seed) blind.

Verification: build-checked only (no Godot here) — needs an in-editor pass: re-snapshot the same
seed to confirm Arid recovers and wetlands thin to genuine margins; eyeball beaches, meanders,
and cliff faces; check the new maps render correctly.

Second snapshot review (seed 1131496905): shore pass, meanders, drainage rebalance all confirmed
working (Sand 6.9→1.6%, wetlands thinned, rivers wiggle and outlet). Remaining structural finding
from the `_moist` map: **moisture was the finest classification field** (freq 0.008 ≈ 125-tile
patches vs 250-tile elevation and a world-spanning temperature gradient), so every climate zone
contained the full wet↔dry spectrum in speckle — no coherent desert/rainforest/bog region could
exist (Arid 0.4% scattered specks). Fixed: `MoistureFrequency` exported (default 0.003, in scale
with the other fields) + `MoistureContrast` (1.25) stretching raw FBM toward the Arid/Bog
extremes it otherwise starves. Re-snapshot to confirm desert/jungle become regions.

## Final state (2026-07-06) — species workstream wrapped
Shipped and confirmed: unified TerrainProfile resolver (speed REPLACE, species-aware avoidance,
concealment); world-snapshot diagnostic (biome map + niche coverage); worldgen A (real deserts)
+ B (thin beaches); terrain-aware food-seeking (HuntTerrain) + niche-aware spawn placement;
LOD-compensated drowning/suffocation + habitat-aware pursuit; behavior-arbitration Option A
(per-species drive thresholds). Penguins resolved (feed from water; stable & reproducing — see
species-attribution-audit). **Still open (parked, not blocking):** #4 connected-seas (ocean-mask
worldgen redesign for the Shark niche) and #5 Scorpion ambush rework; temperature distribution is
cold-biased so deserts stay marginal. Full utility-AI arbitration (Option B) remains a future
option documented in `behavior-arbitration.md`.

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

### 2. Spawn-niche placement validation — DONE (initial)
Symptoms: front-loaded environment deaths (fish spawned off-element), and specialists with no
habitat. **Fixed:** `GetSpawnablePositionsForSpecies` now filters a non-aquatic predator's spawn
tiles to those within ~9 tiles of its `HuntTerrain` (full-set fallback if a chunk has none) — so
penguins spawn on the cold *coast* next to fish instead of inland (where food-seeking + herd
cohesion couldn't rescue them). Also concentrates Crocodile/Polar Bear near water, Scorpion/Snake
near desert, Arctic Fox near tundra. (Aquatics already spawn in water = on their food.)
*Still open:* the early environment-death cluster (fish spawned in/near wrong element) — likely a
separate placement detail; watch whether it shrinks now.

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
