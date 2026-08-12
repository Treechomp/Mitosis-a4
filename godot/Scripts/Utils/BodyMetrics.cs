namespace Mitosis.Utils;

/// <summary>
/// Shared conversion from a creature's render size to the physical body radius the simulation
/// treats it as occupying.
///
/// This existed only inside CollisionSystem, which meant combat measured distance differently from
/// physics: collision pushes two bodies apart to the SUM OF THEIR RADII, while an attack tested
/// raw centre-to-centre distance against AttackRange. Any target bulky enough that the sum exceeds
/// its attacker's reach became literally unhittable — the attacker shoved it around forever
/// without landing a blow. A full-grown Shroomer (render size 36 → radius 1.125) plus a Jaguar
/// (0.375) is 1.5 tiles apart at closest, against a 1.0 reach; Wolves lost the ability at Shroomer
/// scale 2, Bears and Jaguars at scale 3. Only Sectids, with a long 1.3 reach on a tiny body,
/// could still bite one — which quietly made them the sole predator of mature blooms.
/// </summary>
public static class BodyMetrics
{
    /// <summary>Fraction of render size that counts as the physical body radius.</summary>
    public const float CollisionRadiusScale = 0.5f;

    /// <summary>Pixels per world tile; render sizes are authored in pixels.</summary>
    public const float DefaultTileSize = 16f;

    /// <summary>Body radius in TILES for a creature of the given render size.</summary>
    public static float Radius(float renderSize, float tileSize = DefaultTileSize)
        => renderSize * CollisionRadiusScale / tileSize;
}
