using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.World;
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
/// Test-scene extensions (all opt-in via the static toggles below, zero-cost when off):
///   decisions_*.csv / latest_decisions.csv — behavioral decision transitions (hunt/flee/roam/
///     escape) with the parameters that shaped them; filterable to a species subset.
///   terrain_*.csv / latest_terrain.csv     — per-interval tile-type histogram, mean moisture,
///     and terraform activity (every moisture nudge is counted centrally in
///     <see cref="WorldManager.Terraform"/>; tile-class shifts also land in the events CSV).
///   nutrition_*.csv / latest_nutrition.csv — per-interval grazing economy: world nutrition
///     total vs capacity plus consumed / regenerated / corpse-enriched flows.
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
    public static int TrackedSpeciesId
    {
        get => _trackedSpeciesId;
        set { _trackedSpeciesId = value; IsTrackingSpecies = true; }
    }
    private static int _trackedSpeciesId;

    /// <summary>
    /// Whether a species is being tracked. A separate flag rather than a negative sentinel:
    /// species ids come from string.GetHashCode() and are negative about half the time (Wolf is
    /// -1711233758), so every "id >= 0" guard silently disabled tracking for those species —
    /// the TrackSpecies debug feature simply did nothing for half the roster.
    /// </summary>
    public static bool IsTrackingSpecies { get; private set; }

    /// <summary>Turn per-entity tracking off.</summary>
    public static void ClearTrackedSpecies() => IsTrackingSpecies = false;

    // ── Test-scene logging configuration ─────────────────────────────────────
    // All of these must be set BEFORE the logger is constructed (the extra CSV files are only
    // opened when their feature is enabled). TestSceneManager sets them from the scenario.

    /// <summary>Population/stats snapshot cadence in ticks (default 100 = 5 s at 20 TPS).</summary>
    public static int SnapshotInterval { get; set; } = 100;

    /// <summary>Master switch for the behavioral decision log (decisions_*.csv).</summary>
    public static bool DecisionLoggingEnabled { get; set; }

    /// <summary>Restrict decision logging to these species IDs. Null = all species.</summary>
    public static HashSet<int>? DecisionSpeciesFilter { get; set; }

    /// <summary>Terrain histogram/terraform log cadence in ticks. 0 (default) = off.</summary>
    public static int TerrainLogInterval { get; set; }

    /// <summary>Nutrition economy log cadence in ticks. 0 (default) = off.</summary>
    public static int NutritionLogInterval { get; set; }

    public static bool TerrainLoggingEnabled => TerrainLogInterval > 0;
    public static bool NutritionLoggingEnabled => NutritionLogInterval > 0;

    /// <summary>
    /// True when a decision by this species would actually be written. Callers use this to
    /// skip building detail strings for the (default) disabled case.
    /// </summary>
    public static bool DecisionLoggingFor(int speciesId)
        => DecisionLoggingEnabled && Instance != null
           && (DecisionSpeciesFilter == null || DecisionSpeciesFilter.Contains(speciesId));

    private readonly StreamWriter _eventLog;
    private readonly StreamWriter _popLog;
    private readonly StreamWriter _statsLog;
    private readonly StreamWriter _latestEventLog;
    private readonly StreamWriter _latestPopLog;
    private readonly StreamWriter _latestStatsLog;
    private readonly StreamWriter? _decisionLog;
    private readonly StreamWriter? _latestDecisionLog;
    private readonly StreamWriter? _terrainLog;
    private readonly StreamWriter? _latestTerrainLog;
    private readonly StreamWriter? _nutritionLog;
    private readonly StreamWriter? _latestNutritionLog;

    // World reference for the terrain/nutrition chunk scans (null in worldless contexts).
    private readonly WorldManager? _world;

    private readonly Dictionary<int, int> _speciesCounts = new();
    private int _tick;

    // Per-snapshot-interval event counters — reset after each snapshot write.
    private readonly Dictionary<int, int> _intervalBirths          = new();
    private readonly Dictionary<int, int> _intervalDeathsStarve    = new();
    private readonly Dictionary<int, int> _intervalDeathsAge       = new();
    private readonly Dictionary<int, int> _intervalDeathsPred      = new();
    private readonly Dictionary<int, int> _intervalDeathsEnv       = new();
    private readonly Dictionary<int, int> _intervalKillsMade       = new();

    // Per-terrain-interval terraform counters (all directions, and class-crossing shifts).
    private int _intervalTerraformNudges;
    private int _intervalTerraformShifts;

    // Per-nutrition-interval flow counters (fed by WorldManager / TileRegenerationSystem).
    private float _intervalNutritionConsumed;
    private float _intervalNutritionRegen;
    private float _intervalNutritionEnriched;

    public EcosystemLogger(WorldManager? world = null, string logDir = "res://logs")
    {
        _world = world;
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

        if (DecisionLoggingEnabled)
        {
            const string decHeader = "tick,species,entity_id,x,y,system,decision,detail";
            _decisionLog       = Open(resolvedDir, $"decisions_{timestamp}.csv", decHeader);
            _latestDecisionLog = Open(resolvedDir, "latest_decisions.csv",       decHeader);
        }

        if (TerrainLoggingEnabled && world != null)
        {
            string terrainHeader = BuildTerrainHeader();
            _terrainLog       = Open(resolvedDir, $"terrain_{timestamp}.csv", terrainHeader);
            _latestTerrainLog = Open(resolvedDir, "latest_terrain.csv",       terrainHeader);
        }

        if (NutritionLoggingEnabled && world != null)
        {
            const string nutHeader =
                "tick,total_nutrition,total_capacity,fill_pct,consumed,regenerated,corpse_enriched";
            _nutritionLog       = Open(resolvedDir, $"nutrition_{timestamp}.csv", nutHeader);
            _latestNutritionLog = Open(resolvedDir, "latest_nutrition.csv",       nutHeader);
        }

        Instance = this;

        GD.Print("=========================================");
        GD.Print($"  ECOSYSTEM LOGS: {resolvedDir}");
        GD.Print($"  Quick access:   latest_events.csv");
        GD.Print($"                  latest_population.csv");
        GD.Print($"                  latest_species_stats.csv");
        if (_decisionLog != null)  GD.Print("                  latest_decisions.csv");
        if (_terrainLog != null)   GD.Print("                  latest_terrain.csv");
        if (_nutritionLog != null) GD.Print("                  latest_nutrition.csv");
        GD.Print("=========================================");
    }

    private static string BuildTerrainHeader()
    {
        var header = "tick";
        foreach (var name in Enum.GetNames<TileType>())
            header += $",{name}";
        return header + ",mean_moisture,terraform_nudges,terraform_shifts";
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
        if (_terrainLog != null && _tick % TerrainLogInterval == 0)
            WriteTerrainSnapshot();
        if (_nutritionLog != null && _tick % NutritionLogInterval == 0)
            WriteNutritionSnapshot();
    }

    // ── Decision log (test scenes) ────────────────────────────────────────────

    /// <summary>
    /// Log a behavioral decision transition (hunt/flee/roam/escape start/stop and the
    /// parameters that shaped it). Callers should gate on <see cref="DecisionLoggingFor"/>
    /// so detail strings are never built when the log is off. The detail must not contain
    /// commas (use ';' / '=' like the event log).
    /// </summary>
    public void LogDecision(int speciesId, int entityId, float x, float y,
                             string system, string decision, string detail = "")
    {
        if (_decisionLog == null) return;
        if (DecisionSpeciesFilter != null && !DecisionSpeciesFilter.Contains(speciesId)) return;
        var name = SpeciesRegistry.GetById(speciesId)?.Name ?? speciesId.ToString();
        string line = FormattableString.Invariant(
            $"{_tick},{name},{entityId},{x:F1},{y:F1},{system},{decision},{detail}");
        _decisionLog.WriteLine(line);
        _latestDecisionLog!.WriteLine(line);
    }

    // ── Terrain tracking (test scenes) ────────────────────────────────────────

    /// <summary>
    /// Record one terraform moisture nudge (called centrally from
    /// <see cref="WorldManager.Terraform"/> so every terraform path is covered). Nudges are
    /// aggregated into the terrain CSV; a nudge that crosses a tile-class boundary also gets
    /// a terraform_shift row in the events CSV.
    /// </summary>
    public void LogTerraform(float x, float y, TerraformDirection direction,
                              TileType oldTile, TileType newTile, float oldMoisture, float newMoisture)
    {
        if (!TerrainLoggingEnabled) return;
        _intervalTerraformNudges++;
        if (oldTile != newTile)
        {
            _intervalTerraformShifts++;
            WriteEvent($"{_tick},terraform_shift,Terrain,-1,{x:F1},{y:F1},{oldTile}->{newTile};dir={direction};moisture={oldMoisture:F3}->{newMoisture:F3}");
        }
    }

    // ── Nutrition tracking (test scenes) ──────────────────────────────────────

    /// <summary>Accumulate nutrition consumed by grazing/fertility feeding this interval.</summary>
    public void CountNutritionConsumed(float amount)
    {
        if (NutritionLoggingEnabled) _intervalNutritionConsumed += amount;
    }

    /// <summary>Accumulate nutrition regenerated by TileRegenerationSystem this interval.</summary>
    public void CountNutritionRegen(float amount)
    {
        if (NutritionLoggingEnabled) _intervalNutritionRegen += amount;
    }

    /// <summary>Accumulate nutrition added by corpse decomposition this interval.</summary>
    public void CountNutritionEnriched(float amount)
    {
        if (NutritionLoggingEnabled) _intervalNutritionEnriched += amount;
    }

    /// <summary>
    /// Full-world tile histogram + mean moisture + terraform activity row. A full chunk scan —
    /// only viable on the small worlds where terrain logging is enabled (test scenes).
    /// </summary>
    private void WriteTerrainSnapshot()
    {
        var counts = new int[Enum.GetValues<TileType>().Length];
        double moistureSum = 0;
        long tiles = 0;

        foreach (var chunk in _world!.GetLoadedChunks())
        {
            for (int y = 0; y < chunk.Size; y++)
            {
                for (int x = 0; x < chunk.Size; x++)
                {
                    counts[(int)chunk.GetTile(x, y)]++;
                    moistureSum += chunk.GetMoisture(x, y);
                    tiles++;
                }
            }
        }

        var line = _tick.ToString(CultureInfo.InvariantCulture);
        foreach (int c in counts) line += $",{c}";
        float meanMoisture = tiles > 0 ? (float)(moistureSum / tiles) : 0f;
        line += FormattableString.Invariant(
            $",{meanMoisture:F4},{_intervalTerraformNudges},{_intervalTerraformShifts}");
        _terrainLog!.WriteLine(line);
        _latestTerrainLog!.WriteLine(line);

        _intervalTerraformNudges = 0;
        _intervalTerraformShifts = 0;
    }

    /// <summary>
    /// World nutrition economy row: standing total vs capacity plus this interval's flows
    /// (consumed by feeding, regenerated by regrowth, enriched by decomposing corpses).
    /// </summary>
    private void WriteNutritionSnapshot()
    {
        double total = 0, capacity = 0;
        foreach (var chunk in _world!.GetLoadedChunks())
        {
            for (int y = 0; y < chunk.Size; y++)
            {
                for (int x = 0; x < chunk.Size; x++)
                {
                    float cap = chunk.GetTile(x, y).NutritionCap();
                    if (cap <= 0f) continue;
                    capacity += cap;
                    total += chunk.GetNutrition(x, y);
                }
            }
        }

        float fillPct = capacity > 0 ? (float)(total / capacity * 100.0) : 0f;
        string line = FormattableString.Invariant(
            $"{_tick},{total:F1},{capacity:F1},{fillPct:F1},{_intervalNutritionConsumed:F2},{_intervalNutritionRegen:F2},{_intervalNutritionEnriched:F2}");
        _nutritionLog!.WriteLine(line);
        _latestNutritionLog!.WriteLine(line);

        _intervalNutritionConsumed = 0f;
        _intervalNutritionRegen = 0f;
        _intervalNutritionEnriched = 0f;
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
        if (!IsTrackingSpecies || (attackerSid != tracked && targetSid != tracked)) return;
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

        // Structures and spores are tallied separately, NOT as creatures of their faction.
        // Iterate ALL entities (like the F3 overlay) rather than filtering on Species first:
        // nests and crystals carry no Species component, so a Species-gated query could never
        // see them and both columns were pinned at 0 for every run.
        foreach (int entity in em.AllEntities())
        {
            if (em.HasComponents(entity, ComponentFlags.Spore))   { spores++;   continue; }
            if (em.HasComponents(entity, ComponentFlags.Nest))    { nests++;    continue; }
            if (em.HasComponents(entity, ComponentFlags.Crystal)) { crystals++; continue; }
            // Spores carry Species(Shroomer) for type checks, which silently inflated every
            // "Shroomer" population figure in these CSVs before the split above.
            if (!em.HasComponents(entity, ComponentFlags.Species)) continue;

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
            if (IsTrackingSpecies && sid == trackedId)
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
        _decisionLog?.Dispose();
        _latestDecisionLog?.Dispose();
        _terrainLog?.Dispose();
        _latestTerrainLog?.Dispose();
        _nutritionLog?.Dispose();
        _latestNutritionLog?.Dispose();
    }
}
