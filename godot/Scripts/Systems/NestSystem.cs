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
/// Manages Sectid nests: food accumulation, larvae spawning, nest growth,
/// colony tracking, and expedition founding.
///
/// Nest lifecycle:
/// - Sectids deliver food → nest stores it
/// - When food >= threshold, start growing larvae in available slot
/// - After spawn duration, new Sectid hatches
/// - Every 3 spawns, nest upgrades (more larvae slots)
/// - Max stage nest (3 slots) → try founding new nest nearby
/// - 5+ nests in proximity = colony → triggers long-distance expedition
/// </summary>
public sealed class NestSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly Random _rng = SimRandom.Create();
    private readonly List<int> _nearbyBuffer = new(64);

    /// <summary>How far a carrier will look for a nest to haul food back to.</summary>
    private const float NestSearchRadius = 60f;

    // Spawn tracking
    private readonly List<(float x, float y, int colonyId)> _pendingSpawns = new(16);
    private readonly List<(float x, float y, int colonyId)> _pendingNests = new(4);
    private int _nextColonyId = 1;
    private readonly int _maxPopulation;

    /// <summary>Per-class ceiling; Sectids are charged to the Faction budget.</summary>
    private readonly PopulationBudget? _budget;

    public NestSystem(WorldManager worldManager, SpatialHash spatialHash, int maxPopulation,
                       PopulationBudget? budget = null)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
        _maxPopulation = maxPopulation;
        _budget = budget;
    }

    /// <summary>Surplus (in larvae-equivalents) a nest must bank before founding another.</summary>
    private const float NestFoundingCostLarvae = 6f;

    /// <summary>Ticks between ambient terraform pulses from each standing nest.</summary>
    private const int AmbientTerraformInterval = 40;

    private int _ambientTick;

    public void Process(EntityManager em)
    {
        _pendingSpawns.Clear();
        _pendingNests.Clear();
        _ambientTick++;

        // Snapshot the nests once per tick — every carrier's nest lookup reads this list.
        RefreshNestCache(em);

        // Get Sectid species def for nest parameters
        var sectidDef = SpeciesRegistry.Get("Sectid");

        const ComponentFlags nestRequired = ComponentFlags.Position | ComponentFlags.Nest |
                                            ComponentFlags.Renderable;

        foreach (int entity in em.Query(nestRequired))
        {
            ref var nest = ref em.Nests[entity];
            ref var pos = ref em.Positions[entity];

            // === LARVAE PROCESSING ===
            // Check each larvae slot (up to nest.Stage slots active)
            ProcessLarvaeSlot(ref nest.SpawnTimer0, ref nest, ref pos, em, entity, sectidDef);
            if (nest.Stage >= 2)
                ProcessLarvaeSlot(ref nest.SpawnTimer1, ref nest, ref pos, em, entity, sectidDef);
            if (nest.Stage >= 3)
                ProcessLarvaeSlot(ref nest.SpawnTimer2, ref nest, ref pos, em, entity, sectidDef);

            // === FOOD → LARVAE CONVERSION ===
            // Try to fill empty larvae slots with food
            if (nest.FoodStored >= nest.FoodPerSpawn)
            {
                if (TryStartLarvae(ref nest))
                    nest.FoodStored -= nest.FoodPerSpawn;
            }

            // === STAGE ADVANCEMENT ===
            if (nest.SpawnsThisStage >= 3 && !nest.IsMaxStage)
            {
                nest.Stage++;
                nest.SpawnsThisStage = 0;

                // Visual growth
                ref var rend = ref em.Renderables[entity];
                rend.Size = 6f + nest.Stage * 3f; // Grows visually with stage
            }

            // === MAX STAGE: TRY FOUNDING NEW NEST ===
            // Founding costs a real surplus, not pocket change. At the old 2× a larva, any nest
            // that had eaten recently expanded, which is what made colony growth purely
            // exponential: there was no state in which a fed nest declined to build.
            float foundingCost = nest.FoodPerSpawn * NestFoundingCostLarvae;
            // Don't even attempt to expand while the faction budget is full. TryFoundNest QUEUES
            // the nest and the colony pays foundingCost for it a few lines below, so a nest
            // refused later at spawn time would cost a colony its whole surplus and leave nothing
            // standing — a hidden tax that would suppress Sectids far harder than the ceiling
            // itself. No refusal is logged here: a colony declining to expand is not a spawn being
            // turned away, and this branch would fire for every nest on every tick while full.
            bool factionHasRoom = _budget == null || _budget.CanSpawn(sectidDef);
            if (nest.IsMaxStage && nest.FoodStored >= foundingCost && factionHasRoom)
            {
                // Count nearby nests for colony check
                int nearbyNests = CountNearbyNests(em, pos.X, pos.Y, sectidDef.NestColonyRadius, entity);

                if (nearbyNests < sectidDef.NestsForExpedition)
                {
                    // Found new nest nearby — packs the colony tighter rather than creeping out
                    TryFoundNest(pos.X, pos.Y, sectidDef.NestSearchRadius, nest.ColonyId);
                }
                else
                {
                    // Colony full — expedition to found distant colony
                    TryFoundNest(pos.X, pos.Y, sectidDef.ExpeditionDistance, _nextColonyId++);
                }

                if (_pendingNests.Count > 0)
                    nest.FoodStored -= foundingCost;
            }

            // === AMBIENT TERRAFORMING ===
            // A standing nest keeps working the ground under it whether or not it is hatching.
            // Previously terraforming happened ONLY on a hatch, so interior nests — which get no
            // deliveries once the frontline moves past them — fell silent and the colony's mark on
            // the map stayed a scatter of small dry specks around whichever nests were still
            // feeding. Slow, continuous drying lets a colony's footprint close into one contiguous
            // territory, with the hatch burst still providing the fast advance at the frontier.
            if (_ambientTick % AmbientTerraformInterval == 0)
                AmbientTerraform(pos.X, pos.Y, sectidDef);
        }

        // === SECTID LIFE (food delivery + hibernation/waking) ===
        ProcessSectids(em);

        // === SPAWN PENDING SECTIDS (respect population cap AND the faction budget) ===
        // The faction check is the whole point of this change. NestSystem was the ONE creature
        // spawn path with no global throttle at all — ReproductionSystem and SporeSystem each had
        // the pressure ramp, this had only the hard cap. At the cap every death frees a slot, and
        // an unthrottled claimant takes it immediately while a throttled one needs hundreds of
        // attempts, so the Sectid share could only ever rise. Adding the old ramp here would have
        // moved the monoculture a third time; a per-class ceiling ends it, because now no path is
        // privileged over another.
        var sectidSpeciesId = SpeciesRegistry.GetId("Sectid");
        var sectidDefBudget = SpeciesRegistry.Get("Sectid");
        foreach (var (x, y, colonyId) in _pendingSpawns)
        {
            if (em.CreatureCount >= _maxPopulation) break;
            if (_budget != null && !_budget.CanSpawn(sectidDefBudget))
            {
                _budget.LogRefusal(sectidDefBudget);
                continue;
            }
            SpawnSectid(em, x, y, colonyId);
            EcosystemLogger.Instance?.LogReproduction(sectidSpeciesId, -1, x, y, 1);
        }

        // === SPAWN PENDING NESTS (respect population cap) ===
        // A nest is a structure, not a creature, so it is charged to no class — but founding one
        // is a bid to grow the colony, and a colony whose budget is full has nowhere to put the
        // brood it would hatch. Refusing the nest keeps the faction's footprint honest instead of
        // covering the map with structures that can never fill.
        foreach (var (x, y, colonyId) in _pendingNests)
        {
            if (em.CreatureCount >= _maxPopulation) break;
            if (_budget != null && !_budget.CanSpawn(sectidDefBudget))
            {
                _budget.LogRefusal(sectidDefBudget);
                continue;
            }
            SpawnNest(em, x, y, colonyId);
        }
    }

    private void ProcessLarvaeSlot(ref float timer, ref Nest nest, ref Position pos,
                                    EntityManager em, int nestEntity, SpeciesDefinition sectidDef)
    {
        if (timer < 0f) return; // Empty slot

        timer--;
        if (timer <= 0f)
        {
            // Larvae ready — spawn Sectid
            float offsetX = ((float)_rng.NextDouble() * 2f - 1f) * 3f;
            float offsetY = ((float)_rng.NextDouble() * 2f - 1f) * 3f;
            float spawnX = pos.X + offsetX;
            float spawnY = pos.Y + offsetY;

            if (_worldManager.IsSpawnable(spawnX, spawnY))
            {
                _pendingSpawns.Add((spawnX, spawnY, nest.ColonyId));
                nest.SpawnsThisStage++;
                // The colony reshapes the land around its nest as a brood emerges. Concentrated
                // at the stationary nest, so the imprint actually accumulates over many hatches.
                TerraformAroundNest(pos.X, pos.Y, sectidDef);
            }
            timer = -1f; // Clear slot
        }
    }

    // Moisture nudge per hatch tile. Raised 0.05→0.08 and the burst widened (6→14 nudges over
    // the doubled TerraformRadius): the old footprint dried a ~7-tile speck per colony lifetime,
    // leaving untouched green between nests instead of the spreading desertification Sectid
    // dominance is supposed to look like.
    private const float NestTerraformStep = 0.08f;

    private void TerraformAroundNest(float x, float y, SpeciesDefinition sectidDef)
    {
        const int nudges = 14;
        float radius = sectidDef.TerraformRadius;
        for (int i = 0; i < nudges; i++)
        {
            float ox = ((float)_rng.NextDouble() * 2f - 1f) * radius;
            float oy = ((float)_rng.NextDouble() * 2f - 1f) * radius;
            _worldManager.Terraform(x + ox, y + oy, sectidDef.TerraformDir, NestTerraformStep);
        }
    }

    /// <summary>
    /// Slow upkeep drying from a standing nest, independent of hatching. Reaches wider than the
    /// hatch burst so the footprints of clustered nests overlap into one continuous territory
    /// rather than a field of separate dry specks.
    /// </summary>
    private void AmbientTerraform(float x, float y, SpeciesDefinition sectidDef)
    {
        float radius = sectidDef.TerraformRadius * 2.5f;
        float ox = ((float)_rng.NextDouble() * 2f - 1f) * radius;
        float oy = ((float)_rng.NextDouble() * 2f - 1f) * radius;
        _worldManager.Terraform(x + ox, y + oy, sectidDef.TerraformDir, NestTerraformStep);
    }

    private bool TryStartLarvae(ref Nest nest)
    {
        // Fill first empty slot
        if (nest.SpawnTimer0 < 0f) { nest.SpawnTimer0 = nest.SpawnDuration; return true; }
        if (nest.Stage >= 2 && nest.SpawnTimer1 < 0f) { nest.SpawnTimer1 = nest.SpawnDuration; return true; }
        if (nest.Stage >= 3 && nest.SpawnTimer2 < 0f) { nest.SpawnTimer2 = nest.SpawnDuration; return true; }
        return false;
    }

    private int CountNearbyNests(EntityManager em, float x, float y, float radius, int excludeEntity)
    {
        int count = 0;
        _spatialHash.QueryRadius(x, y, radius, _nearbyBuffer);
        foreach (int other in _nearbyBuffer)
        {
            if (other == excludeEntity || !em.IsAlive(other)) continue;
            if (em.HasComponents(other, ComponentFlags.Nest))
                count++;
        }
        return count;
    }

    private void TryFoundNest(float originX, float originY, float distance, int colonyId)
    {
        // Try several random positions at roughly the given distance
        for (int attempt = 0; attempt < 10; attempt++)
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2);
            float dist = distance * (0.7f + (float)_rng.NextDouble() * 0.6f);
            float x = originX + MathF.Cos(angle) * dist;
            float y = originY + MathF.Sin(angle) * dist;

            var tile = _worldManager.GetTile(x, y);
            // Nests only on dry land tiles, away from water (no beach nests).
            // Shrubland/Steppe/Dirt are included because they are the INTERMEDIATE stages of the
            // colony's own drying (Grass → Shrubland → Steppe → Arid). Excluding them meant a
            // colony could never build inside the territory it had already worked — it could only
            // settle undried Grass or fully-desertified Arid, which forced nests ever outward and
            // produced the evenly-scattered dotting instead of a dense colony.
            if (tile == TileType.Arid || tile == TileType.Sand || tile == TileType.Grass ||
                tile == TileType.Savanna || tile == TileType.Tundra ||
                tile == TileType.Shrubland || tile == TileType.Steppe || tile == TileType.Dirt)
            {
                if (_worldManager.HasWaterNearby(x, y, 3))
                    continue; // Too close to water — likely a beach
                _pendingNests.Add((x, y, colonyId));
                return;
            }
        }
    }

    // === Hibernation tuning ===
    // A Sectid that stays hungry (failing to actually feed) for HibernateNoFoodTicks ticks — or
    // that drops to a critical hunger floor — retreats to its nest and goes dormant (low
    // metabolism). It wakes when huntable prey strays within WakeRadius. Driving this off feeding
    // success rather than prey *detection* is deliberate: detection within ~30 tiles was resetting
    // the timer every tick (there's nearly always a spore or stray herbivore around) so they never
    // went dormant and starved instead. This keeps a minimal viable colony alive through prey troughs.
    private const float HibernateHungerRatio = 0.35f;  // Below this, start counting toward dormancy
    private const float HibernateCriticalRatio = 0.15f; // At/below this, shelter immediately (no wait)
    private const int HibernateNoFoodTicks = 600;      // ~30s at 20 TPS of sustained hunger before dormancy
    private const float WakeRadius = 14f;              // Prey within this wakes a dormant Sectid

    // === Off-duty camping ===
    // A sated Sectid with no prey within IdleScanRadius for IdleCampTicks returns to the nest and
    // goes dormant. This is what makes a colony read as a colony — workers at the mound between
    // hunts — and it takes constant swarm pressure off the whole map.
    private const float IdleCampHungerRatio = 0.6f;    // Must be comfortably fed to stand down
    private const float IdleScanRadius = 26f;          // Roughly 2× hunt range: "nothing doing"
    private const int IdleCampTicks = 300;             // ~15s at 20 TPS with nothing to hunt

    private void ProcessSectids(EntityManager em)
    {
        var sectidDef = SpeciesRegistry.Get("Sectid");
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity |
                                        ComponentFlags.FoodCarrier | ComponentFlags.Species |
                                        ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

            ref var carrier = ref em.FoodCarriers[entity];
            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];

            // === Carrying food: deliver to nest (takes priority, cancels dormancy) ===
            if (carrier.IsCarrying)
            {
                carrier.IsHibernating = false;
                carrier.NoFoodTicks = 0;
                // Keep stripping a carcass until the sacks are full; only then ferry it home.
                // (CarrionSystem handles the feeding/holding-position while we're at the corpse.)
                // A big kill therefore takes repeated trips, often the whole colony, to clear.
                if (!carrier.IsFull && CorpseNearby(em, pos.X, pos.Y))
                    continue;
                DeliverFoodToNest(em, entity, ref carrier, ref pos, ref vel, sectidDef);
                continue;
            }

            // === Dormant: idle at nest, wake when prey approaches ===
            if (carrier.IsHibernating)
            {
                if (HuntablePreyNearby(em, entity, pos.X, pos.Y, WakeRadius))
                {
                    carrier.IsHibernating = false; // Prey in reach — rejoin the hunt
                    continue;
                }

                int nest = FindNearestNest(em, pos.X, pos.Y);
                if (nest < 0)
                {
                    carrier.IsHibernating = false; // No refuge left — resume normal behavior
                    continue;
                }

                // Drift toward the nest and settle motionless once there.
                ref var nestPos = ref em.Positions[nest];
                float dx = nestPos.X - pos.X;
                float dy = nestPos.Y - pos.Y;
                float distSq = dx * dx + dy * dy;
                float settleRange = sectidDef.FoodDeliveryRange;
                if (distSq <= settleRange * settleRange)
                {
                    vel.Dx = 0f;
                    vel.Dy = 0f;
                }
                else
                {
                    float dist = MathF.Sqrt(distSq);
                    float speed = sectidDef.CarryingSpeed;
                    vel.Dx = (dx / dist) * speed;
                    vel.Dy = (dy / dist) * speed;
                }
                continue;
            }

            // === Hungry and failing to feed: count down toward dormancy ===
            // Based on sustained low hunger (actual feeding failure), not prey detection — a Sectid
            // surrounded by prey it can't catch must still shelter rather than starve. The critical
            // floor guarantees it goes dormant before starving whenever a nest is reachable.
            ref var hunger = ref em.Hungers[entity];
            float hungerRatio = hunger.Current / hunger.Max;
            if (hungerRatio < HibernateHungerRatio)
            {
                carrier.NoFoodTicks += tickMult;
                bool critical = hungerRatio < HibernateCriticalRatio;
                if ((carrier.NoFoodTicks >= HibernateNoFoodTicks || critical) &&
                    FindNearestNest(em, pos.X, pos.Y) >= 0)
                {
                    carrier.IsHibernating = true;
                    carrier.NoFoodTicks = 0;
                }
            }
            else
            {
                carrier.NoFoodTicks = 0; // recovered — fed enough, stay active
            }

            // === Off duty: fed, nothing to hunt → go home and camp ===
            // Deliberately gated on being WELL FED, the opposite of the starvation shelter above:
            // a hungry colony keeps working, a fed one stops patrolling the whole map. Without
            // this a swarm never rests, because the only route to the nest was starving.
            bool onDuty = em.HasComponents(entity, ComponentFlags.Predator)
                          && em.Predators[entity].HasTarget;
            if (!onDuty && hungerRatio >= IdleCampHungerRatio
                && !HuntablePreyNearby(em, entity, pos.X, pos.Y, IdleScanRadius))
            {
                carrier.IdleTicks += tickMult;
                if (carrier.IdleTicks >= IdleCampTicks && FindNearestNest(em, pos.X, pos.Y) >= 0)
                {
                    carrier.IsHibernating = true;
                    carrier.IdleTicks = 0;
                }
            }
            else
            {
                carrier.IdleTicks = 0;
            }
        }
    }

    /// <summary>
    /// Carry-and-deliver logic: steer a food-laden Sectid to its nearest nest, deposit the
    /// food on arrival, and feed the carrier a little from the delivery.
    /// </summary>
    private void DeliverFoodToNest(EntityManager em, int entity, ref FoodCarrier carrier,
        ref Position pos, ref Velocity vel, SpeciesDefinition sectidDef)
    {
        // Find nearest nest if no target or target dead
        if (carrier.TargetNest < 0 || !em.IsAlive(carrier.TargetNest) ||
            !em.HasComponents(carrier.TargetNest, ComponentFlags.Nest))
        {
            carrier.TargetNest = FindNearestNest(em, pos.X, pos.Y);
            if (carrier.TargetNest < 0) return; // No nest found
        }

        ref var nestPos = ref em.Positions[carrier.TargetNest];
        float dx = nestPos.X - pos.X;
        float dy = nestPos.Y - pos.Y;
        float distSq = dx * dx + dy * dy;

        // Arrived at nest — deliver food
        float deliveryRange = sectidDef.FoodDeliveryRange;
        if (distSq < deliveryRange * deliveryRange)
        {
            ref var nest = ref em.Nests[carrier.TargetNest];
            nest.FoodStored += carrier.FoodCarried;
            carrier.FoodCarried = 0f;
            carrier.TargetNest = -1;

            // Also feed self a bit from the delivery
            if (em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var hunger = ref em.Hungers[entity];
                hunger.Current = MathF.Min(hunger.Max, hunger.Current + 3f);
            }
        }
        else
        {
            // Move toward nest (override wander velocity)
            float dist = MathF.Sqrt(distSq);
            float speed = sectidDef.CarryingSpeed;
            vel.Dx = (dx / dist) * speed;
            vel.Dy = (dy / dist) * speed;
        }
    }

    /// <summary>
    /// True if a huntable prey entity (anything with Prey that isn't another Sectid) is within
    /// the given radius. Shroomer spores and herbivores count as food; same-faction Sectids do not.
    /// </summary>
    /// <summary>True if an edible carcass is right here (a Sectid still chopping it).</summary>
    private bool CorpseNearby(EntityManager em, float x, float y)
    {
        const float radius = 3f;
        _nearbyBuffer.Clear();
        _spatialHash.QueryRadius(x, y, radius, _nearbyBuffer);
        foreach (int other in _nearbyBuffer)
        {
            if (!em.IsAlive(other) || !em.HasComponents(other, ComponentFlags.Carrion)) continue;
            if (!em.Carrions[other].IsDepleted) return true;
        }
        return false;
    }

    private bool HuntablePreyNearby(EntityManager em, int self, float x, float y, float radius)
    {
        _nearbyBuffer.Clear();
        _spatialHash.QueryRadius(x, y, radius, _nearbyBuffer);
        foreach (int other in _nearbyBuffer)
        {
            if (other == self || !em.IsAlive(other)) continue;
            if (!em.HasComponents(other, ComponentFlags.Prey)) continue;
            if (em.HasComponents(other, ComponentFlags.Species) &&
                em.Species[other].Type == SpeciesType.Sectid)
                continue; // Don't count our own kind as food
            return true;
        }
        return false;
    }

    /// <summary>
    /// Nests present this tick, refreshed once in Process. There are a handful of them and they are
    /// created/destroyed rarely, so a linear scan of this list beats what this used to do: a
    /// 60-TILE-radius spatial query, per carrier, per tick, just to locate one of five nests. At
    /// cell size 8 that query sweeps ~225 cells and walks every creature in them — and a Sectid
    /// colony is exactly where creatures are densest. It made NestSystem the third most expensive
    /// system in the game while doing almost no work.
    /// </summary>
    private readonly List<int> _nestCache = new(32);

    private void RefreshNestCache(EntityManager em)
    {
        _nestCache.Clear();
        foreach (int nest in em.Query(ComponentFlags.Nest | ComponentFlags.Position))
            _nestCache.Add(nest);
    }

    private int FindNearestNest(EntityManager em, float x, float y)
    {
        int best = -1;
        float bestDistSq = NestSearchRadius * NestSearchRadius;

        foreach (int nest in _nestCache)
        {
            if (!em.IsAlive(nest))
                continue;

            ref var nestPos = ref em.Positions[nest];
            float distSq = MathUtils.DistanceSquared(x, y, nestPos.X, nestPos.Y);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = nest;
            }
        }

        return best;
    }

    private void SpawnSectid(EntityManager em, float x, float y, int colonyId)
    {
        if (!em.HasRoomForEntity) return;

        var speciesDef = SpeciesRegistry.Get("Sectid");
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.SimulationLODs[entity] = new SimulationLOD(LODLevel.Full);
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        em.Species[entity] = new Species(SpeciesType.Sectid, 0, SpeciesRegistry.GetId("Sectid"));
        em.AddComponent(entity, ComponentFlags.Species);

        em.Ages[entity] = new Age(0, speciesDef.MaxLifespan, speciesDef.MaturityAge);
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(speciesDef.MaxEnergy, speciesDef.MaxEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        em.Hungers[entity] = new Hunger(speciesDef.MaxHunger * 0.7f, speciesDef.MaxHunger, speciesDef.HungerDecayRate,
            speciesDef.StarvationDamage);
        em.AddComponent(entity, ComponentFlags.Hunger);

        em.Wanders[entity] = new Wander(speciesDef.BaseWanderSpeed, speciesDef.DirectionChangeChance);
        em.AddComponent(entity, ComponentFlags.Wander);

        em.Renderables[entity] = new Renderable(speciesDef.BaseColor, speciesDef.BaseSize, speciesDef.Shape);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Sectids are prey (can be eaten by predators)
        em.Preys[entity] = new Prey(speciesDef.FleeRange, speciesDef.FleeSpeedMultiplier);
        em.AddComponent(entity, ComponentFlags.Prey);

        em.Fears[entity] = new Fear(
            speciesDef.FearThreshold, speciesDef.FearMax, speciesDef.FearAccumulationRate,
            speciesDef.FearDecayRate, speciesDef.FearVigilanceDecay, speciesDef.DefaultFearResponse);
        em.AddComponent(entity, ComponentFlags.Fear);

        // Sectids are also predators (hunt small prey and spores)
        em.Predators[entity] = new Predator(
            speciesDef.HuntRange, speciesDef.AttackRange, speciesDef.AttackPower, speciesDef.AttackCooldown);
        em.AddComponent(entity, ComponentFlags.Predator);

        // Food carrier — brings food back to nest
        em.FoodCarriers[entity] = new FoodCarrier(speciesDef.MaxCarryFood);
        em.AddComponent(entity, ComponentFlags.FoodCarrier);

        // Siege slot (SiegeSystem gates on StructureAggression, so this is inert for a species
        // that doesn't besiege). Kept in step with EntityFactory, which assembles the same species
        // on its own path — a Sectid hatched from a nest must be able to do what one spawned at
        // worldgen can.
        if (speciesDef.StructureAggression > 0f)
        {
            em.Sieges[entity] = new Siege(target: -1);
            em.AddComponent(entity, ComponentFlags.Siege);
        }

        // Social — pack behavior for group hunting
        var social = new Social(SocialType.Pack, speciesDef.GroupAffinity,
            speciesDef.PreferredGroupSize, speciesDef.CohesionStrength, speciesDef.AlignmentStrength);
        social.GroupId = colonyId; // Colony ID doubles as initial group
        em.Socials[entity] = social;
        em.AddComponent(entity, ComponentFlags.Social);

        // Terraform
        em.Terraforms[entity] = new Terraform(speciesDef.TerraformDir,
            speciesDef.TerraformRadius, speciesDef.TerraformStrength, speciesDef.TerraformCooldown);
        em.AddComponent(entity, ComponentFlags.Terraform);

        em.TerrainDiscomforts[entity] = new TerrainDiscomfort(
            speciesDef.DiscomfortThreshold, speciesDef.DiscomfortDecayRate);
        em.AddComponent(entity, ComponentFlags.TerrainDiscomfort);
    }

    public int SpawnNest(EntityManager em, float x, float y, int colonyId)
    {
        if (!em.HasRoomForEntity) return -1;

        var nestDef = SpeciesRegistry.Get("Sectid");
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        // Nests don't move
        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.Nests[entity] = new Nest(colonyId, foodPerSpawn: nestDef.NestFoodPerSpawn,
            spawnDuration: nestDef.NestSpawnDuration);
        em.AddComponent(entity, ComponentFlags.Nest);

        // A nest is a destructible objective. Health lives on Structure, not Energy: Energy is a
        // creature's stamina and dragged the nest into every system that regenerates or drains it,
        // each of which then needed teaching to skip structures. It also carried no owner, so
        // nothing could tell whose nest it was.
        em.Structures[entity] = new Structure(StructureKind.Nest, nestDef.NestEnergy,
            SpeciesRegistry.GetId("Sectid"));
        em.AddComponent(entity, ComponentFlags.Structure);

        // Visual: small brown square that grows with stage
        em.Renderables[entity] = new Renderable(
            new Color(0.6f, 0.4f, 0.2f), 9f, ShapeType.Square);
        em.AddComponent(entity, ComponentFlags.Renderable);

        _spatialHash.Update(entity, x, y);

        return entity;
    }
}
