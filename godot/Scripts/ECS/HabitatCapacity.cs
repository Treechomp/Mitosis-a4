using System;
using System.Collections.Generic;
using Mitosis.SpeciesData;
using Mitosis.World;

namespace Mitosis.ECS;

/// <summary>
/// How many of each species the world the seed produced can actually feed, and the birth brake
/// that follows from it.
///
/// WHY THIS EXISTS. <see cref="PopulationBudget"/> is an engineering wall — a thread budget, not
/// an ecology — and its own contract says the simulation should ideally never touch it. It was
/// being touched from the first fifth of a run and held for the rest of it, which is the loud
/// signal that ecology is not binding where it should. A ceiling reached early and held is a flat
/// line, not a world.
///
/// WHAT IT IS. A carrying capacity, per species, derived from the terrain the seed generated:
///
///   share(s, t) = ForageYield(s, t) ÷ Σ over competing species [ ForageYield(j, t) ]
///   supply(s)   = RegenerationRate · Σ over tile types [ tiles of that type · share(s, t) ]
///   demand(s)   = what one animal strips per tick while it is feeding
///   capacity(s) = OccupancyFraction · supply(s) ÷ demand(s)
///
/// Supply is the world's sustainable food FLOW as this species can claim it. Fertility regrows at
/// a flat rate per tile, so the flow is a count of usable ground and not of how much each tile
/// holds (<see cref="TileTypeExtensions.NutritionCap"/> sets the stock, which is what makes a tile
/// worth stopping on, not the rate). The share is what makes a forage table load-bearing: ground
/// every grazer values equally is split equally, and ground one species values far above the rest
/// is very nearly its own. That is the difference between a smaller pool and a *different* pool.
///
/// Demand is the consume rate, not the hunger arithmetic. A grazer draws from the tile under it
/// every tick it stands on fertile ground, whether or not it needs the food, so what the world
/// pays out is the consume rate — the hunger-equilibrium figure is what an animal needs, which is
/// a much smaller number and not what depletes the ground.
///
/// This is what makes a seed's terrain decide which species thrive in it. A world of steppe feeds
/// the species whose forage yield is high on steppe, and the jungle species is rare there — from
/// the map, with no species named anywhere and no per-world tuning.
///
/// WHY IT IS GRADED. The mechanism deleted from ReproductionSystem — one global ramp that fell as
/// a SHARED cap filled — selected for fast breeders, because every species' births were divided by
/// the same number and only the fastest survived it. A ramp against a per-species capacity has no
/// such coupling: crowding among deer says nothing about whether a camel may breed. Graded rather
/// than hard for the same reason the local-density brake is graded — a cliff produces a population
/// that runs flat out and then stops dead, which is the flat line again with a different cause.
/// </summary>
public sealed class HabitatCapacity
{
    /// <summary>
    /// The share of the world's sustainable food flow a population is allowed to be sized to.
    ///
    /// Well below 1, and for a reason that can be measured rather than asserted: an animal is only
    /// standing on ground it can feed from part of the time, so it can only ever claim part of the
    /// flow. Sampled over a run that share came out near a quarter, which is where this sits. It
    /// also carries the margin for a population never being spread evenly over its habitat and for
    /// fertility taking time to come back — the ground has to carry local crowding, not the
    /// average.
    ///
    /// This is the one number that says how densely packed a niche should be, and it is a design
    /// choice. Everything else here is derived from the world and the registry.
    /// </summary>
    public const float OccupancyFraction = 0.22f;

    /// <summary>
    /// Fraction of capacity below which births are unaffected. Above it the pass chance falls
    /// linearly to zero at capacity, so a population slows as it fills its habitat instead of
    /// growing flat out into a wall.
    /// </summary>
    private const float FreeFraction = 0.5f;

    private readonly WorldManager _world;
    private readonly Dictionary<int, float> _capacity = new(32);
    private int[]? _tilesOfType;

    public HabitatCapacity(WorldManager world)
    {
        _world = world;
    }

    /// <summary>
    /// Tiles of each type in the generated world. Taken on first use rather than at construction:
    /// the stack is built before the world is pregenerated, and a census of an ungenerated world
    /// would describe nothing. One pass, once per run.
    /// </summary>
    private int[] TileCensus()
    {
        if (_tilesOfType != null) return _tilesOfType;

        var counts = new int[Enum.GetValues<TileType>().Length];
        for (int cy = 0; cy < _world.WorldSizeChunks; cy++)
        for (int cx = 0; cx < _world.WorldSizeChunks; cx++)
        {
            var chunk = _world.GetChunk(cx, cy);
            if (chunk == null) continue;
            for (int y = 0; y < chunk.Size; y++)
            for (int x = 0; x < chunk.Size; x++)
                counts[(int)chunk.GetTile(x, y)]++;
        }
        _tilesOfType = counts;
        return counts;
    }

