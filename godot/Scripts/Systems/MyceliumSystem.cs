using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Shroomer mycelium: the per-tile territory a bloom holds, and the HEART that territory sustains.
///
/// WHY A HEART IS NOT A POINT. A nest and a crystal are objects — walk up, hit them, they break.
/// A Shroomer colony has no such object; it is a spread of bodies over ground it has made wet.
/// Modelling its anchor as one more hit-point pool would have made all three factions the same
/// puzzle. Instead a heart lives or dies on two conditions measured over its RADIUS:
///
///   · the fraction of tiles still above SporeMoistureThreshold must exceed MyceliumMoistureFloor
///   · living Shroomers inside the radius must exceed MyceliumShroomerFloor
///
/// Fail either and the heart bleeds. That gives the other factions two genuinely different attacks
/// on the same target: a Sectid or Faeling terraformer can DRY the ground from outside the bloom
/// and never trade a blow, or an army can go in and kill the bodies. The first is cheap, slow and
/// safe; the second is fast and expensive. Both are legitimate, and which one a faction can afford
/// is what makes the three-way war asymmetric.
///
/// The mycelium FIELD itself (Chunk._mycelium) is the visible territory rather than the survival
/// test: it thickens under living Shroomers and thins on its own, marks the ground a bloom has
/// actually taken, and is the requirement for founding a heart in the first place. Keeping the
/// survival test on MOISTURE is deliberate — moisture is what terraform attacks, so the drying
/// route acts on the heart directly instead of through a second derived quantity.
///
/// All thresholds are SpeciesDefinition data; this file names no species.
/// </summary>
public sealed class MyceliumSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly List<int> _nearby = new(64);
    private readonly List<int> _destroyed = new(4);
    private readonly List<(float x, float y, int speciesId)> _pendingHearts = new(4);

    /// <summary>
    /// Ticks between territory passes. Mycelium moves on a geological cadence next to a creature's,
    /// and every step here is a tile scan — growth over a spread radius, decay across loaded
    /// chunks, and a heart's whole disc. Running it on a slow interval and scaling the rates by
    /// that interval costs nothing in fidelity and keeps it off the per-tick budget.
    /// </summary>
    private const int TerritoryInterval = 20;

    /// <summary>Tiles sampled per axis across a heart's disc. Keeps the survival scan bounded on
    /// a large radius while still measuring the whole territory rather than a few probes.</summary>
    private const int HeartSampleStride = 2;

    /// <summary>Mycelium a tile must carry to count as claimed ground when founding a heart.</summary>
    private const float ClaimedMycelium = 0.25f;

    /// <summary>
    /// Minimum distance between two hearts of the same faction. Set from GameManager's export of
    /// the same name; the default matches it so an unwired caller behaves identically.
    ///
    /// THIS IS THE HEART COUNT CONTROL, and it is spacing rather than a cap on purpose. Heart
    /// count used to be whatever fell out of MyceliumRadius (14 tiles), which produced ~300 on the
    /// standard world — the same "a number came out of a formula" shape that gave the Faelings 132
    /// crystals. A cap would hide a Shroomer faction that has run away with the map behind a
    /// constant; spacing lets the count stay proportional to how much ground the faction actually
    /// holds, so a player who has been outplayed sees more territory rather than the same number
    /// of hearts. MyceliumRadius still governs the SIZE of a territory and is unchanged.
    /// </summary>
    public float MinHeartSpacing { get; set; } = 50f;

    /// <summary>Fraction of a would-be heart's radius that must already be claimed ground.</summary>
    private const float FoundClaimFraction = 0.15f;

    private int _tick;

    public MyceliumSystem(WorldManager worldManager, SpatialHash spatialHash)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
    }

    public void Process(EntityManager em)
    {
        _tick++;
        bool territoryTick = _tick % TerritoryInterval == 0;

        if (territoryTick)
        {
            GrowTerritory(em);
            _worldManager.DecayMycelium(TerritoryInterval);
        }

        UpdateHearts(em, territoryTick);

        if (territoryTick)
            FoundHearts(em);

        if (_destroyed.Count > 0)
        {
            em.DestroyEntities(_destroyed);
            _destroyed.Clear();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Territory
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every living Shroomer thickens the mycelium under and around it. Growth scale matters: a
    /// mature bloom holds ground a sprout only touches, which is what lets territory read as the
    /// colony's age rather than merely its headcount.
    /// </summary>
    private void GrowTerritory(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species;

        foreach (int entity in em.Query(required))
        {
            // A spore has not colonised anything yet; a structure is not a body.
            if (em.HasComponents(entity, ComponentFlags.Spore)) continue;
            if (em.HasComponents(entity, ComponentFlags.Structure)) continue;

            var def = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
            if (def == null || def.MyceliumRadius <= 0f || def.MyceliumSpreadRadius <= 0f)
                continue;

            ref var pos = ref em.Positions[entity];
            float scale = em.HasComponents(entity, ComponentFlags.Growth)
                ? MathF.Max(1f, em.Growths[entity].CurrentScale) : 1f;

            float spread = def.MyceliumSpreadRadius;
            float amount = def.MyceliumGrowthRate * TerritoryInterval;
            int r = (int)MathF.Ceiling(spread);
            float spreadSq = spread * spread;

            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    float dSq = dx * dx + dy * dy;
                    if (dSq > spreadSq) continue;
                    // Densest underfoot, tapering to the edge of the spread — a bloom's hold on
                    // ground is strongest where its bodies actually stand.
                    float falloff = 1f - MathF.Sqrt(dSq) / spread;
                    _worldManager.AddMycelium(pos.X + dx, pos.Y + dy, amount * falloff * scale);
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Hearts
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Test each heart's territory and bleed or heal it. Sampling is on the slow cadence; the
    /// health change is scaled to it, so the drain rate on SpeciesDefinition is per-tick as
    /// documented regardless of how often this actually runs.
    /// </summary>
    private void UpdateHearts(EntityManager em, bool territoryTick)
    {
        if (!territoryTick) return;

        foreach (int entity in em.Query(ComponentFlags.Structure | ComponentFlags.Position))
        {
            ref var structure = ref em.Structures[entity];
            if (structure.Kind != StructureKind.MyceliumHeart || structure.IsDestroyed) continue;

            var def = SpeciesRegistry.GetById(structure.FactionSpeciesId);
            if (def == null || def.MyceliumRadius <= 0f) continue;

            ref var pos = ref em.Positions[entity];
            float moistFraction = MoistTileFraction(pos.X, pos.Y, def);
            int shroomers = CountFactionCreatures(em, pos.X, pos.Y, def.MyceliumRadius,
                structure.FactionSpeciesId);

            bool wetEnough = moistFraction >= def.MyceliumMoistureFloor;
            bool peopled = shroomers > def.MyceliumShroomerFloor;

            // Report BOTH floors every pass, not only on failure. Which one is close to giving way
            // is the question a balance pass actually asks — "the heart is fine" and "the heart is
            // one dry tile from bleeding" look identical if only failures are logged.
            if (EcosystemLogger.DecisionLoggingFor(structure.FactionSpeciesId))
            {
                EcosystemLogger.Instance!.LogDecision(structure.FactionSpeciesId, entity,
                    pos.X, pos.Y, "mycelium", wetEnough && peopled ? "heart_holding" : "heart_failing",
                    FormattableString.Invariant(
                        $"moist={moistFraction:F2}/{def.MyceliumMoistureFloor:F2};shroomers={shroomers}/{def.MyceliumShroomerFloor};hp={structure.Health:F0}"));
            }

            if (wetEnough && peopled)
            {
                // Territory intact: recover, but never past full.
                structure.Health = MathF.Min(structure.MaxHealth,
                    structure.Health + def.MyceliumHeartRegenRate * TerritoryInterval);
                continue;
            }

            // Bleeding. No attacker is named — the ground itself is what failed, and attributing
            // it to whichever terraformer happened to be nearest would be a guess.
            float drain = def.MyceliumHeartDrainRate * TerritoryInterval;
            Structures.Damage(em, entity, drain, attacker: -1, _destroyed);
        }
    }

    /// <summary>
    /// Found a heart where a mature bloom has taken enough ground and there is no heart already
    /// claiming that radius. One per radius: a heart is the centre of a territory, and two
    /// overlapping centres describe nothing.
    /// </summary>
    private void FoundHearts(EntityManager em)
    {
        _pendingHearts.Clear();

        foreach (int entity in em.Query(ComponentFlags.Position | ComponentFlags.Species
                                        | ComponentFlags.Growth))
        {
            if (em.HasComponents(entity, ComponentFlags.Spore)) continue;
            if (em.HasComponents(entity, ComponentFlags.Structure)) continue;

            int speciesId = em.Species[entity].SpeciesId;
            var def = SpeciesRegistry.GetById(speciesId);
            if (def == null || def.MyceliumRadius <= 0f) continue;
            if (em.Growths[entity].CurrentScale < def.MyceliumFoundScale) continue;

            ref var pos = ref em.Positions[entity];
            // Spacing, not overlap: MinHeartSpacing is far wider than MyceliumRadius, so this
            // subsumes the old "no two centres inside one radius" test. Max() keeps that older
            // guarantee intact if the spacing is ever configured below the radius.
            float spacing = MathF.Max(MinHeartSpacing, def.MyceliumRadius);
            if (HeartWithinSpacing(em, _spatialHash, _nearby, pos.X, pos.Y, spacing, speciesId))
                continue;
            if (ClaimedFraction(pos.X, pos.Y, def.MyceliumRadius) < FoundClaimFraction) continue;
            // The same test worldgen seeding uses — a heart founded onto ground that cannot hold
            // it would start bleeding on its very first territory pass.
            if (!TerritorySupportsHeart(_worldManager, _spatialHash, em, pos.X, pos.Y, def, speciesId))
                continue;

            _pendingHearts.Add((pos.X, pos.Y, speciesId));
            break; // at most one founding per pass — the next pass re-tests against what exists
        }

        foreach (var (x, y, speciesId) in _pendingHearts)
            SpawnHeart(em, x, y, speciesId, _spatialHash);
    }

    /// <summary>
    /// Create a mycelium heart. Public so worldgen can seed one under an established bloom rather
    /// than making every run wait for a Shroomer to grow into founding scale.
    /// </summary>
    public static int SpawnHeart(EntityManager em, float x, float y, int speciesId,
                                  SpatialHash? spatialHash = null)
    {
        var def = SpeciesRegistry.GetById(speciesId);
        if (def == null || def.MyceliumRadius <= 0f) return -1;
        if (!em.HasRoomForEntity) return -1;

        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        // No Species component: a heart is a structure, and Species would charge it to the faction
        // population budget and offer it to every system that reasons about creatures.
        em.Structures[entity] = new Structure(StructureKind.MyceliumHeart,
            def.MyceliumHeartHealth, speciesId);
        em.AddComponent(entity, ComponentFlags.Structure);

        // Pale fungal bulb, larger than a Shroomer so a territory's centre is findable.
        em.Renderables[entity] = new Renderable(new Color(0.75f, 0.55f, 0.85f), 14f, ShapeType.Square);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Index it immediately rather than waiting for SpatialHashUpdateSystem's next pass, so
        // the spacing test below can never miss a heart founded earlier in this same tick.
        spatialHash?.Update(entity, x, y);

        EcosystemLogger.Instance?.LogStructureEvent("heart_founded",
            def.Name, entity, x, y, $"radius={def.MyceliumRadius:F0}");

        return entity;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Territory measurements
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Would a heart placed here SURVIVE — i.e. does the territory already meet both floors?
    ///
    /// Shared between the founding path and worldgen seeding, and it has to be: seeding hearts
    /// without it put 76 of 85 anchors on ground that could not hold them, and every one bled out
    /// by t=819 with cause `environment`. From the outside that is indistinguishable from the
    /// Shroomer faction being destroyed, which is exactly the kind of thing a whole-game invariant
    /// is for. A heart is something a bloom EARNS by having already wet the ground around it.
    /// </summary>
    public static bool TerritorySupportsHeart(WorldManager world, SpatialHash spatialHash,
        EntityManager em, float x, float y, SpeciesDefinition def, int speciesId)
    {
        if (def.MyceliumRadius <= 0f) return false;
        if (MoistFraction(world, x, y, def) < def.MyceliumMoistureFloor) return false;
        return CountFaction(em, spatialHash, x, y, def.MyceliumRadius, speciesId)
               > def.MyceliumShroomerFloor;
    }

    /// <summary>Fraction of tiles in the heart's disc still wet enough for fungus to hold.</summary>
    private float MoistTileFraction(float cx, float cy, SpeciesDefinition def)
        => MoistFraction(_worldManager, cx, cy, def);

    private static float MoistFraction(WorldManager world, float cx, float cy, SpeciesDefinition def)
    {
        int r = (int)MathF.Ceiling(def.MyceliumRadius);
        float rSq = def.MyceliumRadius * def.MyceliumRadius;
        int total = 0, wet = 0;

        for (int dy = -r; dy <= r; dy += HeartSampleStride)
        {
            for (int dx = -r; dx <= r; dx += HeartSampleStride)
            {
                if (dx * dx + dy * dy > rSq) continue;
                total++;
                // The same substrate wetness SporeSystem's drought check reads, so "dry enough to
                // kill a Shroomer" and "dry enough to kill its heart" are one threshold.
                var tile = world.GetTile(cx + dx, cy + dy);
                if (tile.SubstrateMoisture() >= def.SporeMoistureThreshold) wet++;
            }
        }
        return total > 0 ? (float)wet / total : 0f;
    }

    /// <summary>Fraction of tiles in the radius already claimed by mycelium.</summary>
    private float ClaimedFraction(float cx, float cy, float radius)
    {
        int r = (int)MathF.Ceiling(radius);
        float rSq = radius * radius;
        int total = 0, claimed = 0;

        for (int dy = -r; dy <= r; dy += HeartSampleStride)
        {
            for (int dx = -r; dx <= r; dx += HeartSampleStride)
            {
                if (dx * dx + dy * dy > rSq) continue;
                total++;
                if (_worldManager.GetMycelium(cx + dx, cy + dy) >= ClaimedMycelium) claimed++;
            }
        }
        return total > 0 ? (float)claimed / total : 0f;
    }

    /// <summary>Living creatures of one faction within a radius (structures and spores excluded).</summary>
    private int CountFactionCreatures(EntityManager em, float x, float y, float radius, int speciesId)
        => CountFaction(em, _spatialHash, x, y, radius, speciesId);

    private static int CountFaction(EntityManager em, SpatialHash spatialHash,
        float x, float y, float radius, int speciesId)
    {
        var _nearby = new List<int>(64);
        spatialHash.QueryRadius(x, y, radius, _nearby);
        float rSq = radius * radius;
        int count = 0;
        foreach (int other in _nearby)
        {
            if (!em.IsAlive(other)) continue;
            if (!em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Position)) continue;
            if (em.HasComponents(other, ComponentFlags.Spore)) continue;
            if (em.HasComponents(other, ComponentFlags.Structure)) continue;
            if (em.Species[other].SpeciesId != speciesId) continue;
            ref var op = ref em.Positions[other];
            if (MathUtils.DistanceSquared(x, y, op.X, op.Y) <= rSq) count++;
        }
        return count;
    }

    /// <summary>
    /// Is there already a living heart of this faction within <paramref name="spacing"/>?
    ///
    /// Shared by the founding path and worldgen seeding, which must apply the SAME spacing — two
    /// different rules for placing the same structure is how the seeded and organic heart networks
    /// would drift apart.
    ///
    /// Queried through the spatial hash rather than scanning the entity array: this runs once per
    /// candidate bloom, and at a few thousand Shroomers a full-array scan per candidate is a
    /// per-tick cost proportional to their product. Hearts are indexed like anything else with a
    /// Position (LODSystem marks structures due every tick) and <see cref="SpawnHeart"/> also
    /// registers one the moment it is created.
    /// </summary>
    public static bool HeartWithinSpacing(EntityManager em, SpatialHash spatialHash,
        List<int> scratch, float x, float y, float spacing, int speciesId)
    {
        scratch.Clear();
        spatialHash.QueryRadius(x, y, spacing, scratch);
        float sq = spacing * spacing;
        foreach (int e in scratch)
        {
            if (!em.HasComponents(e, ComponentFlags.Structure | ComponentFlags.Position)) continue;
            ref var s = ref em.Structures[e];
            if (s.Kind != StructureKind.MyceliumHeart || s.IsDestroyed) continue;
            if (s.FactionSpeciesId != speciesId) continue;
            ref var p = ref em.Positions[e];
            if (MathUtils.DistanceSquared(x, y, p.X, p.Y) <= sq) return true;
        }
        return false;
    }
}
