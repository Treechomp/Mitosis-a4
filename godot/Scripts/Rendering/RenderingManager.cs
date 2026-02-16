using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Rendering;

/// <summary>
/// Handles all visual rendering: terrain chunk textures and entity MultiMesh batching.
/// </summary>
public sealed class RenderingManager
{
    private readonly EntityManager _entityManager;
    private readonly WorldManager _worldManager;
    private readonly int _chunkSize;
    private readonly int _worldSizeChunks;
    private readonly int _tileSize;

    // Chunk texture cache
    private readonly Dictionary<(int, int), ImageTexture> _chunkTextures = new();
    private readonly HashSet<(int, int)> _dirtyChunks = new();

    // MultiMesh entity rendering
    private MultiMeshInstance2D _circleMMI = null!;
    private MultiMeshInstance2D _triangleMMI = null!;
    private MultiMeshInstance2D _squareMMI = null!;
    private const int MultiMeshInitialCapacity = 4096;

    public RenderingManager(EntityManager entityManager, WorldManager worldManager,
                            int chunkSize, int worldSizeChunks, int tileSize)
    {
        _entityManager = entityManager;
        _worldManager = worldManager;
        _chunkSize = chunkSize;
        _worldSizeChunks = worldSizeChunks;
        _tileSize = tileSize;
    }

    /// <summary>
    /// Creates the three MultiMeshInstance2D nodes for circle, triangle, and square shapes.
    /// The caller is responsible for adding them as children to the scene tree.
    /// </summary>
    public (MultiMeshInstance2D circle, MultiMeshInstance2D triangle, MultiMeshInstance2D square) CreateMultiMeshInstances()
    {
        _circleMMI = CreateMultiMeshInstance(CreateCircleMesh(16));
        _triangleMMI = CreateMultiMeshInstance(CreateTriangleMesh());
        _squareMMI = CreateMultiMeshInstance(CreateSquareMesh());
        return (_circleMMI, _triangleMMI, _squareMMI);
    }

    private static MultiMeshInstance2D CreateMultiMeshInstance(Mesh mesh)
    {
        var mm = new MultiMesh();
        mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
        mm.UseColors = true;
        mm.Mesh = mesh;
        mm.InstanceCount = MultiMeshInitialCapacity;
        mm.VisibleInstanceCount = 0;

        return new MultiMeshInstance2D { Multimesh = mm };
    }

