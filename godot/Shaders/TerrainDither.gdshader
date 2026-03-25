// TerrainDither.gdshader
// Per-triangle vertex-colour gradient with PS2-era colour banding.
//
// Each vertex carries COLOR.rgb set to its terrain type's colour.
// The GPU interpolates these barycentrically across each triangle,
// producing a natural three-way gradient between biome colours.
//
// A posterization step quantizes the interpolated colour to a limited
// number of levels per channel, giving the stepped/banded look of
// early-2000s console hardware (PS2 had 5-bit colour → 32 levels).

shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

// Colour levels per channel. 24 gives a visible banding effect similar
// to PS2 without being overly harsh. Set to 256 (or remove the floor)
// to disable posterization entirely.
uniform float color_levels : hint_range(4.0, 256.0) = 24.0;

void fragment() {
    // Posterize the GPU-interpolated vertex colour.
    vec3 c = COLOR.rgb * color_levels;
    ALBEDO = floor(c) / color_levels;
    ROUGHNESS = 1.0;
    METALLIC  = 0.0;
}
