// TerrainDither.gdshader
// Per-triangle vertex-colour gradient with PS2-era colour banding
// and contour-line elevation shading.
//
// Each vertex carries COLOR.rgb set to its terrain type's colour.
// The GPU interpolates these barycentrically across each triangle,
// producing a natural three-way gradient between biome colours.
//
// Elevation is made visible via two effects:
//   1. Stepped brightness bands — elevation is quantized into discrete
//      levels, each slightly brighter than the one below.
//   2. Contour lines — dark lines at each band boundary, like a
//      topographic map, so height reads clearly regardless of biome.

shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

// Colour levels per channel for PS2-era posterization.
// 24 = visible banding, 256 = smooth.
uniform float color_levels : hint_range(4.0, 256.0) = 24.0;

// Maximum world-Y that elevation can reach (elevation 1.0 * heightScale).
// Must match ElevationHeightScale in GameManager (default 64).
uniform float max_height = 64.0;

// Number of elevation bands. More = finer contours. 8 is readable.
uniform float contour_bands : hint_range(3.0, 20.0) = 8.0;

// Brightness of the lowest band (peaks are 1.0).
uniform float valley_shade : hint_range(0.2, 0.9) = 0.45;

// Contour line darkness (0 = invisible, 1 = solid black).
uniform float contour_strength : hint_range(0.0, 1.0) = 0.4;

// World-space elevation passed from vertex to fragment.
varying float v_elevation;

void vertex() {
    v_elevation = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
}

void fragment() {
    float height_frac = clamp(v_elevation / max_height, 0.0, 1.0);

    // Quantize into discrete elevation bands for stepped shading.
    float band = floor(height_frac * contour_bands) / contour_bands;
    float brightness = mix(valley_shade, 1.0, band);

    // Contour lines: darken pixels near band boundaries.
    // fract gives position within the band (0 at bottom edge, 1 at top).
    float within_band = fract(height_frac * contour_bands);
    // Narrow dark stripe at the bottom of each band.
    float contour = smoothstep(0.0, 0.12, within_band);
    brightness *= mix(1.0 - contour_strength, 1.0, contour);

    // Apply brightness to interpolated vertex colour, then posterize.
    vec3 c = COLOR.rgb * brightness * color_levels;
    ALBEDO = floor(c) / color_levels;
    ROUGHNESS = 1.0;
    METALLIC  = 0.0;
}
