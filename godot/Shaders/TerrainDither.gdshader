// TerrainDither.gdshader
// Per-triangle vertex-colour gradient with PS2-era colour banding
// and altitude-based shading to make elevation clearly visible.
//
// Each vertex carries COLOR.rgb set to its terrain type's colour.
// The GPU interpolates these barycentrically across each triangle,
// producing a natural three-way gradient between biome colours.
//
// Elevation shading darkens valleys and brightens peaks so that
// terrain height differences are immediately readable at any zoom.
// Combined with Lambert diffuse from the DirectionalLight (which
// highlights slopes via normals), this gives strong depth cues.

shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

// Colour levels per channel for PS2-era posterization.
// 24 = visible banding, 256 = smooth.
uniform float color_levels : hint_range(4.0, 256.0) = 24.0;

// Maximum world-Y that elevation can reach (elevation 1.0 * heightScale).
// Must match ElevationHeightScale in GameManager (default 64).
uniform float max_height = 64.0;

// Brightness range: valleys get dark_brightness, peaks get 1.0.
uniform float dark_brightness : hint_range(0.3, 1.0) = 0.55;

// World-space elevation passed from vertex → fragment.
varying float v_elevation;

void vertex() {
    v_elevation = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
}

void fragment() {
    // Altitude-based brightness: low terrain is darker, high terrain is brighter.
    float height_frac = clamp(v_elevation / max_height, 0.0, 1.0);
    float brightness = mix(dark_brightness, 1.0, height_frac);

    // Apply brightness to interpolated vertex colour, then posterize.
    vec3 c = COLOR.rgb * brightness * color_levels;
    ALBEDO = floor(c) / color_levels;
    ROUGHNESS = 1.0;
    METALLIC  = 0.0;
}
