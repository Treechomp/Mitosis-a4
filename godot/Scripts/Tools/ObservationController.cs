using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Tools;

/// <summary>
/// Observation and debugging tools layered over a running simulation: pick an individual out of
/// the world and read its live state, highlight every member of one species, and jump between
/// them. Built for answering "why is this species doing that?" — the population CSVs say a
/// species is starving, this says which of its drives is stuck.
///
/// Controls (handled by GameManager):
///   LMB              select the creature under the cursor
///   Tab / Shift+Tab  cycle the highlighted species (highlighted creatures render bright white)
///   G                jump the camera to the next member of the highlighted species
///   F                toggle free camera (detached from the player entity)
///   Escape           clear selection and highlight
/// </summary>
public sealed class ObservationController
{
    private readonly EntityManager _em;
    private readonly WorldManager _world;

    /// <summary>Currently inspected entity, or -1.</summary>
    public int SelectedEntity { get; private set; } = -1;

    /// <summary>Species id whose members are highlighted; only meaningful when HasHighlight.</summary>
    public int HighlightedSpeciesId { get; private set; }

    /// <summary>
    /// Whether a species is highlighted. A flag rather than a negative id sentinel: species ids
    /// are string hash codes and are negative for roughly half the roster, so "id >= 0" would
    /// silently refuse to highlight those species.
    /// </summary>
    public bool HasHighlight { get; private set; }

    public string HighlightedSpeciesName { get; private set; } = "";

    private readonly List<string> _speciesNames = new();
    private int _speciesCursor = -1;
    private int _jumpCursor;

    public ObservationController(EntityManager em, WorldManager world)
    {
        _em = em;
        _world = world;
        _speciesNames.AddRange(SpeciesRegistry.GetAllNames());
        _speciesNames.Sort();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Select the creature nearest the mouse. Candidates are compared in SCREEN space rather
    /// than by casting onto a ground plane: entities sit at their terrain elevation, so on any
    /// slope a plane-cast lands somewhere quite different from what the cursor is actually over.
    /// A full pass over renderables costs nothing on a one-off click.
    /// </summary>
    public bool SelectAtScreen(Camera3D camera, Vector2 screenPos, int tileSize, float heightScale)
    {
        const float maxPixelDistance = 40f;
        int best = -1;
        float bestDistSq = maxPixelDistance * maxPixelDistance;

        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;
        foreach (int entity in _em.Query(required))
        {
            ref var pos = ref _em.Positions[entity];
            float elevation = _world.GetElevation(pos.X, pos.Y);
            var world3D = GridCoordinates.VertexToWorld3D(pos.X, pos.Y, tileSize, elevation, heightScale);
            // Skip anything behind the camera — UnprojectPosition still returns coordinates for it.
            if (camera.IsPositionBehind(world3D)) continue;

            var screen = camera.UnprojectPosition(world3D);
            float dSq = screen.DistanceSquaredTo(screenPos);
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = entity;
            }
        }

        if (best >= 0) SelectedEntity = best;
        return best >= 0;
    }

    public void ClearSelection()
    {
        SelectedEntity = -1;
        HasHighlight = false;
        HighlightedSpeciesName = "";
        _speciesCursor = -1;
    }

    /// <summary>Drop a selection that has since died, so the panel never shows a stale corpse.</summary>
    public void Validate()
    {
        if (SelectedEntity >= 0 && !_em.IsAlive(SelectedEntity))
            SelectedEntity = -1;
    }

    // ── Species highlight / navigation ────────────────────────────────────────

    public void CycleSpecies(int direction)
    {
        if (_speciesNames.Count == 0) return;
        _speciesCursor += direction;
        // One step past either end returns to "no highlight", so the feature can be turned off
        // by cycling rather than needing a separate key.
        if (_speciesCursor >= _speciesNames.Count || _speciesCursor < -1)
        {
            _speciesCursor = -1;
            HasHighlight = false;
            HighlightedSpeciesName = "";
            return;
        }
        if (_speciesCursor < 0)
        {
            HasHighlight = false;
            HighlightedSpeciesName = "";
            return;
        }
        HighlightedSpeciesName = _speciesNames[_speciesCursor];
        HighlightedSpeciesId = SpeciesRegistry.GetId(HighlightedSpeciesName);
        HasHighlight = true;
        _jumpCursor = 0;
    }

