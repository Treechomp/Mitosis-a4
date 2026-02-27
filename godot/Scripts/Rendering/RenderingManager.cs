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
/// Handles all visual rendering: terrain chunk meshes (MeshInstance3D) and entity
/// MultiMesh batching (MultiMeshInstance3D). Terrain uses smooth per-vertex normals
/// for DirectionalLight shading; entities are rendered unshaded with instance colors.
/// </summary>
public sealed class RenderingManager
{
    private readonly EntityManager _entityManager;
    private readonly WorldManager _worldManager;
    private readonly int _chunkSize;
    private readonly int _worldSizeChunks;
    private readonly int _tileSize;

    // Chunk mesh nodes — one MeshInstance3D per loaded chunk
    private readonly Dictionary<(int, int), MeshInstance3D> _chunkMeshes = new();
    private ShaderMaterial _chunkMaterial = null!;
    private readonly float _heightScale;

    // MultiMesh entity rendering — one per ShapeType
    private const int ShapeCount = 13; // ShapeType values 0..12
    private const int MultiMeshInitialCapacity = 4096;
    private MultiMeshInstance3D[] _shapeMMIs = null!;
    private int[] _shapeIndices = null!;

    // Rotation basis applied to all entity meshes to lay them flat in the XZ plane.
    // Entity shapes are built in the XY plane (Z=0); rotating +90° around X maps Y→Z.
    private static readonly Basis FlattenBasis = new Basis(Vector3.Right, MathF.PI / 2f);

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
    /// Creates MultiMeshInstance3D nodes for all shape types.
    /// Returns them — the caller adds them as children to the scene tree.
    /// </summary>
    public MultiMeshInstance3D[] CreateMultiMeshInstances()
    {
        _shapeMMIs = new MultiMeshInstance3D[ShapeCount];
        _shapeIndices = new int[ShapeCount];

        // Unshaded material for entities (flat vertex colors, no lighting).
        var entityMat = new StandardMaterial3D();
        entityMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        entityMat.VertexColorUseAsAlbedo = true;

        _shapeMMIs[(int)ShapeType.Circle]    = CreateMultiMeshInstance(CreateCircleMesh(16),    entityMat);
        _shapeMMIs[(int)ShapeType.Triangle]  = CreateMultiMeshInstance(CreateTriangleMesh(),    entityMat);
        _shapeMMIs[(int)ShapeType.Square]    = CreateMultiMeshInstance(CreateSquareMesh(),      entityMat);
        _shapeMMIs[(int)ShapeType.Diamond]   = CreateMultiMeshInstance(CreateDiamondMesh(),     entityMat);
        _shapeMMIs[(int)ShapeType.Star]      = CreateMultiMeshInstance(CreateStarMesh(6, 1.0f, 0.5f), entityMat);
        _shapeMMIs[(int)ShapeType.Chevron]   = CreateMultiMeshInstance(CreateChevronMesh(),     entityMat);
        _shapeMMIs[(int)ShapeType.FishShape] = CreateMultiMeshInstance(CreateFishMesh(),        entityMat);
        _shapeMMIs[(int)ShapeType.Fin]       = CreateMultiMeshInstance(CreateFinMesh(),         entityMat);
        _shapeMMIs[(int)ShapeType.Teardrop]  = CreateMultiMeshInstance(CreateTeardropMesh(),    entityMat);
        _shapeMMIs[(int)ShapeType.Crescent]  = CreateMultiMeshInstance(CreateCrescentMesh(),    entityMat);
        _shapeMMIs[(int)ShapeType.Serpent]   = CreateMultiMeshInstance(CreateSerpentMesh(),     entityMat);
        _shapeMMIs[(int)ShapeType.Mushroom]  = CreateMultiMeshInstance(CreateMushroomMesh(),    entityMat);
        _shapeMMIs[(int)ShapeType.Fangs]     = CreateMultiMeshInstance(CreateFangsMesh(),       entityMat);

        return _shapeMMIs;
    }

