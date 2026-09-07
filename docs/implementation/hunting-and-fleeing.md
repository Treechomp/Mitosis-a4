# Hunting, fleeing & social

*Last updated: 2026-09-07 · verified against `7a7fb28`*

## `HuntingSystem` — `Systems/HuntingSystem.cs` · gated

The largest system in the project. Dispatched by `HuntingTactic` (`Solo`, `PackCoordinated`,
`Swarm`, `Ambush`), which is a field on `SpeciesDefinition`, not a branch on a species name.

Hunting is opportunistic: urgency scales with hunger and a predator only stops hunting near full.

### Eligibility — one shared test

`HuntingSystem.Eligibility` is used by target acquisition **and** by hunger tracking.
(Older source comments still refer to a `HuntingSystem.IsEligiblePrey`; no such member exists.) They used
to disagree: tracking asked only "is it prey, is it my own species, is there water in the way" and
ignored the mass ceiling, the exclusive-prey list, the fallback tier and the give-up blacklist. A
fox would therefore track a turtle it could never take, walk to it, be refused on arrival, and
press into it indefinitely — no target set, no attack, no damage either way, and several foxes each
picking the same turtle produced an observed pile-up. Because no target is ever assigned on that
path, none of the abandon logic could rescue it. The fix is to never start walking.

Exclusions: cannibalism, unhuntable species, anything outside an `ExclusivePrey` list, targets
across water (checked both in selection and in the wide tracking scan), and blacklisted give-ups.

**`ExclusivePrey` covers the rally path too.** The defensive counter-attack path deliberately drops
the usual mass and hunger gates for self-defence, and once dropped the exclusive list with them —
so a fish-only specialist used to hunt down whatever bit it, killing its own predators. Species
with no exclusive list are unaffected, so a swarm still mobs an attacker.

**The element barrier holds for the whole hunt**, not only at acquisition: prey standing on terrain
its pursuer's `TerrainProfile` rejects is dropped, and the **shared pack-target channel applies
eligibility too** — without that, one member publishing an ineligible target let the whole group
re-adopt it every tick.

### Scoring — `FindNearestPreyInRange`

A spatial-hash query, scored by distance, preferred-prey bias, a generic terrain penalty, a
species-specific terrain-comfort penalty, prey concealment (`TerrainProfile.Concealment` — a
camouflaged animal inflates its own score, which effectively shrinks detection range over matching
ground), and a **payoff term**: the candidate's score is divided by how much of the predator it
would actually feed, clamped. A low-value target must therefore be proportionally closer to win,
and the effect grades with predator size on its own.

This is a preference, not a gate — nothing is ever excluded for being small — and a hunger term
flattens most of it as the predator gets desperate.

`SporeHuntBias` boosts Shroomer spores and immature Shroomers for species that have it, so a swarm
eats a bloom out before it fortifies. Grown Shroomers still fail the mass gate, so the bias cannot
lure a swarm onto an elder.

`FallbackPrey` / `FallbackPreyHunger` express "only when genuinely hungry", which `PreferredPrey` —
one flat multiplier over a list — cannot: a predator rating two prey identically simply eats
whichever is nearer, which is always the smaller one.

### Mass gates

`GetPreyBodyMass` against the predator's own mass times a solo ratio; for packs and swarms the base
mass is scaled by group size raised to an exponent. `IsPreyIsolated` and `CheckFlankersInPosition`
support the pack phases.

### Attack landing

A strike lands when within `AttackRange` **plus both body radii** (`BodyMetrics`), and when the
attack cooldown has elapsed. Reach must be surface-to-surface because `CollisionSystem` holds
bodies apart by the sum of their radii — testing raw centre distance made bulky targets literally
unhittable, which silently made one faction the sole predator of mature blooms.

The cooldown gate is `<= 0` and the per-tick decrement is clamped at zero. At reduced LOD the
decrement subtracts more than one, which previously overshot into a stuck negative so an `== 0`
gate never re-fired and the predator paced its prey forever without hitting.

### Giving up

| Mechanism | Fires when | Notes |
|---|---|---|
| Viability re-evaluation | only once **engaged** (within a multiple of attack range): near-zero HP removed over an interval | while still closing, dealing no damage is expected; counting it made predators bail mid-approach and starve without ever killing. Pack members holding a non-converging station are exempt, as are ambushers |
| Self-damage bail | a counterattacking target has cost a fraction of the hunter's own HP | fires instantly |
| Approach stall | closest approach fails to improve within a window | the gap the engagement clock leaves open — a hunter that can neither reach nor lose its target had no timeout at all. Ambushers and pack members on station are exempt |
| Hard pursuit ceiling | any single quarry pursued beyond a maximum | the stall clock alone can be kept alive indefinitely by prey drifting in and out of reach |

Tracking has its own stall check mirroring the pursuit one. A given-up target is blacklisted for a
duration; a *collective* no-progress stall also clears the shared pack target, while an individual
peeling off because it is hurt does not.

### Tactics

