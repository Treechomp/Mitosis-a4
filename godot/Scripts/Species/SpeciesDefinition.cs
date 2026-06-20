using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.World;

namespace Mitosis.SpeciesData;

/// <summary>
/// Defines dietary type - what a species eats.
/// </summary>
public enum DietType : byte
{
    Herbivore = 0,    // Eats plants (grazes)
    Carnivore = 1,    // Eats other creatures
    Omnivore = 2,     // Eats both (future)
    Terraformer = 3   // Feeds from terrain it shapes (faction species)
}

/// <summary>
/// Comprehensive species definition containing all stats and behaviors.
/// All behavioral parameters are species-configurable. Systems read these
/// values per-entity rather than using global defaults.
/// </summary>
public sealed class SpeciesDefinition
{
    // === IDENTITY ===
    public string Name { get; init; } = "Unknown";
    public DietType Diet { get; init; } = DietType.Herbivore;
    public SocialType DefaultSocialType { get; init; } = SocialType.Solitary;

    // === MOVEMENT ===
    public float BaseWanderSpeed { get; init; } = 0.03f;
    public float DirectionChangeChance { get; init; } = 0.005f;

    // === ROAMING (long-distance directed travel) ===
    public float RoamDistance { get; init; } = 60f;
    public int RoamCooldown { get; init; } = 500;
    public float RoamSpeedMultiplier { get; init; } = 1.5f;

    // === COMBAT (Predators) ===

    /// <summary>Primary hunting tactic for this species. Determines movement patterns,
    /// coordination behavior, and engagement style during hunts.</summary>
    public HuntingTactic HuntingTactic { get; init; } = HuntingTactic.Solo;

    public float HuntRange { get; init; } = 12f;
    public float AttackRange { get; init; } = 0.8f;
    public float AttackPower { get; init; } = 30f;
    public int AttackCooldown { get; init; } = 20;
    public float BaseHuntSpeed { get; init; } = 0.10f;

    /// <summary>Hunger ratio (0-1) above which predator stops hunting.</summary>
    public float HuntThreshold { get; init; } = 0.75f;

    /// <summary>Hunger ratio below which predator tracks prey at long range.</summary>
    public float TrackingHungerThreshold { get; init; } = 0.5f;

    /// <summary>Maximum range for hunger-driven prey tracking.</summary>
    public float TrackingRange { get; init; } = 80f;

    /// <summary>Radius to coordinate with pack members during hunts.</summary>
    public float PackCoordinationRadius { get; init; } = 8f;

    /// <summary>Radius within which pack members share food from kills.</summary>
    public float PackShareRadius { get; init; } = 10f;

    /// <summary>Fraction of nutrition that goes to the killer (rest split among pack).</summary>
    public float KillerShareRatio { get; init; } = 0.5f;

    /// <summary>Ticks spent positioning before a pack rush.</summary>
    public int PositioningDuration { get; init; } = 40;

    /// <summary>Ticks spent rushing during a pack attack.</summary>
    public int RushDuration { get; init; } = 30;

    /// <summary>Ticks spent retreating after a pack attack pass.</summary>
    public int RetreatDuration { get; init; } = 20;

    /// <summary>Leader auto-triggers convergence (all-in kill rush) after this many ticks
    /// if flankers haven't reached position. Higher = more patient pack.</summary>
    public int ConvergenceTimeout { get; init; } = 120;

    /// <summary>Swarm hunter: counts all nearby same-species for effective mass (not just same group),
    /// can target any living creature (including predators), and never retreats mid-hunt.</summary>
    public bool SwarmHunter { get; init; } = false;

    /// <summary>Semi-aquatic predator: doesn't avoid water when hunting. Land predators
    /// will reject prey targets with too much water on the path.</summary>
    public bool SemiAquatic { get; init; } = false;

    // === AMBUSH HUNTING ===

    /// <summary>Stealth gain per tick when moving slowly or standing still.
    /// 0 = not an ambush predator. Stealth accumulates toward 1.0.</summary>
    public float AmbushStealthGain { get; init; } = 0f;

    /// <summary>Stealth decay per tick when moving fast (chasing openly).</summary>
    public float AmbushStealthDecay { get; init; } = 0.05f;

    /// <summary>Speed threshold below which stealth accumulates (fraction of BaseHuntSpeed).</summary>
    public float AmbushSpeedThreshold { get; init; } = 0.5f;

