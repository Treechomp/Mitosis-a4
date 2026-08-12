using System;
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

    // === BEHAVIOR ARBITRATION (per-species drive priorities) ===
    // These lift previously-hardcoded thresholds that govern how competing drives (hunger vs
    // social vs fear) are prioritized. Defaults match the old constants, so behavior is unchanged
    // unless a species overrides. See docs/behavior-arbitration.md.

    /// <summary>
    /// Hunger ratio below which a creature will MIGRATE toward food (roam to grazeable/FeedTile/
    /// HuntTerrain) and a hungry herd predator leaves the huddle to forage. Higher = forages
    /// sooner (a specialist that must travel to its food, e.g. Penguin → coast); lower = waits
    /// until hungrier. Was a hardcoded 0.7.
    /// </summary>
    public float ForageHungerThreshold { get; init; } = 0.7f;

    /// <summary>
    /// Fear ratio (0-1) at which a prey commits to its flee/panic/freeze response. Lower = more
    /// skittish (bolts early — Rabbit); higher = holds its ground longer (Musk Ox). Was 0.5.
    /// </summary>
    public float FleeFearThreshold { get; init; } = 0.5f;

    /// <summary>
    /// Hunger ratio at/above which a predator stops hunting (sated). Lower = content with less
    /// (lazy apex); higher = keeps hunting opportunistically. Was a hardcoded 0.95.
    /// </summary>
    public float SatedHunger { get; init; } = 0.95f;

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
    /// Per-terrain speed REPLACEMENT. When a tile is listed, this value REPLACES the tile's
    /// intrinsic speed for this species (it does not multiply it) — see TerrainProfile.Speed.
    /// Unlisted tiles fall back to the tile's own base grip. Lets specialists be fast where
    /// others crawl, or especially slow where badly suited.
    /// </summary>
    public Dictionary<TileType, float>? TerrainSpeedModifiers { get; init; }

    /// <summary>
    /// Per-terrain comfort overrides. Negative = comfortable, Positive = uncomfortable.
    /// Feeds TerrainDiscomfortSystem (the SOFT, hunger/fear-overridable preference that drives
    /// creatures off uncomfortable ground). Hard element barriers are handled separately by
    /// TerrainProfile.IsImpassable (IsAquatic / AvoidsWater), not here.
    /// </summary>
    public Dictionary<TileType, float>? TerrainComfortModifiers { get; init; }

    /// <summary>
    /// Per-terrain concealment (camouflage). Higher = harder for predators to detect this species
    /// on that tile (Rabbit in forest, Scorpion in desert, Arctic Fox in snow). Unlisted tiles
    /// fall back to the tile's generic cover (TileType.GetCoverBonus). See TerrainProfile.Concealment.
    /// </summary>
    public Dictionary<TileType, float>? TerrainConcealment { get; init; }

    /// <summary>
    /// Per-terrain steering aversion overrides (0 = happily walks here, 1 = strongly avoided).
    /// REPLACE semantics: a listed tile uses this value instead of the tile's generic
    /// <see cref="TileTypeExtensions.GetAvoidanceWeight"/> — see TerrainProfile.SteerAversion.
    ///
    /// This is what keeps a species inside its own habitat. The generic weights encode "what an
    /// ordinary land animal dislikes" (open water, mountains, and — awkwardly — swamp and bog),
    /// so a wetland specialist steered by them drifts OFF its home ground toward the grass that
    /// kills it. Any species whose habitat the generic table treats as unpleasant needs an entry
    /// here. Hunger still overrides steering (see WanderSystem foraging), so a preference is a
    /// pull, not a cage.
    /// </summary>
    public Dictionary<TileType, float>? TerrainAversionModifiers { get; init; }

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

    /// <summary>
    /// Hunger ratio below which this species starts trading safety for food: its effective flee
    /// radius shrinks toward the desperate floor as hunger approaches zero, so it will feed in
    /// ground a well-fed individual would refuse to enter. 0 disables it (structures, factions).
    /// The alternative is prey that starves to death holding a safe position — observed with
    /// penguins that would not enter the sea while a shark was in it.
    /// </summary>
    public float DesperationHunger { get; init; } = 0.3f;

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

    /// <summary>
    /// Fertility stripped from a FeedTile per feeding tick (0 = the tile is an inexhaustible
    /// supply, the old behaviour). The FeedTiles analogue of GrazeConsumeRate: it is what makes a
    /// water-feeding shoal deplete its own grounds instead of grazing an infinite buffet.
    /// </summary>
    public float FeedConsumeRate { get; init; } = 0f;

    /// <summary>
    /// How strongly local tile fertility gates reproduction, 0..1. At 0 (default) breeding ignores
    /// the ground entirely; at 1 the chance to breed equals the tile's fraction of its own cap, so
    /// a species multiplies in rich ground and barely at all in exhausted ground.
    ///
    /// This is the negative feedback that stops a boom without needing a predator: a shoal that
    /// eats its water down also stops breeding in it, and the survivors leave rather than stack up.
    /// </summary>
    public float BreedingNutritionSensitivity { get; init; } = 0f;

    /// <summary>
    /// Tiles a PARENT must be standing on to reproduce. Null/empty = breeds wherever it lives.
    ///
    /// This is what makes a semi-aquatic species genuinely amphibious rather than an ordinary
    /// animal that happens to tolerate water: a penguin hunts at sea but must haul out onto the
    /// ice to raise a chick, so the colony's whole rhythm — go to sea hungry, come ashore fed —
    /// falls out of the breeding cycle instead of having to be scripted. WanderSystem gives
    /// breeding-ready adults a roam target on the nearest such ground ("seek_breeding_ground").
    /// </summary>
    public List<TileType>? BreedingTiles { get; init; }

    /// <summary>True when a parent standing on this tile is allowed to reproduce.</summary>
    public bool CanBreedOnTile(TileType tile)
        => BreedingTiles == null || BreedingTiles.Count == 0 || BreedingTiles.Contains(tile);

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
    /// How much food this creature's carcass is worth (predator hunger, and the load a Sectid
    /// ferries home to its nest). Defaults to the body-mass curve below; -1 means "use default".
    /// </summary>
    public float NutritionValue { get; init; } = -1f;

    // Carcass food from body mass. Superlinear (exponent > 1) so size matters more than a
    // straight head-count: a swarm that brings down one large animal is far better paid than one
    // that picks off the same mass in small game, which is the point of hunting big prey at all.
    // The scale is pivoted so a mid-size prey animal (BodyMass 4 — Deer/Boar) keeps the value it
    // had under the old flat BodyMass*20, leaving established predator/prey pairs roughly where
    // they were; small prey drops (Rabbit 20 → 14) and large prey rises (Elk 140 → 161).
    private const float NutritionMassExponent = 1.25f;
    private const float NutritionMassScale    = 14.14f;  // = 20 * 4 / 4^1.25 (deer-neutral pivot)

    /// <summary>Resolved carcass food: explicit <see cref="NutritionValue"/> or the mass curve.</summary>
    public float EffectiveNutrition => NutritionValue >= 0
        ? NutritionValue
        : MathF.Pow(BodyMass, NutritionMassExponent) * NutritionMassScale;

    public List<string>? PreferredPrey { get; init; }
    public float PreferredPreyBias { get; init; } = 0.5f;

    /// <summary>
    /// Prey this species treats as a lean fallback rather than proper game: ignored entirely while
    /// hunger sits above <see cref="FallbackPreyHunger"/>, and taken without hesitation below it.
    ///
    /// PreferredPrey alone can only say "I like this better" — one flat multiplier applied to every
    /// name on the list, so a Shark rated a Fish and a Penguin identically and simply ate whichever
    /// was nearer. That is what let a shoal be grazed flat by an apex predator that should be after
    /// bigger game. A fallback tier makes the small prey a famine ration instead of a staple, which
    /// is what keeps the base of the food web from being cropped to nothing.
    /// </summary>
    public List<string>? FallbackPrey { get; init; }

    /// <summary>Hunger ratio (0 = starving, 1 = full) below which FallbackPrey becomes eligible.</summary>
    public float FallbackPreyHunger { get; init; } = 0.4f;

    /// <summary>True when this predator, at its current hunger, will bother with the given prey.</summary>
    public bool WillHunt(string preyName, float hungerRatio)
        => FallbackPrey == null || hungerRatio < FallbackPreyHunger
           || !FallbackPrey.Contains(preyName);

    /// <summary>
    /// Hard prey restriction: if non-null, this predator can ONLY target these species
    /// (by name), ignoring all other otherwise-valid prey. Used for specialists like the
    /// Penguin, which feeds exclusively on Fish. Null = opportunist (any valid prey).
    /// </summary>
    public List<string>? ExclusivePrey { get; init; }

    /// <summary>
    /// Hunt-scoring preference [0..1] for Shroomer spores and immature Shroomers: their score is
    /// multiplied by (1 − this), so a hunter with a positive value eats a bloom out before it
    /// fortifies rather than chasing the nearest random prey. 0 = no preference (default). Grown
    /// Shroomers are still rejected by the mass gate, so this can't make a swarm suicide on an
    /// elder. Set on Sectids, the designated anti-bloom faction.
    /// </summary>
    public float SporeHuntBias { get; init; } = 0f;

    // === GRAZING ===
    public bool CanGraze { get; init; } = false;
    public float GrazeNutrition { get; init; } = 0.5f;

    /// <summary>
    /// Nutrition stripped from a tile per grazing tick. Was one hardcoded rate for every grazer,
    /// which made a rabbit and a deer press the pasture identically. Body size should show in the
    /// ground they leave behind.
    /// </summary>
    public float GrazeConsumeRate { get; init; } = 0.02f;

    /// <summary>
    /// Tile nutrition below which this species stops treating ground as worth feeding on and
    /// starts looking elsewhere. A picky heavy browser abandons a patch long before a small
    /// generalist that can still make a living on what's left — this is the knob that staggers
    /// migration between species instead of moving every herbivore at the same instant.
    /// </summary>
    public float MinAcceptableNutrition { get; init; } = 0.1f;

    // === FUNGIVORY (spore / immature-Shroomer eating) ===
    /// <summary>
    /// If true, this species consumes nearby Shroomer spores and immature Shroomers directly — a
    /// grazing-adjacent behaviour (NOT hunting, so it inherits none of the rally/mass machinery
    /// and takes no thorn damage), giving herbivores a natural check on Shroomer blooms.
    /// </summary>
    public bool IsFungivore { get; init; } = false;
    /// <summary>Max Shroomer <c>Growth.CurrentScale</c> a fungivore will eat (spores are always
    /// edible). Kept below the ~2.5 growth-spurt band so nibblers meet only weak AoE/thorns.</summary>
    public float FungivoreMaxScale { get; init; } = 2.0f;

    /// <summary>
    /// Whether this fungivore crops sprouted Shroomers as well as spores. Off by default: a
    /// grazer nibbling spores off the ground is bloom control, but eating living Shroomers made
    /// small herbivores the hard counter to the entire faction — an unchecked rabbit population
    /// ate a bloom to extinction in testing. Sprout-croppers must opt in.
    /// </summary>
    public bool FungivoreEatsSprouts { get; init; } = false;
    /// <summary>Reach in tiles within which a fungivore consumes spores/sprouts each feed tick.</summary>
    public float FungivoreFeedRadius { get; init; } = 2.5f;
    /// <summary>Hunger restored per spore/sprout eaten.</summary>
    public float FungivoreFeedAmount { get; init; } = 8f;

    // === SHROOMER SELF-LIMITING (competition + drought) ===
    // Biological ceilings that let a bloom be pushed back rather than growing immortally: a dense
    // fungal mat competes with itself for substrate, and it cannot hold ground that has dried out
    // (so Sectid/Faeling terraforming toward dry biomes actively collapses a bloom). All default
    // to off, so only Shroomers opt in.
    /// <summary>Radius (tiles) over which same-species neighbours are counted for crowding.
    /// 0 = crowding disabled.</summary>
    public float CrowdingRadius { get; init; } = 0f;
    /// <summary>Neighbour count above which crowding attrition + spread-suppression kick in.</summary>
    public int CrowdingLimit { get; init; } = 8;
    /// <summary>Neighbour count at which local spread chance is fully suppressed (no open ground
    /// left to colonise). Spread scales linearly from CrowdingLimit → this.</summary>
    public int CrowdingSaturation { get; init; } = 16;
    /// <summary>Energy lost per tick per neighbour above CrowdingLimit (substrate competition).</summary>
    public float CrowdingDamage { get; init; } = 0f;
    /// <summary>Energy lost per tick while mature on a tile drier than SporeMoistureThreshold —
    /// the fungal mat starves on dry ground. Lets faction drying kill a bloom, not just stall it.</summary>
    public float DroughtDamage { get; init; } = 0f;

    // === TERRAFORM (faction species) ===
    public TerraformDirection TerraformDir { get; init; } = TerraformDirection.Balanced;
    public float TerraformRadius { get; init; } = 2f;
    public float TerraformStrength { get; init; } = 0.02f;
    public int TerraformCooldown { get; init; } = 10;

    // === KEEPER (Faelings — anti-dominance balancers) ===
    /// <summary>Radius (tiles) a keeper scans to judge which rival faction is locally
    /// over-dominant (Shroomer bloom vs Sectid swarm). 0 = keeper sensing off.</summary>
    public float KeeperSenseRadius { get; init; } = 0f;
    /// <summary>Minimum members the leading faction needs locally (and ≥1.5× the rival)
    /// before a keeper commits to suppressing it — below this, the area counts as balanced
    /// and the keeper falls back to terrain restoration.</summary>
    public int KeeperMinPresence { get; init; } = 5;

    public List<TileType>? FeedTiles { get; init; }
    public float FeedNutrition { get; init; } = 0.4f;

    // === FERTILITY FEEDING (Shroomers: rely on AND impact land fertility, more than herbivores) ===
    // A faction feeder with FertilityConsumeRate > 0 draws its *growth* fuel from tile nutrition
    // (grazeable tiles), consuming it faster than herbivores and gaining food scaled by what's
    // there. So a bloom only grows where there's fertility to strip; on land it has already
    // depleted (and terraformed to barren swamp) it falls back to the FeedTiles subsistence floor
    // and can't spread — an advancing front that exhausts pasture behind it and self-limits by
    // the land's carrying capacity, the same mechanism that caps herbivores.
    /// <summary>Tile nutrition consumed per tick on a grazeable tile (0 = not a fertility feeder).
    /// Set above the herbivore rate (0.02) so blooms deplete the land faster than grazers.</summary>
    public float FertilityConsumeRate { get; init; } = 0f;
    /// <summary>Hunger restored per tick at full tile fertility (scaled down as nutrition depletes).
    /// High = fertile ground fuels fast bloom growth; near-zero on stripped/swamped ground.</summary>
    public float FertilityFeedNutrition { get; init; } = 0.6f;

    /// <summary>
    /// Terrain where this predator's prey concentrate — its hunting grounds. When a hungry
    /// predator finds no prey in range it roams toward the nearest tile of this type instead of
    /// wandering blind (the predator analogue of grazers seeking grazeable terrain). E.g. Penguin
    /// → water (fish), Scorpion → sand/arid (desert prey). Null = no directed seek (generalists
    /// whose prey is on common land don't need it). See WanderSystem food-seeking.
    /// </summary>
    public List<TileType>? HuntTerrain { get; init; }

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

    /// <summary>
    /// Attacker body mass at which thorns deal exactly <see cref="ThornDamageBase"/>. Counter-damage
    /// scales with sqrt(attackerMass / this), clamped, so a big animal driving its whole weight
    /// onto the spines is hurt far more than something small biting at the edge.
    ///
    /// Flat thorns punish exactly the wrong attacker. Damage-for-damage they cost a swarm most —
    /// many small mouths take the full toll on every one of their many bites — while a wolf pack
    /// pays the same 20 for a 50-damage blow. That is backwards for a bloom that is supposed to
    /// shrug off large predators and still be ground down by persistent Sectid swarms.
    /// </summary>
    public float ThornMassReference { get; init; } = 2f;

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
    // === Elder area denial (Shroomers) ===
    // A fully grown Shroomer that is attacked floods its surroundings with a lethal spore bloom
    // for a short window, then must recharge. The recharge window is deliberate counterplay: a
    // lone hunter cannot outlast it, but a persistent swarm that keeps bodies on it can time
    // bites for the gap. Intended to be beatable only by sustained pressure (or, later, by a
    // stronger Faeling).

    /// <summary>Growth scale at which the elder denial burst unlocks.</summary>
    public float AoEEnrageScale { get; init; } = float.MaxValue;   // never, unless set

    /// <summary>Ticks the heightened denial zone stays up once triggered.</summary>
    public int AoEEnrageDuration { get; init; } = 200;

    /// <summary>Ticks after the burst ends before it can be triggered again — the vulnerable gap.</summary>
    public int AoEEnrageRecharge { get; init; } = 500;

    public float AoEEnrageDamageMultiplier { get; init; } = 3f;
    public float AoEEnrageRadiusMultiplier { get; init; } = 1.5f;

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
