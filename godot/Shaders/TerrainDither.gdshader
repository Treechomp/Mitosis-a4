// TerrainDither.gdshader
// Per-triangle vertex-colour gradient with PS2-era colour banding,
// manual Lambert sun shading, and screen-space slope edge darkening.
//
// Two complementary effects make elevation readable:
//   1. Lambert shading — surfaces facing the sun are bright, surfaces
//      facing away are dark. Works on steep slopes.
//   2. Slope edge darkening — uses dFdx/dFdy on world-space elevation
//      to detect where height changes rapidly across screen pixels.
//      Darkens ridgelines, valley edges, and hillsides regardless of
//      how gentle the slope is. Scales naturally with camera zoom.

shader_type spatial;
render_mode unshaded;

// Colour levels per channel for PS2-era posterization.
uniform float color_levels : hint_range(4.0, 256.0) = 24.0;

// Direction FROM the surface TOWARD the sun (world space).
// Default matches GameManager's DirectionalLight at rotation (-50, 45, 0).
uniform vec3 sun_direction = vec3(-0.45, 0.77, -0.45);

// Ambient brightness floor for Lambert shading.
uniform float ambient : hint_range(0.05, 0.8) = 0.35;

// Slope edge darkening intensity. Higher = stronger edge shading.
uniform float slope_contrast : hint_range(0.0, 5.0) = 2.0;

// Maximum darkening from slope edges (0 = can go full black, 1 = no darkening).
uniform float slope_floor : hint_range(0.0, 1.0) = 0.3;

// World-space data passed from vertex to fragment.
varying vec3 world_normal;
varying float v_elevation;

void vertex() {
    world_normal = mat3(MODEL_MATRIX) * NORMAL;
    v_elevation = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
}

void fragment() {
    // Lambert sun shading
    vec3 N = normalize(world_normal);
    vec3 L = normalize(sun_direction);
    float NdotL = max(dot(N, L), 0.0);
    float sun = mix(ambient, 1.0, NdotL);

    // Screen-space slope detection: darken where elevation changes rapidly.
    // dFdx/dFdy give the rate of elevation change per screen pixel, so this
    // automatically scales with zoom level — close-up shows fine detail,
    // zoomed-out shows major features.
    float slope = length(vec2(dFdx(v_elevation), dFdy(v_elevation)));
    float edge = max(slope_floor, 1.0 - slope * slope_contrast);

    float lighting = sun * edge;

    // Apply lighting to interpolated vertex colour, then posterize.
    vec3 c = COLOR.rgb * lighting * color_levels;
    ALBEDO = floor(c) / color_levels;
}
