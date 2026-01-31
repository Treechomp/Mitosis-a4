using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Processes movement for all entities with Position and Velocity.
/// Applies terrain speed modifiers and prevents movement onto non-walkable tiles.
/// </summary>
public sealed class MovementSystem : ISystem
{
    private readonly int _chunkSize;
    private readonly int _worldSizeTiles;
    private readonly WorldManager _worldManager;

    public MovementSystem(int chunkSize, int worldSizeChunks, WorldManager worldManager)
    {
        _chunkSize = chunkSize;
        _worldSizeTiles = chunkSize * worldSizeChunks;
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity;

        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];

            // Skip if no movement
            if (vel.Dx == 0 && vel.Dy == 0)
                continue;

            // Get current tile for speed modifier
            var currentTile = _worldManager.GetTile(pos.X, pos.Y);
            float speedMult = currentTile.GetSpeedMultiplier();

            // Calculate new position with terrain speed modifier
            float newX = pos.X + vel.Dx * speedMult;
            float newY = pos.Y + vel.Dy * speedMult;

            // World bounds clamping
            if (newX < 0) newX = 0;
            if (newY < 0) newY = 0;
            if (newX >= _worldSizeTiles) newX = _worldSizeTiles - 0.01f;
            if (newY >= _worldSizeTiles) newY = _worldSizeTiles - 0.01f;

            // Check if destination is walkable
            var destTile = _worldManager.GetTile(newX, newY);
            if (destTile.IsWalkable())
            {
                // Move to new position
                pos.X = newX;
                pos.Y = newY;
            }
            else
            {
                // Try sliding along X or Y axis separately
                var destTileX = _worldManager.GetTile(newX, pos.Y);
                var destTileY = _worldManager.GetTile(pos.X, newY);

                if (destTileX.IsWalkable())
                    pos.X = newX;
                else if (destTileY.IsWalkable())
                    pos.Y = newY;
                // else: blocked completely, don't move
            }

            // Update chunk position if entity has it
            if (em.HasComponents(entity, ComponentFlags.ChunkPosition))
            {
                em.ChunkPositions[entity].Update(in pos, _chunkSize);
            }
        }
    }
}
