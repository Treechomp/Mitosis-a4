using System;
using Mitosis.SpeciesData;

namespace Mitosis.ECS;

/// <summary>The budget class an entity's population is charged to.</summary>
public enum PopClass : byte
{
    /// <summary>Not a creature (structure, spore, corpse, the player) — charged to nothing.</summary>
    None = 0,

    /// <summary>The prey base: everything living that is neither a hunter nor a faction.</summary>
    Herbivore = 1,

    /// <summary>Hunters that make their living on live prey.</summary>
    Predator = 2,

    /// <summary>Shroomer / Sectid / Faeling — the three rival factions.</summary>
    Faction = 3,
}

/// <summary>
/// Per-class population ceilings, and the live counts they are checked against.
///
/// WHY THIS EXISTS. <c>MaxPopulation</c> is an engineering number: tick + render cost saturates a
/// thread above roughly 12,000 creatures. It was being spent as though it were an ecological
/// force, in two ways that both reshaped the world invisibly.
///
/// 1. COMPETITIVE EXCLUSION. ReproductionSystem multiplied EVERY species' birth chance by one
///    global ramp p that fell to 0 as the shared cap filled. A species holds equilibrium when
///    b·p = d, so it survives only while b/d ≥ 1/p — as p falls, only the fastest breeders
///    persist, and the winner keeps p pinned near zero for everyone else. A profiled run at
///    99.88% of cap gave p = 0.0023 (99.77% of all births refused) and a composition of 10,746
///    herbivores to 492 predators. A 21.8:1 ratio is the signature of an r/K selection filter,
///    not an ecology — and prey spacing at that density was ~5.6 tiles, well inside every
///    predator's 8-15 tile HuntRange, so the predators were not search-limited. They were
///    birth-limited by a mechanism their prey wins by construction.
///
/// 2. THE MONOCULTURE RATCHET. The ramp was applied in ReproductionSystem and SporeSystem but not
///    in NestSystem or CrystalSystem. At the cap every death frees one slot; a throttled species
///    needs ~430 attempts to claim it and an unthrottled one claims it immediately, so the
///    unthrottled faction's share rises monotonically and cannot fall back. This is the same
///    defect that produced the documented Shroomer monoculture (docs/archive/faction-balance-plan-2026-08.md):
///    the ramp was added to SporeSystem and never to NestSystem, so the monoculture relocated to
///    the Sectids rather than resolving. Adding the ramp to the remaining paths would only move
///    it a third time — the fix is to stop having one shared pool at all.
///
/// A hard per-class ceiling has neither property. Each class is limited by its own budget, so a
/// herbivore boom cannot suppress predator births; and every spawn path is checked, so no
/// species can claim a slot another species is being refused.
///
/// WHAT THIS IS NOT. It is not an ecological brake and must not be used as one. It is a wall the
/// simulation should ideally never touch, and <see cref="LogRefusal"/> exists so that touching it
/// is loud: a run that refuses constantly is telling you the budget is wrong, or that ecology
/// (food, predation, space) is not binding where it should. The diegetic brakes are local density
/// in ReproductionSystem, crowding in SporeSystem, and food.
/// </summary>
public sealed class PopulationBudget
{
    // Default shares of MaxPopulation. GameManager exports these so they stay tunable in the
    // inspector; they live here so the headless harnesses build the same world the game does.
    // They deliberately sum to 0.88 — the remaining 12% is headroom for spores, structures and
    // transients, and is allocated to no class.
    public const float DefaultHerbivoreShare = 0.42f;   // ~5000 of 12000
    public const float DefaultPredatorShare  = 0.13f;   // ~1500
    public const float DefaultFactionShare   = 0.33f;   // ~4000

    private const int ClassCount = 4;   // None, Herbivore, Predator, Faction

    private readonly int[] _budget = new int[ClassCount];
    private readonly int[] _count = new int[ClassCount];
    private readonly long[] _refusals = new long[ClassCount];

