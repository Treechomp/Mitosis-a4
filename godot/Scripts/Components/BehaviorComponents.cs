using System.Runtime.InteropServices;
using Godot;

namespace Mitosis.Components;

/// <summary>
/// Marks entity as a predator that hunts prey.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Predator
{
    public float HuntRange;
    public float AttackPower;
    public int AttackCooldown;
    public int CurrentCooldown;
    public int TargetEntity;  // -1 means no target

    public Predator(
        float huntRange = 5f,
        float attackPower = 25f,
        int attackCooldown = 20)
    {
        HuntRange = huntRange;
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
