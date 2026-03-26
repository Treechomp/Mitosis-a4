using System;
using System.Runtime.CompilerServices;
using Godot;

namespace Mitosis.Utils;

/// <summary>
/// Converts between abstract vertex grid coordinates and screen (pixel) coordinates
/// for the offset-row triangulated grid.
///
/// Every odd row (vy % 2 == 1) is shifted right by half a tile so that connecting
/// adjacent vertices produces triangles rather than axis-aligned squares.
///
/// Abstract grid space  →  screen space
///   even row:  screenX = vx * tileSize
///   odd  row:  screenX = vx * tileSize + tileSize * 0.5
///              screenY = vy * tileSize  (both rows)
///
/// All gameplay systems (movement, pathfinding, spatial hash) continue to operate
/// in abstract grid space. Only rendering and player input use screen space.
/// </summary>
public static class GridCoordinates
{
    /// <summary>
    /// Computes the smooth row offset for a given grid-Y position.
    ///
    /// In the offset-row triangulated grid, odd integer rows are shifted right
    /// by half a tile. For fractional Y values (entity/camera positions), this
    /// method linearly interpolates the offset between the floor and ceil rows,
    /// preventing the visual snap that would occur with a hard binary switch.
    ///
    /// At integer Y values the result matches the discrete offset exactly,
    /// so terrain mesh vertices (always at integer coords) are unaffected.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothRowOffset(float vy, float tileSize)
    {
        int rowBelow = (int)MathF.Floor(vy);
        float fy = vy - rowBelow;
        float offsetBelow = (rowBelow & 1) != 0 ? tileSize * 0.5f : 0f;
        float offsetAbove = ((rowBelow + 1) & 1) != 0 ? tileSize * 0.5f : 0f;
        return offsetBelow + fy * (offsetAbove - offsetBelow);
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
