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
/// Handles reproduction for mature, well-fed entities.
/// </summary>
public sealed class ReproductionSystem : ISystem
{
    private readonly World.WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly int _maxPopulation;
    private readonly Random _rng = SimRandom.Create();
    private readonly List<(float x, float y, SpeciesType speciesType, int speciesId, int groupId)> _toSpawn = new(32);
    private readonly List<int> _nearbyBuffer = new(64);

    private readonly EntityFactory _entityFactory;
    private readonly PopulationBudget? _budget;
    private readonly HabitatCapacity? _habitat;

    /// <summary>
    /// Fraction of the local density limit a species may fill before crowding starts costing it
    /// births. Below this, neighbours are free; above it the chance falls linearly to zero at the
    /// limit. Kept generous so a herd still forms — the point is a soft shoulder, not a smaller cap.
    /// </summary>
    private const float DensityFreeFraction = 0.5f;

    public ReproductionSystem(World.WorldManager worldManager, int maxPopulation,
                               SpatialHash spatialHash, EntityFactory entityFactory,
                               PopulationBudget? budget = null, HabitatCapacity? habitat = null)
    {
        _worldManager = worldManager;
        _maxPopulation = maxPopulation;
        _spatialHash = spatialHash;
        _entityFactory = entityFactory;
        _budget = budget;
        _habitat = habitat;
    }

    public void Process(EntityManager em)
    {
        _toSpawn.Clear();

        // Don't reproduce if at population cap
        if (em.CreatureCount >= _maxPopulation)
            return;

        // GUARD ORDER IS PART OF THE DESIGN: every pure array read and every cheap dice roll runs
        // ABOVE the spatial query below. The rule was violated here and cost real time — the
        // QueryRadius neighbour scan ran before a global dice roll that was rejecting 99.77% of
        // candidates, so the system spent 3.27 ms of a 25.5 ms tick (12.8%) building neighbour
        // lists for creatures that were about to be refused anyway. Anything cheap that can say
        // "no" belongs before anything expensive that can.
        //
        // The global pressure ramp that used to live here is GONE. It multiplied every species'
        // birth chance by one shared number p that fell to zero as the shared cap filled, which
        // is a competitive-exclusion filter, not a population brake: a species holds equilibrium
        // at b*p = d, so it survives only while b/d >= 1/p, and the fastest breeder keeps p pinned
        // near zero for everyone else. Measured at 99.88% of cap: p = 0.0023, and a composition of
        // 10,746 herbivores to 492 predators. Per-class budgets (PopulationBudget) replace it as
        // the ceiling, and the LOCAL density check below is now the primary brake — it is
        // per-species and diegetic, so a crowded rabbit warren cannot suppress wolf births.
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species |
                                         ComponentFlags.Hunger | ComponentFlags.Energy |
                                         ComponentFlags.Age | ComponentFlags.Reproduction;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: cooldowns count down at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

            ref var reproduction = ref em.Reproductions[entity];

            // Reduce cooldown
            if (reproduction.CurrentCooldown > 0)
            {
                reproduction.CurrentCooldown -= tickMult;
                continue;
            }

            ref var age = ref em.Ages[entity];
            if (!age.IsMature)
                continue;

            ref var species = ref em.Species[entity];
            var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);

            // Skip faction species that reproduce via special systems
            if (speciesDef.NestBreeder || speciesDef.SporeReproducer || speciesDef.CrystalSpawned)
                continue;

            ref var hunger = ref em.Hungers[entity];
            ref var energy = ref em.Energies[entity];

            // Check thresholds
            if (hunger.Current < reproduction.HungerThreshold ||
                energy.Current < reproduction.EnergyThreshold)
                continue;

            // Check population cap again
            if (em.CreatureCount + _toSpawn.Count >= _maxPopulation)
                break;

            // Per-class ceiling — a pure array read, so it sits above every world lookup and the
            // spatial query. EntityFactory checks it again at the moment of creation; this one is
            // here to avoid paying for a candidate whose class is already full.
            if (_budget != null && !_budget.CanSpawn(speciesDef))
            {
                _budget.LogRefusal(speciesDef);
                continue;
            }

            // How full this species' habitat already is, in the world this seed generated. A
            // dictionary read and a divide, so it sits with the other cheap gates and above every
            // world lookup. Per species and per world: it cannot select for fast breeders the way
            // one shared ramp did, because no species' crowding is in another's divisor.
            if (_habitat != null && _budget != null)
            {
                float habitatPass = _habitat.BirthPass(speciesDef, _budget.CountForSpecies(species.SpeciesId));
                if (habitatPass < 1f && _rng.NextDouble() > habitatPass)
                    continue;
            }

            ref var pos = ref em.Positions[entity];

            var parentTile = _worldManager.GetTile(pos.X, pos.Y);

            // Species that must come ashore to breed (penguins hauling out onto the ice) wait
            // until they are standing on that ground. WanderSystem walks them there — see
            // SpeciesDefinition.BreedingTiles.
            if (!speciesDef.CanBreedOnTile(parentTile))
                continue;

