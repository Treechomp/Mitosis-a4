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
    /// Convert an abstract vertex position to screen (pixel) coordinates.
    /// Optionally lifts the vertex by elevation for faux-isometric or 2.5D rendering.
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
        float offsetX = ((int)vy % 2 == 1) ? tileSize * 0.5f : 0f;
        return new Vector2(
            vx * tileSize + offsetX,
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
        float offsetX = ((int)vy % 2 == 1) ? tileSize * 0.5f : 0f;
        float vx = (screen.X - offsetX) / tileSize;
        return new Vector2(vx, vy);
    }
}
