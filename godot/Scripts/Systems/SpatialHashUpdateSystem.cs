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

    public SpatialHashUpdateSystem(SpatialHash spatialHash)
    {
        _spatialHash = spatialHash;
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
        }
    }
}
