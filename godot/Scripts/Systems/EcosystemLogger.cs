using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Files produced:
///   events_YYYYMMDD_HHmmss.csv / latest_events.csv        — individual events
///   population_YYYYMMDD_HHmmss.csv / latest_population.csv — per-species headcount every snapshot
///   species_stats_YYYYMMDD_HHmmss.csv / latest_species_stats.csv — per-interval breakdown
///     (births, deaths by cause, kills made, avg hunger%, avg energy%)
///
/// Set <see cref="TrackedSpeciesId"/> at runtime to enable verbose per-entity logging for one
/// species in the events file (prefixed with "TRACKED:"). Useful for diagnosing sudden die-offs.
/// </summary>
public sealed class EcosystemLogger : ISystem
{
    /// <summary>Global instance for easy access from other systems. Null when logging disabled.</summary>
    public static EcosystemLogger? Instance { get; private set; }

    /// <summary>Full OS path to the log directory, printed at startup for easy access.</summary>
    public static string? LogDirectory { get; private set; }

    /// <summary>
    /// Set to a valid species ID to get verbose per-entity events for that species.
    /// Use <c>SpeciesRegistry.GetId("Wolf")</c> etc. to resolve the ID.
    /// Set to -1 (default) to disable.
    /// </summary>
    public static int TrackedSpeciesId { get; set; } = -1;

    private readonly StreamWriter _eventLog;
    private readonly StreamWriter _popLog;
    private readonly StreamWriter _statsLog;
    private readonly StreamWriter _latestEventLog;
    private readonly StreamWriter _latestPopLog;
    private readonly StreamWriter _latestStatsLog;

    private readonly Dictionary<int, int> _speciesCounts = new();
    private int _tick;

    // Per-snapshot-interval event counters — reset after each snapshot write.
    private readonly Dictionary<int, int> _intervalBirths          = new();
    private readonly Dictionary<int, int> _intervalDeathsStarve    = new();
    private readonly Dictionary<int, int> _intervalDeathsAge       = new();
    private readonly Dictionary<int, int> _intervalDeathsPred      = new();
    private readonly Dictionary<int, int> _intervalDeathsEnv       = new();
    private readonly Dictionary<int, int> _intervalKillsMade       = new();

    // Snapshot interval in ticks (every 100 ticks = 5 seconds at 20 TPS)
    private const int SnapshotInterval = 100;

    public EcosystemLogger(string logDir = "res://logs")
    {
        string resolvedDir = ProjectSettings.GlobalizePath(logDir);
        Directory.CreateDirectory(resolvedDir);
        LogDirectory = resolvedDir;

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string evtHeader = "tick,event,species,entity_id,x,y,detail";

        _eventLog = Open(resolvedDir, $"events_{timestamp}.csv", evtHeader);
        _popLog   = Open(resolvedDir, $"population_{timestamp}.csv");
        _statsLog = Open(resolvedDir, $"species_stats_{timestamp}.csv", StatsHeader);

        _latestEventLog = Open(resolvedDir, "latest_events.csv",        evtHeader);
        _latestPopLog   = Open(resolvedDir, "latest_population.csv");
        _latestStatsLog = Open(resolvedDir, "latest_species_stats.csv", StatsHeader);

        Instance = this;

        GD.Print("=========================================");
        GD.Print($"  ECOSYSTEM LOGS: {resolvedDir}");
        GD.Print($"  Quick access:   latest_events.csv");
        GD.Print($"                  latest_population.csv");
        GD.Print($"                  latest_species_stats.csv");
        GD.Print("=========================================");
    }

    private const string StatsHeader =
        "tick,species,population,births,deaths_starve,deaths_age,deaths_predation," +
        "deaths_environment,kills_made,avg_hunger_pct,avg_energy_pct";

    private static StreamWriter Open(string dir, string file, string? header = null)
    {
        var w = new StreamWriter(Path.Combine(dir, file)) { AutoFlush = true };
        if (header != null) w.WriteLine(header);
        return w;
    }

    public void Process(EntityManager em)
    {
        _tick++;
        if (_tick % SnapshotInterval == 0)
            WritePopulationSnapshot(em);
    }

    // ── Event log helpers ─────────────────────────────────────────────────────

    /// <summary>Log a birth event (non-reproduction path).</summary>
    public void LogBirth(int speciesId, int entityId, float x, float y, string detail = "")
    {
        Increment(_intervalBirths, speciesId);
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        WriteEvent($"{_tick},birth,{name},{entityId},{x:F1},{y:F1},{detail}");
    }