- **Pack** — `AssignPackRole`, `ApplyLeaderBehavior`, `ApplyFlankerBehavior`,
  `ApplyDisruptorBehavior`, `ApplyPackTactics`. The leader holds at distance and triggers a
  converging phase on timeout, on a flanker reaching the far side, or on prey isolation.
- **Ambush** — `ApplyAmbushMovement`. Stealth accrues while moving slowly (semi-aquatic ambushers
  gain extra on water); at threshold within pounce range the predator bursts at a speed and damage
  multiplier and then reverts to a slow chase. Pouncing resets stealth.
- **Defensive rally** — `FindProactiveThreat` plus `Predator.LastAttacker`. A pack or swarm picks a
  threat reactively (recent attacker, remembered for a duration) or proactively (an idle member
  scanning for a hunter of another species closing on it or a groupmate). With enough groupmates in
  coordination range it broadcasts the threat as the group target, bypassing the usual mass and
  hunger gates. A committed mobber will not flee its mob target — `FleeingSystem` keeps it engaged
  so it does not oscillate between approach and flight — and if the mob is losing, the viability
  bail clears the target and fear takes over. Lone members with too few allies flee instead.

### Terrain and performance

`SteerForHabitat` keeps hunters in their element mid-hunt; `SteerAroundWater` and
`IsBlockingTerrain` handle the rest. `WorldManager.GetWaterFractionOnPath` is the dominant
per-candidate cost and scales with prey *density*, so it is deferred to only the candidate that
would actually become the new best — a pure rejection filter, behaviour-equivalent. Each scan also
caps the number of scored candidates.

On a kill, the killer immediately gains `EffectiveNutrition × KillNutritionShare` of the prey.
Without that share, predators relied on slow scavenging and starved before they could breed.

**D8 — a kill creates nutrition.** The source comment says the corpse holds "the remainder", and it
does not: `CarrionSystem.SpawnCorpse` computes the corpse pool independently from the prey's own
`EffectiveNutrition` and condition, with nothing subtracting what the killer already ate. A kill
therefore yields more total food than the prey was worth. The emergent-sharing behaviour the design
wants is unaffected; the arithmetic is not what the comment or the previous documentation claimed.

Note also that `EffectiveNutrition` is body-mass-derived only when a species leaves
`NutritionValue` unset; otherwise it is that flat value.

`Systems/HuntFunnelProbe.cs` instruments the rejection funnel (`PreyReject`; the counting call site,
`CountReject`, is a private helper on `HuntingSystem`). It is the tool for answering "which gate is
returning nothing" rather than guessing.

## `FleeingSystem` — `Systems/FleeingSystem.cs` · gated

Scans predators with their stealth levels, then computes a weighted flee-away direction per prey.
Detection is stealth-aware — a fully stealthed predator's effective range collapses — and stacks
with `TerrainProfile.Concealment`. Fear accumulates with proximity, decays slowly through a
vigilance window after the threat leaves, and triggers a response above a ratio:

| `FearResponse` | Behaviour |
|---|---|
| Flee | run at a species multiplier, boosted at high fear, terrain-aware |
| Freeze | slow toward a stop |
| Panic | erratic high-speed movement |
| Defensive | stand ground in a herd |

Flee steering uses `TerrainProfile.SteerAversion`, so a fleeing fish treats *land* as the hazard —
the fix that ended chronic late-game fish suffocations. If terrain discomfort is high and fear is
not extreme, escaping the ground wins over escaping the predator.

**Refuge flight**: a semi-aquatic animal caught ashore biases its flee vector toward the nearest
water, blended with the away-from-predator direction rather than replacing it, so it can never run
into the threat.

**Desperation**: below a hunger ratio, a prey animal's effective flee radius shrinks sharply, so it
feeds in ground a well-fed individual would refuse. Without it, specialists starve beside food they
are afraid to approach.

**Stamina**: prey burst, tire, and recover, so a chase has a shape.

Dormant Sectids are excluded from the predator scan entirely — prey neither flee nor fear a
sleeping swarm.

## `HerdingSystem` — `Systems/HerdingSystem.cs` · gated

Group membership, leader election by an age/lifespan score, cohesion toward the leader at an ideal
follow distance, and velocity alignment. Packs get a cohesion boost, doubled again during an active
hunt. Suspended while fleeing, while solo-hunting, and while Wander's escape flag is set — the
tug-of-war between herd cohesion and any other steering drive is a recurring source of jitter.

## `SeparationSystem` / `CollisionSystem` — `Systems/SpatialSystems.cs` · gated

Separation pushes same-species apart within the species' separation radius with a linear falloff.
Collision resolves physical overlap for all entities using `BodyMetrics` radii and splits the
overlap **by inverse mass** (`PushWeight`); structures are immovable. A flat half-and-half split
meant body mass told the simulation only what a corpse was worth: a fox shunted a turtle as easily
as the reverse, and nests were pushed across the map by passing traffic. Growing creatures use
their grown mass.

Note that a species' attack range must exceed its separation radius for a swarm to land hits — the
reverse holds a swarm spaced just outside biting distance.
