using System;
using Godot;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Systems;
using Mitosis.World;

namespace Mitosis.Testing;

/// <summary>
/// Executes a scenario's [spawn] list: exact species counts at exact positions, with optional
/// scatter radius and grouping, plus faction structures (Sectid nests, Faeling crystals,
/// Shroomer spores). Unlike WorldSpawner (which distributes a population budget across the
/// whole map by niche), this places exactly what the scenario says, where it says — a test may
/// deliberately strand a creature on hostile terrain. A warning is printed when a spawn centre
/// isn't spawnable for the species, but the spawn still happens.
/// </summary>
public sealed class ScenarioSpawner
{
    private readonly WorldManager _world;
    private readonly EntityFactory _factory;
    private readonly EntityManager _entities;
    private readonly Random _rng;
    private int _nextGroupId = 1000; // clear of WorldSpawner's range for mixed setups

    public ScenarioSpawner(WorldManager world, EntityFactory factory, EntityManager entities,
                            Random rng)
    {
        _world = world;
        _factory = factory;
        _entities = entities;
        _rng = rng;
    }

    /// <summary>Run every spawn op. Returns the number of creatures spawned (excl. structures).</summary>
    public int Run(TestScenario scenario, NestSystem? nests, CrystalSystem? crystals,
                   SporeSystem? spores)
    {
        int creatures = 0;
        foreach (var op in scenario.Spawns)
        {
            switch (op.Kind)
            {
                case SpawnKind.Creature:
                    creatures += SpawnCreatures(op);
                    break;

                case SpawnKind.Nest:
                    SpawnNest(op, nests);
                    break;

                case SpawnKind.Crystal:
                    if (crystals == null) { GD.PushWarning("[Scenario] crystal spawn skipped: no CrystalSystem"); break; }
                    crystals.SpawnCrystal(_entities, op.X, op.Y);
                    GD.Print($"  [spawn] crystal @ {op.X:F0},{op.Y:F0}");
                    break;

                case SpawnKind.MyceliumHeart:
                    SpawnHeart(op);
                    break;

                case SpawnKind.Spore:
                    SpawnSpores(op, spores);
                    break;
            }
        }
        return creatures;
    }

    /// <summary>
    /// Seed a mycelium heart directly, so a scenario can put a Shroomer territory under siege
    /// without first waiting the thousands of ticks it takes a Shroomer to grow into founding
    /// scale. The parent species defaults to Shroomer, matching the spore op.
    /// </summary>
    private void SpawnHeart(SpawnOp op)
    {
        string speciesName = op.Species ?? "Shroomer";
        int speciesId = SpeciesRegistry.GetId(speciesName);
        int heart = Systems.MyceliumSystem.SpawnHeart(_entities, op.X, op.Y, speciesId);
        if (heart < 0)
        {
            GD.PushWarning($"[Scenario] heart @ {op.X:F0},{op.Y:F0} skipped: " +
                           $"{speciesName} has no MyceliumRadius");
            return;
        }
        GD.Print($"  [spawn] {speciesName} mycelium heart @ {op.X:F0},{op.Y:F0}");
    }

    private int SpawnCreatures(SpawnOp op)
    {
        var species = SpeciesRegistry.Get(op.Species!);
        if (!_world.IsSpawnableForSpecies(op.X, op.Y, species))
            GD.PushWarning($"[Scenario] {species.Name} spawn centre ({op.X:F0},{op.Y:F0}) is " +
                           $"{_world.GetTile(op.X, op.Y)} — not normally spawnable for this species; spawning anyway");

        int spawned = 0;
        if (op.Groups <= 0)
        {
            // Scattered individuals (species' default social type, no pre-assigned group).
            for (int i = 0; i < op.Count; i++)
            {
                var (x, y) = ScatterPosition(op, species);
                _factory.SpawnCreature(x, y, species, groupId: -1);
                spawned++;
            }
        }
        else
        {
            // Split the count into op.Groups groups, each with its own group id and alpha.
            int perGroup = Math.Max(1, op.Count / op.Groups);
            int remaining = op.Count;
            for (int g = 0; g < op.Groups && remaining > 0; g++)
            {
                int size = g == op.Groups - 1 ? remaining : Math.Min(perGroup, remaining);
                var (cx, cy) = op.Groups == 1 ? (op.X, op.Y) : ScatterPosition(op, species);
                int gid = _nextGroupId++;
                for (int i = 0; i < size; i++)
                {
                    // Group members cluster tightly around the group centre.
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = (float)(_rng.NextDouble() * 3f);
                    float x = i == 0 ? cx : cx + MathF.Cos(angle) * dist;
                    float y = i == 0 ? cy : cy + MathF.Sin(angle) * dist;
                    _factory.SpawnCreature(x, y, species, gid, forceSolitary: false, isAlpha: i == 0);
                    spawned++;
                }
                remaining -= size;
            }
        }

        GD.Print($"  [spawn] {species.Name} x{spawned} @ {op.X:F0},{op.Y:F0}" +
                 (op.Radius > 0 ? $" r{op.Radius:F0}" : "") +
                 (op.Groups > 0 ? $" in {op.Groups} group(s)" : ""));
        return spawned;
    }

    private void SpawnNest(SpawnOp op, NestSystem? nests)
    {
        if (nests == null) { GD.PushWarning("[Scenario] nest spawn skipped: no NestSystem"); return; }
        nests.SpawnNest(_entities, op.X, op.Y, op.ColonyId);

        var sectidDef = SpeciesRegistry.Get("Sectid");
        int starters = 0;
        for (int i = 0; i < op.StarterSectids; i++)
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2);
            float dist = 2f + (float)(_rng.NextDouble() * 4f);
            _factory.SpawnCreature(op.X + MathF.Cos(angle) * dist, op.Y + MathF.Sin(angle) * dist,
                sectidDef, op.ColonyId);
            starters++;
        }
        GD.Print($"  [spawn] nest @ {op.X:F0},{op.Y:F0} colony={op.ColonyId} sectids={starters}");
    }

    private void SpawnSpores(SpawnOp op, SporeSystem? spores)
    {
        if (spores == null) { GD.PushWarning("[Scenario] spore spawn skipped: no SporeSystem"); return; }
        int parentId = SpeciesRegistry.GetId(op.Species ?? "Shroomer");
        for (int i = 0; i < op.Count; i++)
        {
            var (x, y) = ScatterPosition(op, species: null);
            spores.SpawnSpore(_entities, x, y, parentId);
        }
        GD.Print($"  [spawn] spore x{op.Count} @ {op.X:F0},{op.Y:F0} (parent {op.Species ?? "Shroomer"})");
    }

    /// <summary>
    /// A position within the op's scatter radius. Prefers spawnable-for-species tiles (up to 8
    /// attempts) but falls back to whatever the last roll produced — exact placement beats
    /// silent relocation in a test harness.
    /// </summary>
    private (float x, float y) ScatterPosition(SpawnOp op, SpeciesDefinition? species)
    {
        if (op.Radius <= 0f) return (op.X, op.Y);

        float x = op.X, y = op.Y;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2);
            float dist = (float)(_rng.NextDouble() * op.Radius);
            x = op.X + MathF.Cos(angle) * dist;
            y = op.Y + MathF.Sin(angle) * dist;
            if (species == null || _world.IsSpawnableForSpecies(x, y, species))
                break;
        }
        return (x, y);
    }
}
