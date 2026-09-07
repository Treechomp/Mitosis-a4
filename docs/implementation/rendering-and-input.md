# Rendering, camera & input

*Last updated: 2026-09-07 · verified against `7a7fb28`*

Rendering reads simulation state and writes nothing back. Terrain mesh detail and entity-on-terrain
positioning are an area of active work.

## `RenderingManager` — `Rendering/RenderingManager.cs`

### Terrain

One `MeshInstance3D` per chunk, built as a triangulated mesh: vertices via
`GridCoordinates.VertexToWorld3D` (elevation on +Y), per-vertex biome colours from
`Chunk.GetTileColor` / `TerrainPalette`, and smooth per-vertex normals.

Material is `Shaders/TerrainDither.gdshader` (`shader_type spatial`, `unshaded`): manual Lambert
sun shading, screen-space slope-edge darkening via `dFdx`/`dFdy` on elevation, and posterisation
to a fixed number of colour levels. The sun direction matches the scene's `DirectionalLight3D`.

**Water**: sea vertices below sea level are flattened to a level surface and coloured by depth, so
the seabed shape is not visible; lakes and rivers at or above sea level follow the terrain as
shallow water. Movement still uses the real floor elevation. This is a single global ocean plane —
per-water-body levels are not implemented.

Meshes rebuild only for chunks in `WorldManager.DirtyChunks`. A terraform changes only tile colour,
so only the colour stream is rebuilt and cached geometry and normals are reused.

### Entities

One `MultiMeshInstance3D` per `ShapeType`, each a Godot 3D primitive. Each instance sits at its
terrain elevation (lifted by half its height, clamped to the water surface so aquatic creatures
stay visible), rotates to face its velocity, is tinted by `Renderable.Color` and scaled by
`Renderable.Size` times growth.

**Frustum culling** happens before the multimesh is written, so only visible instances are
uploaded — the alternative was writing every entity in the world every frame and relying on Godot
to cull afterwards.

**Render interpolation**: positions are interpolated between the previous and current simulation
tick by the inter-tick fraction, so fast movers glide instead of stepping at the tick rate.

## `PlayerController` — `Scripts/PlayerController.cs`

Owns player input and the camera. The camera is an orthographic `Camera3D` in a fixed isometric
setup (pitch and yaw are fields, so orbiting is a small extension) that smoothly follows a focus
point.

**Movement is camera-relative.** Raw input is gathered in screen space, normalised, rotated by the
camera yaw into world XZ, and then converted to grid velocity by computing the exact world-space
target position and converting it back with `GridCoordinates.WorldToGrid`. Approximating the
Jacobian instead has discretisation error at row boundaries where the offset slope flips, which
showed as a zig-zag in player movement.

**Free camera** (`FreeCamera`, `FocusOn`, `EnterFreeCameraAtPlayer`) detaches the view from the
player entity. Following the player caps panning at whatever terrain it happens to be crossing,
which is fine for playing and useless for watching something on the far side of a swamp. In free
mode the focus moves directly at a rate proportional to the zoom level, so a pan covers the same
fraction of the screen at any zoom.

Zoom is an orthographic size clamped between exported bounds; `CameraSize` is exposed because
`GameManager` uses it for the LOD visible radius.

The player entity is currently a placeholder — a controllable body with a following camera. None of
the faction verbs in [`../design/05-player.md`](../design/05-player.md) exist yet, and
`FactionLives` is not consumed by anything.

## `ObservationController` — `Scripts/Tools/ObservationController.cs`

Development tools layered over a running simulation, available in both the main scene and test
scenes. Input is handled by `GameManager`.

| Key | Action |
|---|---|
| `LMB` | select the creature under the cursor; live panel |
| `Tab` / `Shift+Tab` | cycle the highlighted species (highlighted creatures render near-white) |
| `H` | highlight the selected creature's own species |
| `G` | jump the camera to the next member of the highlighted species |
| `F` | toggle free camera |
| `Esc` | clear selection and highlight |

The inspector reports **drives**, not just vitals: hunt target and distance, pack phase and role,
flee state and stamina, fear ratio and response, terrain discomfort, roam target, colony carrying
and dormancy, growth and elder state. That is what distinguishes a target-acquisition failure from
a balance problem — a predator sitting at "hunt, no target" while starving beside prey is the
former.

`HasHighlight` is a flag rather than a negative-id sentinel, because species ids are string hashes
and are negative for roughly half the roster.

## Debug overlay — `GameManager`

Always on: FPS, TPS, entity count, LOD tier counts, per-category counts, and per-class budget
state, refreshed on an interval. `F3` adds sorted per-system timings with bars and a per-species
population list.

## Runtime controls (test scenes)

| Key | Action |
|---|---|
| `Space` | pause / resume |
| `.` | single-step one tick |
| `,` | cycle simulation speed |
| `P` | toggle terrain paint mode |
| `[` / `]` | previous / next brush tile |
| `1`–`5` | brush radius |
| `LMB` drag | paint tiles |
| `O` | export current terrain as a scenario-ready ASCII map into `logs/` |

Painting sets the tile **and** its moisture and temperature parameters to the canonical values for
that type (`World/ScenarioTileParams.cs`), so terraforming and the palette stay consistent.
Elevation is not editable live — chunk meshes are built once — so painted water and mountains
render flat with their discrete colour while gameplay behaves correctly. For sunken water or raised
mountains, put them in the scenario file and relaunch.
