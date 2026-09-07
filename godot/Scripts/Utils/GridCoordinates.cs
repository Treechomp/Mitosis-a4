using System;
using System.Runtime.CompilerServices;
using Godot;

namespace Mitosis.Utils;

/// <summary>
/// Converts between abstract vertex grid coordinates and screen / 3D-world coordinates.
///
/// The mapping is a plain linear square grid:
///   worldX = vx * tileSize,  worldZ = -vy * tileSize,  worldY = elevation * heightScale
///
/// (Historically odd rows were shifted half a tile to fake a hex/triangular look via
/// SmoothRowOffset, but that made straight grid-space movement render as a zig-zag, so the
/// offset was removed — see docs/archive/3d-terrain-plan-2026-06.md, Phase 1. SmoothRowOffset now returns 0
/// and is retained only so call sites stay stable.)
///
/// All gameplay systems (movement, pathfinding, spatial hash) operate in abstract grid
/// space; only rendering and player input convert to world space.
/// </summary>
public static class GridCoordinates
{
    /// <summary>
    /// Row offset for the (former) offset-row layout. Disabled: returns 0 so the grid maps
    /// linearly to world space and grid-space movement renders straight (no zig-zag).
    /// Kept as a no-op for call-site stability; see docs/archive/3d-terrain-plan-2026-06.md (Phase 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothRowOffset(float vy, float tileSize)
    {
        // Staggered odd-row offset removed (Phase 1) — linear square mapping.
        return 0f;
    }

    /// <summary>
    /// Convert a 3D world-space position (XZ plane) back to abstract grid coordinates.
    /// Inverse of VertexToWorld3D (elevation/Y is ignored).
    /// </summary>
    /// <param name="worldX">World X position.</param>
    /// <param name="worldZ">World Z position (grid Y maps to -Z).</param>
    /// <param name="tileSize">World units per grid unit.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (float gridX, float gridY) WorldToGrid(float worldX, float worldZ, float tileSize)
    {
        float gridY = -worldZ / tileSize;
        float gridX = (worldX - SmoothRowOffset(gridY, tileSize)) / tileSize;
        return (gridX, gridY);
    }

    /// <summary>
    /// Convert an abstract vertex position to screen (pixel) coordinates.
    /// Optionally lifts the vertex by elevation for faux-isometric or 2.5D rendering.
    /// The row offset is smoothly interpolated for fractional Y positions.
    /// </summary>
    /// <param name="vx">Vertex X in abstract grid space (may be fractional).</param>
    /// <param name="vy">Vertex Y in abstract grid space (may be fractional).</param>
    /// <param name="tileSize">Pixel size of one grid unit.</param>
    /// <param name="elevation">Raw elevation value (0–1). Only used when heightScale > 0.</param>
    /// <param name="heightScale">Pixels per unit of elevation. Pass 0 (default) for flat 2D.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 VertexToScreen(float vx, float vy, float tileSize,
                                         float elevation = 0f, float heightScale = 0f)
    {
        return new Vector2(
            vx * tileSize + SmoothRowOffset(vy, tileSize),
            vy * tileSize - elevation * heightScale
        );
    }

    /// <summary>
    /// Convert a screen (pixel) position back to abstract vertex grid coordinates.
    /// Inverse of VertexToScreen (elevation lift is ignored for the reverse).
    /// </summary>
    /// <param name="screen">Screen position in pixels.</param>
    /// <param name="tileSize">Pixel size of one grid unit.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 ScreenToVertex(Vector2 screen, float tileSize)
    {
        float vy = screen.Y / tileSize;
        float vx = (screen.X - SmoothRowOffset(vy, tileSize)) / tileSize;
        return new Vector2(vx, vy);
    }

    /// <summary>
    /// Convert an abstract vertex position to a 3D world position.
    /// The terrain lies in the XZ plane (Y-up); elevation lifts vertices along +Y.
    /// The row offset is smoothly interpolated for fractional Y positions.
    /// </summary>
    /// <param name="vx">Vertex X in abstract grid space.</param>
    /// <param name="vy">Vertex Y in abstract grid space (becomes world -Z).</param>
    /// <param name="tileSize">World units per grid unit.</param>
    /// <param name="elevation">Raw elevation value (0–1). Only used when heightScale > 0.</param>
    /// <param name="heightScale">World units per unit of elevation.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 VertexToWorld3D(float vx, float vy, float tileSize,
                                          float elevation = 0f, float heightScale = 0f)
    {
        // Grid Y maps to world -Z so that:
        //   • +Y movement (south in grid) moves in -Z (away from camera), appearing to go up in the isometric view.
        //   • The terrain winding (vBL→vBR→vTL in XZ) produces +Y face normals, matching the directional light from above.
        return new Vector3(
            vx * tileSize + SmoothRowOffset(vy, tileSize),
            elevation * heightScale,   // +Y is up in Godot 3D
            -vy * tileSize
        );
    }
}