    private static MultiMeshInstance3D CreateMultiMeshInstance(Mesh mesh, StandardMaterial3D mat)
    {
        var mm = new MultiMesh();
        mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        mm.UseColors = true;
        mm.Mesh = mesh;
        mm.InstanceCount = MultiMeshInitialCapacity;
        mm.VisibleInstanceCount = 0;

        var mmi = new MultiMeshInstance3D { Multimesh = mm };
        mmi.MaterialOverride = mat;
        return mmi;
    }

    // ================================================================
    // MESH GENERATORS
    // ================================================================

    /// <summary>Helper: build an ArrayMesh from vertex + index arrays (no normals — entity shapes).</summary>
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

    // --- Circle ---
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

    // --- Triangle (pointing up) ---
    private static ArrayMesh CreateTriangleMesh()
    {
        return BuildMesh(
            new Vector3[] { new(0f, -1f, 0f), new(-0.866f, 0.5f, 0f), new(0.866f, 0.5f, 0f) },
            new int[] { 0, 1, 2 });
    }

    // --- Square ---
    private static ArrayMesh CreateSquareMesh()
    {
        return BuildMesh(
            new Vector3[] { new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(0.5f, 0.5f, 0f), new(-0.5f, 0.5f, 0f) },
            new int[] { 0, 1, 2, 0, 2, 3 });
    }

    // --- Diamond ---
    private static ArrayMesh CreateDiamondMesh()
    {
        return BuildMesh(
            new Vector3[] { new(0f, -1.1f, 0f), new(0.7f, 0f, 0f), new(0f, 0.9f, 0f), new(-0.7f, 0f, 0f) },
            new int[] { 0, 1, 2, 0, 2, 3 });
    }

    // --- 6-pointed Star ---
    private static ArrayMesh CreateStarMesh(int points, float outerR, float innerR)
    {
        int totalVerts = points * 2;
        var vertices = new Vector3[totalVerts + 1];
        var indices = new int[totalVerts * 3];

        vertices[0] = Vector3.Zero;
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

    // --- Chevron ---
    private static ArrayMesh CreateChevronMesh()
    {
        return BuildMesh(
            new Vector3[]
            {
                new(0f, -0.3f, 0f), new(-1f, 0.3f, 0f), new(-0.8f, 0.7f, 0f), new(0f, 0.15f, 0f),
                new(1f, 0.3f, 0f),  new(0.8f, 0.7f, 0f),
            },
            new int[] { 0, 1, 2,  0, 2, 3,  0, 3, 5,  0, 5, 4 });
    }

    // --- Fish ---
    private static ArrayMesh CreateFishMesh()
    {
        return BuildMesh(
            new Vector3[]
            {
                new(-0.8f, 0f, 0f), new(0f, -0.5f, 0f), new(0.4f, 0f, 0f),
                new(0f, 0.5f, 0f),  new(0.9f, -0.5f, 0f), new(0.9f, 0.5f, 0f),
            },
            new int[] { 0, 1, 2,  0, 2, 3,  2, 4, 5 });
    }

    // --- Fin ---
    private static ArrayMesh CreateFinMesh()
    {
        return BuildMesh(
            new Vector3[]
            {
                new(0f, -1.1f, 0f), new(-0.5f, 0.5f, 0f), new(0.7f, 0.5f, 0f), new(0.3f, 0.1f, 0f),
            },
            new int[] { 0, 1, 3, 0, 3, 2 });
    }

    // --- Teardrop ---
    private static ArrayMesh CreateTeardropMesh()
    {
        const int segments = 8;
        var vertices = new Vector3[segments + 2];
        var idxList = new List<int>();

        vertices[0] = new Vector3(0f, 0.1f, 0f);
        for (int i = 0; i <= segments; i++)
        {
            float angle = MathF.PI * i / segments;
            vertices[i + 1] = new Vector3(MathF.Cos(angle) * 0.8f, MathF.Sin(angle) * 0.6f + 0.1f, 0f);
        }
        for (int i = 0; i < segments; i++) { idxList.Add(0); idxList.Add(i + 1); idxList.Add(i + 2); }
        int tipIdx = vertices.Length - 1;
        vertices[tipIdx] = new Vector3(0f, -0.9f, 0f);
        idxList.Add(tipIdx); idxList.Add(segments + 1); idxList.Add(1);
        return BuildMesh(vertices, idxList.ToArray());
    }

    // --- Crescent ---
    private static ArrayMesh CreateCrescentMesh()
    {
        const int segments = 8;
        float outerR = 1.0f, innerR = 0.6f, arcSpan = MathF.PI * 0.8f;
        var vertices = new Vector3[(segments + 1) * 2];
        var idxList = new List<int>();

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = -arcSpan / 2f + t * arcSpan - MathF.PI / 2f;
            vertices[i] = new Vector3(MathF.Cos(angle) * outerR, MathF.Sin(angle) * outerR, 0f);
            vertices[segments + 1 + i] = new Vector3(MathF.Cos(angle) * innerR, MathF.Sin(angle) * innerR, 0f);
        }
        for (int i = 0; i < segments; i++)
        {
            int o0 = i, o1 = i + 1, i0 = segments + 1 + i, i1 = segments + 2 + i;
            idxList.Add(o0); idxList.Add(o1); idxList.Add(i1);
            idxList.Add(o0); idxList.Add(i1); idxList.Add(i0);
        }
        return BuildMesh(vertices, idxList.ToArray());
    }

