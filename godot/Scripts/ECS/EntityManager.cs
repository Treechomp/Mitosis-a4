using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mitosis.Components;

namespace Mitosis.ECS;

/// <summary>
/// High-performance entity manager using Structure of Arrays (SoA) layout.
/// Entities are indices into component arrays.
/// </summary>
public sealed class EntityManager
{
    // Maximum supported entities
    public const int MaxEntities = 16384;

    // Entity management
    private readonly bool[] _alive;
    private readonly Queue<int> _freeIds;
    private int _nextId;
    private int _entityCount;

    // Component flags (which components an entity has)
    [Flags]
    public enum ComponentFlags : uint
    {
        None = 0,
        Position = 1 << 0,
        Velocity = 1 << 1,
        ChunkPosition = 1 << 2,
        Species = 1 << 3,
        Hunger = 1 << 4,
        Energy = 1 << 5,
        Age = 1 << 6,
        Reproduction = 1 << 7,
        Predator = 1 << 8,
        Prey = 1 << 9,
        Wander = 1 << 10,
        Renderable = 1 << 11,
        SimulationLOD = 1 << 12,
        Social = 1 << 13,
        TerrainDiscomfort = 1 << 14,
        Fear = 1 << 15,
        Terraform = 1 << 16,
        Nest = 1 << 17,
        FoodCarrier = 1 << 18,
        Spore = 1 << 19,
        Growth = 1 << 20,
        Crystal = 1 << 21,
        FaelingPower = 1 << 22,
        RangedAttack = 1 << 23,
        VenomEffect = 1 << 24,
    }

    private readonly ComponentFlags[] _componentFlags;

    // Component arrays (Structure of Arrays for cache efficiency)
    public readonly Position[] Positions;
    public readonly Velocity[] Velocities;
    public readonly ChunkPosition[] ChunkPositions;
    public readonly Species[] Species;
    public readonly Hunger[] Hungers;
    public readonly Energy[] Energies;
    public readonly Age[] Ages;
    public readonly Reproduction[] Reproductions;
    public readonly Predator[] Predators;
    public readonly Prey[] Preys;
    public readonly Wander[] Wanders;
    public readonly Renderable[] Renderables;
    public readonly SimulationLOD[] SimulationLODs;
    public readonly Social[] Socials;
    public readonly TerrainDiscomfort[] TerrainDiscomforts;
    public readonly Fear[] Fears;
    public readonly Terraform[] Terraforms;
    public readonly Nest[] Nests;
    public readonly FoodCarrier[] FoodCarriers;
    public readonly Spore[] Spores;
    public readonly Growth[] Growths;
    public readonly Crystal[] Crystals;
    public readonly FaelingPower[] FaelingPowers;
    public readonly RangedAttack[] RangedAttacks;
    public readonly VenomEffect[] VenomEffects;

    public EntityManager()
    {
        _alive = new bool[MaxEntities];
        _freeIds = new Queue<int>(256);
        _nextId = 0;
        _entityCount = 0;
        _componentFlags = new ComponentFlags[MaxEntities];

        // Allocate component arrays
        Positions = new Position[MaxEntities];
        Velocities = new Velocity[MaxEntities];
        ChunkPositions = new ChunkPosition[MaxEntities];
        Species = new Species[MaxEntities];
        Hungers = new Hunger[MaxEntities];
        Energies = new Energy[MaxEntities];
        Ages = new Age[MaxEntities];
        Reproductions = new Reproduction[MaxEntities];
        Predators = new Predator[MaxEntities];
        Preys = new Prey[MaxEntities];
        Wanders = new Wander[MaxEntities];
        Renderables = new Renderable[MaxEntities];
        SimulationLODs = new SimulationLOD[MaxEntities];
        Socials = new Social[MaxEntities];
        TerrainDiscomforts = new TerrainDiscomfort[MaxEntities];
        Fears = new Fear[MaxEntities];
        Terraforms = new Terraform[MaxEntities];
        Nests = new Nest[MaxEntities];
        FoodCarriers = new FoodCarrier[MaxEntities];
        Spores = new Spore[MaxEntities];
        Growths = new Growth[MaxEntities];
        Crystals = new Crystal[MaxEntities];
        FaelingPowers = new FaelingPower[MaxEntities];
        RangedAttacks = new RangedAttack[MaxEntities];
        VenomEffects = new VenomEffect[MaxEntities];
    }

    public int EntityCount => _entityCount;

    /// <summary>
    /// Create a new entity. Returns entity ID.
    /// </summary>
    public int CreateEntity()
    {
        int id;
        if (_freeIds.Count > 0)
        {
            id = _freeIds.Dequeue();
        }
        else
        {
            if (_nextId >= MaxEntities)
                throw new InvalidOperationException("Maximum entity count reached");
            id = _nextId++;
        }

        _alive[id] = true;
        _componentFlags[id] = ComponentFlags.None;
        _entityCount++;
        return id;
    }

    /// <summary>
    /// Destroy an entity.
    /// </summary>
    public void DestroyEntity(int entityId)
    {
        if (entityId < 0 || entityId >= MaxEntities || !_alive[entityId])
            return;

        _alive[entityId] = false;
        _componentFlags[entityId] = ComponentFlags.None;
        _freeIds.Enqueue(entityId);
        _entityCount--;
    }

    /// <summary>
    /// Check if an entity is alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(int entityId)
    {
        return entityId >= 0 && entityId < MaxEntities && _alive[entityId];
    }

    /// <summary>
    /// Check if an entity has specific components.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasComponents(int entityId, ComponentFlags flags)
    {
        return (_componentFlags[entityId] & flags) == flags;
    }

    /// <summary>
    /// Add a component flag to an entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddComponent(int entityId, ComponentFlags flag)
    {
        _componentFlags[entityId] |= flag;
    }

    /// <summary>
    /// Remove a component flag from an entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponent(int entityId, ComponentFlags flag)
    {
        _componentFlags[entityId] &= ~flag;
    }

    /// <summary>
    /// Get component flags for an entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentFlags GetComponents(int entityId)
    {
        return _componentFlags[entityId];
    }

    /// <summary>
    /// Iterate over all alive entities with specific components.
    /// </summary>
    public IEnumerable<int> Query(ComponentFlags requiredFlags)
    {
        for (int i = 0; i < _nextId; i++)
        {
            if (_alive[i] && (_componentFlags[i] & requiredFlags) == requiredFlags)
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// Get count of entities matching a query.
    /// </summary>
    public int QueryCount(ComponentFlags requiredFlags)
    {
        int count = 0;
        for (int i = 0; i < _nextId; i++)
        {
            if (_alive[i] && (_componentFlags[i] & requiredFlags) == requiredFlags)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Iterate over all alive entities.
    /// </summary>
    public IEnumerable<int> AllEntities()
    {
        for (int i = 0; i < _nextId; i++)
        {
            if (_alive[i])
                yield return i;
        }
    }
}