    /// <summary>Distance within which a pounce can be triggered.</summary>
    public float PounceRange { get; init; } = 3f;

    /// <summary>
    /// Sit-and-wait ambush: instead of stalking toward prey, the predator lurks motionless once
    /// stealthed (max stealth → ~invisible to prey, low metabolism via Predator.IsDormant), then
    /// pounces when prey wanders into PounceRange and envenomates it, trailing until the DOT kills.
    /// (Also the template for dormant Sectids' nest ambush.)
    /// </summary>
    public bool AmbushDormant { get; init; } = false;

    /// <summary>Speed multiplier during pounce burst.</summary>
    public float PounceSpeedMult { get; init; } = 3f;

    /// <summary>Attack power multiplier during pounce burst.</summary>
    public float PounceAttackMult { get; init; } = 2f;

    /// <summary>Duration of pounce burst in ticks.</summary>
    public int PounceDuration { get; init; } = 12;

    /// <summary>Minimum stealth level to trigger a pounce (0-1).</summary>
    public float PounceStealthThreshold { get; init; } = 0.7f;

    /// <summary>Bonus stealth gain when on water tiles (for semi-aquatic ambushers like crocs).</summary>
    public float WaterStealthBonus { get; init; } = 0f;

    // === FLEEING (Prey) ===
    public float FleeRange { get; init; } = 6f;
    public float FleeSpeedMultiplier { get; init; } = 2f;

    // === FLEE STAMINA ===
    // Prey flee at full FleeSpeedMultiplier (burst), but stamina drains while fleeing and the
    // burst fades toward FleeTiredSpeedFloor of it (a tired jog), recovering at rest — so prey
    // can't outrun an endless relay of predators.
    /// <summary>Stamina (0..1) drained per tick while actively fleeing. Higher = tires sooner.</summary>
    public float FleeStaminaDrain { get; init; } = 0.005f;
    /// <summary>Stamina recovered per tick while not fleeing.</summary>
    public float FleeStaminaRecovery { get; init; } = 0.0025f;
    /// <summary>Fraction of FleeSpeedMultiplier still available when fully exhausted.</summary>
    public float FleeTiredSpeedFloor { get; init; } = 0.5f;

    // === FEAR RESPONSE ===
    public float FearThreshold { get; init; } = 50f;
    public float FearMax { get; init; } = 100f;
    public float FearAccumulationRate { get; init; } = 5f;
    public float FearDecayRate { get; init; } = 1f;
    public float FearVigilanceDecay { get; init; } = 0.3f;
    public int FearVigilanceDuration { get; init; } = 100;
    public FearResponse DefaultFearResponse { get; init; } = FearResponse.Flee;

    // === SURVIVAL ===
    public float MaxHunger { get; init; } = 240f;
    public float HungerDecayRate { get; init; } = 0.05f;
    /// <summary>How fast this species strips nutrition from a carcass per feed tick. -1 = use the
    /// default (MaxHunger×0.02, floored at 2). Lets a species' feeding behaviour live on the
    /// species instead of as a hardcoded branch in CarrionSystem (e.g. Sectids chop fast).</summary>
    public float CarrionChopRate { get; init; } = -1f;
    public float StarvationDamage { get; init; } = 1f;
    public float MaxEnergy { get; init; } = 100f;
    public float EnergyRegenRate { get; init; } = 0.25f; // HP/tick when out of combat and not starving
    public int MaxLifespan { get; init; } = 30000;
    public int MaturityAge { get; init; } = 2000;

    // === REPRODUCTION ===
    public float ReproHungerThreshold { get; init; } = 210f;
    public float ReproEnergyThreshold { get; init; } = 80f;
    public float ReproHungerCost { get; init; } = 40f;
    public float ReproEnergyCost { get; init; } = 30f;
    public int ReproCooldown { get; init; } = 600;
    public int OffspringCount { get; init; } = 1;
    public float SpawnRadius { get; init; } = 3f;

    /// <summary>
    /// Relative weight for initial spawn budget distribution.
    /// Higher = more individuals spawned. Common species get higher weights,
    /// rare apex predators get lower weights. Default 1.0.
    /// </summary>
    public float SpawnWeight { get; init; } = 1.0f;

    // === SOCIAL ===
    public float GroupAffinity { get; init; } = 0.5f;
    public float PreferredGroupSize { get; init; } = 5f;
    public float CohesionStrength { get; init; } = 0.02f;
    public float AlignmentStrength { get; init; } = 0.01f;
    public float PackHunterChance { get; init; } = 0.6f;