    // --- Serpent ---
    private static ArrayMesh CreateSerpentMesh()
    {
        const float w = 0.25f;
        return BuildMesh(
            new Vector3[]
            {
                new(-0.3f - w, -0.9f, 0f), new(-0.3f + w, -0.9f, 0f),
                new(0.2f + w, -0.2f, 0f),  new(0.2f - w, -0.2f, 0f),
                new(-0.2f + w, 0.3f, 0f),  new(-0.2f - w, 0.3f, 0f),
                new(0.3f + w, 0.9f, 0f),   new(0.3f - w, 0.9f, 0f),
            },
            new int[] { 0, 1, 2, 0, 2, 3,  3, 2, 4, 3, 4, 5,  5, 4, 6, 5, 6, 7 });
    }

    // --- Mushroom ---
    private static ArrayMesh CreateMushroomMesh()
    {
        const int capSegments = 8;
        var vertList = new List<Vector3>();
        var idxList = new List<int>();

        int capCenter = 0;
        vertList.Add(new Vector3(0f, -0.3f, 0f));
        for (int i = 0; i <= capSegments; i++)
        {
            float angle = MathF.PI * i / capSegments;
            vertList.Add(new Vector3(MathF.Cos(angle) * 0.9f, -MathF.Sin(angle) * 0.7f - 0.1f, 0f));
        }
        for (int i = 0; i < capSegments; i++) { idxList.Add(capCenter); idxList.Add(i + 1); idxList.Add(i + 2); }

        int stemStart = vertList.Count;
        vertList.Add(new Vector3(-0.2f, -0.2f, 0f)); vertList.Add(new Vector3(0.2f, -0.2f, 0f));
        vertList.Add(new Vector3(0.2f, 0.8f, 0f));   vertList.Add(new Vector3(-0.2f, 0.8f, 0f));
        idxList.Add(stemStart); idxList.Add(stemStart + 1); idxList.Add(stemStart + 2);
        idxList.Add(stemStart); idxList.Add(stemStart + 2); idxList.Add(stemStart + 3);

        return BuildMesh(vertList.ToArray(), idxList.ToArray());
    }

    // --- Fangs ---
    private static ArrayMesh CreateFangsMesh()
    {
        return BuildMesh(
            new Vector3[]
            {
                new(0f, -1.0f, 0f),    new(-0.9f, 0.6f, 0f),  new(-0.25f, 0.3f, 0f),
                new(0f, 0.7f, 0f),     new(0.25f, 0.3f, 0f),  new(0.9f, 0.6f, 0f),
            },
            new int[] { 0, 1, 2,  0, 2, 4,  0, 4, 5,  2, 1, 3,  4, 3, 5 });
    }