    /// <summary>Log a death from old age.</summary>
    public void LogAgeDeath(int speciesId, int entityId, float x, float y)
    {
        Increment(_intervalDeathsAge, speciesId);
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        WriteEvent($"{_tick},age_death,{name},{entityId},{x:F1},{y:F1},");
    }

    /// <summary>Log a starvation death.</summary>
    public void LogStarvation(int speciesId, int entityId, float x, float y)
    {
        Increment(_intervalDeathsStarve, speciesId);
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        WriteEvent($"{_tick},starvation,{name},{entityId},{x:F1},{y:F1},");
    }

    /// <summary>Log a predation kill.</summary>
    public void LogKill(int predatorSpeciesId, int preySpeciesId, int predatorId, int preyId,
                         float x, float y)
    {
        Increment(_intervalDeathsPred, preySpeciesId);
        Increment(_intervalKillsMade, predatorSpeciesId);
        var predName = SpeciesRegistry.GetById(predatorSpeciesId)?.Name ?? predatorSpeciesId.ToString();
        var preyName = SpeciesRegistry.GetById(preySpeciesId)?.Name    ?? preySpeciesId.ToString();
        WriteEvent($"{_tick},kill,{preyName},{preyId},{x:F1},{y:F1},killed_by:{predName}:{predatorId}");
    }

    /// <summary>Log a reproduction event (offspring born).</summary>
    public void LogReproduction(int speciesId, int parentId, float x, float y, int offspringCount)
    {
        Increment(_intervalBirths, speciesId, offspringCount);
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        WriteEvent($"{_tick},reproduce,{name},{parentId},{x:F1},{y:F1},offspring:{offspringCount}");
    }

    /// <summary>Log a drowning or suffocation death.</summary>
    public void LogEnvironmentDeath(int speciesId, int entityId, float x, float y, string cause)
    {
        Increment(_intervalDeathsEnv, speciesId);
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        WriteEvent($"{_tick},environment_death,{name},{entityId},{x:F1},{y:F1},{cause}");
    }

    /// <summary>Log a hunt attempt (target acquired). Paired with kill or hunt_fail.</summary>
    public void LogHuntStart(int predatorSpeciesId, int preySpeciesId, int predatorId, int preyId,
                              float predX, float predY)
    {
        var predName = SpeciesRegistry.GetById(predatorSpeciesId)?.Name ?? predatorSpeciesId.ToString();
        var preyName = SpeciesRegistry.GetById(preySpeciesId)?.Name    ?? preySpeciesId.ToString();
        WriteEvent($"{_tick},hunt_start,{predName},{predatorId},{predX:F1},{predY:F1},target:{preyName}:{preyId}");
    }

    /// <summary>Log a failed hunt (target lost/escaped/abandoned).</summary>
    public void LogHuntFail(int predatorSpeciesId, int predatorId, float x, float y, string reason)
    {
        var predName = SpeciesRegistry.GetById(predatorSpeciesId)?.Name ?? predatorSpeciesId.ToString();
        WriteEvent($"{_tick},hunt_fail,{predName},{predatorId},{x:F1},{y:F1},{reason}");
    }

    /// <summary>
    /// Log a combat damage hit for tracked-species analysis.
    /// Emits a "damage_dealt" line when the attacker is the tracked species and/or a
    /// "damage_taken" line when the target is, so both perspectives appear in the events log.
    /// No-ops when <see cref="TrackedSpeciesId"/> is -1 or neither side matches it.
    /// </summary>
    public void LogCombatHit(int attackerSid, int attackerId,
                              int targetSid,   int targetId,
                              float damage, float x, float y, string source)
    {
        int tracked = TrackedSpeciesId;
        if (tracked < 0 || (attackerSid != tracked && targetSid != tracked)) return;
        var aName = SpeciesRegistry.GetById(attackerSid)?.Name ?? attackerSid.ToString();
        var tName = SpeciesRegistry.GetById(targetSid)?.Name   ?? targetSid.ToString();
        if (attackerSid == tracked)
            WriteEvent($"{_tick},damage_dealt,{aName},{attackerId},{x:F1},{y:F1},target:{tName}:{targetId} dmg={damage:F1} src={source}");
        if (targetSid == tracked)
            WriteEvent($"{_tick},damage_taken,{tName},{targetId},{x:F1},{y:F1},from:{aName}:{attackerId} dmg={damage:F1} src={source}");
    }

