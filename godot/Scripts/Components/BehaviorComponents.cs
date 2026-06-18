using System.Runtime.InteropServices;
using Godot;

namespace Mitosis.Components;

/// <summary>
/// Primary hunting tactic for a predator species.
/// Determines movement patterns, coordination, and engagement behavior.
/// </summary>
public enum HuntingTactic : byte
{
    Solo = 0,             // Direct chase, no coordination (Fox, Bear, Hawk)
    PackCoordinated = 1,  // Leader/flanker/disruptor roles with phases (Wolf, Boar)
    Swarm = 2,            // Colony-wide rush, no retreat, count all nearby same-species (Sectid)
    Ambush = 3            // Stealth accumulation → pounce burst (Crocodile, Jaguar, Snake, Scorpion)
}

/// <summary>
/// Pack hunting role for coordinated attacks.
/// </summary>
public enum PackRole : byte
{
    None = 0,       // Not in a hunting pack or solitary
    Leader = 1,     // Monitors positioning, triggers convergence
    Flanker = 2,    // Circles behind prey to cut off escape routes
    Disruptor = 3   // Rush/retreat to scatter the herd (max 1-2 per pack)
}

/// <summary>
/// Pack hunting phase for coordinated attack timing.
/// </summary>
public enum PackPhase : byte
{
    Idle = 0,        // Not actively hunting as pack
    Positioning = 1, // Moving to surround positions
    Disrupting = 2,  // Disruptors rush in; flankers tighten encirclement
    Converging = 3,  // All-in kill rush — triggered by leader
    Retreating = 4   // Disruptors backing off between rushes
}

/// <summary>
/// Marks entity as a predator that hunts prey.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Predator
{
    public float HuntRange;      // Detection range for finding prey
    public float AttackRange;    // Distance at which attacks can land (smaller = must overlap, larger = can jab)
    public float AttackPower;
    public int AttackCooldown;
    public int CurrentCooldown;
    public int TargetEntity;     // -1 means no target
    public PackRole Role;        // Role in pack hunting
    public PackPhase Phase;      // Current phase of pack attack
    public int PhaseTimer;       // Ticks remaining in current phase
    public float Stealth;        // 0-1: accumulated stealth level (ambush predators)
    public int PounceTimer;      // Ticks remaining in pounce burst (0 = not pouncing)

    // === Target viability tracking ===
    // Used to abandon prey we can't actually bring down (too fast to hit, out-healing our
    // damage, or counterattacking too hard) and switch to a viable target instead of fixating.
    public int HuntTicks;          // Ticks engaged with the current target (reset on new target)
    public float TargetLastEnergy; // Target's energy at last progress checkpoint
    public float SelfStartEnergy;  // Our own energy when this hunt began (damage-taken check)
    public int AvoidTarget;        // Entity recently given up on — don't re-acquire (-1 = none)
    public int AvoidTicks;         // Ticks remaining on the avoid suppression

    // === Defensive rally ===
    // Who hit us last and for how long we remember it — drives the "call to action" where a
    // pack/swarm member summons nearby groupmates to mob its attacker instead of being picked off.
    public int LastAttacker;       // Entity that most recently attacked us (-1 = none)
    public int LastAttackedTicks;  // Ticks remaining that we'll rally against LastAttacker

    public Predator(
        float huntRange = 5f,
        float attackRange = 0.8f,  // Default: must be close but not fully overlapping
        float attackPower = 25f,
        int attackCooldown = 20)
    {
        HuntRange = huntRange;
        AttackRange = attackRange;
        AttackPower = attackPower;
        AttackCooldown = attackCooldown;
        CurrentCooldown = 0;
        TargetEntity = -1;
        Role = PackRole.None;
        Phase = PackPhase.Idle;
        PhaseTimer = 0;
        Stealth = 0f;
        PounceTimer = 0;
        HuntTicks = 0;
        TargetLastEnergy = 0f;
        SelfStartEnergy = 0f;
        AvoidTarget = -1;
        AvoidTicks = 0;
        LastAttacker = -1;
        LastAttackedTicks = 0;
    }

    public readonly bool HasTarget => TargetEntity >= 0;
}

/// <summary>
/// Marks entity as prey that flees from predators.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Prey
{
    public float FleeRange;
    public float FleeSpeedMultiplier;
    public bool IsFleeing;
    public float Stamina;   // 0..1 flee-burst reserve: drains while fleeing, recovers at rest

    public Prey(float fleeRange = 8f, float fleeSpeedMultiplier = 1.5f)
    {
        FleeRange = fleeRange;
        FleeSpeedMultiplier = fleeSpeedMultiplier;
        IsFleeing = false;
        Stamina = 1f;
    }
}

