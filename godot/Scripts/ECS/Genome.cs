using System;
using Mitosis.Components;
using Mitosis.SpeciesData;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.ECS;

/// <summary>
/// The heritable traits of one individual, by name. Adding a trait here is the only place a new
/// heritable quantity has to be declared: gathering, applying, blending, mutating and reporting all
/// walk this enum.
/// </summary>
public enum Trait : byte
{
    MaxLifespan, MaturityAge,
    ReproHungerThreshold, ReproEnergyThreshold, ReproHungerCost, ReproEnergyCost,
    ReproCooldown, SpawnRadius,
    MaxHunger, HungerDecayRate,
    WanderSpeed, DirectionChangeChance,
    BodySize,
    DiscomfortThreshold, DiscomfortDecayRate, GrazingPressure,
    TerraformRadius, TerraformStrength, TerraformCooldown,
    FleeRange, FleeSpeedMultiplier,
    FearThreshold, FearMax, FearAccumulationRate, FearDecayRate, FearVigilanceDecay,
    HuntRange, AttackRange, AttackPower, AttackCooldown,
    GroupAffinity, PreferredGroupSize, CohesionStrength, AlignmentStrength,
}

/// <summary>
/// An individual's realised trait values, moved from one entity to another.
///
/// WHAT THIS IS NOT. It is not where the values live. They stay on the components that use them —
/// <c>Age</c>, <c>Reproduction</c>, <c>Predator</c>, <c>Prey</c>, <c>Wander</c>, <c>Social</c> and
/// the rest — because moving them into one component would rewrite every read site in fifteen
/// system files to buy nothing. This is transport: one gather, one apply, and inheritance stops
/// being a special case per spawn path.
///
/// WHY IT EXISTS. Every individual already differed from its species mean across all of these —
/// <c>SpeciesDefinition.FounderSpread</c> has always been applied at spawn — and none of that
/// difference reached its offspring, because reproduction spawned from the species definition. So
/// there was variation and no heredity, and therefore nothing that could be selected for, drift,
/// or be written into by a player. That is the gap this closes.
///
/// THE LEASH. A child is not a copy. Its value is pulled slightly back toward the species value
/// (<see cref="RegressionToMean"/>), then perturbed by that trait's own mutation rate, then clamped
/// to a hard band around the species value (<see cref="TraitBand"/>) that no lineage may leave.
/// Regression alone is self-limiting and is what quantitative genetics actually looks like; the
/// band is the guarantee that a long chain of runs cannot produce a roster nobody designed.
/// </summary>
public sealed class Genome
{
    public const int TraitCount = (int)Trait.AlignmentStrength + 1;

    /// <summary>
    /// How far a child's inherited value is pulled back toward its species value before mutation.
    /// 0 is pure descent and 1 discards the parent entirely.
    /// </summary>
    public const float RegressionToMean = 0.10f;

    /// <summary>
    /// Hard band around the species value, as a fraction of it. No lineage may leave it however
    /// long selection pushes in one direction.
    /// </summary>
    public const float TraitBand = 0.5f;

    /// <summary>
    /// Default per-generation mutation, as a fraction of the species value, drawn uniformly in
    /// ±rate. Chosen so that the spread a population settles at under heredity is about the spread
    /// it had without it: with regression r and uniform mutation m, the stationary standard
    /// deviation is m ÷ (√3 · √(1−(1−r)²)), which at r = 0.10 and m = 0.08 lands near the ±20%
    /// founder spread's own standard deviation. Heredity therefore changes what varies, not how
    /// much — a population that regressed harder than it mutated would quietly become uniform.
    /// </summary>
    public const float DefaultMutationRate = 0.08f;

    private readonly float[] _values = new float[TraitCount];