    /// <summary>Log a spore creation.</summary>
    public void LogSporeCreated(float x, float y)
        => WriteEvent($"{_tick},spore_created,Shroomer,-1,{x:F1},{y:F1},");

    /// <summary>Log a spore maturing into a Shroomer.</summary>
    public void LogSporeMatured(float x, float y)
        => WriteEvent($"{_tick},spore_matured,Shroomer,-1,{x:F1},{y:F1},");

    // ── Snapshot ──────────────────────────────────────────────────────────────

    private bool _headerWritten;
    private List<string>? _speciesNames;

    private void WritePopulationSnapshot(EntityManager em)
    {
        // Accumulate per-species counts and vitals in a single entity pass.
        _speciesCounts.Clear();
        var hungerSums  = new Dictionary<int, float>();
        var energySums  = new Dictionary<int, float>();
        int trackedId   = TrackedSpeciesId;
        int spores = 0, nests = 0, crystals = 0;

        const ComponentFlags required = ComponentFlags.Species;
        foreach (int entity in em.Query(required))
        {
            // Structures and spores are tallied separately, NOT as creatures of their faction —
            // spores carry Species(Shroomer) for type checks, which silently inflated every
            // "Shroomer" population figure in these CSVs (the F3 overlay already excluded them,
            // so the two disagreed). Dedicated columns keep the information without the lie.
            if (em.HasComponents(entity, ComponentFlags.Spore))   { spores++;   continue; }
            if (em.HasComponents(entity, ComponentFlags.Nest))    { nests++;    continue; }
            if (em.HasComponents(entity, ComponentFlags.Crystal)) { crystals++; continue; }

            ref var sp = ref em.Species[entity];
            int sid = sp.SpeciesId;
            _speciesCounts.TryGetValue(sid, out int cnt);
            _speciesCounts[sid] = cnt + 1;

            if (em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var h = ref em.Hungers[entity];
                float hRatio = h.Max > 0f ? h.Current / h.Max : 0f;
                if (!float.IsFinite(hRatio)) hRatio = 0f; // one bad entity must not blank the column
                hungerSums.TryGetValue(sid, out float hs);
                hungerSums[sid] = hs + hRatio;
            }

            if (em.HasComponents(entity, ComponentFlags.Energy))
            {
                ref var e = ref em.Energies[entity];
                float eRatio = e.Max > 0f ? e.Current / e.Max : 0f;
                if (!float.IsFinite(eRatio)) eRatio = 0f;
                energySums.TryGetValue(sid, out float es);
                energySums[sid] = es + eRatio;
            }

            // Verbose per-entity snapshot for the tracked species.
            if (trackedId >= 0 && sid == trackedId)
                WriteTrackedEntitySnapshot(entity, sid, em);
        }

        // Build/cache species name list (sorted, stable across snapshots).
        if (!_headerWritten)
        {
            _speciesNames = new List<string>(SpeciesRegistry.GetAllNames());
            _speciesNames.Sort();
            var header = "tick,total";
            foreach (var n in _speciesNames) header += $",{n}";
            header += ",spores,nests,crystals"; // structures/spores tracked apart from creatures
            _popLog.WriteLine(header);
            _latestPopLog.WriteLine(header);
            _headerWritten = true;
        }

        // Population CSV row. `total` is living creatures only (matches the F3 overlay).
        int total = 0;
        foreach (var c in _speciesCounts.Values) total += c;
        var popLine = $"{_tick},{total}";
        foreach (var n in _speciesNames!)
        {
            _speciesCounts.TryGetValue(SpeciesRegistry.GetId(n), out int count);
            popLine += $",{count}";
        }
        popLine += $",{spores},{nests},{crystals}";
        _popLog.WriteLine(popLine);
        _latestPopLog.WriteLine(popLine);

        // Per-species stats CSV: one row per species that has any population or events this interval.
        var allSids = new HashSet<int>(_speciesCounts.Keys);
        foreach (var d in new[] { _intervalBirths, _intervalDeathsStarve, _intervalDeathsAge,
                                   _intervalDeathsPred, _intervalDeathsEnv, _intervalKillsMade })
            foreach (var k in d.Keys) allSids.Add(k);

        foreach (int sid in allSids)
        {
            _speciesCounts.TryGetValue(sid, out int pop);
            _intervalBirths.TryGetValue(sid, out int births);
            _intervalDeathsStarve.TryGetValue(sid, out int dStarve);
            _intervalDeathsAge.TryGetValue(sid, out int dAge);
            _intervalDeathsPred.TryGetValue(sid, out int dPred);
            _intervalDeathsEnv.TryGetValue(sid, out int dEnv);
            _intervalKillsMade.TryGetValue(sid, out int kills);
            hungerSums.TryGetValue(sid, out float hSum);
            energySums.TryGetValue(sid, out float eSum);

            float avgHunger = pop > 0 ? hSum / pop * 100f : 0f;
            float avgEnergy = pop > 0 ? eSum / pop * 100f : 0f;

            var name = SpeciesRegistry.GetById(sid)?.Name ?? sid.ToString();
            // InvariantCulture so the F1 averages use '.' decimals (a ',' would break columns).
            var statsLine = FormattableString.Invariant(
                $"{_tick},{name},{pop},{births},{dStarve},{dAge},{dPred},{dEnv},{kills},{avgHunger:F1},{avgEnergy:F1}");
            _statsLog.WriteLine(statsLine);
            _latestStatsLog.WriteLine(statsLine);

            // Mass-perish alert: if this interval's death total exceeds 40% of previous population
            // and the species has meaningful population, log a prominent warning.
            int totalDeaths = dStarve + dAge + dPred + dEnv;
            int prevPop = pop + totalDeaths - births; // approximate previous pop in the interval
            if (prevPop >= 5 && totalDeaths > 0 && (float)totalDeaths / prevPop >= 0.4f)
            {
                WriteEvent($"{_tick},MASS_PERISH,{name},-1,0,0,lost {totalDeaths}/{prevPop} ({100f * totalDeaths / prevPop:F0}%) starve={dStarve} age={dAge} pred={dPred} env={dEnv}");
            }
        }

        // Reset interval counters.
        _intervalBirths.Clear();
        _intervalDeathsStarve.Clear();
        _intervalDeathsAge.Clear();
        _intervalDeathsPred.Clear();
        _intervalDeathsEnv.Clear();
        _intervalKillsMade.Clear();
    }

