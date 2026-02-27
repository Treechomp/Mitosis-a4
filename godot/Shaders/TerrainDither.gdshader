// TerrainDither.gdshader
// Biome boundary dithering for terrain chunk meshes.
//
// Each vertex carries:
//   COLOR.rgb  — the vertex's own biome colour (interpolated by GPU → blend weight)
//   UV.x       — the vertex's TileType index as a float (flat: provoking vertex wins)
//
// In the fragment shader:
//   primary   = the tile type that "owns" this triangle (from the flat UV.x)
//   secondary = nearest palette entry to the interpolated blend colour, excluding primary
//
// A 4×4 Bayer ordered-dither pattern in WORLD space produces a stable (non-swimming)
// stipple at biome edges.  The dither cell size is tile_size/4 world units so one full
// Bayer period spans exactly one terrain tile regardless of camera zoom or movement.

shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

// Flat varying — provoking-vertex tile index propagates unchanged to every fragment.
// Guarantees primary is always a real palette index, never an interpolated artefact.
varying flat float primary_tile_id;

// World-space XZ position passed from vertex → fragment for world-anchored dithering.
varying vec2 world_xz;

// Terrain colour palette.  Must match Chunk.GetTileColor() exactly, indexed by TileType.
// Set via ShaderMaterial.SetShaderParameter("palette", colors) from RenderingManager.
uniform vec4 palette[20];

// World units per grid tile.  Set to RenderingManager._tileSize (default 16).
// Controls dither cell size: one Bayer period = tile_size world units = one tile.
uniform float tile_size = 16.0;

// ── Bayer 4×4 ordered-dither threshold ──────────────────────────────────────────────────
// Returns a threshold in [0, 15/16] for tile-space integer coordinate p, tiling every 4.
float bayer_threshold(ivec2 p) {
    int bayer[16] = int[16](
         0,  8,  2, 10,
        12,  4, 14,  6,
         3, 11,  1,  9,
        15,  7, 13,  5
    );
    int bx = ((p.x % 4) + 4) % 4;
    int by = ((p.y % 4) + 4) % 4;
    return float(bayer[by * 4 + bx]) / 16.0;
}

void vertex() {
    primary_tile_id = UV.x;
    // World-space XZ: used for stable dithering that doesn't move with the camera.
    world_xz = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
}

void fragment() {
    int primary = int(round(primary_tile_id));
    vec3 color_primary = palette[primary].rgb;

    // Find the palette entry nearest to the interpolated blend colour, excluding primary.
    // This is the biome on the other side of the boundary — always the correct neighbour
    // because we explicitly skip the primary rather than relying on colour proximity alone.
    float best_dist = 1e9;
    vec3 color_secondary = color_primary;
    for (int i = 0; i < 20; i++) {
        if (i == primary) continue;
        float d = distance(COLOR.rgb, palette[i].rgb);
        if (d < best_dist) {
            best_dist = d;
            color_secondary = palette[i].rgb;
        }
    }

    // Blend weight: fraction of the way from primary colour toward secondary.
    // 0.0 → deep inside primary biome, 1.0 → deep inside secondary biome.
    float range = distance(color_primary, color_secondary);
    float blend = (range > 0.001)
        ? clamp(distance(COLOR.rgb, color_primary) / range, 0.0, 1.0)
        : 0.0;

    // Dither in world tile space so the pattern is anchored to the terrain grid.
    // Cell size = tile_size / 4 → one full 4×4 Bayer period per tile.
    float cell = tile_size * 0.25;
    float threshold = bayer_threshold(ivec2(floor(world_xz / cell)));

    ALBEDO   = (blend > threshold) ? color_secondary : color_primary;
    ROUGHNESS = 1.0;
    METALLIC  = 0.0;
}