    /// <summary>Highlight the selected creature's own species (quick "show me the rest of these").</summary>
    public void HighlightSelectedSpecies()
    {
        if (SelectedEntity < 0 || !_em.HasComponents(SelectedEntity, ComponentFlags.Species)) return;
        int sid = _em.Species[SelectedEntity].SpeciesId;
        var def = SpeciesRegistry.GetById(sid);
        if (def == null) return;
        HighlightedSpeciesId = sid;
        HighlightedSpeciesName = def.Name;
        HasHighlight = true;
        _speciesCursor = _speciesNames.IndexOf(def.Name);
    }

    /// <summary>
    /// Next living member of the highlighted species, walking the population in a stable order so
    /// repeated presses tour the whole species rather than flipping between the same two.
    /// Returns false when the species has no members left — itself a useful answer.
    /// </summary>
    public bool TryGetNextMember(out float x, out float y, out int entity)
    {
        x = y = 0f;
        entity = -1;
        if (!HasHighlight) return false;

        var members = new List<int>();
        foreach (int e in _em.Query(ComponentFlags.Species | ComponentFlags.Position))
        {
            if (_em.HasComponents(e, ComponentFlags.Carrion)) continue;
            if (_em.Species[e].SpeciesId == HighlightedSpeciesId) members.Add(e);
        }
        if (members.Count == 0) return false;

        _jumpCursor = (_jumpCursor + 1) % members.Count;
        entity = members[_jumpCursor];
        x = _em.Positions[entity].X;
        y = _em.Positions[entity].Y;
        SelectedEntity = entity;
        return true;
    }

    public int CountSpecies(int speciesId)
    {
        if (!HasHighlight) return 0;
        int n = 0;
        foreach (int e in _em.Query(ComponentFlags.Species))
        {
            if (_em.HasComponents(e, ComponentFlags.Carrion)) continue;
            if (_em.Species[e].SpeciesId == speciesId) n++;
        }
        return n;
    }

    // ── Inspector panel ───────────────────────────────────────────────────────