    /// <summary>
    /// The share of the world's sustainable food flow this species can claim, in nutrition per
    /// tick. Ground it cannot feed on contributes nothing; ground it shares contributes in
    /// proportion to what the ground is worth to it against what it is worth to its competitors.
    /// </summary>
    public float SupplyFor(SpeciesDefinition def)
    {
        var counts = TileCensus();
        var rivals = GroundFedSpecies();
        float claimed = 0f;
        foreach (TileType tile in Enum.GetValues<TileType>())
        {
            int n = counts[(int)tile];
            if (n == 0 || tile.NutritionCap() <= 0f || !FeedsOn(def, tile)) continue;

            float mine = TerrainProfile.ForageYield(def, tile);
            if (mine <= 0f) continue;

            float all = 0f;
            foreach (var other in rivals)
                if (FeedsOn(other, tile))
                    all += TerrainProfile.ForageYield(other, tile);

            if (all > 0f) claimed += n * mine / all;
        }
        return claimed * Chunk.RegenerationRate;
    }

    /// <summary>
    /// Nutrition one animal draws per tick at equilibrium: what it strips while feeding, times the
    /// share of the time it has to be feeding to hold its condition on good ground — which is its
    /// break-even fullness. Zero for anything that does not eat the ground: a hunter is limited by
    /// its prey and a faction by its own systems, neither by this.
    ///
    /// The two terms are the two halves of the design. The consume rate says how hard the ground is
    /// worn while an animal is on it; the break-even says how much of the time it has to be. Only
    /// their product decides how many the world can hold, which is why raising one and lowering the
    /// other leaves the population where it was and changes only how the ground looks.
    /// </summary>
    public static float DemandPerCreature(SpeciesDefinition def)
    {
        float strip = def.CanGraze && def.GrazeConsumeRate > 0f ? def.GrazeConsumeRate
                    : def.FeedConsumeRate > 0f ? def.FeedConsumeRate : 0f;
        return strip * def.BreakEvenFullness;
    }

    /// <summary>Every species that eats tile fertility — the set that contests the same ground.</summary>
    private static List<SpeciesDefinition> GroundFedSpecies()
    {
        var all = new List<SpeciesDefinition>(16);
        foreach (string name in SpeciesRegistry.GetAllNames())
        {
            var def = SpeciesRegistry.Get(name);
            if (DemandPerCreature(def) > 0f) all.Add(def);
        }
        return all;
    }

    /// <summary>
    /// How many of this species the world can feed, or <c>int.MaxValue</c> for a species this
    /// does not limit.
    /// </summary>
    public int CapacityFor(SpeciesDefinition def)
    {
        int id = SpeciesRegistry.GetId(def.Name);
        if (_capacity.TryGetValue(id, out float cached))
            return cached >= int.MaxValue ? int.MaxValue : (int)cached;

        float demand = DemandPerCreature(def);
        float capacity = demand > 0f
            ? OccupancyFraction * SupplyFor(def) / demand
            : int.MaxValue;
        _capacity[id] = capacity;
        return capacity >= int.MaxValue ? int.MaxValue : (int)capacity;
    }

    /// <summary>
    /// Chance a birth is allowed, given how full this species' habitat already is. One below
    /// <see cref="FreeFraction"/> of capacity, falling linearly to zero at it.
    /// </summary>
    public float BirthPass(SpeciesDefinition def, int liveCount)
    {
        int capacity = CapacityFor(def);
        if (capacity == int.MaxValue || capacity <= 0) return 1f;

        float fullness = liveCount / (float)capacity;
        if (fullness <= FreeFraction) return 1f;
        if (fullness >= 1f) return 0f;
        return 1f - (fullness - FreeFraction) / (1f - FreeFraction);
    }

    /// <summary>True when this species eats the fertility of this tile type.</summary>
    private static bool FeedsOn(SpeciesDefinition def, TileType tile)
    {
        if (def.CanGraze && tile.IsGrazeable()) return true;
        return def.FeedConsumeRate > 0f && def.FeedTiles != null && def.FeedTiles.Contains(tile);
    }
}
