using System;
using Mitosis.World;

namespace Mitosis.SpeciesData;

/// <summary>
/// Single resolver for how a species relates to a terrain tile. Replaces the scattered,
/// inconsistently-applied terrain logic documented in docs/terrain-handling-audit.md — every
/// movement/AI system now consults these methods so behaviour is consistent by construction.
///
/// Four dimensions:
///  - Speed:       movement multiplier (REPLACE — a species' own value overrides the tile grip).
///  - IsImpassable / SteerAversion: where a species will/won't go (hard element barrier + soft dislike).
///  - DiscomfortRate: how fast standing here builds the "leave this ground" urge.
///  - Concealment: how hidden the species is here (cover), reducing its detectability.
///
/// SteerAversion is the soft PREFERENCE (a pull, overridable by hunger and fear); DiscomfortRate is
/// the soft INTOLERANCE that eventually forces an escape. Keeping them separate is what lets a
/// shark prefer deep water while still hunting the shallows indefinitely. The hard element barrier
/// (IsImpassable) is neither — it is never crossed voluntarily.
/// </summary>
public static class TerrainProfile
{
    /// <summary>
    /// Discomfort accrual on the wrong element (an aquatic beached on land, a non-swimmer in open
    /// water). Above every species' threshold by a wide margin, so getting back to the right
    /// element outranks any other terrain preference.
    /// </summary>
    private const float WrongElementDiscomfort = 20f;

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
        if (s.IsAquatic) return !tile.IsSubmerged();
        // Submerged, not IsWater: Reef is coral UNDER water, so a creature that cannot swim at all
        // cannot stand on it either. Reef sat outside IsWater, so it was just another walkable tile
        // to a Sectid — no barrier, aversion 0.70, a mere 6/tick discomfort — and swarms strolled
        // onto the reef after fish. The discomfort was never too weak; the tile simply never
        // registered as water.
        if (s.AvoidsWater) return tile.IsSubmerged();
        return false;
    }

    /// <summary>
    /// Steering aversion [0..1]: how strongly the species avoids heading onto this tile when
    /// choosing a movement direction (wander / flee / hunt pursuit). Species-aware:
    ///  - flyers ignore terrain (0),
    ///  - wrong element = 1 (aquatic on land, non-swimmer in water) — a hard barrier,
    ///  - then the species' own TerrainAversionModifiers, on ANY tile including water,
    ///  - then water is free for aquatic/semi-aquatic species,
    ///  - land animals: tile baseline (water naturally ~0.75-0.95 — disliked but enterable).
    ///
    /// The species table is consulted for water tiles too (it used to be land-only), which is what
    /// lets an aquatic species express a preference WITHIN its element — a shark that patrols deep
    /// water but still hunts the shallows. Doing that through discomfort instead would eventually
    /// lock it into a permanent escape (see DiscomfortRate).
    /// </summary>
    public static float SteerAversion(SpeciesDefinition s, TileType tile)
    {
        if (s.IsFlying) return 0f;
        if (IsImpassable(s, tile)) return 1f;
        if (s.TerrainAversionModifiers != null
            && s.TerrainAversionModifiers.TryGetValue(tile, out float own))
            return own;
        if ((s.IsAquatic || s.SemiAquatic) && tile.IsSubmerged()) return 0f;
        return tile.GetAvoidanceWeight();
    }

    /// <summary>
    /// Discomfort accrued per tick while standing on this tile — the input to
    /// TerrainDiscomfortSystem's "this ground is intolerable, leave" urge. Element-aware, because
    /// the tile table (TileType.GetDiscomfortRate) is written from a land animal's point of view:
    /// it rates open water as punishing, which is nonsense for the species that live in it.
    ///
    /// Resolution order, mirroring SteerAversion:
    ///  - flyers feel nothing,
    ///  - wrong element is maximally uncomfortable,
    ///  - home element (water for aquatic/semi-aquatic) has NO baseline cost,
    ///  - everything else takes the tile baseline,
    ///  - plus the species' own comfort modifier, floored at zero.
    ///
    /// Bug history: sharks and fish sat on a net-positive rate in shallow water (tile 5, modifiers
    /// only -2/-3), climbed to the discomfort ceiling and locked into a permanent escape — in
    /// their own feeding grounds. Zeroing the home element removes the whole class of bug rather
    /// than requiring every aquatic species to out-tune the land table tile by tile.
    /// </summary>
    public static float DiscomfortRate(SpeciesDefinition s, TileType tile)
    {
        if (s.IsFlying) return 0f;

        float rate;
        if (IsImpassable(s, tile))
            rate = WrongElementDiscomfort;
        else if ((s.IsAquatic || s.SemiAquatic) && tile.IsSubmerged())
            rate = 0f;
        else
            rate = tile.GetDiscomfortRate();

        return MathF.Max(0f, rate + s.GetTerrainComfortModifier(tile));
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
