// TerrainDither.gdshader
// Biome boundary dithering for terrain chunk meshes.
//
// Each vertex carries:
//   COLOR.rgb  — the vertex's own biome colour (interpolated by GPU → blend weight)
//   UV.x       — the vertex's TileType index as a float (flat: provoking vertex wins)
//
// In the fragment shader we know:
//   primary   = the tile type that "owns" this triangle (from the flat ID)
//   secondary = nearest palette entry to the interpolated blend colour that is NOT primary
//
// A 4×4 Bayer ordered-dither pattern in screen space produces an organic stipple at
// biome edges without any external texture dependency.

shader_type spatial;

// Flat varying — provoking vertex's tile index propagates to all fragments in the triangle.
// This ensures the primary biome is always a real tile type, never an interpolated artefact.
varying flat float primary_tile_id;

// Terrain colour palette.  Must match Chunk.GetTileColor() exactly, indexed by TileType value.
// Set via ShaderMaterial.SetShaderParameter("palette", colors) from RenderingManager.
uniform vec4 palette[20];

// ── Bayer 4×4 ordered-dither threshold ──────────────────────────────────────────────────────
// Returns a threshold in [0, 15/16] for screen-pixel p, tiling every 4 pixels.
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

    // Ordered dither: fragments whose blend weight exceeds the Bayer threshold take the
    // secondary colour, producing a stipple pattern at the biome boundary.
    float threshold = bayer_threshold(ivec2(FRAGCOORD.xy));
    ALBEDO = (blend > threshold) ? color_secondary : color_primary;
}
