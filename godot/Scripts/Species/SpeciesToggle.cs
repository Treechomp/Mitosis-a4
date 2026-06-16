using System;
using System.Collections.Generic;

namespace Mitosis.SpeciesData;

/// <summary>
/// Run-time enable/disable filter for species, configured before a run (e.g. from the
/// GameManager Inspector). Disabled species are never spawned — not at initial seeding, and
/// (because all reproduction and faction spread only continues an existing population) not
/// afterwards either. Lets balance runs isolate subsets of the ecosystem, e.g. "no faction
/// species present" or one predator at a time, for granular snapshots.
/// </summary>
public static class SpeciesToggle
{
    private static readonly HashSet<int> _disabledIds = new();
    private static readonly List<string> _disabledNames = new();

    /// <summary>Re-enable every species.</summary>
    public static void Reset()
    {
        _disabledIds.Clear();
        _disabledNames.Clear();
    }

    /// <summary>True if the species (by registry id) is allowed to spawn.</summary>
    public static bool IsEnabled(int speciesId) => !_disabledIds.Contains(speciesId);

    /// <summary>True if the species is allowed to spawn. Null is treated as enabled.</summary>
    public static bool IsEnabled(SpeciesDefinition def)
        => def == null || !_disabledIds.Contains(SpeciesRegistry.GetId(def.Name));

    public static int DisabledCount => _disabledNames.Count;
    public static IReadOnlyList<string> DisabledNames => _disabledNames;

    /// <summary>
    /// Disable a single species by exact name. Returns false (and does nothing) if the name
    /// isn't in the registry, so callers can surface typos instead of silently no-opping.
    /// </summary>
    public static bool Disable(string name)
    {
        bool known = false;
        foreach (var n in SpeciesRegistry.GetAllNames())
        {
            if (n == name) { known = true; break; }
        }
        if (!known) return false;

        if (_disabledIds.Add(SpeciesRegistry.GetId(name)))
            _disabledNames.Add(name);
        return true;
    }

    /// <summary>
    /// Configure from a free-form list of species names (comma / newline / semicolon separated)
    /// plus an optional "disable all faction species" flag. Resets first, so the result reflects
    /// exactly the given inputs. Returns any names that weren't recognised, for caller warnings.
    /// </summary>
    public static List<string> Configure(string disabledList, bool disableFactions)
    {
        Reset();
        var unknown = new List<string>();

        if (disableFactions)
            foreach (var faction in SpeciesRegistry.GetTerraformers())
                Disable(faction.Name);

        if (!string.IsNullOrWhiteSpace(disabledList))
        {
            var tokens = disabledList.Split(
                new[] { ',', '\n', '\r', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
                if (!Disable(token))
                    unknown.Add(token);
        }

        return unknown;
    }
}
