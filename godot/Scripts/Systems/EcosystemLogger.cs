using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Logs ecosystem events (births, deaths, kills, starvation, population snapshots)
/// to a CSV file for analysis. Intended for the debug branch only.
/// </summary>
public sealed class EcosystemLogger : ISystem
{
    /// <summary>Global instance for easy access from other systems. Null when logging disabled.</summary>
    public static EcosystemLogger? Instance { get; private set; }

    private readonly StreamWriter _eventLog;
    private readonly StreamWriter _popLog;
    private readonly Dictionary<int, int> _speciesCounts = new();
    private int _tick;

    // Snapshot interval in ticks (every 100 ticks = 5 seconds at 20 TPS)
    private const int SnapshotInterval = 100;

    public EcosystemLogger(string logDir = "user://ecosystem_logs")
    {
        // Resolve Godot user:// path
        string resolvedDir = ProjectSettings.GlobalizePath(logDir);
        Directory.CreateDirectory(resolvedDir);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        _eventLog = new StreamWriter(Path.Combine(resolvedDir, $"events_{timestamp}.csv"));
        _eventLog.WriteLine("tick,event,species,entity_id,x,y,detail");
        _eventLog.AutoFlush = true;

        _popLog = new StreamWriter(Path.Combine(resolvedDir, $"population_{timestamp}.csv"));
        // Header written on first snapshot (once we know species list)
        _popLog.AutoFlush = true;

        Instance = this;
        GD.Print($"EcosystemLogger: writing to {resolvedDir}");
    }

    public void Process(EntityManager em)
    {
        _tick++;

        if (_tick % SnapshotInterval == 0)
            WritePopulationSnapshot(em);
    }

    /// <summary>Log a birth event.</summary>
    public void LogBirth(int speciesId, int entityId, float x, float y, string detail = "")
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        _eventLog.WriteLine($"{_tick},birth,{name},{entityId},{x:F1},{y:F1},{detail}");
    }

    /// <summary>Log a death from old age.</summary>
    public void LogAgeDeath(int speciesId, int entityId, float x, float y)
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        _eventLog.WriteLine($"{_tick},age_death,{name},{entityId},{x:F1},{y:F1},");
    }

    /// <summary>Log a starvation death.</summary>
    public void LogStarvation(int speciesId, int entityId, float x, float y)
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        _eventLog.WriteLine($"{_tick},starvation,{name},{entityId},{x:F1},{y:F1},");
    }

    /// <summary>Log a predation kill.</summary>
    public void LogKill(int predatorSpeciesId, int preySpeciesId, int predatorId, int preyId,
                         float x, float y)
    {
        var predName = SpeciesRegistry.GetById(predatorSpeciesId)?.Name ?? predatorSpeciesId.ToString();
        var preyName = SpeciesRegistry.GetById(preySpeciesId)?.Name ?? preySpeciesId.ToString();
        _eventLog.WriteLine($"{_tick},kill,{preyName},{preyId},{x:F1},{y:F1},killed_by:{predName}:{predatorId}");
    }

    /// <summary>Log a reproduction event.</summary>
    public void LogReproduction(int speciesId, int parentId, float x, float y, int offspringCount)
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        _eventLog.WriteLine($"{_tick},reproduce,{name},{parentId},{x:F1},{y:F1},offspring:{offspringCount}");
    }

    /// <summary>Log a spore creation.</summary>
    public void LogSporeCreated(float x, float y)
    {
        _eventLog.WriteLine($"{_tick},spore_created,Shroomer,-1,{x:F1},{y:F1},");
    }

    /// <summary>Log a spore maturing into a Shroomer.</summary>
    public void LogSporeMatured(float x, float y)
    {
        _eventLog.WriteLine($"{_tick},spore_matured,Shroomer,-1,{x:F1},{y:F1},");
    }

    private bool _headerWritten;
    private List<string>? _speciesNames;

    private void WritePopulationSnapshot(EntityManager em)
    {
        _speciesCounts.Clear();

        const ComponentFlags required = ComponentFlags.Species;
        foreach (int entity in em.Query(required))
        {
            ref var species = ref em.Species[entity];
            _speciesCounts.TryGetValue(species.SpeciesId, out int count);
            _speciesCounts[species.SpeciesId] = count + 1;
        }

        if (!_headerWritten)
        {
            // Build header from all known species
            _speciesNames = new List<string>(SpeciesRegistry.GetAllNames());
            _speciesNames.Sort();
            var header = "tick,total";
            foreach (var name in _speciesNames)
                header += $",{name}";
            _popLog.WriteLine(header);
            _headerWritten = true;
        }

        int total = em.EntityCount;
        var line = $"{_tick},{total}";
        foreach (var name in _speciesNames!)
        {
            int id = SpeciesRegistry.GetId(name);
            _speciesCounts.TryGetValue(id, out int count);
            line += $",{count}";
        }
        _popLog.WriteLine(line);
    }

    public void Close()
    {
        _eventLog.Dispose();
        _popLog.Dispose();
    }
}