    /// <summary>
    /// Write a per-entity snapshot line for the currently tracked species, tagged TRACKED.
    /// Includes hunger%, energy%, whether it is fleeing or hunting, and its position.
    /// </summary>
    private void WriteTrackedEntitySnapshot(int entity, int sid, EntityManager em)
    {
        float hungerPct = 0f, energyPct = 0f;
        if (em.HasComponents(entity, ComponentFlags.Hunger))
        {
            ref var h = ref em.Hungers[entity];
            hungerPct = h.Max > 0f ? h.Current / h.Max * 100f : 0f;
        }
        if (em.HasComponents(entity, ComponentFlags.Energy))
        {
            ref var e = ref em.Energies[entity];
            energyPct = e.Max > 0f ? e.Current / e.Max * 100f : 0f;
        }

        bool hunting = em.HasComponents(entity, ComponentFlags.Predator)
                       && em.Predators[entity].HasTarget;
        bool fleeing = em.HasComponents(entity, ComponentFlags.Prey)
                       && em.Preys[entity].IsFleeing;

        ref var pos = ref em.Positions[entity];
        var name = SpeciesRegistry.GetById(sid)?.Name ?? sid.ToString();
        WriteEvent($"{_tick},TRACKED,{name},{entity},{pos.X:F1},{pos.Y:F1},hunger={hungerPct:F0}%;energy={energyPct:F0}%;hunting={hunting};fleeing={fleeing}");
    }

    /// <summary>
    /// Write a CSV event line to both the timestamped and latest event logs. Formats with
    /// <see cref="CultureInfo.InvariantCulture"/> so floats always use a '.' decimal separator —
    /// a ',' separator (the default on many locales) would inject stray commas and break columns.
    /// </summary>
    private void WriteEvent(FormattableString line)
    {
        string s = FormattableString.Invariant(line);
        _eventLog.WriteLine(s);
        _latestEventLog.WriteLine(s);
    }

    private static void Increment(Dictionary<int, int> d, int key, int by = 1)
    {
        d.TryGetValue(key, out int v);
        d[key] = v + by;
    }

    public void Close()
    {
        _eventLog.Dispose();
        _popLog.Dispose();
        _statsLog.Dispose();
        _latestEventLog.Dispose();
        _latestPopLog.Dispose();
        _latestStatsLog.Dispose();
    }
}