    private static ArrayMesh CreateCircleMesh(int segments)
    {
        var mesh = new ArrayMesh();
        var vertices = new Vector3[segments + 1];
        var indices = new int[segments * 3];

        vertices[0] = Vector3.Zero; // center
        for (int i = 0; i < segments; i++)
        {
            float angle = i * (MathF.PI * 2f / segments);
            vertices[i + 1] = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
        }

        for (int i = 0; i < segments; i++)
        {
            indices[i * 3] = 0;
            indices[i * 3 + 1] = i + 1;
            indices[i * 3 + 2] = (i + 1) % segments + 1;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static ArrayMesh CreateTriangleMesh()
    {
        var mesh = new ArrayMesh();
        var vertices = new Vector3[]
        {
            new(0f, -1f, 0f),
            new(-0.866f, 0.5f, 0f),
            new(0.866f, 0.5f, 0f)
        };
        var indices = new int[] { 0, 1, 2 };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static ArrayMesh CreateSquareMesh()
    {
        var mesh = new ArrayMesh();
        var vertices = new Vector3[]
        {
            new(-0.5f, -0.5f, 0f),
            new(0.5f, -0.5f, 0f),
            new(0.5f, 0.5f, 0f),
            new(-0.5f, 0.5f, 0f)
        };
        var indices = new int[] { 0, 1, 2, 0, 2, 3 };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// Draws visible terrain chunks using cached textures.
    /// Must be called from within a CanvasItem._Draw() override.
    /// </summary>
    public void DrawTerrain(CanvasItem canvas, Camera2D camera)
    {
        // Get visible area in world coordinates
        var viewportSize = canvas.GetViewportRect().Size;
        var cameraPos = camera.Position;
        var zoom = camera.Zoom;

        float halfWidth = viewportSize.X / (2 * zoom.X);
        float halfHeight = viewportSize.Y / (2 * zoom.Y);

        float minWorldX = (cameraPos.X - halfWidth) / _tileSize;
        float maxWorldX = (cameraPos.X + halfWidth) / _tileSize;
        float minWorldY = (cameraPos.Y - halfHeight) / _tileSize;
        float maxWorldY = (cameraPos.Y + halfHeight) / _tileSize;

        // Draw visible chunks
        int minChunkX = Math.Max(0, (int)(minWorldX / _chunkSize));
        int maxChunkX = Math.Min(_worldSizeChunks - 1, (int)(maxWorldX / _chunkSize));
        int minChunkY = Math.Max(0, (int)(minWorldY / _chunkSize));
        int maxChunkY = Math.Min(_worldSizeChunks - 1, (int)(maxWorldY / _chunkSize));

        for (int cx = minChunkX; cx <= maxChunkX; cx++)
        {
            for (int cy = minChunkY; cy <= maxChunkY; cy++)
            {
                var chunk = _worldManager.GetChunk(cx, cy);
                if (chunk == null) continue;

                DrawChunk(canvas, chunk);
            }
        }
    }

    private void DrawChunk(CanvasItem canvas, Chunk chunk)
    {
        var key = (chunk.ChunkX, chunk.ChunkY);

        // Check if we need to rebuild this chunk's texture
        bool needsRebuild = !_chunkTextures.ContainsKey(key) ||
                            _worldManager.DirtyChunks.Contains(key) ||
                            _dirtyChunks.Contains(key);

        if (needsRebuild)
        {
            _chunkTextures[key] = RenderChunkTexture(chunk);
            _worldManager.DirtyChunks.Remove(key);
            _dirtyChunks.Remove(key);
        }

        // Draw the cached texture as a single rect
        float chunkWorldX = chunk.ChunkX * _chunkSize * _tileSize;
        float chunkWorldY = chunk.ChunkY * _chunkSize * _tileSize;
        float chunkPixelSize = _chunkSize * _tileSize;

        canvas.DrawTextureRect(_chunkTextures[key],
            new Rect2(chunkWorldX, chunkWorldY, chunkPixelSize, chunkPixelSize),
            false);
    }

    private ImageTexture RenderChunkTexture(Chunk chunk)
    {
        // Render at 4x resolution (4 pixels per tile) for a smooth but clear look.
        // At 1x the bilinear filtering makes tiles too blurry; at 4x the tile
        // boundaries are visible but edges still blend pleasantly.
        const int pixelsPerTile = 4;
        int texSize = chunk.Size * pixelsPerTile;
        var image = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);

        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                var tileType = chunk.GetTile(lx, ly);
                var baseColor = Chunk.GetTileColor(tileType);
                int worldX = chunk.ChunkX * chunk.Size + lx;
                int worldY = chunk.ChunkY * chunk.Size + ly;

                int px = lx * pixelsPerTile;
                int py = ly * pixelsPerTile;
                for (int dy = 0; dy < pixelsPerTile; dy++)
                {
                    for (int dx = 0; dx < pixelsPerTile; dx++)
                    {
                        var color = VaryTilePixel(baseColor, tileType, worldX, worldY, dx, dy);
                        image.SetPixel(px + dx, py + dy, color);
                    }
                }
            }
        }

        var texture = ImageTexture.CreateFromImage(image);
        return texture;
    }

    /// <summary>
    /// Apply deterministic per-pixel color variation to break up flat tile colors.
    /// Uses a fast hash seeded from world position + sub-pixel offset for consistency.
    /// </summary>
    private static Color VaryTilePixel(Color baseColor, TileType tile, int wx, int wy, int dx, int dy)
    {
        uint hash = PixelHash(wx, wy, dx, dy);
        // Normalized random value 0-1
        float rand = (hash & 0xFFFF) / 65535f;
        // Secondary random for feature checks
        float rand2 = ((hash >> 16) & 0xFFFF) / 65535f;

        float r = baseColor.R;
        float g = baseColor.G;
        float b = baseColor.B;

        switch (tile)
        {
            // Vegetation tiles: brightness variation + occasional dark "tree" pixels
            case TileType.Forest:
            case TileType.Taiga:
            case TileType.Jungle:
            {
                float variation = (rand - 0.5f) * 0.12f;
                r += variation;
                g += variation;
                b += variation;
                // ~18% chance of a darker pixel suggesting tree canopy depth
                if (rand2 < 0.18f)
                {
                    r -= 0.06f;
                    g -= 0.04f;
                    b -= 0.05f;
                }
                break;
            }

            // Grasslands: gentle brightness and slight hue shift
            case TileType.Grass:
            case TileType.Steppe:
            case TileType.Savanna:
            case TileType.Shrubland:
            {
                float brightness = (rand - 0.5f) * 0.08f;
                float hueShift = (rand2 - 0.5f) * 0.03f;
                r += brightness + hueShift;
                g += brightness;
                b += brightness - hueShift;
                break;
            }

            // Dry terrain: speckle with occasional lighter grains
            case TileType.Sand:
            case TileType.Dirt:
            case TileType.Arid:
            {
                float variation = (rand - 0.5f) * 0.1f;
                r += variation;
                g += variation;
                b += variation;
                // ~10% lighter grain
                if (rand2 < 0.10f)
                {
                    r += 0.06f;
                    g += 0.05f;
                    b += 0.03f;
                }
                break;
            }

            // Water tiles: very subtle ripple-like variation
            case TileType.DeepWater:
            case TileType.ShallowWater:
            case TileType.River:
            case TileType.Reef:
            {
                float variation = (rand - 0.5f) * 0.05f;
                r += variation * 0.5f;
                g += variation * 0.7f;
                b += variation;
                break;
            }

            // Wet tiles: murky variation with dark spots
            case TileType.Wetland:
            case TileType.Bog:
            {
                float variation = (rand - 0.5f) * 0.1f;
                r += variation;
                g += variation;
                b += variation;
                if (rand2 < 0.12f)
                {
                    r -= 0.04f;
                    g -= 0.02f;
                    b -= 0.03f;
                }
                break;
            }

            // Rocky/cold tiles: higher contrast variation for texture
            case TileType.Mountain:
            case TileType.Tundra:
            case TileType.Ice:
            {
                float variation = (rand - 0.5f) * 0.08f;
                r += variation;
                g += variation;
                b += variation;
                break;
            }

            case TileType.Lava:
            {
                // Flickering glow effect
                float variation = (rand - 0.5f) * 0.15f;
                r += variation;
                g += variation * 0.5f;
                break;
            }

            default:
            {
                float variation = (rand - 0.5f) * 0.06f;
                r += variation;
                g += variation;
                b += variation;
                break;
            }
        }

        return new Color(
            Math.Clamp(r, 0f, 1f),
            Math.Clamp(g, 0f, 1f),
            Math.Clamp(b, 0f, 1f)
        );
    }

    /// <summary>
    /// Fast deterministic hash for per-pixel tile variation.
    /// Produces consistent results for the same world position + sub-pixel offset.
    /// </summary>
    private static uint PixelHash(int wx, int wy, int dx, int dy)
    {
        uint h = (uint)(wx * 374761393 + wy * 668265263 + dx * 2147483647 + dy * 1013904223);
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return h;
    }

    /// <summary>
    /// Updates entity MultiMesh buffers for rendering. Called each frame from _Process.
    /// </summary>
    public void UpdateEntityMultiMeshes(Camera2D camera)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;

        // Compute visible world bounds for frustum culling
        var viewportSize = _circleMMI.GetViewportRect().Size;
        var cameraPos = camera.Position;
        var zoom = camera.Zoom;
        float halfW = viewportSize.X / (2 * zoom.X);
        float halfH = viewportSize.Y / (2 * zoom.Y);
        float cullMinX = cameraPos.X - halfW;
        float cullMaxX = cameraPos.X + halfW;
        float cullMinY = cameraPos.Y - halfH;
        float cullMaxY = cameraPos.Y + halfH;

        var circleMM = _circleMMI.Multimesh;
        var triMM = _triangleMMI.Multimesh;
        var squareMM = _squareMMI.Multimesh;

        // Pre-grow capacity if total entity count exceeds current buffers
        // (avoids mid-loop resize which clears instance data)
        int totalEntities = _entityManager.EntityCount;
        if (circleMM.InstanceCount < totalEntities)
            circleMM.InstanceCount = totalEntities;
        if (triMM.InstanceCount < totalEntities)
            triMM.InstanceCount = totalEntities;
        if (squareMM.InstanceCount < totalEntities)
            squareMM.InstanceCount = totalEntities;

        int circleIdx = 0, triIdx = 0, squareIdx = 0;

        foreach (int entity in _entityManager.Query(required))
        {
            ref var pos = ref _entityManager.Positions[entity];
            ref var rend = ref _entityManager.Renderables[entity];

            float screenX = pos.X * _tileSize;
            float screenY = pos.Y * _tileSize;

            // Frustum cull: skip entities outside the visible viewport
            float margin = rend.Size * 2f;
            if (screenX < cullMinX - margin || screenX > cullMaxX + margin ||
                screenY < cullMinY - margin || screenY > cullMaxY + margin)
                continue;

            var transform = new Transform2D(0f, new Vector2(rend.Size, rend.Size), 0f,
                new Vector2(screenX, screenY));

            switch (rend.Shape)
            {
                case ShapeType.Circle:
                    circleMM.SetInstanceTransform2D(circleIdx, transform);
                    circleMM.SetInstanceColor(circleIdx, rend.Color);
                    circleIdx++;
                    break;
                case ShapeType.Triangle:
                    triMM.SetInstanceTransform2D(triIdx, transform);
                    triMM.SetInstanceColor(triIdx, rend.Color);
                    triIdx++;
                    break;
                case ShapeType.Square:
                    squareMM.SetInstanceTransform2D(squareIdx, transform);
                    squareMM.SetInstanceColor(squareIdx, rend.Color);
                    squareIdx++;
                    break;
            }
        }

        circleMM.VisibleInstanceCount = circleIdx;
        triMM.VisibleInstanceCount = triIdx;
        squareMM.VisibleInstanceCount = squareIdx;
    }
}
