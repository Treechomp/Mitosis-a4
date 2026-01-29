using Mitosis.Components;
using Mitosis.ECS;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Processes movement for all entities with Position and Velocity.
/// </summary>
public sealed class MovementSystem : ISystem
{
    private readonly int _chunkSize;
    private readonly int _worldSizeTiles;

    public MovementSystem(int chunkSize, int worldSizeChunks)
    {
        _chunkSize = chunkSize;
        _worldSizeTiles = chunkSize * worldSizeChunks;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity;

        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];

            // Apply velocity
            pos.X += vel.Dx;
            pos.Y += vel.Dy;

            // World bounds clamping
            if (pos.X < 0) pos.X = 0;
            if (pos.Y < 0) pos.Y = 0;
            if (pos.X >= _worldSizeTiles) pos.X = _worldSizeTiles - 0.01f;
            if (pos.Y >= _worldSizeTiles) pos.Y = _worldSizeTiles - 0.01f;

            // Update chunk position if entity has it
            if (em.HasComponents(entity, ComponentFlags.ChunkPosition))
            {
                em.ChunkPositions[entity].Update(in pos, _chunkSize);
            }
        }
    }
}
