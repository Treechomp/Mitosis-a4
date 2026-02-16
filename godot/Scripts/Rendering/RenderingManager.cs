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
/// Each ShapeType gets its own MultiMesh for efficient batched rendering.
/// Supports velocity-based facing rotation and per-shape animation.
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

    // MultiMesh entity rendering — one per ShapeType
    private const int ShapeCount = 13; // ShapeType values 0..12
    private const int MultiMeshInitialCapacity = 4096;
    private MultiMeshInstance2D[] _shapeMMIs = null!;
    private int[] _shapeIndices = null!;

    // Animation state
    private int _frameTick;

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
    /// Creates MultiMeshInstance2D nodes for all shape types.
    /// Returns them as an array — the caller adds them as children to the scene tree.
    /// </summary>
    public MultiMeshInstance2D[] CreateMultiMeshInstances()
    {
        _shapeMMIs = new MultiMeshInstance2D[ShapeCount];
        _shapeIndices = new int[ShapeCount];

        _shapeMMIs[(int)ShapeType.Circle] = CreateMultiMeshInstance(CreateCircleMesh(16));
        _shapeMMIs[(int)ShapeType.Triangle] = CreateMultiMeshInstance(CreateTriangleMesh());
        _shapeMMIs[(int)ShapeType.Square] = CreateMultiMeshInstance(CreateSquareMesh());
        _shapeMMIs[(int)ShapeType.Diamond] = CreateMultiMeshInstance(CreateDiamondMesh());
        _shapeMMIs[(int)ShapeType.Star] = CreateMultiMeshInstance(CreateStarMesh(6, 1.0f, 0.5f));
        _shapeMMIs[(int)ShapeType.Chevron] = CreateMultiMeshInstance(CreateChevronMesh());
        _shapeMMIs[(int)ShapeType.FishShape] = CreateMultiMeshInstance(CreateFishMesh());
        _shapeMMIs[(int)ShapeType.Fin] = CreateMultiMeshInstance(CreateFinMesh());
        _shapeMMIs[(int)ShapeType.Teardrop] = CreateMultiMeshInstance(CreateTeardropMesh());
        _shapeMMIs[(int)ShapeType.Crescent] = CreateMultiMeshInstance(CreateCrescentMesh());
        _shapeMMIs[(int)ShapeType.Serpent] = CreateMultiMeshInstance(CreateSerpentMesh());
        _shapeMMIs[(int)ShapeType.Mushroom] = CreateMultiMeshInstance(CreateMushroomMesh());
        _shapeMMIs[(int)ShapeType.Fangs] = CreateMultiMeshInstance(CreateFangsMesh());

        return _shapeMMIs;
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

    // ================================================================
    // MESH GENERATORS
    // ================================================================

    /// <summary>Helper: build an ArrayMesh from vertex + index arrays.</summary>
    private static ArrayMesh BuildMesh(Vector3[] vertices, int[] indices)
    {
        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // --- Circle (existing) ---
    private static ArrayMesh CreateCircleMesh(int segments)
    {
        var vertices = new Vector3[segments + 1];
        var indices = new int[segments * 3];

        vertices[0] = Vector3.Zero;
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
        return BuildMesh(vertices, indices);
    }

    // --- Triangle (existing, pointing up) ---
    private static ArrayMesh CreateTriangleMesh()
    {
        return BuildMesh(
            new Vector3[] { new(0f, -1f, 0f), new(-0.866f, 0.5f, 0f), new(0.866f, 0.5f, 0f) },
            new int[] { 0, 1, 2 });
    }

    // --- Square (existing) ---
    private static ArrayMesh CreateSquareMesh()
    {
        return BuildMesh(
            new Vector3[] { new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(0.5f, 0.5f, 0f), new(-0.5f, 0.5f, 0f) },
            new int[] { 0, 1, 2, 0, 2, 3 });
    }

    // --- Diamond (rotated square, taller than wide → shield/armor look) ---
    private static ArrayMesh CreateDiamondMesh()
    {
        return BuildMesh(
            new Vector3[] { new(0f, -1.1f, 0f), new(0.7f, 0f, 0f), new(0f, 0.9f, 0f), new(-0.7f, 0f, 0f) },
            new int[] { 0, 1, 2, 0, 2, 3 });
    }

    // --- 6-pointed Star (compound: outer star + inner hexagon fill) ---
    private static ArrayMesh CreateStarMesh(int points, float outerR, float innerR)
    {
        // Star: alternating outer/inner vertices fan-triangulated from center
        int totalVerts = points * 2;
        var vertices = new Vector3[totalVerts + 1];
        var indices = new int[totalVerts * 3];

        vertices[0] = Vector3.Zero; // center
        for (int i = 0; i < totalVerts; i++)
        {
            float angle = i * MathF.PI / points - MathF.PI / 2f;
            float r = (i % 2 == 0) ? outerR : innerR;
            vertices[i + 1] = new Vector3(MathF.Cos(angle) * r, MathF.Sin(angle) * r, 0f);
        }
        for (int i = 0; i < totalVerts; i++)
        {
            indices[i * 3] = 0;
            indices[i * 3 + 1] = i + 1;
            indices[i * 3 + 2] = (i + 1) % totalVerts + 1;
        }
        return BuildMesh(vertices, indices);
    }

    // --- Chevron (bird V-silhouette — two swept-back wings) ---
    private static ArrayMesh CreateChevronMesh()
    {
        // V shape with thickness: left wing, right wing, each as a quad
        return BuildMesh(
            new Vector3[]
            {
                // Left wing
                new(0f, -0.3f, 0f),    // 0: center top
                new(-1f, 0.3f, 0f),    // 1: left wing tip top
                new(-0.8f, 0.7f, 0f),  // 2: left wing tip bottom
                new(0f, 0.15f, 0f),    // 3: center bottom
                // Right wing
                new(1f, 0.3f, 0f),     // 4: right wing tip top
                new(0.8f, 0.7f, 0f),   // 5: right wing tip bottom
            },
            new int[]
            {
                0, 1, 2,  0, 2, 3,  // Left wing
                0, 3, 5,  0, 5, 4,  // Right wing
            });
    }

    // --- Fish (oval body + forked tail) ---
    private static ArrayMesh CreateFishMesh()
    {
        return BuildMesh(
            new Vector3[]
            {
                // Body (diamond-ish)
                new(-0.8f, 0f, 0f),    // 0: nose
                new(0f, -0.5f, 0f),    // 1: top
                new(0.4f, 0f, 0f),     // 2: rear
                new(0f, 0.5f, 0f),     // 3: bottom
                // Forked tail
                new(0.9f, -0.5f, 0f),  // 4: tail top
                new(0.9f, 0.5f, 0f),   // 5: tail bottom
            },
            new int[]
            {
                0, 1, 2,  0, 2, 3,  // Body
                2, 4, 5,             // Tail fork
            });
    }

    // --- Fin (dorsal fin silhouette — tall triangle with curved base) ---
    private static ArrayMesh CreateFinMesh()
    {
        return BuildMesh(
            new Vector3[]
            {
                new(0f, -1.1f, 0f),    // 0: fin tip
                new(-0.5f, 0.5f, 0f),  // 1: base left
                new(0.7f, 0.5f, 0f),   // 2: base right
                new(0.3f, 0.1f, 0f),   // 3: curve point
            },
            new int[] { 0, 1, 3, 0, 3, 2 });
    }

    // --- Teardrop (rounded bottom, pointed top) ---
    private static ArrayMesh CreateTeardropMesh()
    {
        // Bottom half is a semicircle, top is a pointed tip
        const int segments = 8;
        var vertices = new Vector3[segments + 2]; // center + semicircle + tip
        var idxList = new List<int>();

        vertices[0] = new Vector3(0f, 0.1f, 0f); // center of bottom half
        for (int i = 0; i <= segments; i++)
        {
            float angle = MathF.PI * i / segments; // 0 to PI (bottom semicircle)
            vertices[i + 1] = new Vector3(MathF.Cos(angle) * 0.8f, MathF.Sin(angle) * 0.6f + 0.1f, 0f);
        }
        // Fan triangulate semicircle
        for (int i = 0; i < segments; i++)
        {
            idxList.Add(0);
            idxList.Add(i + 1);
            idxList.Add(i + 2);
        }
        // Pointed top: triangle from leftmost, rightmost of semicircle to tip
        int tipIdx = vertices.Length - 1;
        vertices[tipIdx] = new Vector3(0f, -0.9f, 0f); // pointed tip (top)
        idxList.Add(tipIdx);
        idxList.Add(segments + 1); // rightmost semicircle point
        idxList.Add(1); // leftmost semicircle point
        return BuildMesh(vertices, idxList.ToArray());
    }

    // --- Crescent (curved pincer shape — scorpion) ---
    private static ArrayMesh CreateCrescentMesh()
    {
        // Two arcs: outer and inner, forming a crescent pointing up
        const int segments = 8;
        float outerR = 1.0f;
        float innerR = 0.6f;
        float arcSpan = MathF.PI * 0.8f; // ~144 degree arc

        var vertices = new Vector3[(segments + 1) * 2];
        var idxList = new List<int>();

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = -arcSpan / 2f + t * arcSpan - MathF.PI / 2f;
            vertices[i] = new Vector3(MathF.Cos(angle) * outerR, MathF.Sin(angle) * outerR, 0f);
            vertices[segments + 1 + i] = new Vector3(MathF.Cos(angle) * innerR, MathF.Sin(angle) * innerR, 0f);
        }

        // Quad strip between outer and inner arcs
        for (int i = 0; i < segments; i++)
        {
            int o0 = i, o1 = i + 1;
            int i0 = segments + 1 + i, i1 = segments + 2 + i;
            idxList.Add(o0); idxList.Add(o1); idxList.Add(i1);
            idxList.Add(o0); idxList.Add(i1); idxList.Add(i0);
        }
        return BuildMesh(vertices, idxList.ToArray());
    }

    // --- Serpent (S-curve — three connected segments) ---
    private static ArrayMesh CreateSerpentMesh()
    {
        // S-curve made of 3 connected quads with slight offset
        const float w = 0.25f; // half-width of body
        return BuildMesh(
            new Vector3[]
            {
                // Segment 1 (head, upper-left to mid-right)
                new(-0.3f - w, -0.9f, 0f),  // 0
                new(-0.3f + w, -0.9f, 0f),  // 1
                new(0.2f + w, -0.2f, 0f),   // 2
                new(0.2f - w, -0.2f, 0f),   // 3
                // Segment 2 (mid-right to mid-left)
                new(-0.2f + w, 0.3f, 0f),   // 4
                new(-0.2f - w, 0.3f, 0f),   // 5
                // Segment 3 (tail, mid-left to lower-right)
                new(0.3f + w, 0.9f, 0f),    // 6
                new(0.3f - w, 0.9f, 0f),    // 7
            },
            new int[]
            {
                0, 1, 2, 0, 2, 3,  // Head segment
                3, 2, 4, 3, 4, 5,  // Mid segment
                5, 4, 6, 5, 6, 7,  // Tail segment
            });
    }

    // --- Mushroom (compound: semicircle cap on thin rectangular stem) ---
    private static ArrayMesh CreateMushroomMesh()
    {
        const int capSegments = 8;
        var vertList = new List<Vector3>();
        var idxList = new List<int>();

        // Cap: top semicircle
        int capCenter = 0;
        vertList.Add(new Vector3(0f, -0.3f, 0f)); // center of cap
        for (int i = 0; i <= capSegments; i++)
        {
            float angle = MathF.PI * i / capSegments; // 0 to PI
            vertList.Add(new Vector3(MathF.Cos(angle) * 0.9f, -MathF.Sin(angle) * 0.7f - 0.1f, 0f));
        }
        for (int i = 0; i < capSegments; i++)
        {
            idxList.Add(capCenter);
            idxList.Add(i + 1);
            idxList.Add(i + 2);
        }

        // Stem: thin rectangle
        int stemStart = vertList.Count;
        vertList.Add(new Vector3(-0.2f, -0.2f, 0f));
        vertList.Add(new Vector3(0.2f, -0.2f, 0f));
        vertList.Add(new Vector3(0.2f, 0.8f, 0f));
        vertList.Add(new Vector3(-0.2f, 0.8f, 0f));
        idxList.Add(stemStart); idxList.Add(stemStart + 1); idxList.Add(stemStart + 2);
        idxList.Add(stemStart); idxList.Add(stemStart + 2); idxList.Add(stemStart + 3);

        return BuildMesh(vertList.ToArray(), idxList.ToArray());
    }

    // --- Fangs (wide jaw — triangle with V-notch at bottom for open mouth look) ---
    private static ArrayMesh CreateFangsMesh()
    {
        return BuildMesh(
            new Vector3[]
            {
                new(0f, -1.0f, 0f),    // 0: top point
                new(-0.9f, 0.6f, 0f),  // 1: left jaw
                new(-0.25f, 0.3f, 0f), // 2: left inner notch
                new(0f, 0.7f, 0f),     // 3: mouth center (bottom of V)
                new(0.25f, 0.3f, 0f),  // 4: right inner notch
                new(0.9f, 0.6f, 0f),   // 5: right jaw
            },
            new int[]
            {
                0, 1, 2,  // Left side
                0, 2, 4,  // Center bridge
                0, 4, 5,  // Right side
                2, 1, 3,  // Left fang
                4, 3, 5,  // Right fang
            });
    }

    // ================================================================
    // TERRAIN RENDERING (unchanged)
    // ================================================================

    /// <summary>
    /// Draws visible terrain chunks using cached textures.
    /// Must be called from within a CanvasItem._Draw() override.
    /// </summary>
    public void DrawTerrain(CanvasItem canvas, Camera2D camera)
    {
        var viewportSize = canvas.GetViewportRect().Size;
        var cameraPos = camera.Position;
        var zoom = camera.Zoom;

        float halfWidth = viewportSize.X / (2 * zoom.X);
        float halfHeight = viewportSize.Y / (2 * zoom.Y);

        float minWorldX = (cameraPos.X - halfWidth) / _tileSize;
        float maxWorldX = (cameraPos.X + halfWidth) / _tileSize;
        float minWorldY = (cameraPos.Y - halfHeight) / _tileSize;
        float maxWorldY = (cameraPos.Y + halfHeight) / _tileSize;

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

        bool needsRebuild = !_chunkTextures.ContainsKey(key) ||
                            _worldManager.DirtyChunks.Contains(key) ||
                            _dirtyChunks.Contains(key);

        if (needsRebuild)
        {
            _chunkTextures[key] = RenderChunkTexture(chunk);
            _worldManager.DirtyChunks.Remove(key);
            _dirtyChunks.Remove(key);
        }

        float chunkWorldX = chunk.ChunkX * _chunkSize * _tileSize;
        float chunkWorldY = chunk.ChunkY * _chunkSize * _tileSize;
        float chunkPixelSize = _chunkSize * _tileSize;

        canvas.DrawTextureRect(_chunkTextures[key],
            new Rect2(chunkWorldX, chunkWorldY, chunkPixelSize, chunkPixelSize),
            false);
    }

    private ImageTexture RenderChunkTexture(Chunk chunk)
    {
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

        return ImageTexture.CreateFromImage(image);
    }

    private static Color VaryTilePixel(Color baseColor, TileType tile, int wx, int wy, int dx, int dy)
    {
        uint hash = PixelHash(wx, wy, dx, dy);
        float rand = (hash & 0xFFFF) / 65535f;
        float rand2 = ((hash >> 16) & 0xFFFF) / 65535f;

        float r = baseColor.R;
        float g = baseColor.G;
        float b = baseColor.B;

        switch (tile)
        {
            case TileType.Forest:
            case TileType.Taiga:
            case TileType.Jungle:
            {
                float variation = (rand - 0.5f) * 0.12f;
                r += variation; g += variation; b += variation;
                if (rand2 < 0.18f) { r -= 0.06f; g -= 0.04f; b -= 0.05f; }
                break;
            }
            case TileType.Grass:
            case TileType.Steppe:
            case TileType.Savanna:
            case TileType.Shrubland:
            {
                float brightness = (rand - 0.5f) * 0.08f;
                float hueShift = (rand2 - 0.5f) * 0.03f;
                r += brightness + hueShift; g += brightness; b += brightness - hueShift;
                break;
            }
            case TileType.Sand:
            case TileType.Dirt:
            case TileType.Arid:
            {
                float variation = (rand - 0.5f) * 0.1f;
                r += variation; g += variation; b += variation;
                if (rand2 < 0.10f) { r += 0.06f; g += 0.05f; b += 0.03f; }
                break;
            }
            case TileType.DeepWater:
            case TileType.ShallowWater:
            case TileType.River:
            case TileType.Reef:
            {
                float variation = (rand - 0.5f) * 0.05f;
                r += variation * 0.5f; g += variation * 0.7f; b += variation;
                break;
            }
            case TileType.Wetland:
            case TileType.Bog:
            {
                float variation = (rand - 0.5f) * 0.1f;
                r += variation; g += variation; b += variation;
                if (rand2 < 0.12f) { r -= 0.04f; g -= 0.02f; b -= 0.03f; }
                break;
            }
            case TileType.Mountain:
            case TileType.Tundra:
            case TileType.Ice:
            {
                float variation = (rand - 0.5f) * 0.08f;
                r += variation; g += variation; b += variation;
                break;
            }
            case TileType.Lava:
            {
                float variation = (rand - 0.5f) * 0.15f;
                r += variation; g += variation * 0.5f;
                break;
            }
            default:
            {
                float variation = (rand - 0.5f) * 0.06f;
                r += variation; g += variation; b += variation;
                break;
            }
        }

        return new Color(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
    }

    private static uint PixelHash(int wx, int wy, int dx, int dy)
    {
        uint h = (uint)(wx * 374761393 + wy * 668265263 + dx * 2147483647 + dy * 1013904223);
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return h;
    }

    // ================================================================
    // ENTITY RENDERING
    // ================================================================

    /// <summary>
    /// Updates entity MultiMesh buffers for rendering. Called each frame from _Process.
    /// Creatures face their direction of movement. Sectid stars oscillate.
    /// </summary>
    public void UpdateEntityMultiMeshes(Camera2D camera)
    {
        _frameTick++;

        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;

        // Compute visible world bounds for frustum culling
        var viewportSize = _shapeMMIs[0].GetViewportRect().Size;
        var cameraPos = camera.Position;
        var zoom = camera.Zoom;
        float halfW = viewportSize.X / (2 * zoom.X);
        float halfH = viewportSize.Y / (2 * zoom.Y);
        float cullMinX = cameraPos.X - halfW;
        float cullMaxX = cameraPos.X + halfW;
        float cullMinY = cameraPos.Y - halfH;
        float cullMaxY = cameraPos.Y + halfH;

        // Pre-grow capacity if needed
        int totalEntities = _entityManager.EntityCount;
        for (int s = 0; s < ShapeCount; s++)
        {
            if (_shapeMMIs[s].Multimesh.InstanceCount < totalEntities)
                _shapeMMIs[s].Multimesh.InstanceCount = totalEntities;
            _shapeIndices[s] = 0;
        }

        foreach (int entity in _entityManager.Query(required))
        {
            ref var pos = ref _entityManager.Positions[entity];
            ref var rend = ref _entityManager.Renderables[entity];

            float screenX = pos.X * _tileSize;
            float screenY = pos.Y * _tileSize;

            // Frustum cull
            float margin = rend.Size * 2f;
            if (screenX < cullMinX - margin || screenX > cullMaxX + margin ||
                screenY < cullMinY - margin || screenY > cullMaxY + margin)
                continue;

            // Compute rotation
            float rotation = 0f;
            int shapeIdx = (int)rend.Shape;

            if (rend.Shape == ShapeType.Star)
            {
                // Sectid star: oscillating rotation (walking animation)
                // Use entity ID for phase offset so they don't all sync
                float phase = entity * 1.7f;
                rotation = MathF.Sin(_frameTick * 0.15f + phase) * 0.35f;
            }
            else if (rend.Shape != ShapeType.Circle &&
                     _entityManager.HasComponents(entity, ComponentFlags.Velocity))
            {
                // Non-circle shapes face direction of movement
                ref var vel = ref _entityManager.Velocities[entity];
                float speedSq = vel.Dx * vel.Dx + vel.Dy * vel.Dy;
                if (speedSq > 0.0004f) // Only rotate if actually moving
                {
                    // atan2 gives angle from positive X axis; our shapes point "up" (-Y)
                    // so subtract PI/2 to align shape's "forward" with velocity direction
                    rotation = MathF.Atan2(vel.Dy, vel.Dx) + MathF.PI * 0.5f;
                }
            }

            var transform = new Transform2D(rotation, new Vector2(rend.Size, rend.Size), 0f,
                new Vector2(screenX, screenY));

            var mm = _shapeMMIs[shapeIdx].Multimesh;
            int idx = _shapeIndices[shapeIdx]++;
            mm.SetInstanceTransform2D(idx, transform);
            mm.SetInstanceColor(idx, rend.Color);
        }

        // Set visible counts
        for (int s = 0; s < ShapeCount; s++)
        {
            _shapeMMIs[s].Multimesh.VisibleInstanceCount = _shapeIndices[s];
        }
    }
}
