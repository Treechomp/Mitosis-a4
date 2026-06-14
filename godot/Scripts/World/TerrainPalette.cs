using Godot;

namespace Mitosis.World;

/// <summary>
/// Continuous terrain colour as a smooth blend of biome "anchor" colours positioned in
/// (moisture, temperature, elevation) parameter space — the "terrain cube". Replaces flat
/// per-tile colours for pure-climate land so biome transitions are gradients, not hard edges.
///
/// FIRST PASS: the anchor positions/colours below are reasoned starting values; tune them
/// visually in-editor. See docs/3d-terrain-plan.md (Phase 2a).
/// </summary>
public static class TerrainPalette
{
    private readonly struct Anchor
    {
        public readonly float M, T, E;
        public readonly Color Color;
        public Anchor(float m, float t, float e, Color color) { M = m; T = t; E = e; Color = color; }
    }

    // Biome anchors in (moisture, temperature, elevation) space — each component 0..1.
    private static readonly Anchor[] Anchors =
    {
        new(0.85f, 0.50f, 0.05f, new Color(0.15f, 0.30f, 0.60f)), // water    (low, wet)
        new(0.15f, 0.75f, 0.46f, new Color(0.90f, 0.85f, 0.60f)), // sand     (dry, hot)
        new(0.28f, 0.65f, 0.55f, new Color(0.55f, 0.42f, 0.25f)), // dirt     (dry-ish, warm)
        new(0.45f, 0.50f, 0.55f, new Color(0.30f, 0.70f, 0.30f)), // grass    (centre)
        new(0.62f, 0.52f, 0.60f, new Color(0.15f, 0.50f, 0.20f)), // forest   (wet, temperate)
        new(0.82f, 0.45f, 0.52f, new Color(0.10f, 0.45f, 0.30f)), // swamp    (very wet)
        new(0.45f, 0.08f, 0.62f, new Color(0.85f, 0.92f, 0.97f)), // snow     (cold)
        new(0.40f, 0.42f, 0.90f, new Color(0.50f, 0.50f, 0.50f)), // mountain (high)
    };

    /// <summary>
    /// Blend anchor colours by inverse-square distance in parameter space. Each component is
    /// expected in 0..1; out-of-range values still produce a sensible nearest-anchor colour.
    /// </summary>
    public static Color FromParams(float moisture, float temperature, float elevation)
    {
        float r = 0f, g = 0f, b = 0f, wsum = 0f;
        for (int i = 0; i < Anchors.Length; i++)
        {
            Anchor a = Anchors[i];
            float dm = moisture - a.M;
            float dt = temperature - a.T;
            float de = elevation - a.E;
            float distSq = dm * dm + dt * dt + de * de;
            float w = 1f / (distSq + 0.0025f); // epsilon avoids div-by-zero exactly at an anchor
            r += a.Color.R * w;
            g += a.Color.G * w;
            b += a.Color.B * w;
            wsum += w;
        }
        float inv = 1f / wsum;
        return new Color(r * inv, g * inv, b * inv);
    }
}
