using System;
using System.Collections.Generic;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Damaging and destroying faction structures, and the consequences of losing one.
///
/// Kept as a static helper rather than living inside SiegeSystem because three different things
/// break a structure — a besieging creature (SiegeSystem), a territory that has been dried out or
/// depopulated (MyceliumSystem), and anything added later — and each of them must produce the same
/// events and the same knock-on effects. A consequence implemented once per attacker is a
/// consequence that will eventually differ per attacker.
/// </summary>
public static class Structures
{
    /// <summary>Ticks a structure keeps signalling "under attack" after being hit.</summary>
    public const int UnderAttackMemory = 300;

    /// <summary>
    /// Apply damage. Returns true if this blow destroyed it. <paramref name="attacker"/> may be -1
    /// for environmental damage (a heart whose territory failed has no assailant to name).
    /// </summary>
    public static bool Damage(EntityManager em, int structureEntity, float amount, int attacker,
                              List<int> destroyedOut)
    {
        if (amount <= 0f || !em.IsAlive(structureEntity)) return false;
        if (!em.HasComponents(structureEntity, ComponentFlags.Structure)) return false;

        ref var structure = ref em.Structures[structureEntity];
        if (structure.IsDestroyed) return false;

        structure.Health -= amount;
        structure.UnderAttackTicks = UnderAttackMemory;
        structure.LastAttacker = attacker;

        string attackerName = AttackerName(em, attacker);
        float x = 0f, y = 0f;
        if (em.HasComponents(structureEntity, ComponentFlags.Position))
        {
            ref var p = ref em.Positions[structureEntity];
            x = p.X; y = p.Y;
        }

        EcosystemLogger.Instance?.LogStructureDamaged(structure.Kind.ToString(),
            OwnerName(structure.FactionSpeciesId), attackerName, structureEntity,
            x, y, amount, MathF.Max(0f, structure.Health), structure.MaxHealth);

        if (!structure.IsDestroyed) return false;

        structure.Health = 0f;
        EcosystemLogger.Instance?.LogStructureDestroyed(structure.Kind.ToString(),
            OwnerName(structure.FactionSpeciesId), attackerName, structureEntity, x, y);

        ApplyLossConsequences(em, structureEntity, ref structure, attackerName, x, y);
        destroyedOut.Add(structureEntity);
        return true;
    }

    /// <summary>
    /// What the faction loses beyond the building itself. Runs BEFORE the entity is destroyed, so
    /// the structure's own components are still readable.
    /// </summary>
    private static void ApplyLossConsequences(EntityManager em, int structureEntity,
        ref Structure structure, string attackerName, float x, float y)
    {
        switch (structure.Kind)
        {
            case StructureKind.Nest:
            {
                // Pending larvae die with the nest — they live in the Nest component, so they are
                // lost simply by the entity going away. Reported explicitly because "the colony
                // lost three broods" is the part a player would feel.
                int larvae = 0;
                int colonyId = -1;
                if (em.HasComponents(structureEntity, ComponentFlags.Nest))
                {
                    ref var nest = ref em.Nests[structureEntity];
                    colonyId = nest.ColonyId;
                    if (nest.SpawnTimer0 >= 0f) larvae++;
                    if (nest.SpawnTimer1 >= 0f) larvae++;
                    if (nest.SpawnTimer2 >= 0f) larvae++;
                }
                EcosystemLogger.Instance?.LogStructureEvent("nest_destroyed",
                    OwnerName(structure.FactionSpeciesId), structureEntity, x, y,
                    $"colony={colonyId};larvae_lost={larvae};by={attackerName}");

                // A colony with no nests left has no way to make another Sectid.
                if (colonyId >= 0 && CountColonyNests(em, colonyId, structureEntity) == 0)
                {
                    EcosystemLogger.Instance?.LogStructureEvent("colony_destroyed",
                        OwnerName(structure.FactionSpeciesId), structureEntity, x, y,
                        $"colony={colonyId};by={attackerName}");
                }
                break;
            }

            case StructureKind.Crystal:
            {
                // The linked Faeling is not killed — it keeps fighting — but it will never respawn,
                // because respawning is the crystal's job. Unlink it so nothing holds a dangling id.
                int linked = -1;
                if (em.HasComponents(structureEntity, ComponentFlags.Crystal))
                    linked = em.Crystals[structureEntity].LinkedFaeling;
                if (linked >= 0 && em.IsAlive(linked)
                    && em.HasComponents(linked, ComponentFlags.FaelingPower))
                {
                    em.FaelingPowers[linked].LinkedCrystal = -1;
                }
                EcosystemLogger.Instance?.LogStructureEvent("crystal_destroyed",
                    OwnerName(structure.FactionSpeciesId), structureEntity, x, y,
                    $"linked_faeling={linked};by={attackerName}");
                break;
            }

            case StructureKind.MyceliumHeart:
                EcosystemLogger.Instance?.LogStructureEvent("heart_destroyed",
                    OwnerName(structure.FactionSpeciesId), structureEntity, x, y,
                    $"by={attackerName}");
                break;
        }
    }

