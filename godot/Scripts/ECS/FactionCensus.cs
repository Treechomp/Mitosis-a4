using System.Collections.Generic;
using Mitosis.Components;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.ECS;

/// <summary>
/// Where each faction actually is, globally — totals plus a coarse per-chunk presence grid.
///
/// WHY THIS EXISTS. A Faeling keeper's job is to check whoever is WINNING, and it was deciding
/// that from a 40-tile sense radius. Every colony and every bloom looks locally dominant from
/// inside it, so the only way to make a keeper reliably see the world was to put keepers
/// everywhere — which is exactly what happened: crystal count was derived from sense coverage,
/// giving 132 of them. Presence bought perception, and presence is also force. 132 immortal
/// raiders then destroyed 44 of 46 Sectid nests inside 6,000 ticks.
///
/// A census separates the two. Knowing where the problem is costs one entity pass on a slow
/// cadence; GETTING there is a mobility question (crystal-to-crystal travel), and neither needs
/// more bodies. Twelve keepers that can see the whole map and move across it are the faction the
/// design describes; 132 that can each see a fortieth of it are an army.
///
/// Refreshed by a periodic full pass rather than maintained incrementally from
/// ChunkPosition.Update's changed-flag. The delta route is cheaper per tick but needs three
/// correct call sites (spawn, death, chunk transition) against one that already exists —
/// CrystalSystem was doing this O(n) pass for its global counts anyway, and bucketing by chunk
/// while it walks costs an array increment per entity.
/// </summary>
public sealed class FactionCensus
{
    private readonly int _chunkSize;
    private readonly int _worldSizeChunks;

    // Keyed by SPECIES ID rather than by a hand-written list of factions, so a new faction is
    // counted, ranked and targeted without any system naming it.
    private readonly Dictionary<int, int> _global = new();
    private readonly Dictionary<int, int[]> _byChunk = new();

    public FactionCensus(int chunkSize, int worldSizeChunks)
    {
        _chunkSize = chunkSize;
        _worldSizeChunks = worldSizeChunks;
    }

    /// <summary>One pass over all entities: global totals and per-chunk presence, per faction.</summary>
    public void Refresh(EntityManager em)
    {
        foreach (var key in new List<int>(_global.Keys)) _global[key] = 0;
        foreach (var grid in _byChunk.Values) System.Array.Clear(grid);

        foreach (int e in em.Query(ComponentFlags.Species | ComponentFlags.Position))
        {
            // Bodies only: a spore is not yet a Shroomer, and a structure is not a member.
            if (em.HasComponents(e, ComponentFlags.Spore)) continue;
            if (em.HasComponents(e, ComponentFlags.Structure)) continue;
            if (em.HasComponents(e, ComponentFlags.Nest)) continue;
            if (em.HasComponents(e, ComponentFlags.Crystal)) continue;

            int speciesId = em.Species[e].SpeciesId;
            var def = SpeciesData.SpeciesRegistry.GetById(speciesId);
            if (def == null || def.Diet != SpeciesData.DietType.Terraformer) continue;

            ref var pos = ref em.Positions[e];
            int cx = (int)pos.X / _chunkSize;
            int cy = (int)pos.Y / _chunkSize;
            if ((uint)cx >= (uint)_worldSizeChunks || (uint)cy >= (uint)_worldSizeChunks) continue;

            _global.TryGetValue(speciesId, out int g);
            _global[speciesId] = g + 1;
            if (!_byChunk.TryGetValue(speciesId, out var grid))
                _byChunk[speciesId] = grid = new int[_worldSizeChunks * _worldSizeChunks];
            grid[cy * _worldSizeChunks + cx]++;
        }
    }

    /// <summary>Living members of one faction worldwide.</summary>
    public int GlobalCount(int speciesId) => _global.TryGetValue(speciesId, out int n) ? n : 0;

    /// <summary>
    /// Species id of the faction winning worldwide, or -1 for "no one" when the lead is inside
    /// <paramref name="marginNumerator"/>/<paramref name="marginDenominator"/> (default 1.5x) or
    /// below <paramref name="minPresence"/>. Pass <paramref name="excludeSpeciesId"/> to keep a
    /// faction from ranking itself.
    ///
    /// This is the judgement the keeper used to make from what it could see around it. Making it
    /// globally means a keeper standing in an empty desert still knows a bloom is running away
    /// with the map on the far side of the world.
    /// </summary>
    public int DominantFactionId(int minPresence, int excludeSpeciesId = -1,
        int marginNumerator = 3, int marginDenominator = 2)
    {
        int leadId = -1, lead = 0, trail = 0;
        foreach (var (id, n) in _global)
        {
            if (id == excludeSpeciesId) continue;
            if (n > lead) { trail = lead; lead = n; leadId = id; }
            else if (n > trail) { trail = n; }
        }
        if (leadId < 0 || lead < minPresence) return -1;              // nobody is winning
        if (lead * marginDenominator < trail * marginNumerator) return -1;   // too close to call
        return leadId;
    }

    /// <summary>
    /// Centre of the chunk holding the most of a faction, and how many are there. This is the
    /// "which region" half of the keeper's decision — coarse on purpose, because a keeper is
    /// choosing where in the world to be, not which creature to shoot.
    /// </summary>
    public bool TryHottestChunk(int speciesId, out float worldX, out float worldY, out int count)
    {
        worldX = worldY = 0f;
        count = 0;
        if (!_byChunk.TryGetValue(speciesId, out var grid)) return false;

        int bestIdx = -1;
        for (int i = 0; i < grid.Length; i++)
        {
            if (grid[i] > count) { count = grid[i]; bestIdx = i; }
        }
        if (bestIdx < 0 || count == 0) return false;

        int cx = bestIdx % _worldSizeChunks;
        int cy = bestIdx / _worldSizeChunks;
        worldX = (cx + 0.5f) * _chunkSize;
        worldY = (cy + 0.5f) * _chunkSize;
        return true;
    }
}
