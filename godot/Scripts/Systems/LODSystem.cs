using System;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Updates LOD levels for entities based on distance from player.
/// Must run first each tick to set up LOD state for other systems.
/// </summary>
public sealed class LODSystem : ISystem
{
    private float _playerX;
    private float _playerY;
    private int _playerEntity = -1;

    public void SetPlayerEntity(int entity)
    {
        _playerEntity = entity;
    }

    public void Process(EntityManager em)
    {
        // Get player position
        if (_playerEntity >= 0 && em.IsAlive(_playerEntity) &&
            em.HasComponents(_playerEntity, ComponentFlags.Position))
        {
            ref var playerPos = ref em.Positions[_playerEntity];
            _playerX = playerPos.X;
            _playerY = playerPos.Y;
        }

        // Update LOD for all entities with SimulationLOD component
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.SimulationLOD;

        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            ref var lod = ref em.SimulationLODs[entity];

            // Calculate distance to player
            lod.DistanceToPlayer = MathUtils.Distance(_playerX, _playerY, pos.X, pos.Y);

            // Determine LOD level
            var newLevel = SimulationLOD.GetLevelForDistance(lod.DistanceToPlayer);

            // If level changed, reset timer
            if (newLevel != lod.Level)
            {
                lod.Level = newLevel;
                lod.TicksUntilUpdate = 0;
            }

            // Decrement timer
            if (lod.TicksUntilUpdate > 0)
            {
                lod.TicksUntilUpdate--;
            }
        }
    }

    /// <summary>
    /// Check if an entity should be updated this tick based on its LOD.
    /// </summary>
    public static bool ShouldUpdate(in SimulationLOD lod)
    {
        return lod.TicksUntilUpdate <= 0;
    }

    /// <summary>
    /// Mark that an entity was updated, resetting its timer.
    /// </summary>
    public static void MarkUpdated(ref SimulationLOD lod)
    {
        lod.TicksUntilUpdate = SimulationLOD.GetTickInterval(lod.Level);
    }
}
