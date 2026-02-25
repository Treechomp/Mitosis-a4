using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
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

    // Chunk mesh nodes — one MeshInstance2D per loaded chunk
    private readonly Dictionary<(int, int), MeshInstance2D> _chunkMeshes = new();
    private StandardMaterial3D _chunkMaterial = null!;
    private readonly float _heightScale;

    // MultiMesh entity rendering — one per ShapeType
    private const int ShapeCount = 13; // ShapeType values 0..12
    private const int MultiMeshInitialCapacity = 4096;
    private MultiMeshInstance2D[] _shapeMMIs = null!;
    private int[] _shapeIndices = null!;

    // Animation state
    private int _frameTick;

    public RenderingManager(EntityManager entityManager, WorldManager worldManager,
                            int chunkSize, int worldSizeChunks, int tileSize, float heightScale = 0f)
    {
        _entityManager = entityManager;
        _worldManager = worldManager;
        _chunkSize = chunkSize;
        _worldSizeChunks = worldSizeChunks;
        _tileSize = tileSize;
        _heightScale = heightScale;
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
    // TERRAIN RENDERING — triangle mesh per chunk
    // ================================================================

    /// <summary>
    /// Creates MeshInstance2D nodes for all loaded chunks and adds them as children
    /// of the given parent node. Must be called after world generation and before
    /// entity MultiMesh nodes are added so terrain renders behind entities.
    /// </summary>
    public void InitializeChunkMeshes(Node parent)
    {
        // Unshaded material that uses per-vertex colors directly as albedo.
        _chunkMaterial = new StandardMaterial3D();
        _chunkMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _chunkMaterial.VertexColorUseAsAlbedo = true;

        foreach (var chunk in _worldManager.GetLoadedChunks())
        {
            var mmi = new MeshInstance2D { Mesh = BuildChunkMesh(chunk) };
            parent.AddChild(mmi);
            _chunkMeshes[(chunk.ChunkX, chunk.ChunkY)] = mmi;
        }
    }

    /// <summary>
    /// Rebuilds the mesh for any chunk flagged dirty since the last call.
    /// Should be called once per frame from _Process.
    /// </summary>
    public void UpdateDirtyChunkMeshes()
    {
        if (_worldManager.DirtyChunks.Count == 0) return;

        foreach (var key in _worldManager.DirtyChunks)
        {
            if (_chunkMeshes.TryGetValue(key, out var mmi))
            {
                var chunk = _worldManager.GetChunk(key.Item1, key.Item2);
                if (chunk != null)
                    mmi.Mesh = BuildChunkMesh(chunk);
            }
        }
        _worldManager.DirtyChunks.Clear();
    }

    /// <summary>
    /// Builds a triangle-mesh for one chunk using offset-row vertex positions and
    /// per-vertex biome colors. Each quad is split into two triangles; the diagonal
    /// alternates direction between even and odd rows to match the row offset.
    ///
    /// Vertex grid is (Size+1) × (Size+1) so the mesh seamlessly abuts adjacent
    /// chunk meshes — boundary vertices are sampled from WorldManager.
    /// </summary>
    private ArrayMesh BuildChunkMesh(Chunk chunk)
    {
        int n = chunk.Size + 1;  // vertices per side: 33 for the standard 32-tile chunk
        int vertexCount = n * n;
        int quadCount = chunk.Size * chunk.Size;

        var vertices = new Vector3[vertexCount];
        var colors = new Color[vertexCount];
        var indices = new int[quadCount * 6];  // 2 triangles × 3 indices per quad

        int worldOffsetX = chunk.ChunkX * chunk.Size;
        int worldOffsetY = chunk.ChunkY * chunk.Size;

        // --- Build vertex positions and colors ---
        for (int ly = 0; ly < n; ly++)
        {
            for (int lx = 0; lx < n; lx++)
            {
                int worldX = worldOffsetX + lx;
                int worldY = worldOffsetY + ly;

                // Fetch tile type and elevation: own array for interior, WorldManager for boundary
                bool interior = lx < chunk.Size && ly < chunk.Size;
                TileType tile = interior
                    ? chunk.GetTile(lx, ly)
                    : _worldManager.GetTile(worldX, worldY);
                float elevation = interior
                    ? chunk.GetElevation(lx, ly)
                    : _worldManager.GetElevation(worldX, worldY);

                var screen = GridCoordinates.VertexToScreen(worldX, worldY, _tileSize, elevation, _heightScale);

                vertices[ly * n + lx] = new Vector3(screen.X, screen.Y, 0f);
                colors[ly * n + lx] = Chunk.GetTileColor(tile);
            }
        }

        // --- Build triangle indices ---
        // For each quad, the split diagonal alternates to match the row offset:
        //   Even bottom row:  BL-BR-TL  +  BR-TR-TL
        //   Odd  bottom row:  BL-BR-TR  +  BL-TR-TL
        int idx = 0;
        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                int vBL = ly * n + lx;
                int vBR = ly * n + lx + 1;
                int vTL = (ly + 1) * n + lx;
                int vTR = (ly + 1) * n + lx + 1;

                if ((worldOffsetY + ly) % 2 == 0)
                {
                    indices[idx++] = vBL; indices[idx++] = vBR; indices[idx++] = vTL;
                    indices[idx++] = vBR; indices[idx++] = vTR; indices[idx++] = vTL;
                }
                else
                {
                    indices[idx++] = vBL; indices[idx++] = vBR; indices[idx++] = vTR;
                    indices[idx++] = vBL; indices[idx++] = vTR; indices[idx++] = vTL;
                }
            }
        }

        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, _chunkMaterial);
        return mesh;
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

            var screen = GridCoordinates.VertexToScreen(pos.X, pos.Y, _tileSize);
            float screenX = screen.X;
            float screenY = screen.Y;

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