    private static int CountColonyNests(EntityManager em, int colonyId, int excluding)
    {
        int count = 0;
        foreach (int e in em.Query(ComponentFlags.Nest))
        {
            if (e == excluding) continue;
            if (em.HasComponents(e, ComponentFlags.Structure) && em.Structures[e].IsDestroyed)
                continue;
            if (em.Nests[e].ColonyId == colonyId) count++;
        }
        return count;
    }

    private static string OwnerName(int speciesId)
        => SpeciesRegistry.GetById(speciesId)?.Name ?? "unknown";

    private static string AttackerName(EntityManager em, int attacker)
    {
        if (attacker < 0 || !em.IsAlive(attacker)) return "environment";
        if (!em.HasComponents(attacker, ComponentFlags.Species)) return "unknown";
        return SpeciesRegistry.GetById(em.Species[attacker].SpeciesId)?.Name ?? "unknown";
    }
}

/// <summary>
/// Faction creatures attacking enemy STRUCTURES — a path deliberately separate from hunting.
///
/// WHY NOT IN HuntingSystem. Structures are not food, and almost nothing in the prey path applies
/// to them: the body-mass gate has no meaning for a building, the nutrition-payoff score would
/// rank a crystal by a nutrition figure it does not have, they never flee so the pursuit,
/// give-up and pacing heuristics are dead code, pack roles have nothing to flank, and a kill must
/// NOT drop a carcass. Threading them through would have meant an "is this actually a building"
/// guard at roughly fifteen points inside a 1,200-line loop — and each of those guards is a place
/// for the two concepts to drift apart later. Here the objective path is ~200 lines that read
/// straight through, and the entire coupling to hunting is one guard in HuntingSystem: a creature
/// committed to a siege is not simultaneously hunting.
///
/// WHY IT RUNS BEFORE HuntingSystem. Acquisition needs the prey distance HuntingSystem would pick
/// in order to weigh a structure against it, and the loser of that comparison must not also steer
/// the creature. Deciding first, then letting hunting skip a committed besieger, keeps exactly one
/// system driving a creature's velocity on any given tick.
///
/// All weighting is <c>SpeciesDefinition</c> data (StructureAggression, StructureRetaliationBias,
/// and the attack triple), so no species name appears in this file.
/// </summary>
public sealed class SiegeSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly WorldManager _worldManager;
    private readonly List<int> _nearby = new(64);
    private readonly List<int> _destroyed = new(8);

    // "No prey in sight" is scored as the creature's own SEEK RADIUS, not as infinity.
    //
    // Infinity was the obvious reading — nothing to lose by going — but it makes the aggression
    // ratio meaningless in exactly the case where it should be most conservative: with no prey
    // target, dist(structure) <= infinity x aggression is true for any aggression above zero, so
    // an opportunist with StructureAggression 0.15 behaved identically to a dedicated raider. That
    // is what put a Sectid swarm on a Faeling crystal at t=4,725 in a world where nobody had
    // provoked anyone.
    //
    // The seek radius is the honest stand-in: "the food I might find is about as far as I can
    // see". An unprovoked Sectid (0.15 x 45) is then distracted only by a structure within ~7
    // tiles — genuinely underfoot — while retaliation (x8, so 1.2 x 45 = 54 tiles) still reaches
    // across a whole neighbourhood, and a raider at 4.0 is unaffected because its ratio exceeds 1
    // either way.

    /// <summary>
    /// How long a defender stays alerted by a call to arms. Matches HuntingSystem's own
    /// RallyAlertDuration so a structure's call behaves exactly like being bitten.
    /// </summary>
    private const int DefenceAlertTicks = 150;

    // The world census, shared with CrystalSystem. A keeper decides WHICH faction to move against
    // from this, not from what happens to be nearest.
    private readonly FactionCensus? _census;

    public SiegeSystem(SpatialHash spatialHash, WorldManager worldManager,
                        FactionCensus? census = null)
    {
        _spatialHash = spatialHash;
        _worldManager = worldManager;
        _census = census;
    }

    public void Process(EntityManager em)
    {
        _destroyed.Clear();

        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species |
                                        ComponentFlags.Siege;

        foreach (int entity in em.Query(required))
        {
            if (!em.DueThisTick[entity])
                continue;

            var def = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
            if (def == null || def.StructureAggression <= 0f)
                continue;

            // A dormant Sectid is not out campaigning.
            if (em.HasComponents(entity, ComponentFlags.FoodCarrier)
                && em.FoodCarriers[entity].IsHibernating)
                continue;

            int tickMult = DecisionCadence.Elapsed(em, entity);
            ref var siege = ref em.Sieges[entity];
            if (siege.CurrentCooldown > 0)
                siege.CurrentCooldown = Math.Max(0, siege.CurrentCooldown - tickMult);

            ref var pos = ref em.Positions[entity];

            if (!IsValidTarget(em, entity, siege.TargetStructure))
                siege.TargetStructure = -1;

            if (!siege.HasTarget)
                siege.TargetStructure = Acquire(em, entity, def, ref pos);

            if (!siege.HasTarget)
                continue;

            // Committed: drop any prey target so hunting doesn't hold stale state while we're away.
            if (em.HasComponents(entity, ComponentFlags.Predator))
                em.Predators[entity].TargetEntity = -1;

            ref var targetPos = ref em.Positions[siege.TargetStructure];
            float distSq = MathUtils.DistanceSquared(pos.X, pos.Y, targetPos.X, targetPos.Y);

            // Surface-to-surface, as everywhere else: a nest or a grown crystal is bulky enough
            // that centre-to-centre would put it out of reach of its own attacker (BodyMetrics).
            float reach = AttackRange(def, em, entity)
                        + BodyRadius(em, entity) + BodyRadius(em, siege.TargetStructure);

            if (distSq <= reach * reach)
            {
                if (em.HasComponents(entity, ComponentFlags.Velocity))
                {
                    ref var v = ref em.Velocities[entity];
                    v.Dx = 0f;
                    v.Dy = 0f;
                }
                if (siege.CurrentCooldown <= 0)
                {
                    Structures.Damage(em, siege.TargetStructure, AttackPower(def, em, entity),
                        entity, _destroyed);
                    siege.CurrentCooldown = AttackCooldown(def, em, entity);
                    if (!em.HasComponents(siege.TargetStructure, ComponentFlags.Structure)
                        || em.Structures[siege.TargetStructure].IsDestroyed)
                        siege.TargetStructure = -1;
                }
            }
            else if (em.HasComponents(entity, ComponentFlags.Velocity))
            {
                // Walk in. Structures don't move, so there is nothing to intercept or pace — but
                // the approach is still STEERING, and must be blended like every other steering
                // decision in the project rather than assigned.
                //
                // This used to write velocity outright (v.Dx = dir * speed). Three things followed
                // from that. The creature turned instantly regardless of its mass. The turn was not
                // LOD-compensated, so it behaved differently at distance from the player than in
                // front of it. And because SiegeSystem runs after WanderSystem and before
                // FleeingSystem, overwriting velocity discarded wander, herding and habitat
                // steering entirely for that tick — a besieging Sectid drifted in a straight line
                // and reacted to nothing but a predator. Blending leaves those forces in the mix
                // and merely pulls the result toward the objective.
                float dist = MathF.Sqrt(distSq);
                if (dist > 0.001f)
                {
                    float speed = def.BaseHuntSpeed > 0f ? def.BaseHuntSpeed : def.BaseWanderSpeed;
                    float agility = DecisionCadence.BlendRate(
                        DecisionCadence.TurnAgility(def), DecisionCadence.Interval(em, entity));
                    ref var v = ref em.Velocities[entity];
                    DecisionCadence.BlendVelocity(ref v,
                        (targetPos.X - pos.X) / dist * speed,
                        (targetPos.Y - pos.Y) / dist * speed, agility);
                }
            }
        }

        // Call to arms, then tick the under-attack signal down. Both in one pass over structures,
        // of which there are tens rather than thousands.
        foreach (int e in em.Query(ComponentFlags.Structure))
        {
            ref var s = ref em.Structures[e];
            if (s.IsUnderAttack && !s.IsDestroyed)
                CallDefenders(em, e, ref s);
            if (s.UnderAttackTicks > 0) s.UnderAttackTicks--;
        }

        if (_destroyed.Count > 0)
            em.DestroyEntities(_destroyed);
    }

    /// <summary>
    /// Point the owning faction's nearby creatures at whoever is hitting this structure.
    ///
    /// It sets the same LastAttacker / LastAttackedTicks pair a creature would get from being
    /// bitten itself, so the defence runs through HuntingSystem's existing rally: pack species need
    /// their ally threshold, solitary apexes turn and fight, and a specialist still flees. Nothing
    /// new decides how to defend — the structure only supplies the threat.
    ///
    /// The threat is always the ATTACKING CREATURE. A rally aimed at the building would have every
    /// defender converge on the thing already being destroyed and stand in it.
    /// </summary>
    private void CallDefenders(EntityManager em, int structureEntity, ref Structure structure)
    {
        int attacker = structure.LastAttacker;
        if (attacker < 0 || !em.IsAlive(attacker)) return;

        var ownerDef = SpeciesRegistry.GetById(structure.FactionSpeciesId);
        if (ownerDef == null || ownerDef.StructureDefenseRadius <= 0f) return;

        ref var spos = ref em.Positions[structureEntity];
        _nearby.Clear();
        _spatialHash.QueryRadius(spos.X, spos.Y, ownerDef.StructureDefenseRadius, _nearby);

        foreach (int defender in _nearby)
        {
            if (defender == attacker || !em.IsAlive(defender)) continue;
            if (!em.HasComponents(defender, ComponentFlags.Predator | ComponentFlags.Species)) continue;
            if (em.Species[defender].SpeciesId != structure.FactionSpeciesId) continue;
            if (em.HasComponents(defender, ComponentFlags.FoodCarrier)
                && em.FoodCarriers[defender].IsHibernating) continue;

            ref var predator = ref em.Predators[defender];
            predator.LastAttacker = attacker;
            predator.LastAttackedTicks = DefenceAlertTicks;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Acquisition
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pick an enemy structure worth diverting to, or -1.
    ///
    /// The comparison is deliberately made against the creature's own prey options rather than
    /// against an absolute score: what matters is whether this species would rather break a
    /// building than eat, and that is only answerable relative to how far the food is.
    /// </summary>
    private int Acquire(EntityManager em, int entity, SpeciesDefinition def, ref Position pos)
    {
        // Is there something we are otherwise going to eat? The answer decides BOTH how far we
        // look and whether the aggression ratio means anything, so it has to be settled first.
        float preyDist = -1f;
        if (em.HasComponents(entity, ComponentFlags.Predator))
        {
            ref var predator = ref em.Predators[entity];
            if (predator.HasTarget && em.IsAlive(predator.TargetEntity)
                && em.HasComponents(predator.TargetEntity, ComponentFlags.Position))
            {
                ref var pp = ref em.Positions[predator.TargetEntity];
                preyDist = MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, pp.X, pp.Y));
            }
        }
        bool hasPrey = preyDist >= 0f;

        // With no prey, StructureAggression has nothing to compare against — it is a RATIO against
        // the meal being passed up, and there is no meal. Substituting some stand-in distance for
        // the missing prey makes the one knob that governs siege priority a formality: whatever
        // stand-in is chosen, an idle creature sieges on a rule nobody wrote. So an idle creature
        // uses a separate, explicit radius instead, and the default of 0 means it does not divert
        // at all. Opting in is for a species with no prey to pass up — a Faeling does not eat, and
        // raiding is the whole of its purpose.
        float seek = hasPrey
            ? (def.StructureSeekRadius > 0f ? def.StructureSeekRadius : def.HuntRange)
            : def.StructureIdleSeekRadius;
        if (seek <= 0f) return -1;

        _nearby.Clear();
        _spatialHash.QueryRadius(pos.X, pos.Y, seek, _nearby);

        int ownFaction = em.Species[entity].SpeciesId;
        float seekSq = seek * seek;

        // A keeper besieges the WINNER or nobody. See StructureTargetsDominantOnly for what
        // happened when this was "whatever is nearest".
        int mandated = -1;
        if (def.StructureTargetsDominantOnly)
        {
            if (_census == null) return -1;
            mandated = _census.DominantFactionId(def.KeeperMinPresence, excludeSpeciesId: ownFaction);
            if (mandated < 0) return -1;
        }

        // Aggression rises while our own structures are being broken: answer a siege with a siege.
        bool retaliating = def.StructureRetaliationBias != 1f && OwnStructureUnderAttack(em, ownFaction);
        float aggression = def.StructureAggression;
        if (retaliating) aggression *= def.StructureRetaliationBias;

        // A HUNGRY CREATURE FORAGES; IT DOES NOT LAY SIEGE. Unless its own home is being broken,
        // in which case nothing else matters.
        //
        // This is not a flourish, it is a starvation bug found by the whole-game invariant. The
        // aggression ratio compares the structure's distance against the CURRENT PREY TARGET's,
        // and a creature with no prey target scores that distance as infinite — so "nothing to eat
        // in sight" resolved as "definitely go break a building", at any aggression. Once crystals
        // were toughened from 400 HP to 1,600, a hungry swarm with no prey nearby would commit
        // 600+ ticks to hammering one instead of going to look for food. Across two seeds the
        // Sectid faction went 223 -> 1 and 212 -> 0, holding 47 empty nests: the invariant's
        // "still holds a nest" passed while the faction died.
        if (!retaliating && em.HasComponents(entity, ComponentFlags.Hunger))
        {
            ref var hunger = ref em.Hungers[entity];
            float ratio = hunger.Max > 0f ? hunger.Current / hunger.Max : 1f;
            if (ratio < def.ForageHungerThreshold) return -1;
        }

        int best = -1;
        float bestDistSq = float.MaxValue;
        foreach (int other in _nearby)
        {
            if (!IsValidTarget(em, entity, other)) continue;
            if (em.Structures[other].FactionSpeciesId == ownFaction) continue;
            if (mandated >= 0 && em.Structures[other].FactionSpeciesId != mandated) continue;

            ref var op = ref em.Positions[other];
            float dSq = MathUtils.DistanceSquared(pos.X, pos.Y, op.X, op.Y);
            if (dSq > seekSq || dSq >= bestDistSq) continue;

            bestDistSq = dSq;
            best = other;
        }

        if (best < 0) return -1;
        // Idle: nothing was passed up, so there is no ratio to satisfy. Being inside
        // StructureIdleSeekRadius at all is the whole test.
        if (!hasPrey) return best;

        // The aggression ratio, evaluated against the winner only.
        float structureDist = MathF.Sqrt(bestDistSq);
        return structureDist <= preyDist * aggression ? best : -1;
    }

    /// <summary>
    /// Is any structure of our own faction currently taking damage nearby? Scans structures only
    /// (there are tens of them, not thousands), and only for species that actually retaliate.
    /// </summary>
    private static bool OwnStructureUnderAttack(EntityManager em, int ownFaction)
    {
        foreach (int e in em.Query(ComponentFlags.Structure))
        {
            ref var s = ref em.Structures[e];
            if (s.IsUnderAttack && !s.IsDestroyed && s.FactionSpeciesId == ownFaction)
                return true;
        }
        return false;
    }

    private bool IsValidTarget(EntityManager em, int self, int target)
    {
        if (target < 0 || target == self || !em.IsAlive(target)) return false;
        if (!em.HasComponents(target, ComponentFlags.Structure | ComponentFlags.Position))
            return false;
        if (em.Structures[target].IsDestroyed) return false;
        if (em.Structures[target].FactionSpeciesId == em.Species[self].SpeciesId) return false;

        // Never commit to something standing where we cannot go — the same barrier the hunt path
        // applies to prey, for the same reason: the assault could only ever end in drowning.
        if (_worldManager != null)
        {
            var def = SpeciesRegistry.GetById(em.Species[self].SpeciesId);
            ref var tp = ref em.Positions[target];
            if (def != null && TerrainProfile.IsImpassable(def, _worldManager.GetTile(tp.X, tp.Y)))
                return false;
        }
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Attack parameters — species data, with the creature's own attack as the fallback
    // ══════════════════════════════════════════════════════════════════════════

    private static float AttackRange(SpeciesDefinition def, EntityManager em, int entity)
    {
        if (def.StructureAttackRange > 0f) return def.StructureAttackRange;
        if (em.HasComponents(entity, ComponentFlags.RangedAttack))
            return em.RangedAttacks[entity].Range;
        if (em.HasComponents(entity, ComponentFlags.Predator))
            return em.Predators[entity].AttackRange;
        return 1f;
    }

    private static float AttackPower(SpeciesDefinition def, EntityManager em, int entity)
    {
        if (def.StructureAttackPower > 0f) return def.StructureAttackPower;
        if (em.HasComponents(entity, ComponentFlags.RangedAttack))
            return em.RangedAttacks[entity].BaseDamage;
        if (em.HasComponents(entity, ComponentFlags.Predator))
            return em.Predators[entity].AttackPower;
        return 1f;
    }

    private static int AttackCooldown(SpeciesDefinition def, EntityManager em, int entity)
    {
        if (def.StructureAttackCooldown > 0) return def.StructureAttackCooldown;
        if (em.HasComponents(entity, ComponentFlags.RangedAttack))
            return em.RangedAttacks[entity].Cooldown;
        if (em.HasComponents(entity, ComponentFlags.Predator))
            return em.Predators[entity].AttackCooldown;
        return 30;
    }

    private static float BodyRadius(EntityManager em, int entity)
        => em.HasComponents(entity, ComponentFlags.Renderable)
            ? BodyMetrics.Radius(em.Renderables[entity].Size)
            : 0f;
}
