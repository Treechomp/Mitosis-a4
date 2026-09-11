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

    /// <summary>
    /// Which traits this genome actually carries a value for — one bit per trait, set in
    /// <see cref="From"/> exactly where a component was found.
    ///
    /// WHY IT EXISTS, and why a later simplification must not remove it. Without it, an absent
    /// trait and a trait that is genuinely zero are the same float, and the only way to tell them
    /// apart is to guess from the value. Guessing from the value is what produced D18: a guard that
    /// read "the parent's value is zero" as "the parent does not express this" copied a structural
    /// zero into a child that did have the component, and a herd animal born with a preferred group
    /// size of zero divided by it, put a NaN into its velocity, and hung the tick it was born in.
    /// With a mask there is no value-based test for absence left to reach for.
    ///
    /// A ulong rather than a uint because the trait enum is already wider than 32.
    /// </summary>
    private ulong _present;

    public float this[Trait t]
    {
        get => _values[(int)t];
        set { _values[(int)t] = value; _present |= 1UL << (int)t; }
    }

    /// <summary>True when this genome carries a value for the trait at all.</summary>
    public bool Has(Trait t) => (_present & (1UL << (int)t)) != 0UL;

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
            // Zero here means "this species does not graze", which is a fact about the species and
            // not a trait value. Leaving it unset is what stops it being inherited as one.
            if (d.GrazingPressure > 0f) g[Trait.GrazingPressure] = d.GrazingPressure;
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
        // A solitary animal carries zeroes in all four by construction — the factory writes them
        // that way — so it has no social traits rather than four zero-valued ones. Recording them
        // would hand a herd-born child a group behaviour of nothing, which is how D18 started.
        if (em.HasComponents(entity, ComponentFlags.Social) && em.Socials[entity].IsSocial)
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
            if (Has(Trait.MaxLifespan)) a.MaxLifespan = (int)MathF.Round(this[Trait.MaxLifespan]);
            if (Has(Trait.MaturityAge)) a.MaturityAge = (int)MathF.Round(this[Trait.MaturityAge]);
        }
        if (em.HasComponents(entity, ComponentFlags.Reproduction))
        {
            ref var r = ref em.Reproductions[entity];
            if (Has(Trait.ReproHungerThreshold)) r.HungerThreshold = this[Trait.ReproHungerThreshold];
            if (Has(Trait.ReproEnergyThreshold)) r.EnergyThreshold = this[Trait.ReproEnergyThreshold];
            if (Has(Trait.ReproHungerCost)) r.HungerCost = this[Trait.ReproHungerCost];
            if (Has(Trait.ReproEnergyCost)) r.EnergyCost = this[Trait.ReproEnergyCost];
            if (Has(Trait.ReproCooldown)) r.Cooldown = (int)MathF.Round(this[Trait.ReproCooldown]);
            if (Has(Trait.SpawnRadius)) r.SpawnRadius = this[Trait.SpawnRadius];
        }
        if (em.HasComponents(entity, ComponentFlags.Hunger))
        {
            ref var h = ref em.Hungers[entity];
            if (Has(Trait.MaxHunger)) h.Max = this[Trait.MaxHunger];
            if (Has(Trait.HungerDecayRate)) h.DecayRate = this[Trait.HungerDecayRate];
            // The factory filled Current against the max it rolled, which this has just replaced.
            if (h.Current > h.Max) h.Current = h.Max;
        }
        if (em.HasComponents(entity, ComponentFlags.Wander))
        {
            ref var w = ref em.Wanders[entity];
            if (Has(Trait.WanderSpeed)) w.Speed = this[Trait.WanderSpeed];
            if (Has(Trait.DirectionChangeChance)) w.ChangeDirectionChance = this[Trait.DirectionChangeChance];
        }
        if (CarriesTrait(em, entity, Trait.BodySize) && Has(Trait.BodySize))
            em.Renderables[entity].Size = this[Trait.BodySize];
        if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
        {
            ref var d = ref em.TerrainDiscomforts[entity];
            if (Has(Trait.DiscomfortThreshold)) d.Threshold = this[Trait.DiscomfortThreshold];
            if (Has(Trait.DiscomfortDecayRate)) d.DecayRate = this[Trait.DiscomfortDecayRate];
            // Absent for a species that does not graze, so nothing is written and the factory's
            // zero stands. Measured before the mask existed: a shoal's descendants had acquired
            // 89% of a grazing pressure their founders never had, purely from regression pulling
            // an unexpressed trait toward a species value.
            if (Has(Trait.GrazingPressure)) d.GrazingPressure = this[Trait.GrazingPressure];
        }
        if (em.HasComponents(entity, ComponentFlags.Terraform))
        {
            ref var t = ref em.Terraforms[entity];
            if (Has(Trait.TerraformRadius)) t.Radius = this[Trait.TerraformRadius];
            if (Has(Trait.TerraformStrength)) t.Strength = this[Trait.TerraformStrength];
            if (Has(Trait.TerraformCooldown)) t.Cooldown = (int)MathF.Round(this[Trait.TerraformCooldown]);
        }
        if (em.HasComponents(entity, ComponentFlags.Prey))
        {
            ref var p = ref em.Preys[entity];
            if (Has(Trait.FleeRange)) p.FleeRange = this[Trait.FleeRange];
            if (Has(Trait.FleeSpeedMultiplier)) p.FleeSpeedMultiplier = this[Trait.FleeSpeedMultiplier];
        }
        if (em.HasComponents(entity, ComponentFlags.Fear))
        {
            ref var f = ref em.Fears[entity];
            if (Has(Trait.FearThreshold)) f.Threshold = this[Trait.FearThreshold];
            if (Has(Trait.FearMax)) f.Max = this[Trait.FearMax];
            if (Has(Trait.FearAccumulationRate)) f.AccumulationRate = this[Trait.FearAccumulationRate];
            if (Has(Trait.FearDecayRate)) f.DecayRate = this[Trait.FearDecayRate];
            if (Has(Trait.FearVigilanceDecay)) f.VigilanceDecay = this[Trait.FearVigilanceDecay];
        }
        if (em.HasComponents(entity, ComponentFlags.Predator))
        {
            ref var p = ref em.Predators[entity];
            if (Has(Trait.HuntRange)) p.HuntRange = this[Trait.HuntRange];
            if (Has(Trait.AttackRange)) p.AttackRange = this[Trait.AttackRange];
            if (Has(Trait.AttackPower)) p.AttackPower = this[Trait.AttackPower];
            if (Has(Trait.AttackCooldown)) p.AttackCooldown = (int)MathF.Round(this[Trait.AttackCooldown]);
        }
        if (em.HasComponents(entity, ComponentFlags.Social))
        {
            ref var s = ref em.Socials[entity];
            // A solitary animal carries zeroes here by construction; inheriting a herder's values
            // would hand it a group behaviour its social type does not use. And a genome gathered
            // from a solitary parent carries no social traits at all, so a herd-born child keeps
            // the species values the factory gave it rather than a preferred group size of zero.
            if (s.IsSocial)
            {
                if (Has(Trait.GroupAffinity)) s.GroupAffinity = this[Trait.GroupAffinity];
                if (Has(Trait.PreferredGroupSize)) s.PreferredGroupSize = this[Trait.PreferredGroupSize];
                if (Has(Trait.CohesionStrength)) s.CohesionStrength = this[Trait.CohesionStrength];
                if (Has(Trait.AlignmentStrength)) s.AlignmentStrength = this[Trait.AlignmentStrength];
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
        {
            var t = (Trait)i;
            // Presence, never a value. A contributor that does not carry a trait contributes
            // nothing to it rather than contributing a zero — otherwise every delivery from an
            // animal missing a component would drag the nest's template toward zero, one weighted
            // step at a time, for as long as the colony ran.
            bool ha = a.Has(t), hb = b.Has(t);
            if (ha && hb) g[t] = a._values[i] * (1f - w) + b._values[i] * w;
            else if (ha) g[t] = a._values[i];
            else if (hb) g[t] = b._values[i];
        }
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

            // Absence is carried, never interpreted. A trait this genome does not hold is not
            // regressed, not mutated and not passed on — the child keeps whatever the species
            // default gave it. Reading absence off the value instead is exactly D18.
            if (!Has(trait)) continue;

            float speciesValue = SpeciesValue(species, trait);
            if (speciesValue == 0f)
            {
                // Nothing to regress toward and no scale to mutate against — the species does not
                // use this trait (a herbivore's attack power, a non-terraformer's radius).
                child[trait] = _values[i];
                continue;
            }

            float value = _values[i] + (speciesValue - _values[i]) * RegressionToMean;

            float rate = MutationRate(species, trait);
            if (rate > 0f)
                value += speciesValue * rate * (float)(rng.NextDouble() * 2 - 1);

            float low = speciesValue * (1f - TraitBand);
            float high = speciesValue * (1f + TraitBand);
            child[trait] = Math.Clamp(value, MathF.Min(low, high), MathF.Max(low, high));
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
