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
        Carrion = 1 << 25,
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
    public readonly Carrion[] Carrions;

    /// <summary>
    /// Per-entity LOD gate flag. True = entity is due for processing this tick.
    /// Set by LODSystem each tick. Entities without SimulationLOD are always due.
    /// Systems read this instead of checking SimulationLOD.TicksUntilUpdate directly,
    /// reducing the LOD gate from 2 checks (HasComponents + array read) to 1 array read.
    /// </summary>
    public readonly bool[] DueThisTick;

    /// <summary>
    /// Position at the start of the current tick. Used by the renderer to interpolate entity
    /// positions between simulation ticks (rendering runs faster than the 20 TPS sim).
    /// </summary>
    public readonly Position[] PrevPositions;

    // Entities created since the last snapshot; their PrevPositions is matched to their spawn
    // position at tick end so newly-spawned entities don't render-interpolate from a stale
    // (recycled) slot.
    private readonly List<int> _newbornsThisTick = new(256);

    /// <summary>
    /// Invoked for an entity the instant before it is destroyed, while its components are still
    /// intact. Lets gameplay react to *any* death from a single place (e.g. leaving a corpse)
    /// without the ECS core depending on gameplay systems. Handlers may create new entities but
    /// must not destroy the dying entity again.
    /// </summary>
    public Action<int>? OnEntityDying;

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
        Carrions = new Carrion[MaxEntities];
        DueThisTick = new bool[MaxEntities];
        PrevPositions = new Position[MaxEntities];
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
        _newbornsThisTick.Add(id);
        return id;
    }

    /// <summary>
    /// Snapshot current positions into <see cref="PrevPositions"/> for render interpolation.
    /// Call at the start of each simulation tick (before systems run).
    /// </summary>
    public void SnapshotPositions()
    {
        Array.Copy(Positions, PrevPositions, _nextId);
        _newbornsThisTick.Clear();
    }

    /// <summary>
    /// Match PrevPositions to current positions for entities spawned during this tick, so they
    /// render at their spawn position instead of streaking from a recycled slot. Call at tick end.
    /// </summary>
    public void FinalizeNewborns()
    {
        for (int i = 0; i < _newbornsThisTick.Count; i++)
        {
            int id = _newbornsThisTick[i];
            PrevPositions[id] = Positions[id];
        }
        _newbornsThisTick.Clear();
    }

    /// <summary>
    /// Destroy an entity.
    /// </summary>
    public void DestroyEntity(int entityId)
    {
        if (entityId < 0 || entityId >= MaxEntities || !_alive[entityId])
            return;

        OnEntityDying?.Invoke(entityId);

        _alive[entityId] = false;
        _componentFlags[entityId] = ComponentFlags.None;
        _freeIds.Enqueue(entityId);
        _entityCount--;
    }

    /// <summary>
    /// Destroy multiple entities in batch. More efficient than calling DestroyEntity
    /// in a loop (single count update, inlined bounds checks).
    /// </summary>
    public void DestroyEntities(List<int> entityIds)
    {
        int destroyed = 0;
        foreach (int entityId in entityIds)
        {
            if (entityId >= 0 && entityId < MaxEntities && _alive[entityId])
            {
                OnEntityDying?.Invoke(entityId);
                _alive[entityId] = false;
                _componentFlags[entityId] = ComponentFlags.None;
                _freeIds.Enqueue(entityId);
                destroyed++;
            }
        }
        _entityCount -= destroyed;
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
    /// Upper bound of allocated entity IDs (exclusive). Useful for raw loops.
    /// </summary>
    public int NextId => _nextId;

    /// <summary>
    /// Zero-allocation struct enumerator for iterating entities matching component flags.
    /// Works with foreach via duck typing — no IEnumerable heap allocation.
    /// </summary>
    public struct EntityQuery
    {
        private readonly bool[] _alive;
        private readonly ComponentFlags[] _flags;
        private readonly ComponentFlags _required;
        private readonly int _count;
        private int _current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal EntityQuery(bool[] alive, ComponentFlags[] flags, int count, ComponentFlags required)
        {
            _alive = alive;
            _flags = flags;
            _count = count;
            _required = required;
            _current = -1;
        }

        public int Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _current;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (++_current < _count)
            {
                if (_alive[_current] && (_flags[_current] & _required) == _required)
                    return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityQuery GetEnumerator() => this;
    }

    /// <summary>
    /// Iterate over all alive entities with specific components (zero-allocation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityQuery Query(ComponentFlags requiredFlags)
    {
        return new EntityQuery(_alive, _componentFlags, _nextId, requiredFlags);
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
    /// Zero-allocation struct enumerator for iterating all alive entities.
    /// </summary>
    public struct AllEntityQuery
    {
        private readonly bool[] _alive;
        private readonly int _count;
        private int _current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal AllEntityQuery(bool[] alive, int count)
        {
            _alive = alive;
            _count = count;
            _current = -1;
        }

        public int Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _current;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (++_current < _count)
            {
                if (_alive[_current])
                    return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AllEntityQuery GetEnumerator() => this;
    }

    /// <summary>
    /// Iterate over all alive entities (zero-allocation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AllEntityQuery AllEntities()
    {
        return new AllEntityQuery(_alive, _nextId);
    }
}
