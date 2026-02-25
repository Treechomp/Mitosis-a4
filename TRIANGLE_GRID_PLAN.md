# Triangle Grid Implementation Plan

## Core Concept

The grid points (vertices) stay at the same integer coordinates in data space. Every other row is offset by half a tile width in screen space. Connecting adjacent vertices forms triangles. Data (TileType, elevation, moisture) lives at vertices, and triangle faces interpolate between their 3 vertices for smooth visual blending.

The vertex screen position formula:
```
screenX = vx * tileSize + (vy % 2 == 1 ? tileSize * 0.5f : 0)
screenY = vy * tileSize
```

All gameplay systems (movement, spatial hash, rivers, pathfinding) continue to operate on the abstract integer grid. The offset is a rendering/coordinate concern only — until Phase 5 adds true 3D elevation.

---

## Phase 1 — Store Elevation at Vertices

**Goal**: Persist raw elevation values per vertex in Chunk. No visual change.

**Files**: `Chunk.cs`, `TerrainGenerator.cs`, `WorldManager.cs`

**Changes**:
- Add `private float[,] _elevation` array to `Chunk` (same dimensions as `_tiles`)
- Add `GetElevation(int localX, int localY) → float` and `SetElevation(int localX, int localY, float v)` to `Chunk`
- In `TerrainGenerator.GenerateChunk`, the `elevation` variable is already computed per vertex — call `chunk.SetElevation(localX, localY, elevation)` after computing it
- Add `WorldManager.GetElevation(float worldX, float worldY) → float` pass-through (mirrors `GetTile`)

**Milestone**: Elevation data accessible everywhere. No gameplay or visual change.

**Risk**: None.

---

## Phase 2 — Coordinate Helper

**Goal**: Centralize screen↔world math with row offset in one place.

**Files**: `Scripts/Utils/GridCoordinates.cs` (new file)

**Changes**:
```csharp
public static class GridCoordinates
{
    public static Vector2 VertexToScreen(float vx, float vy, float tileSize)
    {
        float offsetX = ((int)vy % 2 == 1) ? tileSize * 0.5f : 0f;
        return new Vector2(vx * tileSize + offsetX, vy * tileSize);
    }

    public static Vector2 ScreenToVertex(Vector2 screen, float tileSize)
    {
        float vy = screen.Y / tileSize;
        float offsetX = ((int)vy % 2 == 1) ? tileSize * 0.5f : 0f;
        float vx = (screen.X - offsetX) / tileSize;
        return new Vector2(vx, vy);
    }
}
```

**Milestone**: Conversion math isolated and testable. Not yet wired anywhere.

**Risk**: None.

---

## Phase 3 — Triangle Mesh Terrain Renderer

**Goal**: Replace per-chunk `ImageTexture` + `DrawTextureRect` with `MeshInstance2D` triangle meshes per chunk. Row offset applied. Vertex colors interpolated by GPU (smooth biome blending).

**Files**: `RenderingManager.cs`, `GameManager.cs`

**Changes in `RenderingManager.cs`**:
- Replace `Dictionary<(int,int), ImageTexture> _chunkTextures` with `Dictionary<(int,int), MeshInstance2D> _chunkMeshes`
- Add `BuildChunkMesh(Chunk chunk) → ArrayMesh`:
  - Vertices: for each `(lx, ly)` in `0..chunkSize`, compute screen position via `GridCoordinates.VertexToScreen`
  - Colors: `Chunk.GetTileColor(chunk.GetTile(lx, ly))` per vertex
  - Indices: for each quad `(lx,ly)→(lx+1,ly)→(lx,ly+1)→(lx+1,ly+1)`, emit 2 triangles with stagger depending on even/odd row:
    - Even row vy: triangles `[BL, BR, TL]` and `[BR, TR, TL]`
    - Odd row vy: triangles `[BL, BR, TR]` and `[BL, TR, TL]`
  - `ArrayMesh` with `Mesh.ArrayType.Vertex` (Vector3 with Z=0), `Mesh.ArrayType.Color`, `Mesh.ArrayType.Index`
- Add `CreateOrUpdateChunkMesh(Chunk chunk)` that builds the mesh and either creates a new `MeshInstance2D` child or rebuilds an existing one
- Add a `MeshMaterial` with `vertex_color_use_as_albedo = true` (or use `StandardMaterial3D` / `ORMMaterial3D` with unshaded vertex colors)
- Remove `DrawTerrain`, `DrawChunk`, `RenderChunkTexture`, `VaryTilePixel`, `PixelHash`
- Add `InitializeChunkMeshes(Node parent)` — called once on init to create `MeshInstance2D` nodes as scene children
- Add `UpdateDirtyChunkMeshes()` — called each frame, rebuilds meshes for dirty chunks

**Changes in `GameManager.cs`**:
- In `_Ready()`: call `_renderingManager.InitializeChunkMeshes(this)` instead of current MultiMesh setup
- In `_Process()`: call `_renderingManager.UpdateDirtyChunkMeshes()` instead of `QueueRedraw()`
- Remove `_Draw()` override (terrain no longer drawn there)

**Milestone**: World renders as a triangulated mesh with smooth color blending between biomes at triangle edges. Entities may be slightly misaligned (fixed in Phase 4).

**Visual result**: Biome borders look organic and gradient-blended instead of hard-edged squares. The offset rows give the terrain a slightly irregular, non-grid feel.