    /// <summary>
    /// Live readout for the selected creature. Deliberately reports the DRIVES (hunger, fear,
    /// discomfort, hunt target, roam target) rather than just vitals: a species starving amid
    /// abundant prey is a behaviour bug, and this is where you see which drive is stuck.
    /// </summary>
    public string BuildInspectorText()
    {
        if (SelectedEntity < 0 || !_em.IsAlive(SelectedEntity))
            return "";

        int e = SelectedEntity;
        var sb = new StringBuilder(512);

        string name = "unknown";
        if (_em.HasComponents(e, ComponentFlags.Species))
        {
            var def = SpeciesRegistry.GetById(_em.Species[e].SpeciesId);
            if (def != null) name = def.Name;
        }
        else if (_em.HasComponents(e, ComponentFlags.Nest)) name = "Sectid nest";
        else if (_em.HasComponents(e, ComponentFlags.Crystal)) name = "Faeling crystal";
        else if (_em.HasComponents(e, ComponentFlags.Carrion)) name = "carcass";

        sb.Append("── ").Append(name).Append(" #").Append(e).Append(" ──\n");

        if (_em.HasComponents(e, ComponentFlags.Position))
        {
            ref var p = ref _em.Positions[e];
            sb.Append(FormattableString.Invariant(
                $"pos {p.X:F1},{p.Y:F1}  tile {_world.GetTile(p.X, p.Y)}  nutrition {_world.GetNutrition(p.X, p.Y):F2}\n"));
        }

        if (_em.HasComponents(e, ComponentFlags.Hunger))
        {
            ref var h = ref _em.Hungers[e];
            sb.Append(FormattableString.Invariant($"hunger  {h.Percent * 100f,5:F1}%  ({h.Current:F0}/{h.Max:F0})\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.Energy))
        {
            ref var en = ref _em.Energies[e];
            sb.Append(FormattableString.Invariant(
                $"energy  {en.Percent * 100f,5:F1}%  ({en.Current:F0}/{en.Max:F0})"));
            if (en.RegenCooldown > 0) sb.Append("  [in combat]");
            sb.Append('\n');
        }
        if (_em.HasComponents(e, ComponentFlags.Age))
        {
            ref var a = ref _em.Ages[e];
            sb.Append(FormattableString.Invariant(
                $"age     {a.Current}/{a.MaxLifespan}  {(a.IsMature ? "mature" : "juvenile")}\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.Growth))
        {
            ref var g = ref _em.Growths[e];
            sb.Append(FormattableString.Invariant($"growth  {g.CurrentScale:F2}/{g.MaxScale:F2}"));
            if (g.EnrageTicks > 0) sb.Append("  [ENRAGED ").Append(g.EnrageTicks).Append(']');
            else if (g.EnrageCooldown > 0) sb.Append("  [recharging ").Append(g.EnrageCooldown).Append(']');
            sb.Append('\n');
        }

        // === Drives ===
        if (_em.HasComponents(e, ComponentFlags.Predator))
        {
            ref var pred = ref _em.Predators[e];
            sb.Append("hunt    ");
            if (pred.HasTarget && _em.IsAlive(pred.TargetEntity))
            {
                string prey = _em.HasComponents(pred.TargetEntity, ComponentFlags.Species)
                    ? SpeciesRegistry.GetById(_em.Species[pred.TargetEntity].SpeciesId)?.Name ?? "?"
                    : "?";
                ref var tp = ref _em.Positions[pred.TargetEntity];
                ref var mp = ref _em.Positions[e];
                float d = MathF.Sqrt(MathUtils.DistanceSquared(mp.X, mp.Y, tp.X, tp.Y));
                sb.Append(FormattableString.Invariant($"{prey} #{pred.TargetEntity} at {d:F1} tiles"));
            }
            else sb.Append("no target");
            sb.Append(FormattableString.Invariant(
                $"  phase={pred.Phase} role={pred.Role} range={pred.HuntRange:F0}"));
            if (pred.Stealth > 0f) sb.Append(FormattableString.Invariant($" stealth={pred.Stealth:F2}"));
            sb.Append('\n');
        }
        if (_em.HasComponents(e, ComponentFlags.Prey))
        {
            ref var prey = ref _em.Preys[e];
            sb.Append(FormattableString.Invariant(
                $"flee    {(prey.IsFleeing ? "FLEEING" : "calm")}  stamina={prey.Stamina:F2} range={prey.FleeRange:F0}\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.Fear))
        {
            ref var f = ref _em.Fears[e];
            sb.Append(FormattableString.Invariant(
                $"fear    {f.Ratio * 100f,5:F1}%  response={f.Response}{(f.IsVigilant ? " (vigilant)" : "")}\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.TerrainDiscomfort))
        {
            ref var d = ref _em.TerrainDiscomforts[e];
            sb.Append(FormattableString.Invariant(
                $"comfort discomfort={d.Ratio * 100f:F0}%{(d.IsEscaping ? " ESCAPING" : "")}\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.Wander))
        {
            ref var w = ref _em.Wanders[e];
            sb.Append(w.IsRoaming
                ? FormattableString.Invariant($"roam    -> {w.RoamTargetX:F0},{w.RoamTargetY:F0}\n")
                : FormattableString.Invariant($"roam    idle (cooldown {w.RoamCooldown})\n") as string);
        }
        if (_em.HasComponents(e, ComponentFlags.Social))
        {
            ref var s = ref _em.Socials[e];
            sb.Append(FormattableString.Invariant(
                $"social  {s.Type} group={s.GroupId} leader={s.RecognizedLeader}\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.FoodCarrier))
        {
            ref var c = ref _em.FoodCarriers[e];
            sb.Append(FormattableString.Invariant(
                $"colony  carrying={c.FoodCarried:F1}/{c.MaxCarry:F0} nest={c.TargetNest}"));
            sb.Append(c.IsHibernating ? " DORMANT\n" : "\n");
        }
        if (_em.HasComponents(e, ComponentFlags.Nest))
        {
            ref var n = ref _em.Nests[e];
            sb.Append(FormattableString.Invariant(
                $"nest    colony={n.ColonyId} stage={n.Stage} food={n.FoodStored:F0}\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.Carrion))
        {
            ref var c = ref _em.Carrions[e];
            sb.Append(FormattableString.Invariant($"carcass nutrition={c.Nutrition:F1}\n"));
        }
        if (_em.HasComponents(e, ComponentFlags.SimulationLOD))
            sb.Append($"lod     {_em.SimulationLODs[e].Level}\n");

        return sb.ToString();
    }
}
