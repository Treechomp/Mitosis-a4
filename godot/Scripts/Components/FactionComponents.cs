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

    // Hibernation: a hungry Sectid that finds no prey for a prolonged time retreats to its
    // nest and goes dormant (greatly reduced metabolism) instead of wandering off to starve.
    // It wakes when huntable prey strays within range — a defensive, ambush-from-the-nest posture
    // that keeps a minimal viable colony alive through prey troughs.
    public bool IsHibernating;  // Dormant near nest, low metabolism, waiting for prey
    public int NoFoodTicks;     // Consecutive ticks hungry with no prey detected nearby
    // Consecutive ticks fed but with nothing to hunt. A colony whose workers only ever came home
    // to starve was never seen at rest: the hunger gate meant a well-fed swarm roamed the map
    // forever. This is the off-duty counter — sated Sectids with no prey in reach go camp.
    public int IdleTicks;

    public FoodCarrier(float maxCarry = 5f)
    {
        FoodCarried = 0f;
        MaxCarry = maxCarry;
        TargetNest = -1;
        IsHibernating = false;
        NoFoodTicks = 0;
        IdleTicks = 0;
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

    // Elder area denial. An elder attacked by anything floods its surroundings with a lethal
    // bloom for EnrageTicks, then must recharge for EnrageCooldown before it can do so again.
    // The recharge IS the counterplay: it is the window in which a patient swarm gets its bites
    // in, so an elder is brought down by persistence rather than by a single brave charge.
    public int EnrageTicks;     // Remaining ticks of the heightened denial zone (0 = dormant)
    public int EnrageCooldown;  // Ticks until it can rage again (0 = ready)

    public Growth(float maxScale = 3f, float growthRate = 0.00005f)
    {
        CurrentScale = 1f;
        MaxScale = maxScale;
        GrowthRate = growthRate;
        EnrageTicks = 0;
        EnrageCooldown = 0;
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
    /// <summary>Sentinel: linked Faeling was aggregated into statistical sim (not dead).</summary>
    public const int FAELING_AGGREGATED = -2;

    public int LinkedFaeling;     // Entity ID of the active Faeling (-1 = spawning, -2 = aggregated)
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

    public readonly bool IsSpawning => LinkedFaeling == -1 && SpawnTimer > 0;
    public readonly bool HasFaeling => LinkedFaeling >= 0;
    public readonly bool IsFaelingAggregated => LinkedFaeling == FAELING_AGGREGATED;
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

    // Keeper sense (CrystalSystem.SenseDominance): which rival faction is locally
    // over-dominant and where its concentration sits. Drives the keeper's attack
    // preference and the siege patrol (WanderSystem).
    public int KeeperFaction;       // (int)SpeciesType of the dominant faction; 0 = balanced/none
    public float KeeperHotspotX;    // Centroid of the dominant faction's local members
    public float KeeperHotspotY;
    public int KeeperSenseCooldown; // Ticks until the next dominance scan

    /// <summary>Ticks until this keeper may relocate to another crystal (CrystalSystem).</summary>
    public int TravelCooldown;

    public FaelingPower(int linkedCrystal, float powerPerKill = 5f, float powerPerTile = 0.2f)
    {
        Power = 0f;
        PowerPerKill = powerPerKill;
        PowerPerTile = powerPerTile;
        LinkedCrystal = linkedCrystal;
        KillCount = 0;
        TilesRestored = 0;
        KeeperFaction = 0;
        KeeperHotspotX = 0f;
        KeeperHotspotY = 0f;
        KeeperSenseCooldown = 0;
        TravelCooldown = 0;
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

/// <summary>What kind of faction structure this is. Each has its own destruction consequence.</summary>
public enum StructureKind : byte
{
    /// <summary>Sectid nest — a colony's spawn point.</summary>
    Nest = 0,
    /// <summary>Faeling crystal — the anchor exactly one Faeling is bound to.</summary>
    Crystal = 1,
    /// <summary>Shroomer mycelium heart — the centre of a fungal territory.</summary>
    MyceliumHeart = 2,
}

/// <summary>
/// A faction structure: a fixed, destructible objective rather than a creature.
///
/// Structures used to be unattackable by construction. <c>HuntingSystem.IsEligiblePrey</c> requires
/// <c>ComponentFlags.Prey</c> (swarm hunters get a widened <c>Energy | Species</c> test), and a nest
/// carried neither; a crystal's health was the sentinel <c>Energy(999999, 999999)</c>. So no entity
/// in the game could damage either one, and the three-way faction war had nothing to contest except
/// individual creatures — which makes a faction's strength a function of its population, exactly
/// what elite factions like the Faelings can never win.
///
/// HEALTH LIVES HERE, NOT IN <c>Energy</c>. Structures no longer carry an Energy component at all.
/// Energy is a creature's stamina — regenerating, drained by starvation, restored by rest — and
/// giving that to a building meant every system that touches Energy had to be taught to skip
/// structures by flag. One authority, and the skip is structural rather than remembered.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Structure
{
    public float Health;
    public float MaxHealth;

    /// <summary>Species id of the faction that owns it — what makes a structure "enemy" or not.</summary>
    public int FactionSpeciesId;

    public StructureKind Kind;

    /// <summary>
    /// Ticks remaining on the "we are under attack" signal, refreshed on every hit. This is what a
    /// defender reads to know its colony is threatened — see
    /// <c>SpeciesDefinition.StructureRetaliationBias</c>, which lets a species answer a siege by
    /// besieging back rather than by mobbing whatever is closest.
    /// </summary>
    public int UnderAttackTicks;

    /// <summary>
    /// Who last hit it (-1 = none / environmental). A structure cannot defend itself, so this is
    /// what lets its faction's creatures be pointed at the ATTACKER — never at the building, which
    /// is the whole reason it is recorded rather than inferred.
    /// </summary>
    public int LastAttacker;

    public Structure(StructureKind kind, float maxHealth, int factionSpeciesId)
    {
        Kind = kind;
        Health = maxHealth;
        MaxHealth = maxHealth;
        FactionSpeciesId = factionSpeciesId;
        UnderAttackTicks = 0;
        LastAttacker = -1;
    }

    public readonly bool IsDestroyed => Health <= 0f;
    public readonly float HealthFraction => MaxHealth > 0f ? Health / MaxHealth : 0f;
    public readonly bool IsUnderAttack => UnderAttackTicks > 0;
}

/// <summary>
/// A creature's current siege objective — the enemy structure it is attacking, if any.
///
/// Separate from <c>Predator.TargetEntity</c> on purpose: a structure is not food. It has no body
/// mass to gate against, no nutrition payoff to score, it never flees, and killing it must not
/// drop a carcass. Routing it through the prey path would have meant threading "is this actually a
/// building" through mass gates, payoff scoring, pack roles, stealth and the carrion hook — see
/// SiegeSystem for the full argument.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Siege
{
    public int TargetStructure;   // Entity id of the structure under attack (-1 = none)
    public int CurrentCooldown;   // Ticks until the next blow lands

    public Siege(int target = -1)
    {
        TargetStructure = target;
        CurrentCooldown = 0;
    }

    public readonly bool HasTarget => TargetStructure >= 0;
}
