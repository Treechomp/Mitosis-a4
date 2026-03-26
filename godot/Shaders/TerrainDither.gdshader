// TerrainDither.gdshader
// Per-triangle vertex-colour gradient with PS2-era colour banding
// and manual Lambert shading for strong, tunable shadow contrast.
//
// Uses render_mode unshaded so the terrain controls its own lighting
// independently of the scene's DirectionalLight (which still lights
// entities). A hardcoded sun direction and tunable ambient produce
// crisp slope shadows that make elevation immediately readable.

shader_type spatial;
render_mode unshaded;

// Colour levels per channel for PS2-era posterization.
uniform float color_levels : hint_range(4.0, 256.0) = 24.0;

// Direction FROM the surface TOWARD the sun (world space, auto-normalized).
// Default matches GameManager's DirectionalLight at rotation (-50, 45, 0).
uniform vec3 sun_direction = vec3(-0.45, 0.77, -0.45);

// Ambient brightness floor — how dark fully-shadowed slopes get.
// Lower = more dramatic shadows = more visible elevation.
uniform float ambient : hint_range(0.05, 0.8) = 0.35;

// World-space normal passed from vertex to fragment.
varying vec3 world_normal;

void vertex() {
    world_normal = mat3(MODEL_MATRIX) * NORMAL;
}

void fragment() {
    vec3 N = normalize(world_normal);
    vec3 L = normalize(sun_direction);
    float NdotL = max(dot(N, L), 0.0);
    float lighting = mix(ambient, 1.0, NdotL);

    // Apply lighting to interpolated vertex colour, then posterize.
    vec3 c = COLOR.rgb * lighting * color_levels;
    ALBEDO = floor(c) / color_levels;
}
