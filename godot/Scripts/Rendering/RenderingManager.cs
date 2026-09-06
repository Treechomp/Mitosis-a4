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

    // Visual sea level: vertices below this are rendered as a flat surface (seabed shape hidden)
    // and coloured by depth. Matches the land/water classification boundary in TerrainGenerator.
    // Rivers and lakes sit at/above this (land elevation) so they follow the terrain.
    private const float SeaLevel = 0.40f;

    // MultiMesh entity rendering — one per ShapeType
    private const int ShapeCount = 14; // ShapeType values 0..13
    private const int MultiMeshInitialCapacity = 4096;
    private MultiMeshInstance3D[] _shapeMMIs = null!;
    private int[] _shapeIndices = null!;

    // Animation state
    private int _frameTick;

    // === Observation overlay ===
    // Highlighting is applied at draw time rather than by mutating Renderable.Color, so nothing
    // about the simulation state changes just because you are looking at something.
    public int HighlightSpeciesId { get; set; }
    /// <summary>Species ids are hash codes and often negative, so highlighting needs its own flag.</summary>
    public bool HighlightActive { get; set; }
    public int SelectedEntity { get; set; } = -1;

    /// <summary>
    /// The player, which is drawn whatever the camera is doing. Everything else — selected and
    /// highlighted entities included — takes its chances with the frustum: an off-screen
    /// selection does not need drawing. -1 when there is no player.
    /// </summary>
    public int PlayerEntity { get; set; } = -1;

    /// <summary>
    /// Bounding-radius multiplier for the frustum cull. An entity is kept when its bounding
    /// sphere, inflated by this, touches the frustum.
    ///
    /// It buys two things. An entity a little outside the view can still cast a shadow INTO it,
    /// and a camera that pans hard would otherwise reveal a frame of bare ground at the screen
    /// edge before the next update writes the instances. 1.25 is the smallest value that is
    /// clearly more than "exactly the silhouette": it puts a quarter of the entity's own radius
    /// of slack on every side, which covers the interpolated sub-tick motion the loop already
    /// applies (see the alpha blend below) without keeping a meaningful number of extra entities.
    ///
    /// If pop-in ever appears the answer is to RAISE this, never to drop the cull. Note the honest
    /// limit of the current form: a multiplier on the entity's own radius is a small absolute
    /// distance, so it does not cover a long shadow cast by a caster far outside the view. If
    /// shadow pop shows up specifically, this needs to become an absolute world-unit term.
    /// </summary>
    public float CullMargin { get; set; } = 1.25f;

    /// <summary>Entities considered and entities actually written, last frame. Diagnostics.</summary>
    public int LastEntitiesConsidered { get; private set; }
    public int LastEntitiesDrawn { get; private set; }

    // Frustum planes, re-extracted once per frame. Six is what Camera3D.GetFrustum returns
    // (near, far, left, top, right, bottom).
    private readonly Plane[] _frustum = new Plane[6];
    private static readonly Color HighlightColor = new(1f, 1f, 1f);
    private static readonly Color SelectionColor = new(1f, 0.2f, 0.9f);

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

        // Diamond — armored (boar, musk ox, turtle, lizard): a true 4-sided pyramid (pointed
        // gem), so it reads distinctly from the predators' wide Fangs box rather than as another
        // wide rectangle.
        _shapeMMIs[(int)ShapeType.Diamond]  = CreateMMI(
            new CylinderMesh { RadialSegments = 4, TopRadius = 0.0f, BottomRadius = 0.8f, Height = 1.0f }, entityMat);

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

        // Carcass — corpses: a flat, low splayed disc lying on the ground, unmistakably not an
        // upright creature (was reusing Diamond, which looked like the armored herbivores).
        _shapeMMIs[(int)ShapeType.Carcass]  = CreateMMI(
            new CylinderMesh { RadialSegments = 8, TopRadius = 0.55f, BottomRadius = 0.65f, Height = 0.15f }, entityMat);

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
                // Flatten the sea to a level surface so the seabed shape isn't visible (deep
                // water is coloured by depth instead). Land and rivers/lakes (>= SeaLevel) are
                // unaffected.
                float meshElev = elevation < SeaLevel ? SeaLevel : elevation;
                vertices[ly * n + lx] = GridCoordinates.VertexToWorld3D(worldX, worldY, _tileSize, meshElev, _heightScale);

                // Normals from the same sea-flattened field so the sea reads as flat-shaded.
                float eL = MathF.Max(_worldManager.GetVertexElevation(worldX - 1, worldY), SeaLevel);
                float eR = MathF.Max(_worldManager.GetVertexElevation(worldX + 1, worldY), SeaLevel);
                float eD = MathF.Max(_worldManager.GetVertexElevation(worldX, worldY - 1), SeaLevel);
                float eU = MathF.Max(_worldManager.GetVertexElevation(worldX, worldY + 1), SeaLevel);
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

        int maxTile = _worldManager.WorldSizeTiles - 1;

        for (int ly = 0; ly < n; ly++)
        {
            for (int lx = 0; lx < n; lx++)
            {
                TileType tile;
                float moisture, temperature, elevation;
                if (lx < chunk.Size && ly < chunk.Size)
                {
                    tile        = chunk.GetTile(lx, ly);
                    moisture    = chunk.GetMoisture(lx, ly);
                    temperature = chunk.GetTemperature(lx, ly);
                    elevation   = chunk.GetElevation(lx, ly);
                }
                else
                {
                    // Outer +1 edge: sample the neighbour, clamped at the world border so the
                    // rim shows the edge biome instead of ocean.
                    int wx = Math.Min(worldOffsetX + lx, maxTile);
                    int wy = Math.Min(worldOffsetY + ly, maxTile);
                    tile        = _worldManager.GetTile(wx, wy);
                    moisture    = _worldManager.GetVertexMoisture(wx, wy);
                    temperature = _worldManager.GetVertexTemperature(wx, wy);
                    elevation   = _worldManager.GetVertexElevation(wx, wy);
                }

                // Water (ocean/river/lake/reef): coloured by depth so the flat sea reads as
                // water, not blue terrain. Tiles overridden away from their climate class
                // (terraform, landmarks) keep a discrete colour; pure-climate land uses the
                // continuous parameter palette.
                if (tile.IsWater() || tile == TileType.Reef)
                {
                    colors[ly * n + lx] = WaterColor(elevation);
                }
                else if (tile != TerrainGenerator.DetermineTileType(elevation, moisture, temperature))
                {
                    colors[ly * n + lx] = Chunk.GetTileColor(tile);
                }
                else
                {
                    colors[ly * n + lx] = TerrainPalette.FromParams(moisture, temperature, elevation);
                }
            }
        }
        return colors;
    }

    /// <summary>
    /// Water colour by depth: shallow near the shore, darker toward deep water. Floors at/above
    /// sea level (rivers, lakes on land) read as shallow water.
    /// </summary>
    private static Color WaterColor(float floorElevation)
    {
        const float maxDepth = 0.30f;
        float depth = SeaLevel - floorElevation;
        if (depth < 0f) depth = 0f;
        float t = depth / maxDepth;
        if (t > 1f) t = 1f;
        var shallow = new Color(0.30f, 0.55f, 0.72f);
        var deep    = new Color(0.05f, 0.13f, 0.38f);
        return shallow.Lerp(deep, t);
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
    public void UpdateEntityMultiMeshes(Camera3D camera, float alpha)
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

        // ONE marshalled call per frame for the planes; every per-entity test after this is
        // pure C#. That ratio is the whole change: the loop used to do two marshalled calls per
        // entity per frame with no visibility test at all, so its cost tracked world population
        // rather than what was on screen.
        int planeCount = ExtractFrustum(camera);
        int considered = 0, drawn = 0;

        foreach (int entity in _entityManager.Query(required))
        {
            ref var pos  = ref _entityManager.Positions[entity];
            ref var prev = ref _entityManager.PrevPositions[entity];
            ref var rend = ref _entityManager.Renderables[entity];
            considered++;

            // Interpolate between the previous and current tick positions so fast movers
            // (e.g. the player at high exploration speed) glide instead of stepping at 20 TPS.
            float rx = prev.X + (pos.X - prev.X) * alpha;
            float ry = prev.Y + (pos.Y - prev.Y) * alpha;

            // ── FRUSTUM CULL, before any real work ────────────────────────────────────────
            // Placed ahead of GetElevation deliberately: that is four chunk lookups per entity
            // and must not run for something off screen.
            //
            // Which creates an ordering problem, because the test wants a 3D point and the Y
            // comes from the elevation we are trying not to fetch. Resolved by testing a
            // VERTICAL INTERVAL instead of a point: the world's elevation range is bounded and
            // known, so the entity's possible Y is a short, cheap-to-compute span.
            //   lower  = SeaLevel * heightScale   — the render path clamps elevation up to sea
            //            level, so nothing is ever drawn below it.
            //   upper  = 1.0 * heightScale + lift — TerrainGenerator clamps stored elevation to
            //            [0,1] (see SampleTile), and the loop lifts a mesh by at most
            //            Size * 0.5 so its base sits on the ground.
            // Signed distance to a plane is linear in the point, so over that segment the extreme
            // is at one of its two ends; testing both is exact for the interval, not approximate.
            if (planeCount > 0 && entity != PlayerEntity)
            {
                float wx = rx * _tileSize + GridCoordinates.SmoothRowOffset(ry, _tileSize);
                float wz = -ry * _tileSize;
                float yLo = SeaLevel * _heightScale;
                float yHi = _heightScale + rend.Size * 0.5f;
                // Unit primitives centred at origin scaled by Size: the bounding sphere that
                // holds a box is half its diagonal, 0.5 * sqrt(3) ≈ 0.87.
                float radius = rend.Size * 0.87f * CullMargin;
                if (!TouchesFrustum(planeCount, wx, wz, yLo, yHi, radius))
                    continue;
            }
            drawn++;

            // 3D world position: XZ from abstract grid, Y from terrain elevation.
            // Clamp to sea level so entities over water sit on the (flat) surface, not the
            // hidden seabed.
            float elevation = _worldManager.GetElevation(rx, ry);
            if (elevation < SeaLevel) elevation = SeaLevel;
            var pos3D = GridCoordinates.VertexToWorld3D(rx, ry, _tileSize, elevation, _heightScale);

            // Lift entity so the bottom of its mesh sits on the terrain surface.
            // All primitives are unit-sized and centered at origin, so half-height ≈ 0.5.
            // Torus (Crescent) is flat in XZ — use its tube radius (0.3) as the offset.
            pos3D.Y += rend.Size * (rend.Shape == ShapeType.Crescent ? 0.3f
                                    : rend.Shape == ShapeType.Carcass ? 0.075f
                                    : 0.5f);

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

            var color = rend.Color;
            if (entity == SelectedEntity)
            {
                color = SelectionColor;
            }
            else if (HighlightActive
                     && _entityManager.HasComponents(entity, ComponentFlags.Species)
                     && _entityManager.Species[entity].SpeciesId == HighlightSpeciesId
                     && !_entityManager.HasComponents(entity, ComponentFlags.Carrion))
            {
                // Blend toward white rather than replacing: the species keeps its silhouette and
                // its own hue is still readable, it just pops out of the crowd.
                color = color.Lerp(HighlightColor, 0.75f);
            }
            mm.SetInstanceColor(i, color);
        }

        // Set visible counts. The loop already compacted indices as it wrote, so a culled entity
        // simply never claimed one and these stay exactly the number written — which is what
        // stops last frame's instances rendering at last frame's positions.
        for (int s = 0; s < ShapeCount; s++)
            _shapeMMIs[s].Multimesh.VisibleInstanceCount = _shapeIndices[s];

        LastEntitiesConsidered = considered;
        LastEntitiesDrawn = drawn;
    }

    /// <summary>
    /// Copy the camera's six frustum planes into <see cref="_frustum"/>, oriented so that inside
    /// is the POSITIVE side of every one. Returns the plane count, or 0 when there is no camera
    /// (in which case nothing is culled — drawing too much is a performance bug, drawing nothing
    /// is a black screen).
    ///
    /// The re-orientation is not defensive padding. Godot documents the frustum normals as
    /// pointing "into inversed frustum space", which is read both ways in practice, and getting
    /// it wrong does not degrade gracefully: one sign culls every entity in the world, the other
    /// culls none. So the convention is derived rather than assumed, from a point that is
    /// guaranteed inside — on the camera's forward axis, midway between the near and far planes.
    /// Six comparisons once a frame, and the test below is then unambiguous.
    /// </summary>
    private int ExtractFrustum(Camera3D camera)
    {
        if (camera == null) return 0;
        var planes = camera.GetFrustum();
        int n = Math.Min(planes.Count, _frustum.Length);
        if (n == 0) return 0;

        var xf = camera.GlobalTransform;
        Vector3 inside = xf.Origin - xf.Basis.Z * ((camera.Near + camera.Far) * 0.5f);
        for (int i = 0; i < n; i++)
        {
            Plane p = planes[i];
            _frustum[i] = p.DistanceTo(inside) < 0f ? new Plane(-p.Normal, -p.D) : p;
        }
        return n;
    }

    /// <summary>
    /// Does the sphere of <paramref name="radius"/>, swept along the vertical segment from
    /// <paramref name="yLo"/> to <paramref name="yHi"/> at (x, z), touch the frustum? Pure C#,
    /// no allocation, no marshalling — this runs once per entity per frame.
    /// </summary>
    private bool TouchesFrustum(int planeCount, float x, float z,
                                 float yLo, float yHi, float radius)
    {
        for (int i = 0; i < planeCount; i++)
        {
            ref readonly Plane p = ref _frustum[i];
            float flat = p.Normal.X * x + p.Normal.Z * z - p.D;
            float dLo = flat + p.Normal.Y * yLo;
            float dHi = flat + p.Normal.Y * yHi;
            // Wholly behind this plane by more than the radius: outside, and no other plane can
            // bring it back.
            if (dLo < -radius && dHi < -radius) return false;
        }
        return true;
    }
}
