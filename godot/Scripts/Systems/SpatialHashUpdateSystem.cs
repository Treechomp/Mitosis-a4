using Mitosis.ECS;
using Mitosis.Utils;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Centralised spatial hash update — runs once per tick after MovementSystem.
/// All entities with Position are synced so downstream systems (Separation,
/// Herding, Hunting, Fleeing) can skip their own update passes.
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
            ref var pos = ref em.Positions[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);
        }
    }
}
