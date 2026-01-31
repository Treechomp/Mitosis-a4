using System.Runtime.InteropServices;
using Godot;

namespace Mitosis.Components;

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

    public Prey(float fleeRange = 8f, float fleeSpeedMultiplier = 1.5f)
    {
        FleeRange = fleeRange;
        FleeSpeedMultiplier = fleeSpeedMultiplier;
        IsFleeing = false;
    }
}

/// <summary>
/// Simple wandering behavior for entities.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Wander
{
    public float Speed;
    public float ChangeDirectionChance;
    public Vector2 CurrentDirection;

    public Wander(float speed = 0.1f, float changeDirectionChance = 0.02f)
    {
        Speed = speed;
        ChangeDirectionChance = changeDirectionChance;
        CurrentDirection = Vector2.Zero;
    }
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
    Circle = 0,
    Triangle = 1,
    Square = 2
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
        GroupAffinity = groupAffinity;
        PreferredGroupSize = preferredGroupSize;
        CohesionStrength = cohesionStrength;
        AlignmentStrength = alignmentStrength;
        LeadershipScore = 0f;
        IsAlerted = false;
    }

    public readonly bool HasGroup => GroupId >= 0;
    public readonly bool IsSocial => Type == SocialType.Herd || Type == SocialType.Pack;
}
