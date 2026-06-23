using Mitosis.World;

namespace Mitosis.SpeciesData;

/// <summary>
/// Single resolver for how a species relates to a terrain tile. Replaces the scattered,
/// inconsistently-applied terrain logic documented in docs/terrain-handling-audit.md — every
/// movement/AI system now consults these methods so behaviour is consistent by construction.
///
/// Three dimensions:
///  - Speed:       movement multiplier (REPLACE — a species' own value overrides the tile grip).
///  - IsImpassable / SteerAversion: where a species will/won't go (hard element barrier + soft dislike).
///  - Concealment: how hidden the species is here (cover), reducing its detectability.
///
/// Comfort/discomfort accumulation (TerrainComfortModifiers + tile GetDiscomfortRate, driving the
/// "leave uncomfortable ground" escape urge) stays in TerrainDiscomfortSystem — that is the SOFT,
/// hunger/fear-overridable preference. The hard element barrier lives here (IsImpassable).
/// </summary>
public static class TerrainProfile
{
    /// <summary>
    /// Movement speed multiplier on a tile. REPLACE semantics: a species' own value for the tile
    /// overrides the tile's intrinsic grip; otherwise the tile base applies. Lets a specialist be
    /// fast where others crawl (or especially slow if badly suited to the terrain).
    /// </summary>
    public static float Speed(SpeciesDefinition s, TileType tile)
        => s.TerrainSpeedModifiers != null && s.TerrainSpeedModifiers.TryGetValue(tile, out float m)
            ? m : tile.GetSpeedMultiplier();

    /// <summary>
    /// True when a tile is a hard barrier for this species — the wrong element, never entered
    /// voluntarily (only by accident → drowning/suffocation). Fish/Sharks: land. Insects
    /// (AvoidsWater): any water. Semi-aquatic and ordinary land animals have NO hard barrier —
    /// water is merely disliked for land animals, so they can be pressed across it by fear/hunger.
    /// </summary>
    public static bool IsImpassable(SpeciesDefinition s, TileType tile)
    {
        if (s.IsFlying) return false;
        if (s.IsAquatic) return !tile.IsWater();
        if (s.AvoidsWater) return tile.IsWater();
        return false;
    }

    /// <summary>
    /// Steering aversion [0..1]: how strongly the species avoids heading onto this tile when
    /// choosing a movement direction (wander / flee / hunt pursuit). Species-aware:
    ///  - flyers ignore terrain (0),
    ///  - aquatic: land = 1 (stay in water), water = 0,
    ///  - insects (AvoidsWater): water = 1, land = tile baseline,
    ///  - semi-aquatic: water = 0 (at home), land = tile baseline,
    ///  - land animals: tile baseline (water naturally ~0.75-0.95 — disliked but enterable).
    /// </summary>
    public static float SteerAversion(SpeciesDefinition s, TileType tile)
    {
        if (s.IsFlying) return 0f;
        bool water = tile.IsWater();
        if (s.IsAquatic) return water ? 0f : 1f;
        if (s.AvoidsWater) return water ? 1f : tile.GetAvoidanceWeight();
        if (s.SemiAquatic) return water ? 0f : tile.GetAvoidanceWeight();
        return tile.GetAvoidanceWeight();
    }

    /// <summary>
    /// Concealment (cover) on a tile: how hidden the species is from predators here. A species
    /// override (camouflage — Rabbit in forest, Scorpion in desert, Arctic Fox in snow) else the
    /// tile's generic cover. Higher = harder to detect.
    /// </summary>
    public static float Concealment(SpeciesDefinition s, TileType tile)
        => s.TerrainConcealment != null && s.TerrainConcealment.TryGetValue(tile, out float m)
            ? m : tile.GetCoverBonus();
}