    // Live count per species id, maintained by the same Track/Untrack calls that keep the class
    // counts. The class counts alone cannot answer "is this species' habitat full", and a
    // per-tick census of 12,000 entities to find out would cost more than every brake it feeds.
    private readonly System.Collections.Generic.Dictionary<int, int> _countBySpecies = new(32);

    // The species an entity is charged to, so the decrement is exactly the increment even if the
    // registry changes underneath us — the same guarantee _classOf gives the class counts.
    private readonly int[] _speciesOf = new int[EntityManager.MaxEntities];

    // Which class each live entity is charged to, so the decrement on death is exactly the
    // increment on birth even if the species registry changes underneath us.
    private readonly PopClass[] _classOf = new PopClass[EntityManager.MaxEntities];

    /// <summary>The engineering ceiling the shares are taken from.</summary>
    public int MaxPopulation { get; }

    public PopulationBudget(int maxPopulation, float herbivoreShare, float predatorShare,
                             float factionShare)
    {
        MaxPopulation = Math.Max(1, maxPopulation);
        _budget[(int)PopClass.Herbivore] = Share(herbivoreShare);
        _budget[(int)PopClass.Predator] = Share(predatorShare);
        _budget[(int)PopClass.Faction] = Share(factionShare);
        // Unclassified entities (structures, spores, corpses, the player) are charged to nothing
        // and are limited only by MaxPopulation / MaxEntities, as they were before.
        _budget[(int)PopClass.None] = int.MaxValue;
    }

    private int Share(float fraction) => Math.Max(0, (int)(MaxPopulation * Math.Clamp(fraction, 0f, 1f)));

    /// <summary>Ceiling for a class; <see cref="PopClass.None"/> is unlimited.</summary>
    public int BudgetFor(PopClass c) => _budget[(int)c];

    /// <summary>Live creatures currently charged to a class.</summary>
    public int CountFor(PopClass c) => _count[(int)c];

    /// <summary>Live creatures of one species.</summary>
    public int CountForSpecies(int speciesId)
        => _countBySpecies.TryGetValue(speciesId, out int n) ? n : 0;

    /// <summary>Spawns refused for a class since the run began.</summary>
    public long RefusalsFor(PopClass c) => _refusals[(int)c];

    /// <summary>
    /// Headroom the shares deliberately leave unallocated. The classes sum to less than 1 so that
    /// spores, structures and a transient over-shoot have somewhere to live without any class
    /// having to be squeezed for them.
    /// </summary>
    public int Headroom => MaxPopulation
        - _budget[(int)PopClass.Herbivore] - _budget[(int)PopClass.Predator] - _budget[(int)PopClass.Faction];

