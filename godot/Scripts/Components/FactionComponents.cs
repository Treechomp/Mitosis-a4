using System.Runtime.InteropServices;

namespace Mitosis.Components;

/// <summary>
/// Sectid breeding structure. Nests accumulate food brought by Sectids
/// and spawn new Sectids from larvae slots. Three growth stages,
/// each stage allows one additional larvae slot (max 3).
/// Three successful spawns advance the nest to the next stage.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Nest
{
    public int Stage;           // 1-3 (determines max larvae)
    public int ColonyId;        // Which colony this nest belongs to
    public float FoodStored;    // Accumulated food from Sectid deliveries
    public float FoodPerSpawn;  // Food required to begin spawning one Sectid
    public int SpawnsThisStage; // Spawns completed at current stage (3 to advance)
    public float SpawnTimer0;   // Ticks remaining for larvae slot 0 (-1 = empty, 0 = ready)
    public float SpawnTimer1;
    public float SpawnTimer2;
    public float SpawnDuration; // Ticks to grow one Sectid from larvae

    public Nest(int colonyId, float foodPerSpawn = 30f, float spawnDuration = 200f)
    {
        Stage = 1;
        ColonyId = colonyId;
        FoodStored = 0f;
        FoodPerSpawn = foodPerSpawn;
        SpawnsThisStage = 0;
        SpawnTimer0 = -1f;
        SpawnTimer1 = -1f;
        SpawnTimer2 = -1f;
        SpawnDuration = spawnDuration;
    }

    public readonly int MaxLarvae => Stage; // Stage 1 = 1 slot, Stage 3 = 3 slots
    public readonly bool IsMaxStage => Stage >= 3;
}

/// <summary>
/// Sectid component for carrying food back to a nest.
/// Sectids pick up food from kills and deliver it to the nearest nest.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoodCarrier
{
    public float FoodCarried;   // Current food being carried
    public float MaxCarry;      // Maximum food a single Sectid can hold
    public int TargetNest;      // Entity ID of nest being delivered to (-1 = none)

    public FoodCarrier(float maxCarry = 5f)
    {
        FoodCarried = 0f;
        MaxCarry = maxCarry;
        TargetNest = -1;
    }

    public readonly bool IsCarrying => FoodCarried > 0f;
    public readonly bool IsFull => FoodCarried >= MaxCarry;
}

/// <summary>
/// Shroomer spore - a proto-organism that requires sustained moisture to transform
/// into a full Shroomer. Withers quickly without moisture. Edible by Sectids and herbivores.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Spore
{
    public float MoistureAccumulated; // Moisture gathered over time
    public float TransformThreshold;  // Moisture needed to become a Shroomer
    public float WitherRate;          // Energy damage per tick without moisture
    public float MoistureGainRate;    // Moisture gained per tick on wet tiles
    public int ParentSpeciesId;       // SpeciesId of the parent Shroomer

    public Spore(float transformThreshold = 60f, float witherRate = 2f,
                 float moistureGainRate = 0.5f, int parentSpeciesId = 0)
    {
        MoistureAccumulated = 0f;
        TransformThreshold = transformThreshold;
        WitherRate = witherRate;
        MoistureGainRate = moistureGainRate;
        ParentSpeciesId = parentSpeciesId;
    }

    public readonly bool IsReadyToTransform => MoistureAccumulated >= TransformThreshold;
}

/// <summary>
/// Growth component for entities that change size over time.
/// Used by Shroomers (grow very large) and Faelings (slow growth).
/// Affects Renderable.Size and combat stats.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Growth
{
    public float CurrentScale;  // 1.0 = base size, grows upward
    public float MaxScale;      // Maximum growth multiplier
    public float GrowthRate;    // Scale increase per tick

    public Growth(float maxScale = 3f, float growthRate = 0.00005f)
    {
        CurrentScale = 1f;
        MaxScale = maxScale;
        GrowthRate = growthRate;
    }
}

/// <summary>
/// Crystal spawn point for Faelings. Crystals are indestructible world structures.
/// When a linked Faeling dies, the crystal begins spawning a replacement
/// with half the deceased Faeling's power.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Crystal
{
    public int LinkedFaeling;     // Entity ID of the active Faeling (-1 = spawning)
    public float InheritedPower;  // Power to give the next spawned Faeling
    public int SpawnTimer;        // Ticks until new Faeling spawns (0 = not spawning)
    public int SpawnDelay;        // Total ticks to spawn a new Faeling

    public Crystal(int spawnDelay = 500)
    {
        LinkedFaeling = -1;
        InheritedPower = 0f;
        SpawnTimer = spawnDelay; // Start spawning immediately
        SpawnDelay = spawnDelay;
    }

    public readonly bool IsSpawning => LinkedFaeling < 0 && SpawnTimer > 0;
    public readonly bool HasFaeling => LinkedFaeling >= 0;
}

/// <summary>
/// Faeling power accumulation. Power grows from killing terraformers
/// and restoring tiles. Increases ranged attack damage and growth.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FaelingPower
{
    public float Power;           // Current accumulated power
    public float PowerPerKill;    // Power gained per terraformer killed
    public float PowerPerTile;    // Power gained per tile restored to balance
    public int LinkedCrystal;     // Entity ID of home crystal
    public int KillCount;         // Terraformers killed (for stats)
    public int TilesRestored;     // Tiles restored (for stats)

    public FaelingPower(int linkedCrystal, float powerPerKill = 5f, float powerPerTile = 0.2f)
    {
        Power = 0f;
        PowerPerKill = powerPerKill;
        PowerPerTile = powerPerTile;
        LinkedCrystal = linkedCrystal;
        KillCount = 0;
        TilesRestored = 0;
    }
}

/// <summary>
/// Ranged attack for Faelings. Allows attacking at distance
/// to kite Sectid groups and deal with large Shroomers.
/// Damage scales with FaelingPower.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RangedAttack
{
    public float Range;           // Max attack distance (tiles)
    public float BaseDamage;      // Base damage per hit
    public int Cooldown;          // Ticks between attacks
    public int CurrentCooldown;
    public int TargetEntity;      // Current target (-1 = none)

    public RangedAttack(float range = 8f, float baseDamage = 10f, int cooldown = 30)
    {
        Range = range;
        BaseDamage = baseDamage;
        Cooldown = cooldown;
        CurrentCooldown = 0;
        TargetEntity = -1;
    }

    public readonly bool HasTarget => TargetEntity >= 0;
}
