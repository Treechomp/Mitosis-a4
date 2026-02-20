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
/// to CSV files for analysis. Intended for the debug branch only.
///
/// Logs are written to res://logs/ (the godot/logs/ directory in the project).
/// Two sets of files are created:
///   - Timestamped files: events_YYYYMMDD_HHmmss.csv, population_YYYYMMDD_HHmmss.csv
///   - Latest files: latest_events.csv, latest_population.csv (always the current run)
/// Use the "latest" files for quick access — they're overwritten each run.
/// </summary>
public sealed class EcosystemLogger : ISystem
{
    /// <summary>Global instance for easy access from other systems. Null when logging disabled.</summary>
    public static EcosystemLogger? Instance { get; private set; }

    /// <summary>Full OS path to the log directory, printed at startup for easy access.</summary>
    public static string? LogDirectory { get; private set; }

    private readonly StreamWriter _eventLog;
    private readonly StreamWriter _popLog;
    private readonly StreamWriter _latestEventLog;
    private readonly StreamWriter _latestPopLog;
    private readonly Dictionary<int, int> _speciesCounts = new();
    private int _tick;

    // Snapshot interval in ticks (every 100 ticks = 5 seconds at 20 TPS)
    private const int SnapshotInterval = 100;

    public EcosystemLogger(string logDir = "res://logs")
    {
        // Resolve Godot path to OS path (res:// = project directory)
        string resolvedDir = ProjectSettings.GlobalizePath(logDir);
        Directory.CreateDirectory(resolvedDir);
        LogDirectory = resolvedDir;

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // Timestamped files (archived per run)
        _eventLog = new StreamWriter(Path.Combine(resolvedDir, $"events_{timestamp}.csv"));
        _eventLog.WriteLine("tick,event,species,entity_id,x,y,detail");
        _eventLog.AutoFlush = true;

        _popLog = new StreamWriter(Path.Combine(resolvedDir, $"population_{timestamp}.csv"));
        _popLog.AutoFlush = true;

        // Latest files (overwritten each run — always the current session)
        _latestEventLog = new StreamWriter(Path.Combine(resolvedDir, "latest_events.csv"));
        _latestEventLog.WriteLine("tick,event,species,entity_id,x,y,detail");
        _latestEventLog.AutoFlush = true;

        _latestPopLog = new StreamWriter(Path.Combine(resolvedDir, "latest_population.csv"));
        _latestPopLog.AutoFlush = true;

        Instance = this;

        GD.Print("=========================================");
        GD.Print($"  ECOSYSTEM LOGS: {resolvedDir}");
        GD.Print($"  Quick access:   latest_events.csv");
        GD.Print($"                  latest_population.csv");
        GD.Print("=========================================");
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
        var line = $"{_tick},birth,{name},{entityId},{x:F1},{y:F1},{detail}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a death from old age.</summary>
    public void LogAgeDeath(int speciesId, int entityId, float x, float y)
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        var line = $"{_tick},age_death,{name},{entityId},{x:F1},{y:F1},";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a starvation death.</summary>
    public void LogStarvation(int speciesId, int entityId, float x, float y)
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        var line = $"{_tick},starvation,{name},{entityId},{x:F1},{y:F1},";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a predation kill.</summary>
    public void LogKill(int predatorSpeciesId, int preySpeciesId, int predatorId, int preyId,
                         float x, float y)
    {
        var predName = SpeciesRegistry.GetById(predatorSpeciesId)?.Name ?? predatorSpeciesId.ToString();
        var preyName = SpeciesRegistry.GetById(preySpeciesId)?.Name ?? preySpeciesId.ToString();
        var line = $"{_tick},kill,{preyName},{preyId},{x:F1},{y:F1},killed_by:{predName}:{predatorId}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a reproduction event.</summary>
    public void LogReproduction(int speciesId, int parentId, float x, float y, int offspringCount)
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        var line = $"{_tick},reproduce,{name},{parentId},{x:F1},{y:F1},offspring:{offspringCount}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a drowning or suffocation death.</summary>
    public void LogEnvironmentDeath(int speciesId, int entityId, float x, float y, string cause)
    {
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        var line = $"{_tick},environment_death,{name},{entityId},{x:F1},{y:F1},{cause}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a hunt attempt (target acquired). Paired with kill or hunt_fail to track success rate.</summary>
    public void LogHuntStart(int predatorSpeciesId, int preySpeciesId, int predatorId, int preyId,
                              float predX, float predY)
    {
        var predName = SpeciesRegistry.GetById(predatorSpeciesId)?.Name ?? predatorSpeciesId.ToString();
        var preyName = SpeciesRegistry.GetById(preySpeciesId)?.Name ?? preySpeciesId.ToString();
        var line = $"{_tick},hunt_start,{predName},{predatorId},{predX:F1},{predY:F1},target:{preyName}:{preyId}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a failed hunt (target lost/escaped/abandoned).</summary>
    public void LogHuntFail(int predatorSpeciesId, int predatorId, float x, float y, string reason)
    {
        var predName = SpeciesRegistry.GetById(predatorSpeciesId)?.Name ?? predatorSpeciesId.ToString();
        var line = $"{_tick},hunt_fail,{predName},{predatorId},{x:F1},{y:F1},{reason}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a spore creation.</summary>
    public void LogSporeCreated(float x, float y)
    {
        var line = $"{_tick},spore_created,Shroomer,-1,{x:F1},{y:F1},";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log a spore maturing into a Shroomer.</summary>
    public void LogSporeMatured(float x, float y)
    {
        var line = $"{_tick},spore_matured,Shroomer,-1,{x:F1},{y:F1},";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    // --- Statistical sim aggregate events ---
    // These log population-level births/deaths from StatisticalSimSystem.
    // Position is chunk center; entity_id is -1; count is in the detail field.

    /// <summary>Log aggregate births from the statistical sim.</summary>
    public void LogStatBirths(int speciesId, int count, float chunkCenterX, float chunkCenterY)
    {
        if (count <= 0) return;
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        var line = $"{_tick},stat_birth,{name},-1,{chunkCenterX:F1},{chunkCenterY:F1},count:{count}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
    }

    /// <summary>Log aggregate deaths from the statistical sim, split by cause.</summary>
    public void LogStatDeaths(int speciesId, int count, float chunkCenterX, float chunkCenterY, string cause)
    {
        if (count <= 0) return;
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        var line = $"{_tick},stat_{cause},{name},-1,{chunkCenterX:F1},{chunkCenterY:F1},count:{count}";
        _eventLog.WriteLine(line);
        _latestEventLog.WriteLine(line);
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
            _latestPopLog.WriteLine(header);
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
        _latestPopLog.WriteLine(line);
    }

    public void Close()
    {
        _eventLog.Dispose();
        _popLog.Dispose();
        _latestEventLog.Dispose();
        _latestPopLog.Dispose();
    }
}
