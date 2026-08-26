using System.Collections.Generic;
using Mitosis.ECS;
using Mitosis.World;

namespace Mitosis.Systems;

/// <summary>
/// The single definition of WHICH per-tick systems run and in WHAT ORDER.
///
/// Two callers build a simulation: <c>GameManager</c> (the game, and through it the test scene)
/// and <c>LodDifferentialRunner</c> (the headless LOD differential harness). Order matters —
/// LODSystem must run first so every other system can gate on <c>DueThisTick</c>, and several
/// pairs are deliberately adjacent (Carrion after hunting/fleeing, before NestSystem) — so a
/// second hand-copied list would let the thing under test drift away from the thing shipped.
/// With one builder, a system added to the game is automatically covered by the harness.
///
/// The <c>EcosystemLogger</c> is deliberately NOT added here: it is per-run configuration
/// (log directory, tracked species, CSV toggles) rather than simulation, and it must run LAST.
/// Callers append their own once it is built.
/// </summary>
public sealed class SimulationStack
{
    /// <summary>Runs first; writes DueThisTick and the LOD tier bookkeeping.</summary>
    public LODSystem Lod { get; }

    // Structure systems, exposed because spawning (and the scenario spawner) registers with them.
    public NestSystem Nest { get; }
    public SporeSystem Spore { get; }
    public CrystalSystem Crystal { get; }

    private SimulationStack(LODSystem lod, NestSystem nest, SporeSystem spore, CrystalSystem crystal)
    {
        Lod = lod;
        Nest = nest;
        Spore = spore;
        Crystal = crystal;
    }

    /// <summary>
    /// Append the full per-tick system stack to <paramref name="systems"/>, in run order.
    /// Construct this AFTER <c>SimRandom.SetSeed</c>: several systems take a random stream at
    /// construction time, and the streams are handed out in construction order.
    /// </summary>
    public static SimulationStack Build(
        List<ISystem> systems,
        WorldManager world,
        EntityFactory entityFactory,
        int chunkSize,
        int worldSizeChunks,
        float tileSize,
        int maxPopulation,
        PopulationBudget? budget = null)
    {
        var spatialHash = world.SpatialHash;

        // LODSystem must run FIRST to set tick gating
        var lod = new LODSystem(spatialHash);
        systems.Add(lod);
        systems.Add(new MovementSystem(chunkSize, worldSizeChunks, world));
        systems.Add(new SpatialHashUpdateSystem(spatialHash, world.PredatorHash));
        systems.Add(new TerrainDiscomfortSystem(world));
        systems.Add(new HungerSystem());
        systems.Add(new GrazingSystem(world, spatialHash));
        systems.Add(new WanderSystem(world, spatialHash: spatialHash));
        systems.Add(new HerdingSystem(spatialHash));
        systems.Add(new SeparationSystem(spatialHash));
        systems.Add(new CollisionSystem(spatialHash, collisionRadiusScale: 0.5f, tileSize: tileSize));
        systems.Add(new HuntingSystem(spatialHash, world));
        systems.Add(new FleeingSystem(spatialHash, world));
        // Carrion: corpses persist and are scavenged over time. Runs after hunting/fleeing so it
        // can steer idle hungry predators to carcasses, and before NestSystem so a chopping Sectid
        // is fed/held at the corpse before NestSystem decides whether to ferry the load home.
        systems.Add(new CarrionSystem(spatialHash, world));
        systems.Add(new AgingSystem());
        systems.Add(new ReproductionSystem(world, maxPopulation, spatialHash, entityFactory, budget));
        systems.Add(new TerraformSystem(world));
        systems.Add(new TileRegenerationSystem(world));

        // All four reproduction paths take the same budget. Handing it to only some of them is
        // precisely the defect being fixed — see PopulationBudget's "monoculture ratchet".
        var nest = new NestSystem(world, spatialHash, maxPopulation, budget);
        systems.Add(nest);
        var spore = new SporeSystem(world, spatialHash, maxPopulation, budget);
        systems.Add(spore);
        var crystal = new CrystalSystem(world, spatialHash, maxPopulation, budget);
        systems.Add(crystal);

        return new SimulationStack(lod, nest, spore, crystal);
    }
}
