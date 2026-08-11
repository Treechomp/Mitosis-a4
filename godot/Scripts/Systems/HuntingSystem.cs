using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Processes hunting behavior for predators using spatial hashing.
/// Features:
/// - Hunger-driven hunting with urgency scaling
/// - Terrain discomfort balance
/// - Pack hunting coordination (leader selects target, pack adopts)
/// - Pack tactics: fan out to surround, rush/retreat cycles
/// </summary>
public sealed class HuntingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly WorldManager? _worldManager;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly List<int> _packMembers = new(8);
    private readonly List<int> _entitiesToKill = new(16);
    private readonly Dictionary<int, int> _groupTargets = new(16);  // groupId -> target entity
    private readonly Dictionary<int, (float x, float y)> _groupLeaderPositions = new(16);  // groupId -> leader pos
    private readonly Dictionary<int, bool> _groupConverging = new(16);  // groupId -> leader triggered convergence
    private const float SporeBodyMass = 0.2f;

    // === Target viability re-evaluation ===
    // How often (ticks) to check whether a hunt is making progress, the minimum prey-HP we must
    // have removed in that window to count as progress, how long to avoid a target we gave up on,
    // and the fraction of our own HP we'll lose before bailing on a too-dangerous target.
    private const int HuntReevalInterval = 150;

    /// <summary>
    /// Ticks a pursuit may run without the hunter ever getting closer before it is abandoned as
    /// unreachable. The engagement clock below only runs once in striking distance, so a target
    /// the hunter can neither reach nor lose had no timeout at all: a Shark that locked onto a
    /// Penguin standing on the ice paced the shoreline indefinitely — it was never engaged (so no
    /// progress check) and never 3× hunt range away (so no escape check). Meanwhile the penguin
    /// refused to enter the water with a shark in it and starved. Ten seconds at 20 TPS.
    /// </summary>
    private const int HuntApproachStallTicks = 200;

    /// <summary>Tiles of closure that count as real progress toward a target (noise floor).</summary>
    private const float HuntApproachMinGain = 0.5f;
    private const float HuntMinProgress = 5f;
    private const int HuntAvoidDuration = 600;
    private const float HuntSelfDamageBailFraction = 0.4f;
    // The "non-viable" progress check only runs once a predator is within this distance of its
    // target (or AttackRange×4, whichever is larger). Beyond it the predator is still closing in,
    // where dealing no damage is expected and must not count as a failed hunt.
    private const float HuntEngageRange = 4f;

    // === Defensive rally (call-to-action) ===
    // How long an attacked pack/swarm member remembers and rallies against its attacker, how many
    // groupmates must be near to commit to a mob (otherwise it flees), and how far the threat can be.
    private const int RallyAlertDuration = 150;
    private const int RallyAllyThreshold = 2;
    private const float RallyRangeMult = 2f; // × HuntRange: don't mob a threat that already fled far

    // Fraction of a prey's nutrition the killer eats immediately on the kill (the "prime cut").
    // The remainder still drops as a corpse for packmates and scavengers. Without an eat-on-kill
    // bonus, predators relied solely on slow corpse-scavenging and starved before they could breed.
    private const float KillNutritionShare = 0.6f;

    // Upper bound on viable prey scored per neighbour scan. Caps per-tick work in dense prey
    // clusters (cost otherwise scales with prey density, not predator count). High enough that
    // ordinary neighbourhoods are unaffected; only pathological crowds get clamped.
    private const int MaxHuntCandidates = 48;

    public HuntingSystem(SpatialHash spatialHash, WorldManager? worldManager = null)
    {
        _spatialHash = spatialHash;
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        _entitiesToKill.Clear();
        _groupTargets.Clear();

        // Spatial hash already updated by SpatialHashUpdateSystem

        // First pass: Share targets, leader positions, and convergence state within packs
        const ComponentFlags predatorRequired = ComponentFlags.Position | ComponentFlags.Predator | ComponentFlags.Hunger;
        _groupLeaderPositions.Clear();
        _groupConverging.Clear();

        foreach (int entity in em.Query(predatorRequired))
        {
            ref var predator = ref em.Predators[entity];

            if (!em.HasComponents(entity, ComponentFlags.Social))
                continue;

            ref var social = ref em.Socials[entity];
            if (social.Type != SocialType.Pack || social.GroupId < 0)
                continue;

            // Track leader positions and convergence triggers
            if (predator.Role == PackRole.Leader)
            {
                ref var ldrPos = ref em.Positions[entity];
                _groupLeaderPositions[social.GroupId] = (ldrPos.X, ldrPos.Y);

                if (predator.Phase == PackPhase.Converging)
                    _groupConverging[social.GroupId] = true;
            }

            if (!predator.HasTarget || !em.IsAlive(predator.TargetEntity))
                continue;

            // Leaders always set the group target; non-leaders only if no target set yet
            bool isLeader = social.RecognizedLeader < 0 || social.RecognizedLeader == entity;
            if (isLeader || !_groupTargets.ContainsKey(social.GroupId))
            {
                _groupTargets[social.GroupId] = predator.TargetEntity;
            }
        }

        // Second pass: All predators hunt
        foreach (int entity in em.Query(predatorRequired))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // Hibernating Sectids are dormant — NestSystem wakes them when prey strays near
            if (em.HasComponents(entity, ComponentFlags.FoodCarrier) && em.FoodCarriers[entity].IsHibernating)
                continue;

            // LOD tick multiplier: attack/phase cooldowns count down at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var pos = ref em.Positions[entity];
            ref var predator = ref em.Predators[entity];
            ref var hunger = ref em.Hungers[entity];

            // Cleared each due tick; re-armed only while a dormant ambusher is lurking motionless
            // (so it isn't flagged dormant while attacking, chasing, or target-less).
            predator.IsDormant = false;

            // Get species definition for this predator (all hunting params are per-species)
            SpeciesDefinition speciesDef;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var sp = ref em.Species[entity];
                speciesDef = SpeciesRegistry.GetById(sp.SpeciesId);
            }
            else
            {
                speciesDef = SpeciesRegistry.Get("Wolf"); // fallback
            }

            // Reduce cooldowns (compensated for LOD tick rate). Clamp at 0 — at any LOD below
            // Full, tickMult > 1, so a bare subtraction overshoots 0 into the negatives (e.g.
            // 2 - 3 = -1). Because the guard only decrements while > 0, it would then STICK at
            // that negative value forever, and the attack gate (== 0) would never fire again —
            // the predator paces its prey in range without ever landing a hit (the prey looks
            // "invulnerable"). Math.Max keeps it from ever overshooting.
            if (predator.CurrentCooldown > 0)
                predator.CurrentCooldown = Math.Max(0, predator.CurrentCooldown - tickMult);
            if (predator.PhaseTimer > 0)
                predator.PhaseTimer -= tickMult;
            if (predator.AvoidTicks > 0)
            {
                predator.AvoidTicks -= tickMult;
                if (predator.AvoidTicks <= 0)
                    predator.AvoidTarget = -1;
            }
            if (predator.LastAttackedTicks > 0)
            {
                predator.LastAttackedTicks -= tickMult;
                if (predator.LastAttackedTicks <= 0)
                    predator.LastAttacker = -1;
            }

            // Passive stealth accumulation for ambush predators (builds while idle/wandering)
            if (speciesDef.HuntingTactic == HuntingTactic.Ambush && !predator.HasTarget && predator.PounceTimer <= 0)
            {
                float currentSpd = 0f;
                if (em.HasComponents(entity, ComponentFlags.Velocity))
                {
                    ref var v = ref em.Velocities[entity];
                    currentSpd = MathF.Sqrt(v.Dx * v.Dx + v.Dy * v.Dy);
                }
                float stealthLimit = speciesDef.BaseHuntSpeed * speciesDef.AmbushSpeedThreshold;
                if (currentSpd <= stealthLimit)
                {
                    float gain = speciesDef.AmbushStealthGain;
                    if (speciesDef.WaterStealthBonus > 0f && _worldManager != null)
                    {
                        var tile = _worldManager.GetTile(pos.X, pos.Y);
                        if (tile.IsWater())
                            gain += speciesDef.WaterStealthBonus;
                    }
                    predator.Stealth = MathF.Min(1f, predator.Stealth + gain);
                }
                else
                {
                    predator.Stealth = MathF.Max(0f, predator.Stealth - speciesDef.AmbushStealthDecay);
                }
            }

            // Clear target if dead (killed by another predator or other cause)
            if (predator.HasTarget && !em.IsAlive(predator.TargetEntity))
            {
                if (em.HasComponents(entity, ComponentFlags.Species))
                {
                    ref var sp = ref em.Species[entity];
                    EcosystemLogger.Instance?.LogHuntFail(sp.SpeciesId, entity, pos.X, pos.Y, "target_died");
                }
                predator.TargetEntity = -1;
                predator.Phase = PackPhase.Idle;
                predator.Role = PackRole.None;
            }

            // Abandon target if it's too far away (3× hunt range)
            // Prevents cross-map chases when prey roams/flees far from predator
            if (predator.HasTarget && em.IsAlive(predator.TargetEntity))
            {
                float abandonRange = predator.HuntRange * 3f;
                ref var targetPos = ref em.Positions[predator.TargetEntity];
                float abandonDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, targetPos.X, targetPos.Y);
                if (abandonDistSq > abandonRange * abandonRange)
                {
                    if (em.HasComponents(entity, ComponentFlags.Species))
                    {
                        ref var sp = ref em.Species[entity];
                        EcosystemLogger.Instance?.LogHuntFail(sp.SpeciesId, entity, pos.X, pos.Y, "prey_escaped");
                    }
                    predator.TargetEntity = -1;
                    predator.Phase = PackPhase.Idle;
                    predator.Role = PackRole.None;
                }
            }

            // Target viability re-evaluation: give up on prey we can't actually bring down —
            // too fast to land hits on, out-healing our damage, or hurting us too much — and
            // briefly blacklist it so we switch to a viable target instead of fixating.
            // (This is what stops a Sectid swarm from chasing a Crocodile forever.)
            if (predator.HasTarget && em.IsAlive(predator.TargetEntity))
            {
                float targetEnergy = em.HasComponents(predator.TargetEntity, ComponentFlags.Energy)
                    ? em.Energies[predator.TargetEntity].Current : 0f;

                // The non-viable timer only runs once we're actually engaged (in striking distance).
                // While still closing the gap, dealing no damage is expected — counting it as failure
                // made predators abandon mid-approach and never land a hit, so they starved without
                // ever killing. Prey we genuinely can't catch is handled by the prey_escaped abandon
                // (3× hunt range) above.
                ref var tgtPos = ref em.Positions[predator.TargetEntity];
                float tgtDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, tgtPos.X, tgtPos.Y);
                float engageRange = MathF.Max(predator.AttackRange * 4f, HuntEngageRange);
                bool engaged = tgtDistSq <= engageRange * engageRange;

                // A pack member following coordination tactics (flanking/positioning/disrupting)
                // deliberately holds off attacking until the convergence rush. Judging it by its
                // own damage output during that dance is a false failure: flankers circle within
                // engage range without striking, and one whose timer fires mid-positioning aborts
                // the whole pack hunt (collectiveFail) right before the kill lands. The check still
                // applies once committed (Converging) and to solo hunters, so a pack that genuinely
                // can't hurt its target still gives up.
                bool packCoordinating =
                    em.HasComponents(entity, ComponentFlags.Social)
                    && em.Socials[entity].Type == SocialType.Pack
                    && predator.Role != PackRole.None
                    && predator.Phase != PackPhase.Converging;

                bool giveUp = false;
                bool collectiveFail = false; // whole hunt is stalled (vs. just this one retreating hurt)
                bool unreachable = false;    // gave up because we could never close the distance

                // Bail immediately if the hunt is costing us too much health (strong/counterattacking prey)
                if (em.HasComponents(entity, ComponentFlags.Energy) && predator.SelfStartEnergy > 0f
                    && em.Energies[entity].Current < predator.SelfStartEnergy * (1f - HuntSelfDamageBailFraction))
                {
                    giveUp = true;
                }
                else if (engaged && !packCoordinating && speciesDef.HuntingTactic != HuntingTactic.Ambush)
                {
                    predator.HuntTicks += tickMult;
                    if (predator.HuntTicks >= HuntReevalInterval)
                    {
                        // Progress = prey HP removed since the last checkpoint. Near-zero while
                        // engaged means we're not out-damaging it (its regen is suppressed while we
                        // land hits, so a full-health target after a full in-range window means we
                        // simply can't hit it). Skipped for ambush hunters (damage-free stalk).
                        float progress = predator.TargetLastEnergy - targetEnergy;
                        if (progress < HuntMinProgress)
                        {
                            giveUp = true;
                            collectiveFail = true; // nobody is making headway — the target is non-viable
                        }
                        predator.TargetLastEnergy = targetEnergy;
                        predator.HuntTicks = 0;
                    }
                }
                else
                {
                    // Still closing in, coordinating the pack, or an ambush stalk: pause the
                    // engagement clock and keep the damage baseline current, so the window measures
                    // only in-range attacking time (and a committing pack gets a fresh full window).
                    predator.HuntTicks = 0;
                    predator.TargetLastEnergy = targetEnergy;

                    // Closing-the-gap clock. A pursuit that never gets nearer cannot be won, and
                    // the most common reason is that the prey is somewhere we physically cannot
                    // follow — ashore, across a cliff, over deep water. Give up on it the same way
                    // we give up on prey we can't damage. Ambushers are exempt: lying in wait
                    // without approaching is their entire tactic. Pack coordinators are exempt for
                    // the same reason the progress check exempts them — a flanker holding station
                    // is doing its job, not stalling.
                    if (!packCoordinating && speciesDef.HuntingTactic != HuntingTactic.Ambush)
                    {
                        float tgtDist = MathF.Sqrt(tgtDistSq);
                        if (tgtDist < predator.TargetBestDist - HuntApproachMinGain)
                        {
                            predator.TargetBestDist = tgtDist;
                            predator.ApproachTicks = 0;
                        }
                        else
                        {
                            predator.ApproachTicks += tickMult;
                            if (predator.ApproachTicks >= HuntApproachStallTicks)
                            {
                                giveUp = true;
                                collectiveFail = true; // nobody can reach it — drop the pack target too
                                unreachable = true;
                            }
                        }
                    }
                }

                if (giveUp)
                {
                    if (em.HasComponents(entity, ComponentFlags.Species))
                    {
                        ref var sp = ref em.Species[entity];
                        EcosystemLogger.Instance?.LogHuntFail(sp.SpeciesId, entity, pos.X, pos.Y,
                            unreachable ? "unreachable" : "not_viable");
                    }
                    predator.AvoidTarget = predator.TargetEntity;
                    predator.AvoidTicks = HuntAvoidDuration;
                    // Only a collective stall clears the shared pack target. An individual peeling
                    // off because it's hurt must not abandon a hunt the rest of the swarm is winning.
                    if (collectiveFail && em.HasComponents(entity, ComponentFlags.Social))
                    {
                        int gid = em.Socials[entity].GroupId;
                        if (gid >= 0 && _groupTargets.TryGetValue(gid, out int gt)
                            && gt == predator.TargetEntity)
                            _groupTargets.Remove(gid);
                    }
                    predator.TargetEntity = -1;
                    predator.Phase = PackPhase.Idle;
                    predator.Role = PackRole.None;
                    // Suppress instant re-acquisition (longer for swarms to break fixation loops)
                    predator.PhaseTimer = speciesDef.IsSwarmHunter ? 120 : 60;
                    continue;
                }
            }

            float hungerRatio = hunger.Current / hunger.Max;

            // Only stop hunting when sated (per-species; default 95%). Between HuntThreshold and
            // this, predators hunt opportunistically, keeping herbivores in check.
            float fullThreshold = speciesDef.SatedHunger;
            // A full predator stops hunting — unless it was just attacked, in which case it still
            // needs to defend itself / rally the group below.
            if (hungerRatio >= fullThreshold && predator.LastAttackedTicks <= 0)
            {
                if (predator.HasTarget && em.HasComponents(entity, ComponentFlags.Species))
                {
                    int sid = em.Species[entity].SpeciesId;
                    if (EcosystemLogger.DecisionLoggingFor(sid))
                        EcosystemLogger.Instance!.LogDecision(sid, entity, pos.X, pos.Y,
                            "hunting", "hunt_stop_sated",
                            FormattableString.Invariant($"hunger={hungerRatio:P0}"));
                }
                predator.TargetEntity = -1;
                predator.Phase = PackPhase.Idle;
                predator.Role = PackRole.None;
                continue;
            }

            // Urgency scales with hunger:
            // - Below HuntThreshold: full urgency (desperate hunting, faster, wider range)
            // - Above HuntThreshold: low opportunistic urgency (well-fed, still takes easy kills)
            float urgency;
            if (hungerRatio < speciesDef.HuntThreshold)
            {
                urgency = 1f - (hungerRatio / speciesDef.HuntThreshold);
            }
            else
            {
                float wellFedRange = fullThreshold - speciesDef.HuntThreshold;
                urgency = 0.15f * (1f - (hungerRatio - speciesDef.HuntThreshold) / wellFedRange);
            }

            // Check terrain discomfort
            float discomfortRatio = 0f;
            if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
            {
                ref var discomfort = ref em.TerrainDiscomforts[entity];
                discomfortRatio = discomfort.Ratio;
                float discomfortTolerance = urgency;
                // Swarm hunters commit harder to hunts — relentless pursuit
                if (speciesDef.IsSwarmHunter && predator.HasTarget)
                    discomfortTolerance += 0.8f;
                if (discomfort.ExceedsThreshold && discomfortRatio > discomfortTolerance + 0.3f)
                {
                    if (predator.HasTarget && em.HasComponents(entity, ComponentFlags.Species))
                    {
                        ref var sp = ref em.Species[entity];
                        EcosystemLogger.Instance?.LogHuntFail(sp.SpeciesId, entity, pos.X, pos.Y, "discomfort");
                    }
                    predator.TargetEntity = -1;
                    predator.Phase = PackPhase.Idle;
                    // Suppress re-acquisition: longer for swarm hunters to prevent
                    // target fixation (find→abandon→retarget same prey loop)
                    predator.PhaseTimer = speciesDef.IsSwarmHunter ? 120 : 60;
                    continue;
                }
            }

            // Calculate modifiers
            float rangeMultiplier = 1f + (urgency * 0.5f);
            if (discomfortRatio > 0.3f)
                rangeMultiplier *= (1f - discomfortRatio * 0.5f);

            // Hungrier = more desperate = faster hunting (up to 1.5x at starvation)
            float speedMultiplier = 1f + (urgency * 0.5f);

            // Check for pack membership and coordination
            // A predator is only "in a pack" if it has SocialType.Pack, a valid group,
            // AND at least one nearby pack member. Otherwise it hunts solo.
            bool isPack = false;
            int groupId = -1;
            if (em.HasComponents(entity, ComponentFlags.Social))
            {
                ref var social = ref em.Socials[entity];
                if (social.Type == SocialType.Pack && social.GroupId >= 0)
                {
                    groupId = social.GroupId;

                    // Count nearby pack members to confirm this is a real pack hunt
                    _packMembers.Clear();
                    _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.PackCoordinationRadius, _packMembers);
                    int nearbyPackCount = 0;
                    foreach (int other in _packMembers)
                    {
                        if (other == entity || !em.IsAlive(other)) continue;
                        if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social)) continue;
                        ref var otherSocial = ref em.Socials[other];
                        if (otherSocial.GroupId == groupId)
                        {
                            nearbyPackCount++;
                            break;  // At least one is enough
                        }
                    }

                    isPack = nearbyPackCount > 0;

                    // If alone and not already committed to a pack hunt, reset to solo
                    if (!isPack)
                    {
                        if (predator.HasTarget && predator.Role != PackRole.None)
                        {
                            // Already committed to a pack hunt — maintain pack state
                            // even if temporarily out of coordination range (e.g., flanking)
                            isPack = true;
                        }
                        else
                        {
                            predator.Role = PackRole.None;
                            predator.Phase = PackPhase.Idle;
                        }
                    }
                }

                // Adopt pack target — any member's chase triggers group hunt
                // But only if the target is within reasonable range (3× hunt range) and we
                // haven't personally given up on it as non-viable.
                if (isPack && _groupTargets.TryGetValue(groupId, out int packTarget) && em.IsAlive(packTarget)
                    && packTarget != predator.AvoidTarget)
                {
                    if (!predator.HasTarget || predator.TargetEntity != packTarget)
                    {
                        ref var ptPos = ref em.Positions[packTarget];
                        float packTargetDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, ptPos.X, ptPos.Y);
                        float adoptRange = predator.HuntRange * 3f;
                        if (packTargetDistSq <= adoptRange * adoptRange)
                        {
                            predator.TargetEntity = packTarget;
                            BeginHuntTracking(em, entity, ref predator, packTarget);
                            AssignPackRole(entity, packTarget, em, ref predator, ref social, speciesDef);
                        }
                    }
                }

                // Propagate convergence: if leader triggered all-in, everyone follows
                if (isPack && _groupConverging.ContainsKey(groupId))
                {
                    predator.Phase = PackPhase.Converging;
                }

                // === DEFENSIVE RALLY / SOLITARY COUNTER-ATTACK ===
                // Pick a threat: reactively from a recent attacker, or proactively (idle pack member
                // spots a stalker/intruder via FindProactiveThreat). Then act on it:
                //   Solitary apex (Bear, Croc, Jaguar, Polar Bear): turn and fight immediately —
                //   no ally check, no pack needed. They're individually strong enough.
                //   Pack/Swarm (Wolves, Boars, Sectids): need a minimum number of allies before
                //   committing to a mob. A lone member falls through and may flee instead.
                int threat = -1;
                if (predator.LastAttackedTicks > 0 && em.IsAlive(predator.LastAttacker))
                    threat = predator.LastAttacker;
                else if (groupId >= 0 && !predator.HasTarget)
                    threat = FindProactiveThreat(entity, groupId, pos.X, pos.Y, speciesDef, em);

                // A diet specialist (ExclusivePrey) defends by FLEEING, not by hunting its
                // attacker down. This rally/counter-attack path deliberately bypasses the usual
                // mass/hunger gates — and it was bypassing the ExclusivePrey filter too, so a
                // Fish-only Penguin lethally pursued whatever bit it (7 Arctic Fox + 4 Hawk kills
                // in one run — its own predators — driving Arctic Fox to extinction). Clearing an
                // off-diet threat here drops the specialist through to FleeingSystem instead.
                if (threat >= 0 && speciesDef.ExclusivePrey != null
                    && em.HasComponents(threat, ComponentFlags.Species)
                    && !speciesDef.ExclusivePrey.Contains(
                           SpeciesRegistry.GetById(em.Species[threat].SpeciesId).Name))
                    threat = -1;

                if (threat >= 0 && em.IsAlive(threat) && threat != predator.AvoidTarget)
                {
                    ref var threatPos = ref em.Positions[threat];
                    float threatDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, threatPos.X, threatPos.Y);
                    float rallyRange = predator.HuntRange * RallyRangeMult;

                    if (threatDistSq <= rallyRange * rallyRange)
                    {
                        bool isSolitary = !em.HasComponents(entity, ComponentFlags.Social)
                            || em.Socials[entity].Type == SocialType.Solitary;

                        if (isSolitary)
                        {
                            // Apex predator: turn and counter-attack immediately, no allies needed.
                            predator.LastAttacker = threat;
                            predator.LastAttackedTicks = RallyAlertDuration;
                            if (predator.TargetEntity != threat)
                            {
                                predator.TargetEntity = threat;
                                BeginHuntTracking(em, entity, ref predator, threat);
                            }
                        }
                        else if (groupId >= 0)
                        {
                            // Pack/swarm: only commit to a mob with strength in numbers.
                            _packMembers.Clear();
                            _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.PackCoordinationRadius, _packMembers);
                            int allies = 0;
                            foreach (int other in _packMembers)
                            {
                                if (other == entity || !em.IsAlive(other)) continue;
                                if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social)) continue;
                                if (em.Socials[other].GroupId == groupId)
                                {
                                    allies++;
                                    if (allies >= RallyAllyThreshold) break;
                                }
                            }

                            if (allies >= RallyAllyThreshold)
                            {
                                // Remember the threat, broadcast it so groupmates converge.
                                predator.LastAttacker = threat;
                                predator.LastAttackedTicks = RallyAlertDuration;
                                _groupTargets[groupId] = threat;
                                if (predator.TargetEntity != threat)
                                {
                                    predator.TargetEntity = threat;
                                    BeginHuntTracking(em, entity, ref predator, threat);
                                }
                                predator.Role = PackRole.Leader;
                                predator.Phase = PackPhase.Converging;
                            }
                        }
                    }
                }
            }

            // Find target if we don't have one (and not suppressed from recent abandon)
            if (!predator.HasTarget && predator.PhaseTimer <= 0)
            {
                float effectiveRange = predator.HuntRange * rangeMultiplier;
                float huntRangeSq = effectiveRange * effectiveRange;
                _spatialHash.QueryRadius(pos.X, pos.Y, effectiveRange, _nearbyEntities);

                // Calculate effective hunting mass (solo or pack)
                float effectiveMass = speciesDef.BodyMass;
                float maxHuntRatio = speciesDef.SoloHuntMaxRatio;
                bool isSwarm = speciesDef.IsSwarmHunter;
                if (isPack)
                {
                    // Count nearby pack/swarm members for effective mass
                    int packSize = 1;
                    foreach (int other in _nearbyEntities)
                    {
                        if (other == entity || !em.IsAlive(other))
                            continue;
                        if (isSwarm)
                        {
                            // Swarm: count ALL nearby same-species (colony-wide bravery)
                            if (em.HasComponents(other, ComponentFlags.Species) &&
                                em.HasComponents(entity, ComponentFlags.Species) &&
                                em.Species[other].SpeciesId == em.Species[entity].SpeciesId)
                                packSize++;
                        }
                        else
                        {
                            // Traditional pack: same-group members only
                            if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social))
                                continue;
                            ref var otherSocial = ref em.Socials[other];
                            if (otherSocial.GroupId == groupId)
                                packSize++;
                        }
                    }
                    float exponent = speciesDef.PackHuntMassExponent;
                    effectiveMass *= MathF.Pow(packSize, exponent);
                }
                float maxPreyMass = effectiveMass * maxHuntRatio;

                float bestScore = float.MaxValue;
                int bestPrey = -1;
                int evaluated = 0;

                foreach (int preyEntity in _nearbyEntities)
                {
                    if (!em.IsAlive(preyEntity))
                        continue;

                    // Skip a target we recently gave up on as non-viable
                    if (preyEntity == predator.AvoidTarget)
                        continue;

                    // Swarm hunters can target any living creature (including predators)
                    // Normal hunters can only target entities with the Prey flag
                    bool isValidTarget = em.HasComponents(preyEntity, ComponentFlags.Prey);
                    if (!isValidTarget && isSwarm)
                        isValidTarget = em.HasComponents(preyEntity, ComponentFlags.Energy | ComponentFlags.Species);
                    if (!isValidTarget)
                        continue;

                    // Size-based eligibility: prey must not be too large
                    float preyMass = GetPreyBodyMass(preyEntity, em);
                    if (preyMass > maxPreyMass)
                        continue;  // Too large to hunt

                    // Skip same-species targets (no cannibalism) and unhuntable species
                    if (em.HasComponents(preyEntity, ComponentFlags.Species))
                    {
                        ref var preySpecies = ref em.Species[preyEntity];
                        var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);

                        // Don't hunt your own kind
                        if (em.HasComponents(entity, ComponentFlags.Species))
                        {
                            ref var mySpecies = ref em.Species[entity];
                            if (preySpecies.SpeciesId == mySpecies.SpeciesId)
                                continue;
                        }

                        if (preyDef.UnhuntableByPredators
                            && speciesDef.Diet == DietType.Carnivore)
                            continue;  // Faelings can't be hunted by carnivores

                        // Specialist diet: a predator with an ExclusivePrey list ignores
                        // everything not on it (e.g. Penguins only ever hunt Fish).
                        if (speciesDef.ExclusivePrey != null
                            && !speciesDef.ExclusivePrey.Contains(preyDef.Name))
                            continue;

                        // Fallback tier: small fry a well-fed predator won't waste effort on
                        // (a Shark passes over fish until it is genuinely hungry, so the shoal
                        // is thinned rather than cropped flat).
                        if (!speciesDef.WillHunt(preyDef.Name, hungerRatio))
                            continue;
                    }

                    ref var preyPos = ref em.Positions[preyEntity];
                    float distSq = MathUtils.DistanceSquared(pos.X, pos.Y, preyPos.X, preyPos.Y);

                    if (distSq >= huntRangeSq)
                        continue;

                    // Bound expensive scoring/path work in dense clusters.
                    if (++evaluated > MaxHuntCandidates)
                        break;

                    float score = distSq;

                    // Terrain penalty
                    if (_worldManager != null)
                    {
                        var preyTile = _worldManager.GetTile(preyPos.X, preyPos.Y);
                        // Species-aware: the raw tile table rates open water as near-impassable,
                        // which penalised an aquatic hunter for every target it has — a shark
                        // scored fish in deep water as if it were chasing them up a mountain.
                        float terrainPenalty = TerrainProfile.SteerAversion(speciesDef, preyTile) * 50f;
                        terrainPenalty *= (1f - urgency * 0.7f);
                        score += terrainPenalty;

                        // Species-specific terrain comfort: heavily penalize prey on tiles
                        // the hunter finds uncomfortable. Prevents chasing into enemy terrain
                        // (e.g. Sectids chasing Monkeys deep into Wetland).
                        float comfortPenalty = speciesDef.GetTerrainComfortModifier(preyTile);
                        if (comfortPenalty > 0f)
                        {
                            // Scale: +3 comfort → +60 score, +6 comfort → +120 score
                            // Urgency reduces penalty (desperate hunters tolerate more)
                            score += comfortPenalty * 20f * (1f - urgency * 0.5f);
                        }
                    }

                    // Concealment: camouflaged prey are harder to detect (Rabbit in forest,
                    // Scorpion in desert, Arctic Fox in snow). Inflate their score so a hunter
                    // only locks on when close or lacking better options — effectively shrinking
                    // detection range over terrain the prey blends into. Weight tuned DOWN from
                    // 2.5 (which over-blinded the detection-reliant generalists — Hawk/Wolf/Jaguar
                    // — and handed dominance to ambushers). Flying hunters see from above, so
                    // ground cover barely hides prey from them.
                    if (_worldManager != null && em.HasComponents(preyEntity, ComponentFlags.Species))
                    {
                        var concealDef = SpeciesRegistry.GetById(em.Species[preyEntity].SpeciesId);
                        float conceal = TerrainProfile.Concealment(
                            concealDef, _worldManager.GetTile(preyPos.X, preyPos.Y));
                        if (conceal > 0f)
                            score *= 1f + conceal * (speciesDef.IsFlying ? 0.3f : 1.2f);
                    }

                    // Preferred prey bias: familiar prey scores better (lower)
                    if (speciesDef.PreferredPrey != null && em.HasComponents(preyEntity, ComponentFlags.Species))
                    {
                        ref var preySpecies = ref em.Species[preyEntity];
                        var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);
                        if (speciesDef.PreferredPrey.Contains(preyDef.Name))
                            score *= speciesDef.PreferredPreyBias;
                    }

                    // Spore/bloom preference: an anti-bloom hunter (Sectid) strongly prefers
                    // Shroomer spores and immature Shroomers, eating a bloom out before it
                    // fortifies instead of chasing the nearest random prey. Any Shroomer that
                    // reached scoring already passed the mass gate (grown ones are rejected
                    // earlier), so this can't lure a swarm onto an elder.
                    if (speciesDef.SporeHuntBias > 0f)
                    {
                        bool isSpore = em.HasComponents(preyEntity, ComponentFlags.Spore);
                        bool isShroomer = em.HasComponents(preyEntity, ComponentFlags.Species)
                            && em.Species[preyEntity].Type == SpeciesType.Shroomer;
                        if (isSpore || isShroomer)
                            score *= 1f - speciesDef.SporeHuntBias;
                    }

                    if (score < bestScore)
                    {
                        // Defer the across-water test until a candidate would actually win — only
                        // the prospective best pays for path sampling, not every prey in range.
                        // (Equivalent to the old per-candidate reject: a worse-scoring candidate
                        // never displaced the best, so its water state never mattered.)
                        if (speciesDef.AvoidsOpenWater && _worldManager != null
                            && _worldManager.GetWaterFractionOnPath(pos.X, pos.Y, preyPos.X, preyPos.Y,
                                   deepOnly: !speciesDef.AvoidsWater) > 0.15f)
                            continue; // Too much water between us and prey

                        bestScore = score;
                        bestPrey = preyEntity;
                    }
                }

                if (bestPrey >= 0)
                {
                    predator.TargetEntity = bestPrey;
                    BeginHuntTracking(em, entity, ref predator, bestPrey);
                    // Log hunt start
                    if (em.HasComponents(entity, ComponentFlags.Species) &&
                        em.HasComponents(bestPrey, ComponentFlags.Species))
                    {
                        ref var predSp = ref em.Species[entity];
                        ref var preySp = ref em.Species[bestPrey];
                        EcosystemLogger.Instance?.LogHuntStart(
                            predSp.SpeciesId, preySp.SpeciesId,
                            entity, bestPrey, pos.X, pos.Y);
                        // Decision log: WHY this target was taken (events log has the what).
                        if (EcosystemLogger.DecisionLoggingFor(predSp.SpeciesId))
                        {
                            ref var bp = ref em.Positions[bestPrey];
                            var preyName = SpeciesRegistry.GetById(preySp.SpeciesId)?.Name ?? "?";
                            EcosystemLogger.Instance!.LogDecision(predSp.SpeciesId, entity,
                                pos.X, pos.Y, "hunting", "target_acquired",
                                FormattableString.Invariant(
                                    $"prey={preyName}:{bestPrey};dist={MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, bp.X, bp.Y)):F1};hunger={hungerRatio:P0};urgency={urgency:F2};pack={isPack}"));
                        }
                    }
                    if (isPack)
                    {
                        _groupTargets[groupId] = bestPrey;
                        predator.Role = PackRole.Leader;
                        predator.Phase = PackPhase.Positioning;
                        predator.PhaseTimer = speciesDef.ConvergenceTimeout;
                    }
                }
            }

            // Hunger-driven tracking: when hungry and no target, search wide range
            if (!predator.HasTarget && hungerRatio < speciesDef.TrackingHungerThreshold
                && em.HasComponents(entity, ComponentFlags.Velocity))
            {
                // Wide-range scan for nearest prey (simulates scent/tracking)
                _nearbyEntities.Clear();
                _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.TrackingRange, _nearbyEntities);

                float bestTrackDistSq = float.MaxValue;
                int bestTrackTarget = -1;
                int trackEvaluated = 0;

                foreach (int preyEntity in _nearbyEntities)
                {
                    if (!em.IsAlive(preyEntity))
                        continue;

                    // Swarm hunters can track any living creature
                    bool isTrackable = em.HasComponents(preyEntity, ComponentFlags.Prey);
                    if (!isTrackable && speciesDef.IsSwarmHunter)
                        isTrackable = em.HasComponents(preyEntity, ComponentFlags.Energy | ComponentFlags.Species);
                    if (!isTrackable)
                        continue;

                    // Don't track your own species
                    if (em.HasComponents(entity, ComponentFlags.Species) &&
                        em.HasComponents(preyEntity, ComponentFlags.Species))
                    {
                        if (em.Species[entity].SpeciesId == em.Species[preyEntity].SpeciesId)
                            continue;
                    }

                    ref var preyPos2 = ref em.Positions[preyEntity];
                    float trackDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, preyPos2.X, preyPos2.Y);
                    if (trackDistSq >= bestTrackDistSq)
                        continue;

                    // Bound work in dense crowds (TrackingRange is wide — up to 80 tiles).
                    if (++trackEvaluated > MaxHuntCandidates)
                        break;

                    // Land predators skip tracking targets across water. Deferred to the
                    // prospective-best only, so path sampling runs a handful of times, not once
                    // per entity in the (large) tracking radius.
                    if (speciesDef.AvoidsOpenWater && _worldManager != null
                        && _worldManager.GetWaterFractionOnPath(pos.X, pos.Y, preyPos2.X, preyPos2.Y,
                               deepOnly: !speciesDef.AvoidsWater) > 0.15f)
                        continue;

                    bestTrackDistSq = trackDistSq;
                    bestTrackTarget = preyEntity;
                }

                if (bestTrackTarget >= 0)
                {
                    // Move toward prey at wander speed (tracking, not chasing)
                    ref var vel = ref em.Velocities[entity];
                    ref var trackPreyPos = ref em.Positions[bestTrackTarget];
                    float tdx = trackPreyPos.X - pos.X;
                    float tdy = trackPreyPos.Y - pos.Y;
                    var trackDir = MathUtils.Normalize(tdx, tdy);
                    float trackSpeed = speciesDef.BaseHuntSpeed * 0.8f;
                    // Blend toward tracking direction — heavier predators commit more
                    float trackAgility = Math.Clamp(1.5f / speciesDef.BodyMass, 0.25f, 1f);
                    vel.Dx += (trackDir.X * trackSpeed - vel.Dx) * trackAgility;
                    vel.Dy += (trackDir.Y * trackSpeed - vel.Dy) * trackAgility;

                    SteerForHabitat(ref vel, pos.X, pos.Y, speciesDef);

                    continue;  // Skip normal hunt movement — we're just tracking
                }
            }

            // Sit-and-wait ambushers snap their target onto whatever prey has wandered into pounce
            // range while they lurk, so they strike whatever comes close — not only the one prey
            // they first locked onto (which rarely strays into the small pounce zone on its own).
            if (speciesDef.AmbushDormant && predator.Stealth >= speciesDef.PounceStealthThreshold)
            {
                int snap = FindNearestPreyInRange(entity, pos.X, pos.Y, speciesDef.PounceRange, predator.AvoidTarget, em);
                if (snap >= 0 && snap != predator.TargetEntity)
                {
                    predator.TargetEntity = snap;
                    BeginHuntTracking(em, entity, ref predator, snap);
                }
            }

            // Hunt the target
            if (predator.HasTarget && em.IsAlive(predator.TargetEntity))
            {
                ref var preyPos = ref em.Positions[predator.TargetEntity];
                float dx = preyPos.X - pos.X;
                float dy = preyPos.Y - pos.Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float distSq = dx * dx + dy * dy;

                // Mass-based agility for direction blending during pursuit
                float huntAgility = Math.Clamp(1.5f / speciesDef.BodyMass, 0.25f, 1f);

                // === AMBUSH STEALTH UPDATE ===
                bool isAmbush = speciesDef.HuntingTactic == HuntingTactic.Ambush;
                if (isAmbush)
                {
                    // Tick down pounce timer
                    if (predator.PounceTimer > 0)
                        predator.PounceTimer--;

                    // Update stealth based on movement speed
                    if (predator.PounceTimer <= 0) // No stealth gain during pounce
                    {
                        float currentSpeed = MathF.Sqrt(
                            em.HasComponents(entity, ComponentFlags.Velocity)
                                ? em.Velocities[entity].Dx * em.Velocities[entity].Dx +
                                  em.Velocities[entity].Dy * em.Velocities[entity].Dy
                                : 0f);
                        float stealthSpeedLimit = speciesDef.BaseHuntSpeed * speciesDef.AmbushSpeedThreshold;

                        if (currentSpeed <= stealthSpeedLimit)
                        {
                            // Gain stealth when slow/still
                            float gain = speciesDef.AmbushStealthGain;

                            // Water tile bonus for semi-aquatic ambushers
                            if (speciesDef.WaterStealthBonus > 0f && _worldManager != null)
                            {
                                var currentTile = _worldManager.GetTile(pos.X, pos.Y);
                                if (currentTile.IsWater())
                                    gain += speciesDef.WaterStealthBonus;
                            }

                            predator.Stealth = MathF.Min(1f, predator.Stealth + gain);
                        }
                        else
                        {
                            // Lose stealth when moving fast
                            predator.Stealth = MathF.Max(0f, predator.Stealth - speciesDef.AmbushStealthDecay);
                        }
                    }
                }

                // Attack if in range
                float attackRangeSq = predator.AttackRange * predator.AttackRange;
                // Pounce attack multiplier: amplified damage during pounce burst
                float attackMult = (isAmbush && predator.PounceTimer > 0) ? speciesDef.PounceAttackMult : 1f;

                // <= 0 (not == 0): self-heals any cooldown that may already be sitting at a
                // stuck negative value from a prior LOD overshoot, so the attack always fires
                // the moment a predator is in range and off cooldown.
                if (distSq < attackRangeSq && predator.CurrentCooldown <= 0)
                {
                    if (em.HasComponents(predator.TargetEntity, ComponentFlags.Energy))
                    {
                        ref var preyEnergy = ref em.Energies[predator.TargetEntity];
                        float actualDamage = predator.AttackPower * attackMult;
                        preyEnergy.Current -= actualDamage;
                        preyEnergy.RegenCooldown = 60; // 3s combat cooldown at 20 TPS

                        if (EcosystemLogger.IsTrackingSpecies
                            && em.HasComponents(predator.TargetEntity, ComponentFlags.Species))
                        {
                            int aSid = em.HasComponents(entity, ComponentFlags.Species)
                                ? em.Species[entity].SpeciesId : -1;
                            ref var hitPos = ref em.Positions[predator.TargetEntity];
                            EcosystemLogger.Instance?.LogCombatHit(
                                aSid, entity,
                                em.Species[predator.TargetEntity].SpeciesId, predator.TargetEntity,
                                actualDamage, hitPos.X, hitPos.Y, "melee");
                        }

                        // Mark the attacker on victims that can fight back, so a pack/swarm member
                        // rallies its group to mob us (see DEFENSIVE RALLY). Also wakes a dormant
                        // Sectid being eaten so it can defend or flee instead of sleeping through it.
                        if (em.HasComponents(predator.TargetEntity, ComponentFlags.Predator))
                        {
                            ref var victimPred = ref em.Predators[predator.TargetEntity];
                            victimPred.LastAttacker = entity;
                            victimPred.LastAttackedTicks = RallyAlertDuration;
                        }
                        if (em.HasComponents(predator.TargetEntity, ComponentFlags.FoodCarrier))
                        {
                            ref var victimCarrier = ref em.FoodCarriers[predator.TargetEntity];
                            victimCarrier.IsHibernating = false;
                        }

                        // Thorn defense — attackers take growth-scaled counter-damage
                        if (em.HasComponents(predator.TargetEntity, ComponentFlags.Growth | ComponentFlags.Species))
                        {
                            ref var preySpecies = ref em.Species[predator.TargetEntity];
                            var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);
                            if (preyDef.ThornDamageBase > 0f)
                            {
                                ref var growth = ref em.Growths[predator.TargetEntity];
                                float thornFactor = preyDef.GetGrowthScalingFactor(growth.CurrentScale);
                                float thornDamage = preyDef.ThornDamageBase * thornFactor;
                                if (em.HasComponents(entity, ComponentFlags.Energy))
                                {
                                    ref var predEnergy = ref em.Energies[entity];
                                    predEnergy.Current -= thornDamage;
                                    predEnergy.RegenCooldown = 30;
                                    if (EcosystemLogger.IsTrackingSpecies
                                        && em.HasComponents(entity, ComponentFlags.Species))
                                    {
                                        EcosystemLogger.Instance?.LogCombatHit(
                                            preySpecies.SpeciesId, predator.TargetEntity,
                                            em.Species[entity].SpeciesId, entity,
                                            thornDamage, pos.X, pos.Y, "thorn");
                                    }
                                }
                            }
                        }

                        // Apply venom DOT if this predator has it
                        if (speciesDef.HasVenom && !em.HasComponents(predator.TargetEntity, ComponentFlags.VenomEffect))
                        {
                            em.VenomEffects[predator.TargetEntity] = new VenomEffect(
                                speciesDef.VenomDamagePerTick, speciesDef.VenomDurationTicks);
                            em.AddComponent(predator.TargetEntity, ComponentFlags.VenomEffect);
                        }

                        if (preyEnergy.IsDead)
                        {
                            _entitiesToKill.Add(predator.TargetEntity);

                            // Log the kill
                            if (em.HasComponents(entity, ComponentFlags.Species) &&
                                em.HasComponents(predator.TargetEntity, ComponentFlags.Species))
                            {
                                ref var predSp = ref em.Species[entity];
                                ref var preySp = ref em.Species[predator.TargetEntity];
                                ref var killPos = ref em.Positions[predator.TargetEntity];
                                EcosystemLogger.Instance?.LogKill(
                                    predSp.SpeciesId, preySp.SpeciesId,
                                    entity, predator.TargetEntity,
                                    killPos.X, killPos.Y);
                            }

                            // The killer eats first: a successful kill grants an immediate "prime
                            // cut" of nutrition scaled to prey size. The death still drops a corpse
                            // (via the ECS death hook) that packmates and scavengers feed from over
                            // time (CarrionSystem). Pack "sharing" of the remainder stays emergent.
                            if (em.HasComponents(predator.TargetEntity, ComponentFlags.Species))
                            {
                                var killedDef = SpeciesRegistry.GetById(
                                    em.Species[predator.TargetEntity].SpeciesId);
                                float killFeed = killedDef.EffectiveNutrition * KillNutritionShare;
                                hunger.Current = MathF.Min(hunger.Max, hunger.Current + killFeed);
                            }
                            predator.TargetEntity = -1;
                            predator.Phase = PackPhase.Idle;
                            predator.Role = PackRole.None;
                            // Linger on the fresh kill to feed rather than immediately re-hunting.
                            predator.PhaseTimer = Math.Max(predator.PhaseTimer, 30);
                        }
                    }
                    // Ambush burst: while a pounce is active the predator latches on and rakes the
                    // prey in a fast flurry (a quarter of the normal cooldown) — a short window of
                    // very high DPS that secures the kill (or lands the venom bite) before the prey
                    // bolts, instead of one slow hit per pounce. Normal cadence otherwise.
                    predator.CurrentCooldown = (isAmbush && predator.PounceTimer > 0)
                        ? Math.Max(3, predator.AttackCooldown / 4)
                        : predator.AttackCooldown;

                    // After attacking, only disruptors retreat (to continue harassment cycle)
                    // Swarm hunters never retreat — they just keep biting
                    if (isPack && speciesDef.HuntingTactic == HuntingTactic.PackCoordinated
                        && predator.Role == PackRole.Disruptor
                        && predator.Phase == PackPhase.Disrupting)
                    {
                        bool preyIsolated = IsPreyIsolated(predator.TargetEntity, em);
                        if (!preyIsolated)
                        {
                            predator.Phase = PackPhase.Retreating;
                            predator.PhaseTimer = speciesDef.RetreatDuration;
                        }
                    }
                }
                else if (em.HasComponents(entity, ComponentFlags.Velocity))
                {
                    ref var vel = ref em.Velocities[entity];

                    // In attack range but mid-cooldown (and not mid-pounce): pace the prey — match
                    // its velocity to stay locked alongside it and strike the instant cooldown
                    // clears, instead of steering into it and shoving it across the map (the
                    // "prolonged push" that looked so off, worst on the slow long-cooldown predators).
                    if (distSq < attackRangeSq && predator.PounceTimer <= 0
                        && em.HasComponents(predator.TargetEntity, ComponentFlags.Velocity))
                    {
                        ref var preyVel = ref em.Velocities[predator.TargetEntity];
                        vel.Dx = preyVel.Dx;
                        vel.Dy = preyVel.Dy;
                    }
                    else
                    {
                    float huntSpeed = speciesDef.BaseHuntSpeed * speedMultiplier;

                    // === TACTIC-BASED MOVEMENT DISPATCH ===
                    switch (speciesDef.HuntingTactic)
                    {
                        case HuntingTactic.Ambush:
                            ApplyAmbushMovement(ref vel, ref predator, dx, dy, distSq,
                                huntSpeed, huntAgility, speedMultiplier, speciesDef);
                            break;

                        case HuntingTactic.Swarm:
                        {
                            // Direct swarm chase — all rush together, no retreat
                            var dir = MathUtils.Normalize(dx, dy);
                            float swarmSpeed = huntSpeed * 1.2f;
                            BlendVelocity(ref vel, dir.X * swarmSpeed, dir.Y * swarmSpeed, huntAgility);
                            break;
                        }

                        case HuntingTactic.PackCoordinated when isPack && predator.Role != PackRole.None:
                        {
                            // Coordinated pack: leader/flanker/disruptor roles
                            bool preyIsolated = IsPreyIsolated(predator.TargetEntity, em);
                            ApplyPackTactics(entity, ref pos, ref vel, ref predator,
                                preyPos.X, preyPos.Y, dist, huntSpeed, em,
                                preyIsolated, speciesDef, huntAgility);
                            break;
                        }

                        default:
                        {
                            // Solo / PackCoordinated without pack — direct chase
                            var dir = MathUtils.Normalize(dx, dy);
                            BlendVelocity(ref vel, dir.X * huntSpeed, dir.Y * huntSpeed, huntAgility);
                            break;
                        }
                    }

                    // Keep the predator in its element during pursuit: sharks off land, land
                    // waders out of DEEP water (they may still lunge through shallows on a pounce,
                    // but never dive into drowning depth chasing prey), insects out of all water.
                    SteerForHabitat(ref vel, pos.X, pos.Y, speciesDef);
                    }
                }
            }
        }

        // Kill dead prey
        foreach (int preyEntity in _entitiesToKill)
        {
            // Faeling death: pass power to crystal for next spawn
            if (em.HasComponents(preyEntity, ComponentFlags.FaelingPower))
            {
                ref var power = ref em.FaelingPowers[preyEntity];
                int crystalId = power.LinkedCrystal;
                if (crystalId >= 0 && em.IsAlive(crystalId) && em.HasComponents(crystalId, ComponentFlags.Crystal))
                {
                    ref var crystal = ref em.Crystals[crystalId];
                    crystal.InheritedPower = power.Power * 0.5f;
                    crystal.LinkedFaeling = -1;
                    crystal.SpawnTimer = crystal.SpawnDelay;
                }
            }

            _spatialHash.Remove(preyEntity);
            em.DestroyEntity(preyEntity);
        }
    }

    /// <summary>
    /// Check if a prey is isolated from its herd (few/no other prey nearby).
    /// </summary>
    private bool IsPreyIsolated(int preyEntity, EntityManager em)
    {
        if (!em.IsAlive(preyEntity))
            return true;

        ref var preyPos = ref em.Positions[preyEntity];
        float isolationRadius = 6f;  // Distance to check for other prey
        int minHerdSize = 2;         // Need at least this many nearby to be "in herd"

        _nearbyEntities.Clear();
        _spatialHash.QueryRadius(preyPos.X, preyPos.Y, isolationRadius, _nearbyEntities);

        int nearbyPreyCount = 0;
        foreach (int other in _nearbyEntities)
        {
            if (other == preyEntity || !em.IsAlive(other))
                continue;

            if (em.HasComponents(other, ComponentFlags.Prey))
                nearbyPreyCount++;

            if (nearbyPreyCount >= minHerdSize)
                return false;  // Still has herd protection
        }

        return true;  // Isolated - few or no other prey nearby
    }

    private void AssignPackRole(int entity, int target, EntityManager em, ref Predator predator, ref Social social,
                                SpeciesDefinition speciesDef)
    {
        // Find other pack members and count existing roles
        ref var pos = ref em.Positions[entity];
        _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.PackCoordinationRadius, _packMembers);

        int disruptorCount = 0;
        int flankerCount = 0;
        bool hasLeader = false;

        foreach (int other in _packMembers)
        {
            if (other == entity || !em.IsAlive(other))
                continue;
            if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social))
                continue;

            ref var otherSocial = ref em.Socials[other];
            if (otherSocial.GroupId != social.GroupId)
                continue;

            ref var otherPredator = ref em.Predators[other];
            if (otherPredator.Role == PackRole.Leader)
                hasLeader = true;
            else if (otherPredator.Role == PackRole.Flanker)
                flankerCount++;
            else if (otherPredator.Role == PackRole.Disruptor)
                disruptorCount++;
        }

        // Assign role:
        // 1 leader, 1 disruptor first, then fill flankers, then 2nd disruptor if pack is large
        if (!hasLeader && social.LeadershipScore > 0.5f)
        {
            predator.Role = PackRole.Leader;
        }
        else if (disruptorCount < 1)
        {
            predator.Role = PackRole.Disruptor;  // Always need at least 1 disruptor
        }
        else if (flankerCount < 2)
        {
            predator.Role = PackRole.Flanker;    // Fill flanker positions
        }
        else if (disruptorCount < 2)
        {
            predator.Role = PackRole.Disruptor;  // 2nd disruptor in larger packs
        }
        else
        {
            predator.Role = PackRole.Flanker;    // Extra members flank
        }

        // Set initial phase and timer based on role
        predator.Phase = PackPhase.Positioning;
        predator.PhaseTimer = predator.Role switch
        {
            PackRole.Leader => speciesDef.ConvergenceTimeout,
            PackRole.Disruptor => speciesDef.PositioningDuration,
            _ => 0  // Flankers don't use timer
        };
    }

    private void ApplyPackTactics(int entity, ref Position pos, ref Velocity vel, ref Predator predator,
                                   float targetX, float targetY, float dist, float huntSpeed, EntityManager em,
                                   bool preyIsolated, SpeciesDefinition speciesDef, float agility)
    {
        float dx = targetX - pos.X;
        float dy = targetY - pos.Y;

        float roleSpeedMult = predator.Role switch
        {
            PackRole.Leader => speciesDef.LeaderSpeedMult,
            PackRole.Flanker => speciesDef.FlankerSpeedMult,
            PackRole.Disruptor => speciesDef.ChaserSpeedMult,
            _ => 1.0f
        };
        float effectiveSpeed = huntSpeed * roleSpeedMult;

        // === CONVERGENCE: all-in kill rush ===
        // Triggered by leader, prey isolation, or phase propagation from first pass
        if (predator.Phase == PackPhase.Converging || preyIsolated)
        {
            predator.Phase = PackPhase.Converging;
            var chaseDir = MathUtils.Normalize(dx, dy);
            BlendVelocity(ref vel, chaseDir.X * effectiveSpeed * 1.3f, chaseDir.Y * effectiveSpeed * 1.3f, agility);
            return;
        }

        // === Per-role behavior ===
        switch (predator.Role)
        {
            case PackRole.Leader:
                ApplyLeaderBehavior(entity, ref pos, ref vel, ref predator, targetX, targetY, dx, dy, dist, effectiveSpeed, em, speciesDef, agility);
                break;

            case PackRole.Flanker:
                ApplyFlankerBehavior(entity, ref pos, ref vel, targetX, targetY, effectiveSpeed, em, agility);
                break;

            case PackRole.Disruptor:
                ApplyDisruptorBehavior(ref pos, ref vel, ref predator, dx, dy, dist, effectiveSpeed, speciesDef, agility);
                break;

            default:
                var dir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, dir.X * effectiveSpeed, dir.Y * effectiveSpeed, agility);
                break;
        }
    }

    /// <summary>
    /// Leader holds at observation distance while monitoring flanker positions.
    /// Triggers convergence when flankers are in position or timeout expires.
    /// </summary>
    private void ApplyLeaderBehavior(int entity, ref Position pos, ref Velocity vel, ref Predator predator,
                                      float targetX, float targetY, float dx, float dy, float dist,
                                      float speed, EntityManager em, SpeciesDefinition speciesDef, float agility)
    {
        float holdDist = 6f;

        if (dist > holdDist)
        {
            // Approach to observation distance
            var dir = MathUtils.Normalize(dx, dy);
            BlendVelocity(ref vel, dir.X * speed * 0.7f, dir.Y * speed * 0.7f, agility);
        }
        else
        {
            // Hold position, circle slowly to maintain pressure
            float normDist = MathF.Sqrt(dx * dx + dy * dy);
            if (normDist > 0.01f)
                BlendVelocity(ref vel, -dy / normDist * 0.02f, dx / normDist * 0.02f, agility);
        }

        // Decrement convergence timer
        predator.PhaseTimer--;

        // Check convergence triggers:
        // 1. Timer expired — enough disruption, commit to kill
        // 2. Flanker reached opposite side of prey — escape cut off
        if (predator.PhaseTimer <= 0)
        {
            predator.Phase = PackPhase.Converging;
        }
        else
        {
            int groupId = em.HasComponents(entity, ComponentFlags.Social) ? em.Socials[entity].GroupId : -1;
            if (groupId >= 0 && CheckFlankersInPosition(entity, em, targetX, targetY, pos.X, pos.Y, groupId))
            {
                predator.Phase = PackPhase.Converging;
            }
        }
    }

    /// <summary>
    /// Flankers circle around to the OPPOSITE side of prey from the leader,
    /// cutting off escape routes. Two flankers spread to form a V behind the prey.
    /// </summary>
    private void ApplyFlankerBehavior(int entity, ref Position pos, ref Velocity vel,
                                       float preyX, float preyY, float speed,
                                       EntityManager em, float agility)
    {
        // Find leader position for this pack
        int groupId = em.HasComponents(entity, ComponentFlags.Social) ? em.Socials[entity].GroupId : -1;

        if (groupId < 0 || !_groupLeaderPositions.TryGetValue(groupId, out var leaderPos))
        {
            // No leader info — direct approach as fallback
            float fdx = preyX - pos.X;
            float fdy = preyY - pos.Y;
            var dir = MathUtils.Normalize(fdx, fdy);
            BlendVelocity(ref vel, dir.X * speed, dir.Y * speed, agility);
            return;
        }

        // Direction from leader to prey (the "front" of the attack)
        float frontDx = preyX - leaderPos.x;
        float frontDy = preyY - leaderPos.y;
        float frontLen = MathF.Sqrt(frontDx * frontDx + frontDy * frontDy);
        if (frontLen < 0.01f)
        {
            BlendVelocity(ref vel, 0, 0, agility);
            return;
        }
        frontDx /= frontLen;
        frontDy /= frontLen;

        // Target position: BEHIND the prey (past it from leader's perspective)
        float behindDist = 4f;
        float idealX = preyX + frontDx * behindDist;
        float idealY = preyY + frontDy * behindDist;

        // Spread flankers to opposite sides using perpendicular offset
        float perpX = -frontDy;
        float perpY = frontDx;

        // Determine which side based on current position relative to attack axis
        float relX = pos.X - preyX;
        float relY = pos.Y - preyY;
        float cross = relX * perpY - relY * perpX;
        float sideSign = cross >= 0 ? 1f : -1f;

        float spreadDist = 3f;
        idealX += perpX * sideSign * spreadDist;
        idealY += perpY * sideSign * spreadDist;

        // Move toward ideal flank position
        float toIdealDx = idealX - pos.X;
        float toIdealDy = idealY - pos.Y;
        var flankerDir = MathUtils.Normalize(toIdealDx, toIdealDy);
        BlendVelocity(ref vel, flankerDir.X * speed, flankerDir.Y * speed, agility);
    }

    /// <summary>
    /// Disruptors cycle between rushing toward prey and retreating.
    /// Their job is to scatter the herd so flankers can cut off isolated prey.
    /// Limited to 1-2 per pack.
    /// </summary>
    private void ApplyDisruptorBehavior(ref Position pos, ref Velocity vel, ref Predator predator,
                                         float dx, float dy, float dist, float speed,
                                         SpeciesDefinition speciesDef, float agility)
    {
        switch (predator.Phase)
        {
            case PackPhase.Positioning:
                // Initial approach before first rush
                if (dist > 5f)
                {
                    var dir = MathUtils.Normalize(dx, dy);
                    BlendVelocity(ref vel, dir.X * speed * 0.8f, dir.Y * speed * 0.8f, agility);
                }
                predator.PhaseTimer--;
                if (predator.PhaseTimer <= 0)
                {
                    predator.Phase = PackPhase.Disrupting;
                    predator.PhaseTimer = speciesDef.RushDuration;
                }
                break;

            case PackPhase.Disrupting:
                // Rush toward prey to scatter herd
                var rushDir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, rushDir.X * speed * 1.3f, rushDir.Y * speed * 1.3f, agility);
                predator.PhaseTimer--;
                if (predator.PhaseTimer <= 0)
                {
                    predator.Phase = PackPhase.Retreating;
                    predator.PhaseTimer = speciesDef.RetreatDuration;
                }
                break;

            case PackPhase.Retreating:
                // Back off to let herd scatter
                if (dist < 5f)
                {
                    var retreatDir = MathUtils.Normalize(-dx, -dy);
                    BlendVelocity(ref vel, retreatDir.X * speed * 0.8f, retreatDir.Y * speed * 0.8f, agility);
                }
                predator.PhaseTimer--;
                if (predator.PhaseTimer <= 0)
                {
                    // Next rush cycle
                    predator.Phase = PackPhase.Disrupting;
                    predator.PhaseTimer = speciesDef.RushDuration;
                }
                break;

            default:
                // Fallback: approach
                var defDir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, defDir.X * speed, defDir.Y * speed, agility);
                break;
        }
    }

    /// <summary>
    /// Ambush hunting movement: stalk → build stealth → pounce burst.
    /// Separates ambush behavior into its own method for clean tactic dispatch.
    /// </summary>
    private static void ApplyAmbushMovement(ref Velocity vel, ref Predator predator,
        float dx, float dy, float distSq,
        float huntSpeed, float huntAgility, float speedMultiplier,
        SpeciesDefinition speciesDef)
    {
        float pounceRangeSq = speciesDef.PounceRange * speciesDef.PounceRange;

        if (predator.PounceTimer > 0)
        {
            // POUNCING: explosive burst toward prey
            var dir = MathUtils.Normalize(dx, dy);
            float pounceSpeed = speciesDef.BaseHuntSpeed * speciesDef.PounceSpeedMult * speedMultiplier;
            BlendVelocity(ref vel, dir.X * pounceSpeed, dir.Y * pounceSpeed, 0.8f);
        }
        else if (predator.Stealth >= speciesDef.PounceStealthThreshold && distSq <= pounceRangeSq)
        {
            // TRIGGER POUNCE: within range and stealthed enough
            predator.PounceTimer = speciesDef.PounceDuration;
            predator.Stealth = 0f;

            var dir = MathUtils.Normalize(dx, dy);
            float pounceSpeed = speciesDef.BaseHuntSpeed * speciesDef.PounceSpeedMult * speedMultiplier;
            BlendVelocity(ref vel, dir.X * pounceSpeed, dir.Y * pounceSpeed, 0.8f);
        }
        else if (predator.Stealth > 0.1f)
        {
            if (speciesDef.AmbushDormant)
            {
                // LURK: sit motionless and let prey wander into pounce range. Approaching is too
                // slow to close and would shed stealth; staying still maxes stealth (≈invisible to
                // prey) and IsDormant drops metabolism so it can wait out a lean patch.
                vel.Dx = 0f;
                vel.Dy = 0f;
                predator.IsDormant = true;
            }
            else
            {
                // STALKING: approach slowly to maintain/build stealth
                var dir = MathUtils.Normalize(dx, dy);
                float stalkSpeed = speciesDef.BaseHuntSpeed * speciesDef.AmbushSpeedThreshold * 0.9f;
                BlendVelocity(ref vel, dir.X * stalkSpeed, dir.Y * stalkSpeed, huntAgility * 0.5f);
            }
        }
        else
        {
            // NO STEALTH: chase openly (post-pounce or stealth broke)
            var dir = MathUtils.Normalize(dx, dy);
            BlendVelocity(ref vel, dir.X * huntSpeed, dir.Y * huntSpeed, huntAgility);
        }
    }

    /// <summary>
    /// Check if any flanker has reached the opposite side of the prey from the leader.
    /// Uses dot product: negative means the flanker is behind the prey relative to the leader.
    /// </summary>
    private bool CheckFlankersInPosition(int leaderEntity, EntityManager em,
                                          float preyX, float preyY, float leaderX, float leaderY, int groupId)
    {
        // Direction from prey toward leader
        float pToLdx = leaderX - preyX;
        float pToLdy = leaderY - preyY;
        float pToLLen = MathF.Sqrt(pToLdx * pToLdx + pToLdy * pToLdy);
        if (pToLLen < 0.01f) return false;
        pToLdx /= pToLLen;
        pToLdy /= pToLLen;

        // Check pack members around the prey
        _packMembers.Clear();
        _spatialHash.QueryRadius(preyX, preyY, 15f, _packMembers);

        foreach (int other in _packMembers)
        {
            if (other == leaderEntity || !em.IsAlive(other)) continue;
            if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social)) continue;
            ref var otherSocial = ref em.Socials[other];
            if (otherSocial.GroupId != groupId) continue;
            ref var otherPred = ref em.Predators[other];
            if (otherPred.Role != PackRole.Flanker) continue;

            // Dot product of (prey→leader) and (prey→flanker)
            // Negative = flanker is on opposite side = escape route blocked
            ref var flankerPos = ref em.Positions[other];
            float pToFdx = flankerPos.X - preyX;
            float pToFdy = flankerPos.Y - preyY;
            float dot = pToLdx * pToFdx + pToLdy * pToFdy;
            if (dot < 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Blend velocity toward a target using mass-based agility.
    /// Smaller creatures (high agility) snap quickly; heavier ones turn gradually.
    /// </summary>
    private static void BlendVelocity(ref Velocity vel, float targetDx, float targetDy, float agility)
    {
        vel.Dx += (targetDx - vel.Dx) * agility;
        vel.Dy += (targetDy - vel.Dy) * agility;
    }

    /// <summary>
    /// Proactive threat detection for the defensive rally: find the nearest predator of another
    /// species that is either hunting us / a groupmate, or intruding close into our space. Returns
    /// -1 if none. Lets an idle pack/swarm member spot a stalker and rally before being bitten.
    /// </summary>
    private int FindProactiveThreat(int self, int groupId, float x, float y,
        SpeciesDefinition speciesDef, EntityManager em)
    {
        int mySpeciesId = em.HasComponents(self, ComponentFlags.Species) ? em.Species[self].SpeciesId : -1;
        float detectRange = speciesDef.HuntRange;
        float intrudeRangeSq = (detectRange * 0.5f) * (detectRange * 0.5f);

        _nearbyEntities.Clear();
        _spatialHash.QueryRadius(x, y, detectRange, _nearbyEntities);

        int best = -1;
        float bestDistSq = float.MaxValue;
        foreach (int other in _nearbyEntities)
        {
            if (other == self || !em.IsAlive(other)) continue;
            if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Position)) continue;
            // Don't rally against our own faction
            if (em.HasComponents(other, ComponentFlags.Species) && em.Species[other].SpeciesId == mySpeciesId)
                continue;
            // A dormant Sectid is no threat
            if (em.HasComponents(other, ComponentFlags.FoodCarrier) && em.FoodCarriers[other].IsHibernating)
                continue;

            ref var op = ref em.Positions[other];
            float dSq = MathUtils.DistanceSquared(x, y, op.X, op.Y);

            ref var otherPred = ref em.Predators[other];
            bool huntingUs = otherPred.HasTarget && IsSelfOrGroupmate(otherPred.TargetEntity, self, groupId, em);
            bool intruding = dSq <= intrudeRangeSq;
            if (!huntingUs && !intruding) continue;

            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = other;
            }
        }
        return best;
    }

    /// <summary>True if <paramref name="target"/> is the entity itself or one of its groupmates.</summary>
    private static bool IsSelfOrGroupmate(int target, int self, int groupId, EntityManager em)
    {
        if (target == self) return true;
        if (groupId < 0 || !em.IsAlive(target)) return false;
        if (!em.HasComponents(target, ComponentFlags.Social)) return false;
        return em.Socials[target].GroupId == groupId;
    }

    /// <summary>
    /// Record the baselines used by target-viability re-evaluation when a hunt begins:
    /// the target's current HP (to measure damage progress) and our own HP (to detect when a
    /// counterattacking target is hurting us too much to be worth it).
    /// </summary>
    private static void BeginHuntTracking(EntityManager em, int self, ref Predator predator, int target)
    {
        predator.HuntTicks = 0;
        predator.ApproachTicks = 0;
        predator.TargetBestDist = float.MaxValue;
        predator.TargetLastEnergy = em.HasComponents(target, ComponentFlags.Energy)
            ? em.Energies[target].Current : 0f;
        predator.SelfStartEnergy = em.HasComponents(self, ComponentFlags.Energy)
            ? em.Energies[self].Current : 0f;
    }


    /// <summary>
    /// Steer velocity away from water tiles ahead. Checks 2 tiles in the movement
    /// direction; if water is found, tries ±45° and ±90° offsets and picks the clearest.
    /// </summary>
    // Terrain a steering creature treats as an obstacle. avoidLand = an aquatic creature (Shark/
    // Fish) that must stay in water — anything NOT water blocks it. Otherwise: deep water only for
    // waders (land predators), any water for non-swimmers (insects passing deepOnly = false).
    private static bool IsBlockingTerrain(TileType tile, bool avoidLand, bool deepOnly)
        => avoidLand ? !tile.IsWater() : (deepOnly ? tile.IsDeepWater() : tile.IsWater());

    /// <summary>
    /// Steer a hunting predator off terrain it can't safely traverse, so pursuit doesn't beach a
    /// shark or drown a wolf: aquatic → avoid land; insects (AvoidsWater) → avoid all water; land
    /// waders (AvoidsOpenWater) → avoid DEEP water (they wade shallows, even mid-pounce, but never
    /// dive into drowning depth). Semi-aquatic (Croc/Polar Bear) steer around nothing.
    /// </summary>
    private void SteerForHabitat(ref Velocity vel, float posX, float posY, SpeciesDefinition sp)
    {
        if (sp.IsAquatic)
            SteerAroundWater(ref vel, posX, posY, deepOnly: false, avoidLand: true);
        else if (sp.AvoidsWater)
            SteerAroundWater(ref vel, posX, posY, deepOnly: false);
        else if (sp.AvoidsOpenWater)
            SteerAroundWater(ref vel, posX, posY, deepOnly: true);
    }

    private void SteerAroundWater(ref Velocity vel, float posX, float posY, bool deepOnly, bool avoidLand = false)
    {
        if (_worldManager == null || (vel.Dx == 0 && vel.Dy == 0)) return;

        float speed = MathF.Sqrt(vel.Dx * vel.Dx + vel.Dy * vel.Dy);
        float nx = vel.Dx / speed;
        float ny = vel.Dy / speed;

        // Look ahead 2 tiles for water (deep-only for waders, any water for non-swimmers)
        bool waterAhead = false;
        for (float d = 1f; d <= 2f; d += 1f)
        {
            if (IsBlockingTerrain(_worldManager.GetTile(posX + nx * d, posY + ny * d), avoidLand, deepOnly))
            {
                waterAhead = true;
                break;
            }
        }
        if (!waterAhead) return;

        // Try offset angles and pick the clearest path
        int bestWater = 3;
        float bestAngle = 0f;
        ReadOnlySpan<float> offsets = stackalloc float[]
        {
            MathF.PI / 4, -MathF.PI / 4,
            MathF.PI / 2, -MathF.PI / 2
        };

        foreach (float angle in offsets)
        {
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            float rnx = nx * cos - ny * sin;
            float rny = nx * sin + ny * cos;

            int waterCount = 0;
            for (float d = 1f; d <= 2f; d += 1f)
            {
                if (IsBlockingTerrain(_worldManager.GetTile(posX + rnx * d, posY + rny * d), avoidLand, deepOnly))
                    waterCount++;
            }

            if (waterCount < bestWater)
            {
                bestWater = waterCount;
                bestAngle = angle;
                if (waterCount == 0) break; // Clear path found
            }
        }

        if (bestAngle != 0f)
        {
            float cos = MathF.Cos(bestAngle);
            float sin = MathF.Sin(bestAngle);
            vel.Dx = (nx * cos - ny * sin) * speed;
            vel.Dy = (nx * sin + ny * cos) * speed;
        }
    }

    /// <summary>
    /// Get the effective body mass of a prey entity for hunting eligibility.
    /// <summary>
    /// Nearest huntable prey within <paramref name="range"/> of (x,y), excluding self, same-species,
    /// and the avoid target. Used by sit-and-wait ambushers to snap onto prey that strays into
    /// pounce range while they lurk. Returns -1 if none.
    /// </summary>
    private int FindNearestPreyInRange(int self, float x, float y, float range, int avoid, EntityManager em)
    {
        _nearbyEntities.Clear();
        _spatialHash.QueryRadius(x, y, range, _nearbyEntities);
        int mySpecies = em.HasComponents(self, ComponentFlags.Species) ? em.Species[self].SpeciesId : 0;
        int best = -1;
        float bestDistSq = range * range;
        foreach (int p in _nearbyEntities)
        {
            if (p == self || p == avoid || !em.IsAlive(p)) continue;
            if (!em.HasComponents(p, ComponentFlags.Prey)) continue;
            if (mySpecies != 0 && em.HasComponents(p, ComponentFlags.Species)
                && em.Species[p].SpeciesId == mySpecies) continue;
            ref var pp = ref em.Positions[p];
            float d = MathUtils.DistanceSquared(x, y, pp.X, pp.Y);
            if (d < bestDistSq) { bestDistSq = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// Spores use a small fixed mass. Growing creatures scale mass with CurrentScale.
    /// </summary>
    private float GetPreyBodyMass(int preyEntity, EntityManager em)
    {
        if (em.HasComponents(preyEntity, ComponentFlags.Spore))
            return SporeBodyMass;

        if (em.HasComponents(preyEntity, ComponentFlags.Species))
        {
            ref var preySpecies = ref em.Species[preyEntity];
            var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);
            float mass = preyDef.BodyMass;

            // Growing creatures (Shroomers) scale mass with current size
            if (em.HasComponents(preyEntity, ComponentFlags.Growth))
            {
                ref var growth = ref em.Growths[preyEntity];
                mass *= growth.CurrentScale;
            }

            return mass;
        }

        return 1f;
    }
}
