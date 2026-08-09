using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Centralised spatial hash update — runs once per tick after MovementSystem.
/// LOD-gated: only updates entities that actually moved this tick (i.e. entities
/// that were "due" for processing). Entities skipped by LOD gating did not move,
/// so their spatial hash entry is already correct.
/// CollisionSystem still does inline updates because it mutates positions.
/// </summary>
public sealed class SpatialHashUpdateSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly SpatialHash? _predatorHash;

    public SpatialHashUpdateSystem(SpatialHash spatialHash, SpatialHash? predatorHash = null)
    {
        _spatialHash = spatialHash;
        _predatorHash = predatorHash;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip entities that didn't move this tick
            if (!em.DueThisTick[entity])
                continue;

            ref var pos = ref em.Positions[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);

            // Mirror active threats into the predator-only index so prey scan a short list.
            // A dormant Sectid frightens nobody, so it drops out until it wakes.
            if (_predatorHash != null)
            {
                bool threat = em.HasComponents(entity, ComponentFlags.Predator)
                              && !(em.HasComponents(entity, ComponentFlags.FoodCarrier)
                                   && em.FoodCarriers[entity].IsHibernating);
                if (threat) _predatorHash.Update(entity, pos.X, pos.Y);
                else _predatorHash.Remove(entity);
            }
        }
    }
}
