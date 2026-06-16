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
/// Carrion / corpse persistence. When a creature dies it leaves a corpse holding an edible
/// nutrient pool (bigger for larger and better-fed animals). Predators, omnivores and Sectids
/// scavenge corpses over time — feeding happens AFTER the kill, not instantly. Sectids chop a
/// carcass quickly into their carrier sacks, which (for a big kill) means repeated trips and the
/// whole colony ferrying it home. Corpses stay fresh for a grace period, then slowly rot away.
/// </summary>
public sealed class CarrionSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly WorldManager _worldManager;
    private readonly List<int> _toRemove = new(32);
    private readonly List<int> _nearby = new(32);

    // === Tuning ===
    private const int GraceTicks = 200;            // ~10s fresh before rot starts
    private const int RotTicks = 1400;             // ticks to fully rot once decay begins
    private const float MinCorpseNutrition = 4f;   // below this, a death leaves no worthwhile corpse
    private const float ConditionFloor = 0.4f;     // a starved corpse still yields this fraction of base
    private const float EatRange = 1.8f;           // must be this close to feed
    private const float SeekRadius = 18f;          // eaters notice corpses within this
    private const float SectidChopRate = 6f;       // Sectids strip a carcass fast (into carrier sacks)
    private const int FeedCommitTicks = 20;        // suppress re-hunting while feeding on a corpse
    private const float DecompositionEnrich = 0.01f; // fraction of rotted nutrition that fertilises soil

    public CarrionSystem(SpatialHash spatialHash, WorldManager worldManager)
    {
        _spatialHash = spatialHash;
        _worldManager = worldManager;
    }

    /// <summary>
    /// Create a corpse from a dying creature, reading its position/species/condition. Call this
    /// just before the entity is destroyed, while its components are still intact. Skips entities
    /// that shouldn't leave scavengeable remains (structures, spores, Faelings) or that are too
    /// small to matter. The corpse is registered with the spatial hash next tick (LOD marks it due).
    /// </summary>
    public static void SpawnCorpse(EntityManager em, int source)
    {
        if (!em.HasComponents(source, ComponentFlags.Position | ComponentFlags.Species))
            return;
        // No corpse for structures, spores, existing corpses, or Faelings (they return to a crystal)
        if (em.HasComponents(source, ComponentFlags.Nest) ||
            em.HasComponents(source, ComponentFlags.Crystal) ||
            em.HasComponents(source, ComponentFlags.Spore) ||
            em.HasComponents(source, ComponentFlags.Carrion) ||
            em.HasComponents(source, ComponentFlags.FaelingPower))
            return;

        ref var sp = ref em.Species[source];
        var def = SpeciesRegistry.GetById(sp.SpeciesId);

        // Base nutrition by body size; better-fed animals leave more (condition factor).
        float condition = ConditionFloor;
        if (em.HasComponents(source, ComponentFlags.Hunger))
        {
            ref var h = ref em.Hungers[source];
            // Guard against a non-finite ratio: Math.Clamp(NaN, 0, 1) returns NaN, which
            // would otherwise produce a NaN-nutrition corpse and infect every scavenger
            // that feeds on it. Fall back to the condition floor in that case.
            float ratio = h.Max > 0f ? h.Current / h.Max : 0f;
            float clampedRatio = float.IsFinite(ratio) ? Math.Clamp(ratio, 0f, 1f) : 0f;
            condition = ConditionFloor + (1f - ConditionFloor) * clampedRatio;
        }
        float nutrition = def.EffectiveNutrition * condition;
        // Large grown bodies (Shroomers) carry proportionally more.
        if (em.HasComponents(source, ComponentFlags.Growth))
            nutrition *= MathF.Max(1f, em.Growths[source].CurrentScale);

        if (nutrition < MinCorpseNutrition)
            return;
        if (em.EntityCount >= EntityManager.MaxEntities - 1)
            return; // no room — skip the corpse rather than risk overflow

        ref var spos = ref em.Positions[source];
        int corpse = em.CreateEntity();

        em.Positions[corpse] = new Position(spos.X, spos.Y);
        em.AddComponent(corpse, ComponentFlags.Position);

        em.ChunkPositions[corpse] = new ChunkPosition();
        em.AddComponent(corpse, ComponentFlags.ChunkPosition);

        float decayPerTick = nutrition / RotTicks;
        em.Carrions[corpse] = new Carrion(nutrition, GraceTicks, decayPerTick, sp.SpeciesId);
        em.AddComponent(corpse, ComponentFlags.Carrion);

        // Dark fleshy mound; size scales with how much meat is on it.
        float size = Math.Clamp(4f + nutrition * 0.08f, 4f, 14f);
        em.Renderables[corpse] = new Renderable(new Color(0.32f, 0.22f, 0.18f), size, ShapeType.Diamond);
        em.AddComponent(corpse, ComponentFlags.Renderable);
    }

    public void Process(EntityManager em)
    {
        DecayCorpses(em);
        FeedScavengers(em);
    }

    private void DecayCorpses(EntityManager em)
    {
        _toRemove.Clear();
        const ComponentFlags required = ComponentFlags.Carrion | ComponentFlags.Position;

        foreach (int entity in em.Query(required))
        {
            ref var carrion = ref em.Carrions[entity];

            if (carrion.GraceTicks > 0)
            {
                carrion.GraceTicks--;
            }
            else
            {
                // Rot: the carcass loses nutrition, and a fraction of it fertilises the soil
                // beneath, speeding the tile's grazeable-nutrient regrowth (decomposition).
                carrion.Nutrition -= carrion.DecayPerTick;
                ref var cpos = ref em.Positions[entity];
                _worldManager.AddNutrition(cpos.X, cpos.Y, carrion.DecayPerTick * DecompositionEnrich);
            }

            if (carrion.IsDepleted)
            {
                _toRemove.Add(entity);
                continue;
            }

            // Shrink the carcass as it is eaten / rots.
            if (em.HasComponents(entity, ComponentFlags.Renderable))
            {
                ref var rend = ref em.Renderables[entity];
                float baseSize = Math.Clamp(4f + carrion.MaxNutrition * 0.08f, 4f, 14f);
                rend.Size = baseSize * (0.5f + 0.5f * carrion.Freshness);
            }
        }

        em.DestroyEntities(_toRemove);
    }

    private void FeedScavengers(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Predator | ComponentFlags.Hunger |
                                        ComponentFlags.Position | ComponentFlags.Velocity;

        foreach (int entity in em.Query(required))
        {
            if (!em.DueThisTick[entity])
                continue;

            ref var predator = ref em.Predators[entity];
            ref var hunger = ref em.Hungers[entity];
            if (hunger.Current >= hunger.Max * 0.95f)
                continue; // sated

            var def = em.HasComponents(entity, ComponentFlags.Species)
                ? SpeciesRegistry.GetById(em.Species[entity].SpeciesId) : null;
            if (def == null)
                continue;

            bool isSectid = em.Species[entity].Type == SpeciesType.Sectid;

            // A Sectid whose sacks are full should head home (NestSystem), not keep chopping.
            if (isSectid && em.HasComponents(entity, ComponentFlags.FoodCarrier)
                && em.FoodCarriers[entity].IsFull)
                continue;

            ref var pos = ref em.Positions[entity];

            // Corpse-seek range starts small and grows as hunger rises for dedicated hunters, so a
            // well-fed predator won't abandon the hunt to wander to a distant carcass. Sectids are
            // foragers and always range out to the full radius.
            float hungerRatio = hunger.Max > 0f ? hunger.Current / hunger.Max : 0f;
            float seek = isSectid ? SeekRadius : SeekRadius * Math.Clamp(1f - hungerRatio, 0.15f, 1f);

            // Land foragers won't path across water to a carcass (they'd drown getting there).
            bool avoidWater = !def.SemiAquatic && !def.IsAquatic;
            int corpse = FindNearestCorpse(em, pos.X, pos.Y, seek, avoidWater);
            if (corpse < 0)
                continue;

            ref var corpsePos = ref em.Positions[corpse];
            float dx = corpsePos.X - pos.X;
            float dy = corpsePos.Y - pos.Y;
            float distSq = dx * dx + dy * dy;

            // Live-prey hunting normally takes priority over scavenging (HuntingSystem owns the
            // chase). Sectids instead weigh a corpse and live prey equally and commit to whichever
            // is closer — so a Sectid only diverts to a corpse nearer than its current target.
            if (predator.HasTarget)
            {
                if (!isSectid)
                    continue;
                if (em.IsAlive(predator.TargetEntity))
                {
                    ref var tp = ref em.Positions[predator.TargetEntity];
                    if (MathUtils.DistanceSquared(pos.X, pos.Y, tp.X, tp.Y) <= distSq)
                        continue; // prey is closer — stay on the hunt
                    predator.TargetEntity = -1; // corpse is closer — drop the chase and scavenge
                }
            }

            ref var vel = ref em.Velocities[entity];

            if (distSq <= EatRange * EatRange)
            {
                // At the carcass — feed.
                ref var carrion = ref em.Carrions[corpse];
                float rate = isSectid ? SectidChopRate : MathF.Max(2f, def.MaxHunger * 0.02f);
                float taken = MathF.Min(rate, carrion.Nutrition);
                carrion.Nutrition -= taken;

                if (isSectid && em.HasComponents(entity, ComponentFlags.FoodCarrier))
                {
                    ref var carrier = ref em.FoodCarriers[entity];
                    carrier.FoodCarried = MathF.Min(carrier.MaxCarry, carrier.FoodCarried + taken);
                    hunger.Current = MathF.Min(hunger.Max, hunger.Current + taken * 0.5f);
                }
                else
                {
                    hunger.Current = MathF.Min(hunger.Max, hunger.Current + taken);
                }

                // Stay put and stay committed (don't wander off or re-hunt mid-meal).
                vel.Dx = 0f;
                vel.Dy = 0f;
                predator.PhaseTimer = Math.Max(predator.PhaseTimer, FeedCommitTicks);
            }
            else
            {
                // Move toward the carcass.
                float dist = MathF.Sqrt(distSq);
                float speed = def.BaseHuntSpeed > 0f ? def.BaseHuntSpeed : def.BaseWanderSpeed;
                vel.Dx = (dx / dist) * speed;
                vel.Dy = (dy / dist) * speed;
                predator.PhaseTimer = Math.Max(predator.PhaseTimer, FeedCommitTicks);
            }
        }
    }

    private int FindNearestCorpse(EntityManager em, float x, float y, float radius, bool avoidWater)
    {
        _nearby.Clear();
        _spatialHash.QueryRadius(x, y, radius, _nearby);
        int best = -1;
        float bestDistSq = float.MaxValue;
        float radiusSq = radius * radius;
        foreach (int other in _nearby)
        {
            if (!em.IsAlive(other) || !em.HasComponents(other, ComponentFlags.Carrion | ComponentFlags.Position))
                continue;
            if (em.Carrions[other].IsDepleted)
                continue;
            ref var op = ref em.Positions[other];
            float dSq = MathUtils.DistanceSquared(x, y, op.X, op.Y);
            // QueryRadius is cell-granular, so enforce the (now hunger-scaled) radius exactly.
            if (dSq >= bestDistSq || dSq > radiusSq)
                continue;
            // Don't pick a carcass on the far side of water — reaching it would mean drowning.
            if (avoidWater && _worldManager.GetWaterFractionOnPath(x, y, op.X, op.Y) > 0.15f)
                continue;
            bestDistSq = dSq;
            best = other;
        }
        return best;
    }

    /// <summary>True if an edible corpse is within feeding/seek range of a point.</summary>
    public bool CorpseNearby(EntityManager em, float x, float y, float radius)
    {
        _nearby.Clear();
        _spatialHash.QueryRadius(x, y, radius, _nearby);
        foreach (int other in _nearby)
        {
            if (!em.IsAlive(other) || !em.HasComponents(other, ComponentFlags.Carrion)) continue;
            if (!em.Carrions[other].IsDepleted) return true;
        }
        return false;
    }
}