    /// <summary>Max radius to look for group members.</summary>
    public float SocialRadius { get; init; } = 8f;

    /// <summary>Max distance to recognize/follow a leader.</summary>
    public float LeaderInfluenceRadius { get; init; } = 6f;

    /// <summary>Max distance to join a new group.</summary>
    public float MaxJoinDistance { get; init; } = 12f;

    /// <summary>Allow groups to exceed preferred size by this factor before splitting.</summary>
    public float GroupSizeTolerance { get; init; } = 1.3f;

    /// <summary>Ticks before seeking new leader after losing current one.</summary>
    public int LeaderLostThreshold { get; init; } = 40;

    // === PACK ROLE MODIFIERS (speed multipliers per role) ===
    public float LeaderSpeedMult { get; init; } = 1.0f;
    public float FlankerSpeedMult { get; init; } = 1.1f;
    public float ChaserSpeedMult { get; init; } = 1.15f;

    // === SEPARATION ===
    public float SeparationRadius { get; init; } = 2f;
    public float SeparationStrength { get; init; } = 0.02f;

    // === TERRAIN ===
    public float DiscomfortThreshold { get; init; } = 50f;
    public float DiscomfortDecayRate { get; init; } = 2f;
    public float GrazingPressure { get; init; } = 0f;

    /// <summary>
    /// Per-terrain speed modifiers. Values multiply the base terrain speed.
    /// </summary>
    public Dictionary<TileType, float>? TerrainSpeedModifiers { get; init; }

    /// <summary>
    /// Per-terrain comfort overrides. Negative = comfortable, Positive = uncomfortable.
    /// </summary>
    public Dictionary<TileType, float>? TerrainComfortModifiers { get; init; }

    /// <summary>
    /// Biomes where this species can spawn. Empty/null = spawn in any biome.
    /// </summary>
    public List<BiomeType>? PreferredBiomes { get; init; }

    public bool IsAquatic { get; init; } = false;
    public bool IsFlying { get; init; } = false;

    /// <summary>
    /// Land creature that routes around water when hunting/tracking. Aquatic and semi-aquatic
    /// species are excluded (water is their element / fully traversable). By default a land
    /// creature only avoids and drowns in DEEP water and wades shallow/river freely; set
    /// <see cref="AvoidsWater"/> for species that can't swim at all and avoid/drown in any water.
    /// (Bug history: gating on `!SemiAquatic` alone made aquatic Sharks reject every in-water
    /// target and starve with zero kills.)
    /// </summary>
    public bool AvoidsOpenWater => !IsAquatic && !SemiAquatic;

    /// <summary>
    /// Cannot swim at all — routes around ALL water (even shallow/river) and drowns in any of it.
    /// For insects/desert species (e.g. Sectid, Scorpion). Land creatures otherwise wade shallow.
    /// </summary>
    public bool AvoidsWater { get; init; } = false;

    /// <summary>Ticks in wrong element (water for land, land for aquatic) before damage starts.
    /// Good swimmers get longer grace; panicky species drown fast. Default 60 (3 sec at 20 TPS).</summary>
    public int WrongElementGraceTicks { get; init; } = 60;
    /// <summary>Base damage per tick in wrong element, scaled by energy (healthier = less damage).
    /// Higher = drowns/suffocates faster. Default 2.0.</summary>
    public float WrongElementDamageRate { get; init; } = 2.0f;

    /// <summary>
    /// Specific tile types where this species can spawn. If null, uses default logic.
    /// </summary>
    public List<TileType>? AllowedSpawnTiles { get; init; }

    public bool CanSpawnInBiome(BiomeType biome)
    {
        if (PreferredBiomes == null || PreferredBiomes.Count == 0)
            return true;
        return PreferredBiomes.Contains(biome);
    }

    public bool CanSpawnOnTile(TileType tile)
    {
        if (AllowedSpawnTiles != null && AllowedSpawnTiles.Count > 0)
            return AllowedSpawnTiles.Contains(tile);
        if (IsAquatic)
            return tile.IsWater();
        return tile.IsSpawnable();
    }

    // === TROPHIC INTERACTIONS ===
    public float BodyMass { get; init; } = 1.0f;
    public float SoloHuntMaxRatio { get; init; } = 1.2f;
    public float PackHuntMassExponent { get; init; } = 0.7f;