    // ================================================================
    // TERRAIN RENDERING — 3D triangle mesh per chunk
    // ================================================================

    /// <summary>
    /// Creates MeshInstance3D nodes for all loaded chunks and adds them as children
    /// of the given parent node. Terrain renders in the XZ plane with elevation as Y.
    /// </summary>
    public void InitializeChunkMeshes(Node parent)
    {
        // Custom dither shader: flat tile-type ID (UV.x) + interpolated vertex colour
        // lets the fragment shader identify the exact primary biome and find the correct
        // secondary via palette search, then apply a Bayer ordered-dither at boundaries.
        var shader = GD.Load<Shader>("res://Shaders/TerrainDither.gdshader");
        _chunkMaterial = new ShaderMaterial { Shader = shader };

        // Build palette matching Chunk.GetTileColor() order (index = TileType enum value).
        var palette = new Color[20];
        for (int i = 0; i < 20; i++)
            palette[i] = Chunk.GetTileColor((TileType)i);
        _chunkMaterial.SetShaderParameter("palette", palette);

        foreach (var chunk in _worldManager.GetLoadedChunks())
        {
            var mmi = new MeshInstance3D { Mesh = BuildChunkMesh(chunk) };
            parent.AddChild(mmi);
            _chunkMeshes[(chunk.ChunkX, chunk.ChunkY)] = mmi;
        }
    }

