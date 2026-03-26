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
/// for DirectionalLight shading; entities use Godot 3D primitives (sphere, prism, box,
/// capsule, cylinder, torus) as placeholder meshes, lit by the same DirectionalLight.
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
    /// Each ShapeType maps to a Godot primitive mesh standing upright on the terrain.
    /// </summary>
    public MultiMeshInstance3D[] CreateMultiMeshInstances()
    {
        _shapeMMIs   = new MultiMeshInstance3D[ShapeCount];
        _shapeIndices = new int[ShapeCount];

        // Lit material: instance color tints the albedo so DirectionalLight
        // adds shading and depth to each 3D primitive.
        var entityMat = new StandardMaterial3D();
        entityMat.VertexColorUseAsAlbedo = true;
        entityMat.Roughness = 0.8f;

        // Circle  — herbivores, player: sphere
        _shapeMMIs[(int)ShapeType.Circle]   = CreateMMI(
            new SphereMesh { Radius = 0.5f, Height = 1.0f }, entityMat);

        // Triangle — carnivores (wolf, fox): triangular prism
        _shapeMMIs[(int)ShapeType.Triangle] = CreateMMI(
            new PrismMesh(), entityMat);

        // Square — Faelings (magic entities): cube
        _shapeMMIs[(int)ShapeType.Square]   = CreateMMI(
            new BoxMesh(), entityMat);

        // Diamond — armored (boar, musk ox, turtle): wide flat box
        _shapeMMIs[(int)ShapeType.Diamond]  = CreateMMI(
            new BoxMesh { Size = new Vector3(1.4f, 0.5f, 1.0f) }, entityMat);

        // Star — Sectids (colony insects): 6-sided cylinder
        _shapeMMIs[(int)ShapeType.Star]     = CreateMMI(
            new CylinderMesh { RadialSegments = 6, TopRadius = 0.4f, BottomRadius = 0.5f, Height = 1.0f }, entityMat);

        // Chevron — birds (hawk, parrot): thin wide prism (wing wedge)
        _shapeMMIs[(int)ShapeType.Chevron]  = CreateMMI(
            new PrismMesh { Size = new Vector3(1.6f, 0.4f, 0.8f) }, entityMat);

        // FishShape — fish: short wide capsule
        _shapeMMIs[(int)ShapeType.FishShape] = CreateMMI(
            new CapsuleMesh { Radius = 0.35f, Height = 1.1f }, entityMat);

        // Fin — shark: tall narrow prism
        _shapeMMIs[(int)ShapeType.Fin]      = CreateMMI(
            new PrismMesh { Size = new Vector3(0.6f, 1.4f, 0.5f) }, entityMat);

        // Teardrop — penguin, rabbit, tapir: standard capsule
        _shapeMMIs[(int)ShapeType.Teardrop] = CreateMMI(
            new CapsuleMesh { Radius = 0.4f, Height = 1.2f }, entityMat);

        // Crescent — scorpion: torus
        _shapeMMIs[(int)ShapeType.Crescent] = CreateMMI(
            new TorusMesh { InnerRadius = 0.3f, OuterRadius = 0.6f }, entityMat);

        // Serpent — snake: elongated thin capsule
        _shapeMMIs[(int)ShapeType.Serpent]  = CreateMMI(
            new CapsuleMesh { Radius = 0.18f, Height = 1.8f }, entityMat);

        // Mushroom — shroomer: flattened sphere (cap placeholder)
        _shapeMMIs[(int)ShapeType.Mushroom] = CreateMMI(
            new SphereMesh { Radius = 0.6f, Height = 0.7f, Rings = 4 }, entityMat);

        // Fangs — bear, jaguar, croc: broad squat box
        _shapeMMIs[(int)ShapeType.Fangs]    = CreateMMI(
            new BoxMesh { Size = new Vector3(1.5f, 0.7f, 1.0f) }, entityMat);

        return _shapeMMIs;
    }

    private static MultiMeshInstance3D CreateMMI(Mesh mesh, StandardMaterial3D mat)
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
    // TERRAIN RENDERING — 3D triangle mesh per chunk
    // ================================================================

    /// <summary>
    /// Creates MeshInstance3D nodes for all loaded chunks and adds them as children
    /// of the given parent node. Terrain renders in the XZ plane with elevation as Y.
    /// </summary>
    public void InitializeChunkMeshes(Node parent)
    {
        // Vertex-colour gradient shader with manual Lambert lighting for strong
        // shadow contrast. Uses render_mode unshaded so terrain controls its own
        // sun shadows independently of the scene DirectionalLight (which still
        // lights entities).
        var shader = GD.Load<Shader>("res://Shaders/TerrainDither.gdshader");
        _chunkMaterial = new ShaderMaterial { Shader = shader };

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
    /// Each entity is placed at its terrain elevation + a Y-offset so the primitive
    /// mesh sits on the surface rather than clipping through it. Non-circle shapes
    /// rotate around the world Y axis to face their direction of movement.
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

            // 3D world position: XZ from abstract grid, Y from terrain elevation.
            float elevation = _worldManager.GetElevation(pos.X, pos.Y);
            var pos3D = GridCoordinates.VertexToWorld3D(pos.X, pos.Y, _tileSize, elevation, _heightScale);

            // Lift entity so the bottom of its mesh sits on the terrain surface.
            // All primitives are unit-sized and centered at origin, so half-height ≈ 0.5.
            // Torus (Crescent) is flat in XZ — use its tube radius (0.3) as the offset.
            pos3D.Y += rend.Size * (rend.Shape == ShapeType.Crescent ? 0.3f : 0.5f);

            // Facing rotation around world Y axis
            float rotation = 0f;

            if (rend.Shape == ShapeType.Star)
            {
                // Sectids oscillate slowly
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
                    rotation = MathF.Atan2(vel.Dx, -vel.Dy);
                }
            }

            // Diamond (armored) shows a corner-on profile — rotate 45° extra.
            if (rend.Shape == ShapeType.Diamond)
                rotation += MathF.PI / 4f;

            // Build transform: Y-axis rotation for facing + uniform scale.
            // No FlattenBasis — 3D primitives are already Y-up upright.
            var basis = new Basis(Vector3.Up, rotation).Scaled(new Vector3(rend.Size, rend.Size, rend.Size));
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