    /// <summary>
    /// How much hunger a predator gains from killing this creature.
    /// Defaults to BodyMass * 20 if not explicitly set (-1 means use default).
    /// </summary>
    public float NutritionValue { get; init; } = -1f;

    /// <summary>Resolved nutrition: explicit value or BodyMass * 20.</summary>
    public float EffectiveNutrition => NutritionValue >= 0 ? NutritionValue : BodyMass * 20f;

    public List<string>? PreferredPrey { get; init; }
    public float PreferredPreyBias { get; init; } = 0.5f;

    /// <summary>
    /// Hard prey restriction: if non-null, this predator can ONLY target these species
    /// (by name), ignoring all other otherwise-valid prey. Used for specialists like the
    /// Penguin, which feeds exclusively on Fish. Null = opportunist (any valid prey).
    /// </summary>
    public List<string>? ExclusivePrey { get; init; }

    // === GRAZING ===
    public bool CanGraze { get; init; } = false;
    public float GrazeNutrition { get; init; } = 0.5f;

    // === TERRAFORM (faction species) ===
    public TerraformDirection TerraformDir { get; init; } = TerraformDirection.Balanced;
    public float TerraformRadius { get; init; } = 2f;
    public float TerraformStrength { get; init; } = 0.02f;
    public int TerraformCooldown { get; init; } = 10;

    public List<TileType>? FeedTiles { get; init; }
    public float FeedNutrition { get; init; } = 0.4f;

    // === FACTION-SPECIFIC ===
    public float MaxCarryFood { get; init; } = 5f;
    public bool NestBreeder { get; init; } = false;
    public bool SporeReproducer { get; init; } = false;
    public bool CrystalSpawned { get; init; } = false;
    public bool ImmuneToStarvation { get; init; } = false;
    public bool UnhuntableByPredators { get; init; } = false;

    // === AOE ATTACK (Shroomers) ===
    public bool HasAoEAttack { get; init; } = false;

    /// <summary>Maximum AoE radius at full growth. At birth, radius = this * AoEMinScaleFactor.</summary>
    public float AoEAttackRadius { get; init; } = 3f;

    /// <summary>Maximum AoE damage at full growth. At birth, damage = this * AoEMinScaleFactor.</summary>
    public float AoEAttackDamage { get; init; } = 8f;

    /// <summary>Base AoE cooldown (used as fallback). See AoEPassiveCooldown/AoECombatCooldown.</summary>
    public int AoEAttackCooldown { get; init; } = 40;

    /// <summary>AoE cooldown when passive (not in combat). Higher = less frequent passive pulses.
    /// Defaults to AoEAttackCooldown * 3 if not set (0).</summary>
    public int AoEPassiveCooldown { get; init; } = 0;

    /// <summary>AoE cooldown when in combat (being attacked). Lower = more frequent reactive pulses.
    /// Defaults to AoEAttackCooldown if not set (0).</summary>
    public int AoECombatCooldown { get; init; } = 0;

    /// <summary>Minimum AoE scaling factor at birth (fraction of max radius/damage).
    /// 0.1 = 10% of max values at initial growth scale. Growth follows an S-curve
    /// (smoothstep) from this floor to 1.0 at max growth scale.</summary>
    public float AoEMinScaleFactor { get; init; } = 0.1f;

    /// <summary>Base thorn damage per melee hit when attacked. Scales with growth via S-curve.
    /// 0 = no thorn defense.</summary>
    public float ThornDamageBase { get; init; } = 0f;

    // === GROWTH (Shroomers, Faelings) ===
    /// <summary>Max growth scale multiplier. 0 or negative = no growth component.</summary>
    public float GrowthMaxScale { get; init; } = 0f;
    public float GrowthRate { get; init; } = 0f;
    /// <summary>Initial growth scale at spawn (e.g. 0.5 for small start).</summary>
    public float InitialScale { get; init; } = 1f;
    /// <summary>Growth scale threshold before AoE becomes active.</summary>
    public float AoEMinScale { get; init; } = 1.5f;

    // === SPORE REPRODUCTION (Shroomers) ===
    public float SporeSpreadChance { get; init; } = 0f;
    public float SporeSpreadRadius { get; init; } = 8f;
    public int SporesPerSpread { get; init; } = 2;
    public float SporeMoistureThreshold { get; init; } = 0.6f;
    public float SporeSpreadHungerCost { get; init; } = 0.15f;
    public float SporeTransformThreshold { get; init; } = 60f;
    public float SporeWitherRate { get; init; } = 2f;
    public float SporeMoistureGainRate { get; init; } = 0.5f;
    public float SporeEnergy { get; init; } = 40f;

