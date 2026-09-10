using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Manages Faeling crystals and their linked Faelings.
///
/// Crystal lifecycle:
/// - Crystals are indestructible structures placed during world generation
/// - Each crystal spawns and links to one Faeling
/// - When the linked Faeling dies, crystal begins spawning a replacement
///   with half the deceased Faeling's power
/// - Faelings don't starve, grow slowly, gain power from kills and tile restoration
/// - Ranged attack allows kiting Sectid groups and fighting large Shroomers
/// </summary>
public sealed class CrystalSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly Random _rng = SimRandom.Create();

    private readonly List<(float x, float y, int crystalEntity, float inheritedPower)> _pendingFaelings = new(4);

    /// <summary>
    /// What each crystal remembers of its keeper: the traits of the Faeling it is currently linked
    /// to, refreshed while that Faeling lives, and all that is left of it once it does not.
    ///
    /// Taken from the living keeper rather than recorded at its death because a Faeling can die in
    /// four different systems, and a memory that has to be written at every one of them is a memory
    /// that will be missed at the fifth. Fidelity is a flat copy in this change; the design wants
    /// it purchasable, which is a crystal-economy decision recorded in
    /// docs/design/04-factions.md and deliberately not built here.
    /// </summary>
    private readonly Dictionary<int, (Genome genome, int generation)> _crystalMemory = new(16);
    private readonly List<int> _nearbyBuffer = new(32);
    private readonly List<int> _senseBuffer = new(64);
    private readonly int _maxPopulation;

    // Ticks between keeper dominance scans (the scan is a wide spatial query; ~8 keepers → cheap).
    private const int KeeperSenseInterval = 150;

    // Global faction census, refreshed once per keeper-sense interval and shared by all keepers.
    // Guards the dominance sense against its local-density blind spot: Sectid colonies are ALWAYS
    // locally dense (8+ per nest), so a purely local read had keepers besieging nests of a faction
    // that was globally collapsing (77→4) while Shroomers tripled elsewhere unopposed.
    // Where each faction is, worldwide — totals AND a per-chunk presence grid. Replaces the two
    // bare global counters this system used to keep: a keeper now knows not just that a faction is
    // winning but WHERE, which is what lets twelve of them matter across a 1152-tile world.
    private readonly FactionCensus _census;
    private int _censusCooldown;

    /// <summary>Per-class ceiling; Faelings are charged to the Faction budget.</summary>
    private readonly PopulationBudget? _budget;

    public CrystalSystem(WorldManager worldManager, SpatialHash spatialHash, int maxPopulation,
                          PopulationBudget? budget = null, FactionCensus? census = null)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
        _maxPopulation = maxPopulation;
        _budget = budget;
        _census = census ?? new FactionCensus(worldManager.ChunkSize, worldManager.WorldSizeChunks);
    }

    /// <summary>The world census this system maintains, for anything else that needs it.</summary>
    public FactionCensus Census => _census;

    public void Process(EntityManager em)
    {
        _pendingFaelings.Clear();

        // Refresh the global faction census on the sense cadence (one O(entities) pass).
        _censusCooldown--;
        if (_censusCooldown <= 0)
        {
            _censusCooldown = KeeperSenseInterval;
            _census.Refresh(em);
        }

        // === CRYSTAL PROCESSING ===
        const ComponentFlags crystalRequired = ComponentFlags.Position | ComponentFlags.Crystal;

        foreach (int entity in em.Query(crystalRequired))
        {
            ref var crystal = ref em.Crystals[entity];

            // Faeling aggregated into statistical sim — skip processing until materialized
            if (crystal.IsFaelingAggregated)
                continue;

            // Check if linked Faeling is still alive
            if (crystal.HasFaeling)
            {
                if (em.IsAlive(crystal.LinkedFaeling))
                {
                    // Keep the crystal's memory of its keeper current while there is one to read.
                    _crystalMemory[entity] = (Genome.From(em, crystal.LinkedFaeling),
                                              em.Species[crystal.LinkedFaeling].Generation);
                }
                else
                {
                    // Faeling died — start spawning replacement
                    // Try to inherit power from the dead faeling
                    // (power was stored in InheritedPower when faeling died, via FactionCombatSystem)
                    crystal.LinkedFaeling = -1;
                    crystal.SpawnTimer = crystal.SpawnDelay;
                }
                continue;
            }

            // Spawning countdown
            if (crystal.SpawnTimer > 0)
            {
                crystal.SpawnTimer--;
                if (crystal.SpawnTimer <= 0)
                {
                    ref var pos = ref em.Positions[entity];
                    _pendingFaelings.Add((pos.X, pos.Y, entity, crystal.InheritedPower));
                }
            }
        }

        // === FAELING POWER TRACKING ===
        // Faelings gain power from tile restoration (tracked here since TerraformSystem runs separately)
        const ComponentFlags faelingRequired = ComponentFlags.Position | ComponentFlags.FaelingPower |
                                                ComponentFlags.Terraform;

        foreach (int entity in em.Query(faelingRequired))
        {
            ref var power = ref em.FaelingPowers[entity];
            ref var growth = ref em.Growths[entity];

            // Power-based growth boost
            if (em.HasComponents(entity, ComponentFlags.Growth) &&
                em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var faeSpecies = ref em.Species[entity];
                var faeDef = SpeciesRegistry.GetById(faeSpecies.SpeciesId);
                // Faelings grow faster with more power
                float powerBoost = 1f + power.Power * 0.01f;
                growth.GrowthRate = faeDef.GrowthRate * powerBoost;
            }

            // Power affects ranged attack damage
            if (em.HasComponents(entity, ComponentFlags.RangedAttack) &&
                em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var faeSpecies = ref em.Species[entity];
                var faeDef = SpeciesRegistry.GetById(faeSpecies.SpeciesId);
                ref var ranged = ref em.RangedAttacks[entity];
                ranged.BaseDamage = faeDef.RangedAttackDamage + power.Power * 0.5f;
            }

            // Keeper sense: periodically judge which rival faction is locally over-dominant
            // (option ① "keeper of order" — suppress whoever is winning). Feeds the ranged
            // attack preference below and the siege patrol in WanderSystem.
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                var faeDef = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
                if (faeDef.KeeperSenseRadius > 0f)
                {
                    power.KeeperSenseCooldown--;
                    if (power.KeeperSenseCooldown <= 0)
                    {
                        power.KeeperSenseCooldown = KeeperSenseInterval;
                        SenseDominance(em, entity, ref power, faeDef);
                    }
                }
            }
        }

        // === RANGED ATTACK PROCESSING ===
        ProcessRangedAttacks(em);

        // === SPAWN PENDING FAELINGS (respect population cap AND the faction budget) ===
        // Like NestSystem, this path had only the hard cap and no throttle, so it was on the
        // winning side of the ratchet — it just never had the numbers to exploit it (8 Faelings in
        // a profiled 12k world). Gating it anyway is what makes "no path is privileged" true
        // rather than true-for-now.
        var faelingSpeciesId = SpeciesRegistry.GetId("Faeling");
        var faelingDefBudget = SpeciesRegistry.Get("Faeling");
        foreach (var (x, y, crystalEntity, inheritedPower) in _pendingFaelings)
        {
            if (em.CreatureCount >= _maxPopulation) break;
            if (_budget != null && !_budget.CanSpawn(faelingDefBudget))
            {
                _budget.LogRefusal(faelingDefBudget);
                continue;
            }
            int faeling = SpawnFaeling(em, x, y, crystalEntity, inheritedPower);
            if (faeling >= 0)
            {
                ref var crystal = ref em.Crystals[crystalEntity];
                crystal.LinkedFaeling = faeling;
                crystal.InheritedPower = 0f;
                EcosystemLogger.Instance?.LogReproduction(faelingSpeciesId, crystalEntity, x, y, 1);
            }
        }
    }

    /// <summary>
    /// Scan the keeper's surroundings and decide which rival faction is locally over-dominant:
    /// the leading faction must have at least <c>KeeperMinPresence</c> members nearby AND at
    /// least 1.5× the rival's count. Stores the winner and the centroid of its local members
    /// (the "hotspot" the siege patrol moves toward); anything less counts as balanced and the
    /// keeper falls back to plain terrain restoration. Spores and structures (nests/crystals)
    /// don't count — dominance is about active creatures.
    /// </summary>
    private void SenseDominance(EntityManager em, int entity, ref FaelingPower power,
        SpeciesDefinition faeDef)
    {
        ref var pos = ref em.Positions[entity];
        _senseBuffer.Clear();
        _spatialHash.QueryRadius(pos.X, pos.Y, faeDef.KeeperSenseRadius, _senseBuffer);

        int shroomers = 0, sectids = 0;
        float shroomX = 0f, shroomY = 0f, sectX = 0f, sectY = 0f;
        foreach (int other in _senseBuffer)
        {
            if (other == entity || !em.IsAlive(other)) continue;
            if (!em.HasComponents(other, ComponentFlags.Species)) continue;
            if (em.HasComponents(other, ComponentFlags.Spore) ||
                em.HasComponents(other, ComponentFlags.Nest) ||
                em.HasComponents(other, ComponentFlags.Crystal)) continue;

            ref var otherSpecies = ref em.Species[other];
            ref var otherPos = ref em.Positions[other];
            if (otherSpecies.Type == SpeciesType.Shroomer)
            {
                shroomers++;
                shroomX += otherPos.X;
                shroomY += otherPos.Y;
            }
            else if (otherSpecies.Type == SpeciesType.Sectid)
            {
                sectids++;
                sectX += otherPos.X;
                sectY += otherPos.Y;
            }
        }

        int lead = Math.Max(shroomers, sectids);
        int trail = Math.Min(shroomers, sectids);
        int shroomerId = SpeciesRegistry.GetId("Shroomer");
        int sectidId = SpeciesRegistry.GetId("Sectid");
        if (lead >= faeDef.KeeperMinPresence && lead * 2 >= trail * 3) // ≥1.5× margin
        {
            bool shroomerDominant = shroomers >= sectids;

            // Global-context guard: don't commit to suppressing a faction that is already
            // globally well behind its rival (≤ 2/3 of the rival's count) — a keeper's job is
            // checking the WINNER, and every colony/bloom looks locally dominant up close.
            int leadGlobal = _census.GlobalCount(shroomerDominant ? shroomerId : sectidId);
            int trailGlobal = _census.GlobalCount(shroomerDominant ? sectidId : shroomerId);
            if (leadGlobal * 3 < trailGlobal * 2)
            {
                power.KeeperFaction = 0;
                return;
            }

            power.KeeperFaction = (int)(shroomerDominant ? SpeciesType.Shroomer : SpeciesType.Sectid);
            power.KeeperHotspotX = shroomerDominant ? shroomX / shroomers : sectX / sectids;
            power.KeeperHotspotY = shroomerDominant ? shroomY / shroomers : sectY / sectids;
        }
        else
        {
            power.KeeperFaction = 0;
        }
    }

    private void ProcessRangedAttacks(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.RangedAttack |
                                         ComponentFlags.FaelingPower;
        _nearbyBuffer.Clear();

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: attack cooldown counts down at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

            ref var ranged = ref em.RangedAttacks[entity];
            ref var pos = ref em.Positions[entity];

            // Cooldown
            if (ranged.CurrentCooldown > 0)
            {
                ranged.CurrentCooldown -= tickMult;
                continue;
            }

            // Find terraformer target (Sectids and Shroomers — NOT other Faelings).
            // MANDATE. A species that only besieges the dominant faction only SHOOTS it either:
            // "keeper of order" cannot mean "kills whichever faction is nearest". Without this the
            // siege path checked the winner while the bolts went into whoever was underfoot, and
            // near a colony that is always a Sectid. Twelve immortal keepers firing on the weakest
            // faction ground it from 223 to 1 across a 20,000-tick run — and the only reason it
            // survived at all in an earlier build was that it could break crystals and reduce the
            // number of keepers shooting it. A faction should not have to kill its counterweight
            // to exist.
            //
            // While no faction is clearly ahead, a keeper has no mandate and holds its fire.
            int mandate = -1;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                int selfId = em.Species[entity].SpeciesId;
                var shooterDef = SpeciesRegistry.GetById(selfId);
                if (shooterDef != null && shooterDef.StructureTargetsDominantOnly)
                {
                    mandate = _census.DominantFactionId(shooterDef.KeeperMinPresence,
                        excludeSpeciesId: selfId);
                    if (mandate < 0) continue;
                }
            }

            // Keeper preference: if a rival faction is locally over-dominant (SenseDominance),
            // its members are preferred over the other faction's — the keeper suppresses
            // whoever is winning.
            _spatialHash.QueryRadius(pos.X, pos.Y, ranged.Range, _nearbyBuffer);
            int keeperFaction = em.FaelingPowers[entity].KeeperFaction;

            int bestTarget = -1, bestPreferred = -1;
            float bestDistSq = ranged.Range * ranged.Range;
            float bestPrefDistSq = bestDistSq;

            foreach (int other in _nearbyBuffer)
            {
                if (other == entity || !em.IsAlive(other)) continue;
                if (!em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Energy)) continue;

                ref var otherSpecies = ref em.Species[other];
                // Target only Sectids and Shroomers (not Faelings)
                if (otherSpecies.Type != SpeciesType.Sectid && otherSpecies.Type != SpeciesType.Shroomer)
                    continue;
                if (mandate >= 0 && otherSpecies.SpeciesId != mandate) continue;

                // Don't target spores (they have low priority)
                if (em.HasComponents(other, ComponentFlags.Spore)) continue;

                // Elder-safety: never bolt-duel a Shroomer whose growth-scaled AoE reach rivals
                // our attack range — 30 damage per combat pulse against 150 HP is a fight the
                // keeper loses (this is how the pre-keeper Faelings bled out, 8→5, in the bloom
                // run). Grown Shroomers are left to the siege: the keeper's balanced terraform
                // dries their substrate and #3's drought does the killing. Bolts are for the
                // young, still-spreading front and for Sectids.
                if (otherSpecies.Type == SpeciesType.Shroomer &&
                    em.HasComponents(other, ComponentFlags.Growth))
                {
                    var shroomDef = SpeciesRegistry.GetById(otherSpecies.SpeciesId);
                    float aoeReach = shroomDef.AoEAttackRadius *
                        shroomDef.GetGrowthScalingFactor(em.Growths[other].CurrentScale);
                    if (aoeReach + 2f >= ranged.Range)
                        continue;
                }

                ref var otherPos = ref em.Positions[other];
                float distSq = MathUtils.DistanceSquared(pos.X, pos.Y, otherPos.X, otherPos.Y);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestTarget = other;
                }
                if ((int)otherSpecies.Type == keeperFaction && distSq < bestPrefDistSq)
                {
                    bestPrefDistSq = distSq;
                    bestPreferred = other;
                }
            }

            if (bestPreferred >= 0)
                bestTarget = bestPreferred;

            if (bestTarget >= 0)
            {
                // Attack!
                ref var targetEnergy = ref em.Energies[bestTarget];
                targetEnergy.Current -= ranged.BaseDamage;
                targetEnergy.RegenCooldown = 60; // 3s combat cooldown at 20 TPS
                ranged.CurrentCooldown = ranged.Cooldown;
                ranged.TargetEntity = bestTarget;

                if (EcosystemLogger.IsTrackingSpecies
                    && em.HasComponents(entity, ComponentFlags.Species)
                    && em.HasComponents(bestTarget, ComponentFlags.Species))
                {
                    ref var tPos = ref em.Positions[bestTarget];
                    EcosystemLogger.Instance?.LogCombatHit(
                        em.Species[entity].SpeciesId, entity,
                        em.Species[bestTarget].SpeciesId, bestTarget,
                        ranged.BaseDamage, tPos.X, tPos.Y, "ranged");
                }

                // Check if kill — award power
                if (targetEnergy.IsDead)
                {
                    ref var power = ref em.FaelingPowers[entity];
                    power.Power += power.PowerPerKill;
                    power.KillCount++;

                    // Log ranged attack kill
                    if (em.HasComponents(entity, ComponentFlags.Species) &&
                        em.HasComponents(bestTarget, ComponentFlags.Species))
                    {
                        ref var killerSpecies = ref em.Species[entity];
                        ref var victimSpecies = ref em.Species[bestTarget];
                        ref var victimPos = ref em.Positions[bestTarget];
                        EcosystemLogger.Instance?.LogKill(
                            killerSpecies.SpeciesId, victimSpecies.SpeciesId,
                            entity, bestTarget, victimPos.X, victimPos.Y);
                    }

                    em.DestroyEntity(bestTarget);
                }
            }
            else
            {
                ranged.TargetEntity = -1;
            }
        }
    }

    private int SpawnFaeling(EntityManager em, float x, float y, int crystalEntity, float inheritedPower)
    {
        var speciesDef = SpeciesRegistry.Get("Faeling");

        // Offset spawn slightly from crystal
        float angle = (float)(_rng.NextDouble() * Math.PI * 2);
        float spawnX = x + MathF.Cos(angle) * 2f;
        float spawnY = y + MathF.Sin(angle) * 2f;

        if (!_worldManager.IsSpawnable(spawnX, spawnY))
        {
            spawnX = x;
            spawnY = y;
        }

        if (!em.HasRoomForEntity) return -1;

        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(spawnX, spawnY);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.SimulationLODs[entity] = new SimulationLOD(LODLevel.Full);
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        _crystalMemory.TryGetValue(crystalEntity, out var kept);
        em.Species[entity] = new Species(SpeciesType.Faeling,
            kept.genome != null ? kept.generation + 1 : 0, SpeciesRegistry.GetId("Faeling"));
        em.AddComponent(entity, ComponentFlags.Species);

        // Faelings live very long
        em.Ages[entity] = new Age(0, speciesDef.MaxLifespan, speciesDef.MaturityAge);
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(speciesDef.MaxEnergy, speciesDef.MaxEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        // Faelings don't starve — keep hunger always high
        em.Hungers[entity] = new Hunger(speciesDef.MaxHunger, speciesDef.MaxHunger, speciesDef.HungerDecayRate,
            speciesDef.StarvationDamage);
        em.AddComponent(entity, ComponentFlags.Hunger);

        em.Wanders[entity] = new Wander(speciesDef.BaseWanderSpeed, speciesDef.DirectionChangeChance);
        em.AddComponent(entity, ComponentFlags.Wander);

        em.Renderables[entity] = new Renderable(speciesDef.BaseColor, speciesDef.BaseSize, speciesDef.Shape);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Faelings are NOT prey (predators don't hunt them)
        // No Prey component, no Fear component

        // Terraform — restore balance
        em.Terraforms[entity] = new Terraform(speciesDef.TerraformDir,
            speciesDef.TerraformRadius, speciesDef.TerraformStrength, speciesDef.TerraformCooldown);
        em.AddComponent(entity, ComponentFlags.Terraform);

        // Growth — slow, power-boosted
        em.Growths[entity] = new Growth(maxScale: speciesDef.GrowthMaxScale, growthRate: speciesDef.GrowthRate);
        em.AddComponent(entity, ComponentFlags.Growth);

        // Power system
        em.FaelingPowers[entity] = new FaelingPower(crystalEntity, powerPerKill: 5f, powerPerTile: 0.2f);
        em.FaelingPowers[entity].Power = inheritedPower; // Inherit half of predecessor's power
        em.AddComponent(entity, ComponentFlags.FaelingPower);

        // Ranged attack
        em.RangedAttacks[entity] = new RangedAttack(
            range: speciesDef.RangedAttackRange,
            baseDamage: speciesDef.RangedAttackDamage + inheritedPower * 0.5f,
            cooldown: speciesDef.RangedAttackCooldown);
        em.AddComponent(entity, ComponentFlags.RangedAttack);

        // Social — solitary guardians
        em.Socials[entity] = new Social(SocialType.Solitary);
        em.AddComponent(entity, ComponentFlags.Social);

        em.TerrainDiscomforts[entity] = new TerrainDiscomfort(
            speciesDef.DiscomfortThreshold, speciesDef.DiscomfortDecayRate);
        em.AddComponent(entity, ComponentFlags.TerrainDiscomfort);

        // Siege slot — a keeper is a raider, and breaking the rival faction's structures is the
        // only way eight of them amount to a faction at all.
        if (speciesDef.StructureAggression > 0f)
        {
            em.Sieges[entity] = new Siege(target: -1);
            em.AddComponent(entity, ComponentFlags.Siege);
        }

        // Descent: a copy of what the crystal kept of its last keeper, mutated and banded like any
        // other birth. A crystal that has never held one — the first keeper of a run — produces a
        // founder, which is what every crystal did before this.
        if (kept.genome != null)
            kept.genome.Inherit(_rng, speciesDef).ApplyTo(em, entity);

        return entity;
    }

    /// <summary>
    /// Spawns a crystal at the given position. Called during world generation.
    /// </summary>
    public int SpawnCrystal(EntityManager em, float x, float y)
    {
        if (!em.HasRoomForEntity) return -1;

        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        var faelingDef = SpeciesRegistry.Get("Faeling");
        em.Crystals[entity] = new Crystal(spawnDelay: faelingDef.CrystalSpawnDelay);
        em.AddComponent(entity, ComponentFlags.Crystal);

        // Finite health. It used to be Energy(999999, 999999) — a sentinel infinity that made the
        // Faeling faction's only anchor literally indestructible, so there was nothing to take
        // from them and nothing for them to defend.
        em.Structures[entity] = new Structure(StructureKind.Crystal, faelingDef.CrystalHealth,
            SpeciesRegistry.GetId("Faeling"));
        em.AddComponent(entity, ComponentFlags.Structure);

        // Teal diamond visual
        em.Renderables[entity] = new Renderable(
            new Color(0.1f, 1f, 0.9f), 7f, ShapeType.Square);
        em.AddComponent(entity, ComponentFlags.Renderable);

        _spatialHash.Update(entity, x, y);

        return entity;
    }
}