            // Fertility-coupled breeding: rich ground multiplies, exhausted ground barely breeds.
            // Gives a population a brake that isn't predation — a shoal that has eaten its water
            // down stops replacing itself there long before it starves.
            if (speciesDef.BreedingNutritionSensitivity > 0f)
            {
                float cap = parentTile.NutritionCap();
                float richness = cap > 0f
                    ? Math.Clamp(_worldManager.GetNutrition(pos.X, pos.Y) / cap, 0f, 1f) : 0f;
                float pass = 1f - speciesDef.BreedingNutritionSensitivity * (1f - richness);
                if (_rng.NextDouble() > pass)
                    continue;
            }

            // Find spawn position. Moved ABOVE the neighbour scan: it is two dice rolls and one
            // tile lookup, and it rejects outright (a landlocked spot for a fish, a lake for a
            // deer), so paying for a radius query first was pure waste.
            float spawnX = pos.X + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;
            float spawnY = pos.Y + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;

            // Only spawn on tiles valid for this species (aquatic spawn in water, etc.)
            if (!speciesDef.CanSpawnOnTile(_worldManager.GetTile(spawnX, spawnY)))
                continue;

            // === LOCAL DENSITY — the primary brake, and the last thing checked ===
            // Per-species and per-place, which is what the deleted global ramp was not: crowding
            // among rabbits says nothing about whether a wolf may breed, so this cannot select for
            // fast breeders the way one shared multiplier did. It is also diegetic — a creature
            // ringed by its own kind has no room or forage for young — where a number derived from
            // the engine's thread budget is not.
            //
            // Deliberately the most expensive guard and therefore the last: everything above can
            // reject a candidate without touching the spatial hash.
            if (_spatialHash != null)
            {
                float densityRadius = speciesDef.SocialRadius > 0 ? speciesDef.SocialRadius * 1.5f : 15f;
                _nearbyBuffer.Clear();
                _spatialHash.QueryRadius(pos.X, pos.Y, densityRadius, _nearbyBuffer);
                int sameSpeciesCount = 0;
                foreach (int other in _nearbyBuffer)
                {
                    if (other == entity || !em.IsAlive(other)) continue;
                    if (!em.HasComponents(other, ComponentFlags.Species)) continue;
                    if (em.Species[other].SpeciesId == species.SpeciesId)
                        sameSpeciesCount++;
                }

                // Graded, not a cliff. The old test was a hard cutoff at 2x preferred group size:
                // below it crowding cost nothing at all, and one animal over it stopped breeding
                // outright. With the global ramp gone this check carries the load the ramp used to,
                // so it needs to bite before the world is full rather than only at the edge —
                // otherwise every population runs flat out until it slams into its class budget.
                // Free below half the limit, then falling linearly to zero at the limit.
                float maxLocal = MathF.Max(6f, speciesDef.PreferredGroupSize * 2f);
                if (sameSpeciesCount >= maxLocal)
                    continue;
                float crowding = sameSpeciesCount / maxLocal;          // 0 = alone, 1 = at the limit
                if (crowding > DensityFreeFraction)
                {
                    float pass = 1f - (crowding - DensityFreeFraction) / (1f - DensityFreeFraction);
                    if (_rng.NextDouble() > pass)
                        continue;
                }
            }

            // Pay reproduction cost
            hunger.Current -= reproduction.HungerCost;
            energy.Current -= reproduction.EnergyCost;
            reproduction.CurrentCooldown = reproduction.Cooldown;

            EcosystemLogger.Instance?.LogReproduction(
                species.SpeciesId, entity, pos.X, pos.Y, reproduction.OffspringCount);

            // Queue offspring for spawning (inherit parent species)
            for (int i = 0; i < reproduction.OffspringCount; i++)
            {
                _toSpawn.Add((spawnX, spawnY, species.Type, species.SpeciesId,
                    em.HasComponents(entity, ComponentFlags.Social) ? em.Socials[entity].GroupId : -1));
            }
        }

        // Spawn offspring
        foreach (var (x, y, speciesType, speciesId, groupId) in _toSpawn)
        {
            if (em.CreatureCount >= _maxPopulation) break;
            SpawnCreature(em, x, y, speciesType, speciesId, groupId);
        }
    }

    /// <summary>
    /// Bring one offspring into the world. Delegates to EntityFactory so a creature born here is
    /// assembled exactly like one spawned at worldgen. This used to be a parallel copy of the
    /// assembly code, and it had drifted: newborns were created without TerrainDiscomfort, Fear,
    /// Social or Growth. The consequences were invisible in short tests but severe over a long
    /// run — born creatures could not drown, ignored terrain and grazing pressure, never built
    /// fear, and pack species could not be seen by pack coordination at all (it keys on Social),
    /// so an ageing wolf population lost the ability to hunt together as its founders died off.
    /// </summary>
    private void SpawnCreature(EntityManager em, float x, float y, SpeciesType speciesType,
        int speciesId, int groupId)
    {
        if (em.CreatureCount >= _maxPopulation) return;
        // Disabled species never spawn, even via reproduction (belt-and-suspenders: with no
        // initial population a disabled species can't reproduce anyway, but this keeps the
        // toggle a hard guarantee regardless of how offspring are queued).
        if (!SpeciesToggle.IsEnabled(speciesId)) return;

        var speciesDef = SpeciesRegistry.GetById(speciesId);
        if (speciesDef == null) return;

        // Offspring start at age 0 and inherit the parent's group so herds and packs stay whole
        // across generations instead of every newborn being an unaffiliated loner.
        _entityFactory.SpawnCreature(x, y, speciesDef, groupId, forceSolitary: false,
            isAlpha: false, startAge: 0);
    }
}