    // === NEST BREEDING (Sectids) ===
    public float NestColonyRadius { get; init; } = 40f;
    public int NestsForExpedition { get; init; } = 5;
    public float NestSearchRadius { get; init; } = 15f;
    public float ExpeditionDistance { get; init; } = 80f;
    public float NestFoodPerSpawn { get; init; } = 30f;
    public float NestSpawnDuration { get; init; } = 200f;
    public float NestEnergy { get; init; } = 200f;
    public float FoodDeliveryRange { get; init; } = 4f;
    public float CarryingSpeed { get; init; } = 0.08f;

    // === CRYSTAL SPAWNING (Faelings) ===
    public int CrystalSpawnDelay { get; init; } = 500;
    public float RangedAttackRange { get; init; } = 8f;
    public float RangedAttackDamage { get; init; } = 10f;
    public int RangedAttackCooldown { get; init; } = 30;

    // === VISUALS ===
    public Color BaseColor { get; init; } = new(0.5f, 0.5f, 0.5f);
    public float BaseSize { get; init; } = 8f;
    public ShapeType Shape { get; init; } = ShapeType.Circle;

    // === VENOM ===
    /// <summary>Damage per tick applied to prey after a venomous attack. 0 = no venom.</summary>
    public float VenomDamagePerTick { get; init; } = 0f;
    /// <summary>Duration of venom effect in ticks.</summary>
    public int VenomDurationTicks { get; init; } = 0;
    public bool HasVenom => VenomDamagePerTick > 0f && VenomDurationTicks > 0;

    // === STAT VARIATION ===
    public float StatVariation { get; init; } = 0.2f;

    // === COMPUTED PROPERTIES ===

    public float GetTerrainSpeedModifier(TileType tile)
    {
        if (TerrainSpeedModifiers != null && TerrainSpeedModifiers.TryGetValue(tile, out float mod))
            return mod;
        return 1.0f;
    }

    public float GetTerrainComfortModifier(TileType tile)
    {
        if (TerrainComfortModifiers != null && TerrainComfortModifiers.TryGetValue(tile, out float mod))
            return mod;
        return 0f;
    }

    /// <summary>
    /// Get the growth scaling factor for AoE/thorn damage using an S-curve (smoothstep).
    /// Returns a value between AoEMinScaleFactor (at InitialScale) and 1.0 (at GrowthMaxScale).
    /// Shape: slow increase at birth → growth spurt mid-life → tapering toward elder age.
    /// </summary>
    public float GetGrowthScalingFactor(float currentScale)
    {
        if (GrowthMaxScale <= InitialScale) return 1f;
        float t = (currentScale - InitialScale) / (GrowthMaxScale - InitialScale);
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        // Smoothstep: S-curve with slow start, steep middle, tapering end
        float s = t * t * (3f - 2f * t);
        return AoEMinScaleFactor + (1f - AoEMinScaleFactor) * s;
    }

    public bool IsPredator => Diet == DietType.Carnivore || Diet == DietType.Omnivore;
    public bool IsPrey => Diet == DietType.Herbivore || Diet == DietType.Omnivore || Diet == DietType.Terraformer;
    public bool HasGrowth => GrowthMaxScale > 0f;
    public bool IsAmbushPredator => HuntingTactic == HuntingTactic.Ambush || AmbushStealthGain > 0f;
    public bool IsSwarmHunter => HuntingTactic == HuntingTactic.Swarm || SwarmHunter;
    public bool IsPackCoordinated => HuntingTactic == HuntingTactic.PackCoordinated;

    /// <summary>Effective passive AoE cooldown (uses AoEPassiveCooldown if set, otherwise AoEAttackCooldown * 3).</summary>
    public int EffectiveAoEPassiveCooldown => AoEPassiveCooldown > 0 ? AoEPassiveCooldown : AoEAttackCooldown * 3;

    /// <summary>Effective combat AoE cooldown (uses AoECombatCooldown if set, otherwise AoEAttackCooldown).</summary>
    public int EffectiveAoECombatCooldown => AoECombatCooldown > 0 ? AoECombatCooldown : AoEAttackCooldown;
}
