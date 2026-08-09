using System;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using static Mitosis.ECS.EntityManager;

namespace Mitosis;

/// <summary>
/// Creates individual entities from species definitions.
/// Handles component assembly for creatures and the player.
/// </summary>
public sealed class EntityFactory
{
    private readonly EntityManager _entityManager;
    private readonly Random _rng;
    private int _populationCap = int.MaxValue;

    public EntityFactory(EntityManager entityManager, Random rng)
    {
        _entityManager = entityManager;
        _rng = rng;
    }

    /// <summary>Set the soft population cap. SpawnCreature will refuse to spawn beyond this.</summary>
    public void SetPopulationCap(int cap) => _populationCap = cap;

    /// <summary>True if entity count is at or above the population cap.</summary>
    public bool AtCapacity => _entityManager.CreatureCount >= _populationCap;

    /// <summary>
    /// Applies random variation to a base value (+-percentage).
    /// </summary>
    public float Vary(float baseValue, float variationPercent = 0.2f)
    {
        float variation = baseValue * variationPercent;
        return baseValue + (float)(_rng.NextDouble() * 2 - 1) * variation;
    }

    /// <summary>
    /// Spawns a creature at the given position using a species definition.
    /// </summary>
    /// <param name="species">The species definition to use</param>
    /// <param name="groupId">Pre-assigned group ID (-1 for no group)</param>
    /// <param name="forceSolitary">Force solitary social type</param>
    /// <param name="isAlpha">Whether this is the group leader (higher leadership score)</param>
    public void SpawnCreature(float x, float y, SpeciesDefinition species, int groupId = -1,
                               bool forceSolitary = false, bool isAlpha = false)
    {
        // Hard population cap — refuse to spawn beyond the limit
        if (_entityManager.CreatureCount >= _populationCap)
            return;

        int entity = _entityManager.CreateEntity();
        float variation = species.StatVariation;

        // Core components
        _entityManager.Positions[entity] = new Position(x, y);
        _entityManager.AddComponent(entity, ComponentFlags.Position);

        _entityManager.Velocities[entity] = new Velocity();
        _entityManager.AddComponent(entity, ComponentFlags.Velocity);

        _entityManager.ChunkPositions[entity] = new ChunkPosition();
        _entityManager.AddComponent(entity, ComponentFlags.ChunkPosition);

        // Age with variation
        _entityManager.Ages[entity] = new Age(
            current: _rng.Next(0, species.MaturityAge * 2),  // Start at random age
            maxLifespan: (int)Vary(species.MaxLifespan, variation),
            maturityAge: (int)Vary(species.MaturityAge, variation)
        );
        _entityManager.AddComponent(entity, ComponentFlags.Age);

        _entityManager.Energies[entity] = new Energy(species.MaxEnergy, species.MaxEnergy);
        _entityManager.AddComponent(entity, ComponentFlags.Energy);

        // Reproduction from species (all costs and parameters from species definition)
        _entityManager.Reproductions[entity] = new Reproduction(
            hungerThreshold: Vary(species.ReproHungerThreshold, variation),
            energyThreshold: Vary(species.ReproEnergyThreshold, variation),
            hungerCost: Vary(species.ReproHungerCost, variation),
            energyCost: Vary(species.ReproEnergyCost, variation),
            cooldown: (int)Vary(species.ReproCooldown, variation),
            offspringCount: species.OffspringCount,
            spawnRadius: Vary(species.SpawnRadius, variation)
        );
        _entityManager.AddComponent(entity, ComponentFlags.Reproduction);

        _entityManager.SimulationLODs[entity] = new SimulationLOD(LODLevel.Full);
        _entityManager.AddComponent(entity, ComponentFlags.SimulationLOD);

        // Species type based on diet
        SpeciesType speciesType = species.Diet switch
        {
            DietType.Herbivore => SpeciesType.Herbivore,
            DietType.Carnivore => SpeciesType.Carnivore,
            DietType.Omnivore => SpeciesType.Omnivore,
            DietType.Terraformer => species.Name switch
            {
                "Shroomer" => SpeciesType.Shroomer,
                "Sectid" => SpeciesType.Sectid,
                "Faeling" => SpeciesType.Faeling,
                _ => SpeciesType.Faeling
            },
            _ => SpeciesType.Herbivore
        };
        _entityManager.Species[entity] = new Species(speciesType, 0, SpeciesRegistry.GetId(species.Name));
        _entityManager.AddComponent(entity, ComponentFlags.Species);

        // Hunger from species
        _entityManager.Hungers[entity] = new Hunger(
            current: Vary(species.MaxHunger * 0.8f, variation),
            max: Vary(species.MaxHunger, variation),
            decayRate: Vary(species.HungerDecayRate, variation),
            starvationDamage: species.StarvationDamage
        );
        _entityManager.AddComponent(entity, ComponentFlags.Hunger);

        // Wander behavior from species
        _entityManager.Wanders[entity] = new Wander(
            speed: Vary(species.BaseWanderSpeed, variation),
            changeDirectionChance: Vary(species.DirectionChangeChance, variation)
        );
        _entityManager.AddComponent(entity, ComponentFlags.Wander);

        // Visuals from species (slight size variation)
        _entityManager.Renderables[entity] = new Renderable(
            species.BaseColor,
            Vary(species.BaseSize, 0.15f),
            species.Shape
        );
        _entityManager.AddComponent(entity, ComponentFlags.Renderable);

        // Terrain discomfort from species
        _entityManager.TerrainDiscomforts[entity] = new TerrainDiscomfort(
            threshold: Vary(species.DiscomfortThreshold, variation),
            decayRate: Vary(species.DiscomfortDecayRate, variation),
            grazingPressure: species.CanGraze ? Vary(species.GrazingPressure, variation) : 0f
        );
        _entityManager.AddComponent(entity, ComponentFlags.TerrainDiscomfort);

        // Terraform component for faction species
        if (species.Diet == DietType.Terraformer)
        {
            _entityManager.Terraforms[entity] = new Terraform(
                direction: species.TerraformDir,
                radius: Vary(species.TerraformRadius, variation),
                strength: Vary(species.TerraformStrength, variation),
                cooldown: (int)Vary(species.TerraformCooldown, variation)
            );
            _entityManager.AddComponent(entity, ComponentFlags.Terraform);
        }

        // Diet-specific components
        if (species.IsPrey)
        {
            // Prey component for fleeing
            _entityManager.Preys[entity] = new Prey(
                fleeRange: Vary(species.FleeRange, variation),
                fleeSpeedMultiplier: Vary(species.FleeSpeedMultiplier, variation)
            );
            _entityManager.AddComponent(entity, ComponentFlags.Prey);

            // Fear component for nuanced threat response
            _entityManager.Fears[entity] = new Fear(
                threshold: Vary(species.FearThreshold, variation),
                max: Vary(species.FearMax, variation),
                accumulationRate: Vary(species.FearAccumulationRate, variation),
                decayRate: Vary(species.FearDecayRate, variation),
                vigilanceDecay: Vary(species.FearVigilanceDecay, variation),
                response: species.DefaultFearResponse
            );
            _entityManager.AddComponent(entity, ComponentFlags.Fear);
        }

        if (species.IsPredator)
        {
            // Predator component for hunting
            _entityManager.Predators[entity] = new Predator(
                huntRange: Vary(species.HuntRange, variation),
                attackRange: Vary(species.AttackRange, variation),
                attackPower: Vary(species.AttackPower, variation),
                attackCooldown: (int)Vary(species.AttackCooldown, variation)
            );
            _entityManager.AddComponent(entity, ComponentFlags.Predator);
        }

        // Social behavior based on species default and group assignment
        SocialType socialType = species.DefaultSocialType;
        if (forceSolitary)
        {
            socialType = SocialType.Solitary;
        }

        var social = new Social(
            type: socialType,
            groupAffinity: socialType == SocialType.Solitary ? 0f : Vary(species.GroupAffinity, variation),
            preferredGroupSize: socialType == SocialType.Solitary ? 0f : Vary(species.PreferredGroupSize, variation),
            cohesionStrength: socialType == SocialType.Solitary ? 0f : Vary(species.CohesionStrength, variation),
            alignmentStrength: socialType == SocialType.Solitary ? 0f : Vary(species.AlignmentStrength, variation)
        );
        social.GroupId = groupId;
        social.LeadershipScore = isAlpha ? 0.8f : Vary(0.3f, 0.5f);
        _entityManager.Socials[entity] = social;
        _entityManager.AddComponent(entity, ComponentFlags.Social);

        // === Faction-specific components ===

        // Sectids: add Predator + FoodCarrier (they hunt AND carry food to nests)
        if (species.NestBreeder)
        {
            if (!species.IsPredator) // Don't double-add if already a predator
            {
                _entityManager.Predators[entity] = new Predator(
                    huntRange: Vary(species.HuntRange, variation),
                    attackRange: Vary(species.AttackRange, variation),
                    attackPower: Vary(species.AttackPower, variation),
                    attackCooldown: (int)Vary(species.AttackCooldown, variation)
                );
                _entityManager.AddComponent(entity, ComponentFlags.Predator);
            }
            _entityManager.FoodCarriers[entity] = new FoodCarrier(species.MaxCarryFood);
            _entityManager.AddComponent(entity, ComponentFlags.FoodCarrier);
        }

        // Shroomers: add Growth component (they grow over their lifetime)
        if (species.HasGrowth)
        {
            _entityManager.Growths[entity] = new Growth(
                maxScale: species.GrowthMaxScale,
                growthRate: species.GrowthRate);
            _entityManager.AddComponent(entity, ComponentFlags.Growth);
        }

        // Faelings spawned from crystals get their components from CrystalSystem,
        // not from GameManager, so no special handling needed here.
    }

    /// <summary>
    /// Spawns the player entity near the world center.
    /// Returns the entity ID on success, or -1 on failure.
    /// </summary>
    public int SpawnPlayer(float centerX, float centerY, World.WorldManager worldManager)
    {
        // Find walkable position near center
        for (int attempts = 0; attempts < 100; attempts++)
        {
            float x = centerX + _rng.Next(-10, 10);
            float y = centerY + _rng.Next(-10, 10);

            if (worldManager.IsSpawnable(x, y))
            {
                int playerEntity = _entityManager.CreateEntity();

                _entityManager.Positions[playerEntity] = new Position(x, y);
                _entityManager.AddComponent(playerEntity, ComponentFlags.Position);

                _entityManager.Velocities[playerEntity] = new Velocity();
                _entityManager.AddComponent(playerEntity, ComponentFlags.Velocity);

                _entityManager.Renderables[playerEntity] = new Renderable(
                    new Color(1f, 1f, 0f), 12f, ShapeType.Circle);
                _entityManager.AddComponent(playerEntity, ComponentFlags.Renderable);

                return playerEntity;
            }
        }

        return -1;
    }
}