**Risk**: Medium. Main risk is mesh rebuilding performance. Mitigation: 33×33=1,089 vertices and 32×32×2=2,048 triangles per chunk is very lightweight — faster to build than the current CPU pixel loop.

---

## Phase 4 — Entity Screen Position Alignment

**Goal**: Apply row offset to entity screen positions so entities sit correctly on the offset terrain.

**Files**: `RenderingManager.cs`, `PlayerController.cs`

**Changes in `RenderingManager.cs`**:
- In `UpdateEntityMultiMeshes`, replace:
  ```csharp
  float screenX = pos.X * _tileSize;
  float screenY = pos.Y * _tileSize;
  ```
  with:
  ```csharp
  var screen = GridCoordinates.VertexToScreen(pos.X, pos.Y, _tileSize);
  float screenX = screen.X;
  float screenY = screen.Y;
  ```

**Changes in `PlayerController.cs`**:
- Update any `screenPos / tileSize` world conversion to use `GridCoordinates.ScreenToVertex`
- Update camera positioning math if it uses screen→world for centering

**Milestone**: Entities visually sit on the correct terrain positions. Game looks correct end-to-end with the triangulated grid.

**Risk**: Low. Mostly mechanical substitution of coordinate math.

---

## Phase 5A — Fake Isometric Elevation (Quick Win)

**Goal**: Use stored elevation to offset vertex Y positions in 2D screen space — giving a faux-3D look without changing the scene tree.

**Files**: `RenderingManager.cs`, `GridCoordinates.cs`

**Changes**:
- Add `heightScale` parameter (e.g. `tileSize * 4f`)
- Update `GridCoordinates.VertexToScreen` to accept optional elevation:
  ```csharp
  public static Vector2 VertexToScreen(float vx, float vy, float tileSize, float elevation = 0f, float heightScale = 0f)
  {
      float offsetX = ((int)vy % 2 == 1) ? tileSize * 0.5f : 0f;
      return new Vector2(
          vx * tileSize + offsetX,
          vy * tileSize - elevation * heightScale  // elevation lifts vertices up
      );
  }
  ```
- In `BuildChunkMesh`, pass `chunk.GetElevation(lx, ly)` to `VertexToScreen`

**Milestone**: Mountains visually "rise" above sea level. Valleys and oceans sit lower. The terrain has a hand-drawn topographic map appearance. No engine changes required.

**Risk**: Low-medium. Visual-only. May require camera adjustment for comfortable viewing.

---

## Phase 5B — True 3D (Full Milestone)

**Goal**: Proper 3D terrain mesh with perspective/isometric camera.

**Architecture shift** — this is the big one:

**Files**: `GameManager.cs`, `RenderingManager.cs`, all `MultiMeshInstance2D` → `MultiMeshInstance3D`

**Changes**:
- `GameManager` inherits `Node3D` instead of `Node2D`
- Terrain: `MeshInstance2D` → `MeshInstance3D`
- In `BuildChunkMesh`, vertex positions become `Vector3(screenX, -elevation * heightScale, screenY)` (Godot Y-up, so elevation lifts in -Y)
- Camera: `Camera2D` → `Camera3D` with `ProjectionType = Orthographic`, rotated to isometric angle (e.g. -30° X rotation, 45° Y rotation) or perspective
- Entity rendering: `MultiMeshInstance2D` → `MultiMeshInstance3D`, entity Z position = terrain elevation at their position (sampled via `WorldManager.GetElevation`)
- Add `DirectionalLight3D` for depth shading (sunlight angle)
- Add normals to `ArrayMesh` for proper lighting (compute per-triangle or smooth per-vertex)

**Milestone**: Full 3D terrain. Mountains cast shadows, creatures walk up slopes, camera can be freely orbited.

**Risk**: High. This is a scene architecture change. Recommend doing on a dedicated branch after Phase 4 is stable.

---

## Phase 6 — Gameplay System Alignment

**Goal**: Align remaining systems with offset grid and elevation.

**Files**: `RiverMapper.cs`, `MovementSystem.cs`, `WorldSpawner.cs`

**Changes**:
- `RiverMapper`: Update neighbor directions from 8-directional to 6-directional (offset grid has 6 natural neighbors per vertex). Current `DX/DY[8]` → split into even-row and odd-row neighbor tables
- `MovementSystem`: Entity movement slope resistance — penalize movement up steep elevation gradients (sample elevation delta between current and destination vertex)
- `WorldSpawner`: Spawning on flat terrain — use elevation data to avoid spawning on steep slopes
- `PlayerController`: 3D camera orbit controls (pan, zoom, rotate) for the full 3D mode

---

## Implementation Order

```
Phase 1  →  Phase 2  →  Phase 3  →  Phase 4
   ↓
(stable 2D triangulated baseline)
   ↓
Phase 5A  →  validate look  →  Phase 5B
   ↓
Phase 6
```

Phases 1–4 are safe and incremental. The game stays playable throughout. Phase 5A is a quick visual experiment. Phase 5B is the architectural commitment.

---

## Key Invariants to Preserve

1. The abstract grid (integer worldX, worldY) is the canonical data space. All gameplay logic uses it.
2. `WorldManager.GetTile(float x, float y)` continues to return the biome/type at any float position.
3. Entity positions remain continuous float world coordinates throughout.
4. `TerrainGenerator` noise sampling is unchanged — only the persistence of elevation changes.
5. `RiverMapper` can be updated in Phase 6 independently; the flow logic is correct regardless of visual offset.