    // ══════════════════════════════════════════════════════════════════════════
    // Classification
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Which budget a species is charged to. Faction first (a Sectid is given
    /// <c>ComponentFlags.Predator</c> by EntityFactory and would otherwise read as a hunter),
    /// then hunters, then everything else.
    /// </summary>
    public static PopClass Classify(SpeciesDefinition? def)
    {
        if (def == null) return PopClass.None;

        if (def.NestBreeder || def.SporeReproducer || def.CrystalSpawned)
            return PopClass.Faction;

        // The three omnivores carry ComponentFlags.Predator (SpeciesDefinition.IsPredator covers
        // Carnivore AND Omnivore) but do not all live as hunters, and the class a species lands in
        // decides which ceiling its population competes for. Resolved by what the species eats for
        // a living and how numerous it is meant to be:
        //
        //  · Otter    → Predator.  A fish specialist (ExclusivePrey = Fish) that hunts for
        //                          everything it eats, and is deliberately sparse — a wide
        //                          territorial SocialRadius keeps it thinning a shoal rather than
        //                          eating it out. It belongs in the hunter budget and barely dents it.
        //  · Penguin  → Herbivore. Its staple is passive water-column foraging (FeedTiles); the
        //                          Fish hunting is opportunistic, and it is itself prey for shark,
        //                          polar bear and arctic fox. It also breeds in colonies, so
        //                          charging it to the small hunter budget would let one seabird
        //                          colony crowd out the world's actual predators — the very
        //                          exclusion this class split exists to prevent.
        //  · Boar     → Herbivore. Grazes for a living and hunts opportunistically; a sounder is
        //                          numerous and is itself wolf prey. Prey base, same reasoning.
        //
        // "Herbivore" here means the prey base rather than a strict diet — the name is the budget's,
        // not the biology's.
        switch (def.Name)
        {
            case "Otter": return PopClass.Predator;
            case "Penguin":
            case "Boar": return PopClass.Herbivore;
        }

        return def.IsPredator ? PopClass.Predator : PopClass.Herbivore;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The check
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// May one more of this species be born? A pure array read — cheap enough to sit above any
    /// spatial query in a spawn path, which is where it belongs.
    /// </summary>
    public bool CanSpawn(SpeciesDefinition? def)
    {
        var c = Classify(def);
        return _count[(int)c] < _budget[(int)c];
    }

    /// <summary>
    /// Record a refusal and emit the event. Kept separate from <see cref="CanSpawn"/> so a caller
    /// that probes speculatively (queueing candidates before it knows how many it can afford)
    /// doesn't inflate the count — only the paths that actually turned a spawn away report one.
    /// </summary>
    public void LogRefusal(SpeciesDefinition? def)
    {
        var c = Classify(def);
        _refusals[(int)c]++;
        Systems.EcosystemLogger.Instance?.LogPopulationBudgetRefused(
            def?.Name ?? "unknown", c.ToString(), _count[(int)c], _budget[(int)c]);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Live counts — maintained by EntityManager alongside CreatureCount
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Charge an entity to its class. Called when a Species component is attached. Idempotent:
    /// an entity already charged is left alone, so a repeated AddComponent cannot double-count.
    /// </summary>
    public void Track(int entity, int speciesId)
    {
        if ((uint)entity >= (uint)_classOf.Length) return;
        if (_classOf[entity] != PopClass.None) return;

        var c = Classify(SpeciesRegistry.GetById(speciesId));
        if (c == PopClass.None) return;
        _classOf[entity] = c;
        _count[(int)c]++;
        _speciesOf[entity] = speciesId;
        _countBySpecies.TryGetValue(speciesId, out int n);
        _countBySpecies[speciesId] = n + 1;
    }

    /// <summary>
    /// Release an entity's charge — on death, or when it turns out not to be a creature after all
    /// (a spore carries Species(Shroomer) for type checks, and structures and corpses may attach
    /// their marker component in either order relative to Species).
    /// </summary>
    public void Untrack(int entity)
    {
        if ((uint)entity >= (uint)_classOf.Length) return;
        var c = _classOf[entity];
        if (c == PopClass.None) return;
        _classOf[entity] = PopClass.None;
        _count[(int)c]--;

        int speciesId = _speciesOf[entity];
        _speciesOf[entity] = 0;
        if (speciesId != 0 && _countBySpecies.TryGetValue(speciesId, out int n))
            _countBySpecies[speciesId] = n - 1;
    }

    /// <summary>
    /// Split a seed population across the three classes in the same proportions as their
    /// ceilings, so a world STARTS in the shape its budgets will let it hold. Seeding by some
    /// other ratio (the old HerbivoreRatio of 0.85) just means the first few thousand ticks are
    /// spent filtering the composition back toward what the budgets permit — and under the old
    /// global ramp that filtering was the competitive-exclusion mechanism itself.
    /// </summary>
    public void SplitSeed(int total, out int herbivores, out int predators, out int factions)
    {
        long h = _budget[(int)PopClass.Herbivore];
        long p = _budget[(int)PopClass.Predator];
        long f = _budget[(int)PopClass.Faction];
        long sum = h + p + f;
        if (sum <= 0 || total <= 0)
        {
            herbivores = predators = factions = 0;
            return;
        }
        herbivores = (int)(total * h / sum);
        predators = (int)(total * p / sum);
        factions = total - herbivores - predators;   // remainder to factions, so nothing is lost
    }

    /// <summary>The class an entity is currently charged to (None if it is not a creature).</summary>
    public PopClass ClassOf(int entity)
        => (uint)entity < (uint)_classOf.Length ? _classOf[entity] : PopClass.None;
}