    /// <summary>
    /// Rebuilds the mesh for any chunk flagged dirty since the last call.
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
    /// Builds a 3D triangle mesh for one chunk.
    /// Vertices are placed in world XZ (terrain XY) with elevation on world Y (+Y = up).
    /// Smooth per-vertex normals are computed for DirectionalLight shading.
    /// </summary>
    private ArrayMesh BuildChunkMesh(Chunk chunk)
    {
        int n = chunk.Size + 1;  // vertices per side: 33 for a 32-tile chunk
        int vertexCount = n * n;
        int quadCount = chunk.Size * chunk.Size;

        var vertices = new Vector3[vertexCount];
        var colors   = new Color[vertexCount];
        var uvs      = new Vector2[vertexCount]; // UV.x = TileType index for flat biome ID
        var normals  = new Vector3[vertexCount];
        var indices  = new int[quadCount * 6];  // 2 triangles × 3 indices per quad

        int worldOffsetX = chunk.ChunkX * chunk.Size;
        int worldOffsetY = chunk.ChunkY * chunk.Size;

        // --- Pass 1: vertex positions and colors ---
        for (int ly = 0; ly < n; ly++)
        {
            for (int lx = 0; lx < n; lx++)
            {
                int worldX = worldOffsetX + lx;
                int worldY = worldOffsetY + ly;

                bool interior = lx < chunk.Size && ly < chunk.Size;
                TileType tile = interior ? chunk.GetTile(lx, ly) : _worldManager.GetTile(worldX, worldY);
                float elevation = interior ? chunk.GetElevation(lx, ly) : _worldManager.GetElevation(worldX, worldY);

                vertices[ly * n + lx] = GridCoordinates.VertexToWorld3D(worldX, worldY, _tileSize, elevation, _heightScale);
                colors[ly * n + lx]   = Chunk.GetTileColor(tile);
                uvs[ly * n + lx]      = new Vector2((float)(byte)tile, 0f);
            }
        }

        // --- Pass 2: triangle indices ---
        // Diagonal alternates each row to match the offset-row stagger.
        int idx = 0;
        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                int vBL = ly * n + lx;
                int vBR = ly * n + lx + 1;
                int vTL = (ly + 1) * n + lx;
                int vTR = (ly + 1) * n + lx + 1;

                // With grid Y mapped to world -Z, the original BL→BR→TL winding becomes
                // CW in screen space and is back-face culled.  Swap v1↔v2 within each
                // triangle to restore CCW screen-space winding (visible to camera).
                if ((worldOffsetY + ly) % 2 == 0)
                {
                    indices[idx++] = vBL; indices[idx++] = vTL; indices[idx++] = vBR;
                    indices[idx++] = vBR; indices[idx++] = vTL; indices[idx++] = vTR;
                }
                else
                {
                    indices[idx++] = vBL; indices[idx++] = vTR; indices[idx++] = vBR;
                    indices[idx++] = vBL; indices[idx++] = vTL; indices[idx++] = vTR;
                }
            }
        }

        // --- Pass 3: smooth per-vertex normals (area-weighted average of face normals) ---
        for (int i = 0; i < indices.Length; i += 3)
        {
            var v0 = vertices[indices[i]];
            var v1 = vertices[indices[i + 1]];
            var v2 = vertices[indices[i + 2]];
            // Face normal (not normalized — area-weighted by default)
            var faceN = (v1 - v0).Cross(v2 - v0);
            normals[indices[i]]     += faceN;
            normals[indices[i + 1]] += faceN;
            normals[indices[i + 2]] += faceN;
        }
        // The reversed CCW winding (required after the Z-axis flip) makes cross products
        // point in -Y; negate to restore +Y face normals so DirectionalLight from above
        // illuminates the terrain surface correctly.
        for (int i = 0; i < normals.Length; i++)
            normals[i] = (-normals[i]).Normalized();

        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color]  = colors;
        arrays[(int)Mesh.ArrayType.TexUV]  = uvs;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index]  = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, _chunkMaterial);
        return mesh;
    }

    // ================================================================
    // ENTITY RENDERING
    // ================================================================

    /// <summary>
    /// Updates entity MultiMesh buffers for rendering in 3D.
    /// Entities are laid flat in the XZ plane (like terrain tokens) at their
    /// terrain elevation. Non-circle shapes face their direction of movement.
    /// </summary>
    public void UpdateEntityMultiMeshes(Camera3D camera)
    {
        _frameTick++;

        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;

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
            ref var pos  = ref _entityManager.Positions[entity];
            ref var rend = ref _entityManager.Renderables[entity];

            // 3D world position: XZ from abstract grid, Y from terrain elevation
            float elevation = _worldManager.GetElevation(pos.X, pos.Y);
            var pos3D = GridCoordinates.VertexToWorld3D(pos.X, pos.Y, _tileSize, elevation, _heightScale);

            // Facing rotation around world Y axis
            float rotation = 0f;

            if (rend.Shape == ShapeType.Star)
            {
                float phase = entity * 1.7f;
                rotation = MathF.Sin(_frameTick * 0.15f + phase) * 0.35f;
            }
            else if (rend.Shape != ShapeType.Circle &&
                     _entityManager.HasComponents(entity, ComponentFlags.Velocity))
            {
                ref var vel = ref _entityManager.Velocities[entity];
                float speedSq = vel.Dx * vel.Dx + vel.Dy * vel.Dy;
                if (speedSq > 0.0004f)
                {
                    // Y-axis rotation so entity faces its movement direction in XZ.
                    // vel.Dx maps to world X, vel.Dy maps to world Z.
                    rotation = MathF.Atan2(vel.Dx, -vel.Dy);
                }
            }

            // Build transform: flatten XY mesh to XZ plane, then rotate for facing, then scale.
            var yawBasis = new Basis(Vector3.Up, rotation);
            var basis = (yawBasis * FlattenBasis).Scaled(new Vector3(rend.Size, rend.Size, rend.Size));
            var transform = new Transform3D(basis, pos3D);

            int shapeIdx = (int)rend.Shape;
            var mm = _shapeMMIs[shapeIdx].Multimesh;
            int i = _shapeIndices[shapeIdx]++;
            mm.SetInstanceTransform(i, transform);
            mm.SetInstanceColor(i, rend.Color);
        }

        // Set visible counts
        for (int s = 0; s < ShapeCount; s++)
            _shapeMMIs[s].Multimesh.VisibleInstanceCount = _shapeIndices[s];
    }
}