    public float this[Trait t]
    {
        get => _values[(int)t];
        set => _values[(int)t] = value;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Gather and apply — the only two methods that know about components
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Read an individual's realised values off its components.</summary>
    public static Genome From(EntityManager em, int entity)
    {
        var g = new Genome();

        if (em.HasComponents(entity, ComponentFlags.Age))
        {
            ref var a = ref em.Ages[entity];
            g[Trait.MaxLifespan] = a.MaxLifespan;
            g[Trait.MaturityAge] = a.MaturityAge;
        }
        if (em.HasComponents(entity, ComponentFlags.Reproduction))
        {
            ref var r = ref em.Reproductions[entity];
            g[Trait.ReproHungerThreshold] = r.HungerThreshold;
            g[Trait.ReproEnergyThreshold] = r.EnergyThreshold;
            g[Trait.ReproHungerCost] = r.HungerCost;
            g[Trait.ReproEnergyCost] = r.EnergyCost;
            g[Trait.ReproCooldown] = r.Cooldown;
            g[Trait.SpawnRadius] = r.SpawnRadius;
        }
        if (em.HasComponents(entity, ComponentFlags.Hunger))
        {
            ref var h = ref em.Hungers[entity];
            g[Trait.MaxHunger] = h.Max;
            g[Trait.HungerDecayRate] = h.DecayRate;
        }
        if (em.HasComponents(entity, ComponentFlags.Wander))
        {
            ref var w = ref em.Wanders[entity];
            g[Trait.WanderSpeed] = w.Speed;
            g[Trait.DirectionChangeChance] = w.ChangeDirectionChance;
        }
        if (CarriesTrait(em, entity, Trait.BodySize))
            g[Trait.BodySize] = em.Renderables[entity].Size;
        if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
        {
            ref var d = ref em.TerrainDiscomforts[entity];
            g[Trait.DiscomfortThreshold] = d.Threshold;
            g[Trait.DiscomfortDecayRate] = d.DecayRate;
            g[Trait.GrazingPressure] = d.GrazingPressure;
        }
        if (em.HasComponents(entity, ComponentFlags.Terraform))
        {
            ref var t = ref em.Terraforms[entity];
            g[Trait.TerraformRadius] = t.Radius;
            g[Trait.TerraformStrength] = t.Strength;
            g[Trait.TerraformCooldown] = t.Cooldown;
        }
        if (em.HasComponents(entity, ComponentFlags.Prey))
        {
            ref var p = ref em.Preys[entity];
            g[Trait.FleeRange] = p.FleeRange;
            g[Trait.FleeSpeedMultiplier] = p.FleeSpeedMultiplier;
        }
        if (em.HasComponents(entity, ComponentFlags.Fear))
        {
            ref var f = ref em.Fears[entity];
            g[Trait.FearThreshold] = f.Threshold;
            g[Trait.FearMax] = f.Max;
            g[Trait.FearAccumulationRate] = f.AccumulationRate;
            g[Trait.FearDecayRate] = f.DecayRate;
            g[Trait.FearVigilanceDecay] = f.VigilanceDecay;
        }
        if (em.HasComponents(entity, ComponentFlags.Predator))
        {
            ref var p = ref em.Predators[entity];
            g[Trait.HuntRange] = p.HuntRange;
            g[Trait.AttackRange] = p.AttackRange;
            g[Trait.AttackPower] = p.AttackPower;
            g[Trait.AttackCooldown] = p.AttackCooldown;
        }
        if (em.HasComponents(entity, ComponentFlags.Social))
        {
            ref var s = ref em.Socials[entity];
            g[Trait.GroupAffinity] = s.GroupAffinity;
            g[Trait.PreferredGroupSize] = s.PreferredGroupSize;
            g[Trait.CohesionStrength] = s.CohesionStrength;
            g[Trait.AlignmentStrength] = s.AlignmentStrength;
        }
        return g;
    }

    /// <summary>
    /// Write these values into an entity the factory has already assembled. Only components the
    /// entity actually carries are touched, so a genome gathered from a predator can be applied to
    /// a species that is not one without inventing a component for it.
    /// </summary>
    public void ApplyTo(EntityManager em, int entity)
    {
        if (em.HasComponents(entity, ComponentFlags.Age))
        {
            ref var a = ref em.Ages[entity];
            a.MaxLifespan = (int)MathF.Round(this[Trait.MaxLifespan]);
            a.MaturityAge = (int)MathF.Round(this[Trait.MaturityAge]);
        }
        if (em.HasComponents(entity, ComponentFlags.Reproduction))
        {
            ref var r = ref em.Reproductions[entity];
            r.HungerThreshold = this[Trait.ReproHungerThreshold];
            r.EnergyThreshold = this[Trait.ReproEnergyThreshold];
            r.HungerCost = this[Trait.ReproHungerCost];
            r.EnergyCost = this[Trait.ReproEnergyCost];
            r.Cooldown = (int)MathF.Round(this[Trait.ReproCooldown]);
            r.SpawnRadius = this[Trait.SpawnRadius];
        }
        if (em.HasComponents(entity, ComponentFlags.Hunger))
        {
            ref var h = ref em.Hungers[entity];
            h.Max = this[Trait.MaxHunger];
            h.DecayRate = this[Trait.HungerDecayRate];
            // The factory filled Current against the max it rolled, which this has just replaced.
            if (h.Current > h.Max) h.Current = h.Max;
        }
        if (em.HasComponents(entity, ComponentFlags.Wander))
        {
            ref var w = ref em.Wanders[entity];
            w.Speed = this[Trait.WanderSpeed];
            w.ChangeDirectionChance = this[Trait.DirectionChangeChance];
        }
        if (CarriesTrait(em, entity, Trait.BodySize) && this[Trait.BodySize] > 0f)
            em.Renderables[entity].Size = this[Trait.BodySize];
        if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
        {
            ref var d = ref em.TerrainDiscomforts[entity];
            d.Threshold = this[Trait.DiscomfortThreshold];
            d.DecayRate = this[Trait.DiscomfortDecayRate];
            d.GrazingPressure = this[Trait.GrazingPressure];
        }
        if (em.HasComponents(entity, ComponentFlags.Terraform))
        {
            ref var t = ref em.Terraforms[entity];
            t.Radius = this[Trait.TerraformRadius];
            t.Strength = this[Trait.TerraformStrength];
            t.Cooldown = (int)MathF.Round(this[Trait.TerraformCooldown]);
        }
        if (em.HasComponents(entity, ComponentFlags.Prey))
        {
            ref var p = ref em.Preys[entity];
            p.FleeRange = this[Trait.FleeRange];
            p.FleeSpeedMultiplier = this[Trait.FleeSpeedMultiplier];
        }
        if (em.HasComponents(entity, ComponentFlags.Fear))
        {
            ref var f = ref em.Fears[entity];
            f.Threshold = this[Trait.FearThreshold];
            f.Max = this[Trait.FearMax];
            f.AccumulationRate = this[Trait.FearAccumulationRate];
            f.DecayRate = this[Trait.FearDecayRate];
            f.VigilanceDecay = this[Trait.FearVigilanceDecay];
        }
        if (em.HasComponents(entity, ComponentFlags.Predator))
        {
            ref var p = ref em.Predators[entity];
            p.HuntRange = this[Trait.HuntRange];
            p.AttackRange = this[Trait.AttackRange];
            p.AttackPower = this[Trait.AttackPower];
            p.AttackCooldown = (int)MathF.Round(this[Trait.AttackCooldown]);
        }
        if (em.HasComponents(entity, ComponentFlags.Social))
        {
            ref var s = ref em.Socials[entity];
            // A solitary animal carries zeroes here by construction; inheriting a herder's values
            // would hand it a group behaviour its social type does not use.
            if (s.IsSocial)
            {
                s.GroupAffinity = this[Trait.GroupAffinity];
                s.PreferredGroupSize = this[Trait.PreferredGroupSize];
                s.CohesionStrength = this[Trait.CohesionStrength];
                s.AlignmentStrength = this[Trait.AlignmentStrength];
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Descent
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Weighted mix of two genomes: 0 returns <paramref name="a"/>, 1 returns <paramref name="b"/>.
    /// Used by the Sectid nest, where the brood resembles whoever filled the larder rather than any
    /// one parent.
    /// </summary>
    public static Genome Blend(Genome a, Genome b, float weightOfB)
    {
        float w = Math.Clamp(weightOfB, 0f, 1f);
        var g = new Genome();
        for (int i = 0; i < TraitCount; i++)
            g._values[i] = a._values[i] * (1f - w) + b._values[i] * w;
        return g;
    }

    /// <summary>
    /// The genome of a child of this one: regressed toward the species value, mutated per trait,
    /// then clamped to the band. A trait whose mutation rate is zero still regresses, so a field
    /// meant to stay species-level converges on it rather than freezing wherever a founder landed.
    /// </summary>
    public Genome Inherit(Random rng, SpeciesDefinition species)
    {
        var child = new Genome();
        for (int i = 0; i < TraitCount; i++)
        {
            var trait = (Trait)i;
            float speciesValue = SpeciesValue(species, trait);
            if (speciesValue == 0f)
            {
                // Nothing to regress toward and no scale to mutate against — the species does not
                // use this trait (a herbivore's attack power, a non-terraformer's radius).
                child._values[i] = _values[i];
                continue;
            }

            float value = _values[i] + (speciesValue - _values[i]) * RegressionToMean;

            float rate = MutationRate(species, trait);
            if (rate > 0f)
                value += speciesValue * rate * (float)(rng.NextDouble() * 2 - 1);

            float low = speciesValue * (1f - TraitBand);
            float high = speciesValue * (1f + TraitBand);
            child._values[i] = Math.Clamp(value, MathF.Min(low, high), MathF.Max(low, high));
        }
        return child;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The species values these are measured against
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// What a species' definition says this trait should be. The one place traits are mapped onto
    /// <see cref="SpeciesDefinition"/>, used for regression, for the band, and as the baseline the
    /// drift measurement is read against.
    /// </summary>
    public static float SpeciesValue(SpeciesDefinition s, Trait t) => t switch
    {
        Trait.MaxLifespan => s.MaxLifespan,
        Trait.MaturityAge => s.MaturityAge,
        Trait.ReproHungerThreshold => s.ReproHungerThreshold,
        Trait.ReproEnergyThreshold => s.ReproEnergyThreshold,
        Trait.ReproHungerCost => s.ReproHungerCost,
        Trait.ReproEnergyCost => s.ReproEnergyCost,
        Trait.ReproCooldown => s.ReproCooldown,
        Trait.SpawnRadius => s.SpawnRadius,
        Trait.MaxHunger => s.MaxHunger,
        Trait.HungerDecayRate => s.HungerDecayRate,
        Trait.WanderSpeed => s.BaseWanderSpeed,
        Trait.DirectionChangeChance => s.DirectionChangeChance,
        Trait.BodySize => s.BaseSize,
        Trait.DiscomfortThreshold => s.DiscomfortThreshold,
        Trait.DiscomfortDecayRate => s.DiscomfortDecayRate,
        Trait.GrazingPressure => s.GrazingPressure,
        Trait.TerraformRadius => s.TerraformRadius,
        Trait.TerraformStrength => s.TerraformStrength,
        Trait.TerraformCooldown => s.TerraformCooldown,
        Trait.FleeRange => s.FleeRange,
        Trait.FleeSpeedMultiplier => s.FleeSpeedMultiplier,
        Trait.FearThreshold => s.FearThreshold,
        Trait.FearMax => s.FearMax,
        Trait.FearAccumulationRate => s.FearAccumulationRate,
        Trait.FearDecayRate => s.FearDecayRate,
        Trait.FearVigilanceDecay => s.FearVigilanceDecay,
        Trait.HuntRange => s.HuntRange,
        Trait.AttackRange => s.AttackRange,
        Trait.AttackPower => s.AttackPower,
        Trait.AttackCooldown => s.AttackCooldown,
        Trait.GroupAffinity => s.GroupAffinity,
        Trait.PreferredGroupSize => s.PreferredGroupSize,
        Trait.CohesionStrength => s.CohesionStrength,
        Trait.AlignmentStrength => s.AlignmentStrength,
        _ => 0f,
    };

    /// <summary>
    /// Which component carries a trait. An entity that does not have it has no value for that
    /// trait rather than a value of zero — a Shroomer grown from a spore carries no Reproduction
    /// component at all, and averaging its absent thresholds in as zeroes reads as a 45% collapse
    /// that never happened.
    /// </summary>
    public static bool CarriesTrait(EntityManager em, int entity, Trait t)
    {
        // A grower's body size belongs to GrowthSystem, which scales it over a lifetime. Reading
        // it as a trait would have a spore inherit its parent's *grown* size and start there.
        if (t == Trait.BodySize && em.HasComponents(entity, ComponentFlags.Growth))
            return false;
        return em.HasComponents(entity, ComponentOf(t));
    }

    private static ComponentFlags ComponentOf(Trait t) => t switch
    {
        Trait.MaxLifespan or Trait.MaturityAge => ComponentFlags.Age,
        Trait.ReproHungerThreshold or Trait.ReproEnergyThreshold or Trait.ReproHungerCost
            or Trait.ReproEnergyCost or Trait.ReproCooldown or Trait.SpawnRadius
            => ComponentFlags.Reproduction,
        Trait.MaxHunger or Trait.HungerDecayRate => ComponentFlags.Hunger,
        Trait.WanderSpeed or Trait.DirectionChangeChance => ComponentFlags.Wander,
        Trait.BodySize => ComponentFlags.Renderable,
        Trait.DiscomfortThreshold or Trait.DiscomfortDecayRate or Trait.GrazingPressure
            => ComponentFlags.TerrainDiscomfort,
        Trait.TerraformRadius or Trait.TerraformStrength or Trait.TerraformCooldown
            => ComponentFlags.Terraform,
        Trait.FleeRange or Trait.FleeSpeedMultiplier => ComponentFlags.Prey,
        Trait.FearThreshold or Trait.FearMax or Trait.FearAccumulationRate or Trait.FearDecayRate
            or Trait.FearVigilanceDecay => ComponentFlags.Fear,
        Trait.HuntRange or Trait.AttackRange or Trait.AttackPower or Trait.AttackCooldown
            => ComponentFlags.Predator,
        _ => ComponentFlags.Social,
    };

    /// <summary>
    /// How far this trait may move per generation, as a fraction of the species value. A species'
    /// own table wins where it has an entry; otherwise the default, except for the traits below
    /// which are held at species level deliberately.
    ///
    /// The three terraform traits are zero because they are how a faction reshapes the world, and
    /// the world-deviation invariant is asserted against them. Drift there would move a gate's
    /// subject rather than a creature's behaviour, and faction balance is a separate decision from
    /// this one.
    /// </summary>
    public static float MutationRate(SpeciesDefinition s, Trait t)
    {
        if (s.TraitMutationRates != null && s.TraitMutationRates.TryGetValue(t, out float own))
            return MathF.Max(0f, own);

        return t switch
        {
            Trait.TerraformRadius or Trait.TerraformStrength or Trait.TerraformCooldown => 0f,
            _ => DefaultMutationRate,
        };
    }
}
