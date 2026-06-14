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

    // Chunk mesh nodes — one per loaded chunk. Geometry (positions/normals/indices)
    // is cached so terraform updates (which change only tile colour) can skip recomputing
    // it and rewrite the colour stream alone.
    private sealed class ChunkMeshData
    {
        public MeshInstance3D Instance = null!;
        public Vector3[] Vertices = null!;
        public Vector3[] Normals = null!;
        public int[] Indices = null!;
    }
    private readonly Dictionary<(int, int), ChunkMeshData> _chunkMeshes = new();
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
        // Prevent Godot's frustum culling from hiding the batch when the
        // auto-computed AABB doesn't encompass all instances. Without this,
        // entities (including the player) can pop in/out as the camera moves.
        mmi.ExtraCullMargin = 1e6f;
        return mmi;
    }

    // ================================================================
    // TERRAIN RENDERING — 3D triangle mesh per chunk
    // ================================================================

    /// <summary>
    /// Creates a MeshInstance3D for every loaded chunk and adds it under the given parent.
    /// Geometry (positions, normals, indices) is built once and cached per chunk so later
    /// terraform updates only rewrite vertex colours. Terrain renders in the XZ plane with
    /// elevation on +Y.
    /// </summary>
    public void InitializeChunkMeshes(Node parent)
    {
        // Vertex-colour gradient shader with manual Lambert lighting + slope shading.
        // render_mode unshaded: terrain computes its own sun term, so the sun direction
        // is supplied via SetSunDirection() to match the scene DirectionalLight.
        var shader = GD.Load<Shader>("res://Shaders/TerrainDither.gdshader");
        _chunkMaterial = new ShaderMaterial { Shader = shader };

        foreach (var chunk in _worldManager.GetLoadedChunks())
        {
            BuildChunkGeometry(chunk, out var vertices, out var normals, out var indices);
            var colors = BuildChunkColors(chunk);

            var mmi = new MeshInstance3D { Mesh = AssembleMesh(vertices, colors, normals, indices) };
            mmi.ExtraCullMargin = _heightScale * 2f;
            parent.AddChild(mmi);

            _chunkMeshes[(chunk.ChunkX, chunk.ChunkY)] = new ChunkMeshData
            {
                Instance = mmi,
                Vertices = vertices,
                Normals  = normals,
                Indices  = indices,
            };
        }
    }

    /// <summary>
    /// Points the terrain shader's sun at a world-space "toward the sun" direction so the
    /// self-lit terrain matches the scene DirectionalLight that lights entities.
    /// </summary>
    public void SetSunDirection(Vector3 towardSun)
    {
        if (towardSun.LengthSquared() > 0f)
            _chunkMaterial?.SetShaderParameter("sun_direction", towardSun.Normalized());
    }

    /// <summary>
    /// Rebuilds dirty chunks. Terraforming only changes tile TYPE (colour) — never
    /// elevation — so positions, normals, and indices are reused from the cache and only
    /// the colour stream is recomputed.
    /// </summary>
    public void UpdateDirtyChunkMeshes()
    {
        if (_worldManager.DirtyChunks.Count == 0) return;

        foreach (var key in _worldManager.DirtyChunks)
        {
            if (!_chunkMeshes.TryGetValue(key, out var data)) continue;
            var chunk = _worldManager.GetChunk(key.Item1, key.Item2);
            if (chunk == null) continue;

            var colors = BuildChunkColors(chunk);
            data.Instance.Mesh = AssembleMesh(data.Vertices, colors, data.Normals, data.Indices);
        }
        _worldManager.DirtyChunks.Clear();
    }

    /// <summary>
    /// Builds chunk geometry: vertex positions, triangle indices, and seam-free analytic
    /// normals. Depends only on elevation (fixed after generation), so it is built once.
    /// </summary>
    private void BuildChunkGeometry(Chunk chunk, out Vector3[] vertices, out Vector3[] normals, out int[] indices)
    {
        int n = chunk.Size + 1;  // vertices per side: 33 for a 32-tile chunk
        int vertexCount = n * n;
        int quadCount = chunk.Size * chunk.Size;

        vertices = new Vector3[vertexCount];
        normals  = new Vector3[vertexCount];
        indices  = new int[quadCount * 6];  // 2 triangles × 3 indices per quad

        int worldOffsetX = chunk.ChunkX * chunk.Size;
        int worldOffsetY = chunk.ChunkY * chunk.Size;

        // --- Vertex positions + analytic heightfield normals ---
        // Normals are central differences on the GLOBAL elevation field, so a vertex shared
        // by two chunks resolves to the same normal in both (no lighting seam at chunk
        // borders). Grid Y maps to world -Z and elevation to world +Y, giving an upward
        // normal of (eLeft - eRight, 2*tileSize, eUp - eDown).
        for (int ly = 0; ly < n; ly++)
        {
            for (int lx = 0; lx < n; lx++)
            {
                int worldX = worldOffsetX + lx;
                int worldY = worldOffsetY + ly;

                bool interior = lx < chunk.Size && ly < chunk.Size;
                float elevation = interior
                    ? chunk.GetElevation(lx, ly)
                    : _worldManager.GetElevation(worldX, worldY);
                vertices[ly * n + lx] = GridCoordinates.VertexToWorld3D(worldX, worldY, _tileSize, elevation, _heightScale);

                float eL = _worldManager.GetVertexElevation(worldX - 1, worldY);
                float eR = _worldManager.GetVertexElevation(worldX + 1, worldY);
                float eD = _worldManager.GetVertexElevation(worldX, worldY - 1);
                float eU = _worldManager.GetVertexElevation(worldX, worldY + 1);
                normals[ly * n + lx] = new Vector3(
                    (eL - eR) * _heightScale,
                    2f * _tileSize,
                    (eU - eD) * _heightScale).Normalized();
            }
        }

        // --- Triangle indices ---
        // Diagonal alternates each row to match the offset-row stagger. With grid Y mapped
        // to world -Z, this winding stays CCW (front-facing) in screen space.
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
    }

    /// <summary>
    /// Builds the per-vertex colour stream for a chunk from its current tile types.
    /// Cheap to recompute, so it is rebuilt on every terraform update.
    /// </summary>
    private Color[] BuildChunkColors(Chunk chunk)
    {
        int n = chunk.Size + 1;
        var colors = new Color[n * n];
        int worldOffsetX = chunk.ChunkX * chunk.Size;
        int worldOffsetY = chunk.ChunkY * chunk.Size;

        for (int ly = 0; ly < n; ly++)
        {
            for (int lx = 0; lx < n; lx++)
            {
                bool interior = lx < chunk.Size && ly < chunk.Size;
                TileType tile;
                if (interior)
                {
                    tile = chunk.GetTile(lx, ly);
                }
                else
                {
                    // Outer +1 edge: sample the neighbouring tile, but clamp at the world
                    // border so the outermost rim shows the edge biome instead of ocean.
                    int wx = Math.Min(worldOffsetX + lx, _worldManager.WorldSizeTiles - 1);
                    int wy = Math.Min(worldOffsetY + ly, _worldManager.WorldSizeTiles - 1);
                    tile = _worldManager.GetTile(wx, wy);
                }
                colors[ly * n + lx] = Chunk.GetTileColor(tile);
            }
        }
        return colors;
    }

    /// <summary>
    /// Packs vertex/colour/normal/index arrays into an ArrayMesh with the terrain material.
    /// </summary>
    private ArrayMesh AssembleMesh(Vector3[] vertices, Color[] colors, Vector3[] normals, int[] indices)
    {
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