/// <summary>
/// Simple wandering behavior for entities.
/// Includes roaming state for long-distance travel to increase species encounters.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Wander
{
    public float Speed;
    public float ChangeDirectionChance;
    public Vector2 CurrentDirection;

    // Roaming: long-distance directed travel
    public float RoamTargetX;       // World-space target (0,0 = no target)
    public float RoamTargetY;
    public int RoamCooldown;        // Ticks until next roam check
    public float RoamSpeedMultiplier; // Speed boost while roaming (default 1.5x)

    public Wander(float speed = 0.1f, float changeDirectionChance = 0.02f)
    {
        Speed = speed;
        ChangeDirectionChance = changeDirectionChance;
        CurrentDirection = Vector2.Zero;
        RoamTargetX = 0f;
        RoamTargetY = 0f;
        RoamCooldown = 0;
        RoamSpeedMultiplier = 1.5f;
    }

    public readonly bool IsRoaming => RoamTargetX != 0f || RoamTargetY != 0f;
}

/// <summary>
/// Rendering information for an entity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Renderable
{
    public Color Color;
    public float Size;
    public ShapeType Shape;

    public Renderable(Color color, float size = 8f, ShapeType shape = ShapeType.Circle)
    {
        Color = color;
        Size = size;
        Shape = shape;
    }
}

public enum ShapeType : byte
{
    Circle = 0,      // Deer, Elk, Frog, Camel, Monkey
    Triangle = 1,    // Wolf, Fox, Arctic Fox
    Square = 2,      // Faeling (crystal guardian)
    Diamond = 3,     // Boar, Musk Ox, Turtle, Lizard (tough/armored)
    Star = 4,        // Sectid (6-armed star, oscillating rotation)
    Chevron = 5,     // Hawk, Parrot (bird V-silhouette)
    FishShape = 6,   // Fish (body + forked tail)
    Fin = 7,         // Shark (dorsal fin)
    Teardrop = 8,    // Penguin, Tapir, Rabbit (rounded bottom, pointed top)
    Crescent = 9,    // Scorpion (curved pincers)
    Serpent = 10,    // Snake (S-curve)
    Mushroom = 11,   // Shroomer (cap on stem)
    Fangs = 12,      // Bear, Polar Bear, Jaguar, Crocodile (wide jaw)
    Carcass = 13,    // Corpses — a flat splayed disc lying on the ground (clearly not a creature)
}

/// <summary>
/// Social behavior type - determines how an entity interacts with others of its species.
/// </summary>
public enum SocialType : byte
{
    Solitary = 0,       // Prefers to be alone, avoids others
    Territorial = 1,    // Maintains distance, defends area
    Herd = 2,           // Groups with others for safety (herbivores)
    Pack = 3            // Coordinates with others for hunting (predators)
}

/// <summary>
/// Social behavior component for herding and pack dynamics.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Social
{
    public SocialType Type;
    public int GroupId;              // -1 = no group, otherwise group identifier
    public int RecognizedLeader;     // Entity ID of the leader this entity follows (-1 = none)
    public int LeaderLostTicks;      // Ticks since leader was last in range (for leadership transfer)
    public float GroupAffinity;      // 0-1: How strongly attracted to group (0 = lone, 1 = highly social)
    public float PreferredGroupSize; // Ideal number of nearby same-species
    public float CohesionStrength;   // How strongly pulled toward group center
    public float AlignmentStrength;  // How strongly matches group velocity
    public float LeadershipScore;    // Higher = more likely to lead (based on age/size)
    public bool IsAlerted;           // Has been warned of danger by group member

    public Social(
        SocialType type = SocialType.Herd,
        float groupAffinity = 0.5f,
        float preferredGroupSize = 5f,
        float cohesionStrength = 0.02f,
        float alignmentStrength = 0.01f)
    {
        Type = type;
        GroupId = -1;
        RecognizedLeader = -1;
        LeaderLostTicks = 0;
        GroupAffinity = groupAffinity;
        PreferredGroupSize = preferredGroupSize;
        CohesionStrength = cohesionStrength;
        AlignmentStrength = alignmentStrength;
        LeadershipScore = 0f;
        IsAlerted = false;
    }

    public readonly bool HasGroup => GroupId >= 0;
    public readonly bool HasLeader => RecognizedLeader >= 0;
    public readonly bool IsSocial => Type == SocialType.Herd || Type == SocialType.Pack;
}

/// <summary>
/// Terraform influence direction for faction species.
/// </summary>
public enum TerraformDirection : sbyte
{
    Drier = -1,     // Sectids: push tiles toward Arid
    Balanced = 0,   // Faelings: push tiles toward Grass
    Wetter = 1      // Shroomers: push tiles toward Wetland
}

/// <summary>
/// Enables a creature to gradually modify terrain tiles over time.
/// Part of the faction species mechanic: Shroomers moisturize, Sectids dry, Faelings balance.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Terraform
{
    public TerraformDirection Direction;
    public float Radius;            // Tile radius of influence
    public float Strength;          // Probability per attempt of changing a tile (0-1)
    public int Cooldown;            // Ticks between terraform attempts
    public int CurrentCooldown;

    public Terraform(
        TerraformDirection direction = TerraformDirection.Balanced,
        float radius = 2f,
        float strength = 0.02f,
        int cooldown = 10)
    {
        Direction = direction;
        Radius = radius;
        Strength = strength;
        Cooldown = cooldown;
        CurrentCooldown = 0;
    }
}
