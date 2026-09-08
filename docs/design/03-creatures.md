# 03 — Creatures

*Last updated: 2026-09-08*

## A species is data, not code

Every animal in the game is the same program running on a different set of numbers: how fast, how
heavy, what it eats, what it fears, where it can live, how it hunts, how it breeds, what it looks
like. There is no per-species branching in the systems.

This is a hard rule and it has teeth. When a behaviour can only be expressed by naming a species
in a system, the correct response is to find the **property** that species has and give every
species that property — usually at zero. "Sectids avoid prey in wetland" is not a Sectid rule, it
is a *terrain comfort* rule that happens to be non-zero for Sectids. The value of the rule is that
adding the twenty-ninth species costs a table row instead of an edit to twelve systems.

## What a creature is

A body with a position, a mass, a size, an amount of health and an amount of food in it, plus a
set of drives. Mass is not cosmetic: it decides what can eat it, what it can eat, how quickly it
turns, who gets shoved aside in a collision, and how much of a meal its corpse is.

## Drives, and the order they are decided in

A creature evaluates a small number of competing urges each time it thinks. The design fixes their
priority rather than scoring them against each other:

1. **Fear** — something is hunting me.
2. **Intolerance** — the ground I am standing on is hurting me.
3. **Hunger** — I need to eat, which means hunting, grazing, or going to find either.
4. **Reproduction** — I am fed and it is safe enough.
5. **Company** — stay with the herd or pack.
6. **Wander** — none of the above; move anyway.

The order is deliberately blunt. A utility system that weighs every drive against every other is
more expressive and much harder to reason about, and the project has repeatedly had to debug cases
where two drives fought each other for control of one creature — a herd pulling one way while a
hunt pulled another, an escape from bad ground reversed every tick by an avoidance of the same
ground. Priority makes those cases decidable. (Moving to weighted arbitration is **open** — see
[08-open-questions.md](08-open-questions.md).)

The priorities are not absolute; the thresholds at which one drive overrides another are per
species. A frail, fast-breeding animal panics early; a heavy defensive one does not. A starving
animal takes risks a fed one refuses — which is the mechanism that stops a specialist starving on
the shore of water it is afraid to enter.

## Feeding

**Grazers** eat the ground. They strip fertility from the tile they stand on and gain food in
proportion to what was actually there, so a stripped pasture feeds nobody and pushes the herd on.
A hungry grazer on poor ground actively looks for better, sampling the country around it and
heading for the best food it can see rather than wandering blindly.

**Water feeders** do the same thing to the water column. Some skim it as a subsistence floor;
others strip it properly and have to move.

**Hunters** eat other creatures, and the rules are covered below.

**Scavengers** eat the dead. Death always leaves a body — from any cause, including starvation,
age, drowning and faction weapons — and that body holds real food that rots away over time and
fertilises the ground as it goes. Corpses are what make a kill matter to more than the killer: the
killer eats first, but it takes a hunter's mouthful and not the carcass, and the bulk of the body
is left for whoever finds it. Pack "sharing" is emergent from this, not scripted.

That split is a live balance control, not a flavour detail. A kill yields one body, never more, so
what the killer eats is taken from what the scavengers get. The faction that lives on carrion rises
and falls with the remainder, which is why the killer's portion is the smaller one: a hunter that
ate most of its kill would quietly starve a whole faction. The value is in the code and the
measurement behind it is in the changelog.

**Omnivores** do two of these. They are deliberately rare and each one is a specific design
statement rather than a hedge.

## Hunting

A hunt is an economic decision with four parts.

**Can I take it?** Mass gates what a predator can attempt. A fox cannot bring down a deer. A wolf
pack can, because a pack's effective mass scales with its size — and a swarm scales further, which
is what lets tiny insects threaten large predators when there are enough of them. This is the main
structural device holding the trophic layers apart.

**Is it worth it?** A hunt costs roughly the same effort whatever it catches, so what separates
good prey from bad is how much of the hunter it actually feeds. Without this, an apex predator
spends its life chasing whatever is nearest — a bear running down lizards while elk graze past.
With it, a low-value target has to be proportionally closer to be chosen, and the effect grades
with predator size by itself.

Crucially this is a **preference, not a gate**. Nothing is ever excluded for being small. A
specialist whose entire diet is small game must never starve on principle, and a world containing
only small prey must still be hunted normally. Hunger flattens the preference: a desperate
predator takes what it can reach.

**Can I find it?** Detection is not a fixed radius. Prey that matches its ground is noticed from
closer — a rabbit in forest, an arctic fox in snow — so terrain is genuinely a hiding place. An
ambusher that has built up stealth is nearly invisible until it commits, and committing gives it
away.

**Can I finish it?** Predators must be able to give up. Three separate failure modes are designed
against, because each one produced a real, observed deadlock:

- Prey it cannot reach (across water, on a shore, on the wrong side of an element barrier) — the
  pursuit is abandoned, and, more importantly, never started.
- Prey it cannot hurt. Once actually engaged, a hunter that is removing no health concludes it
  cannot win and leaves. While still closing the gap, dealing no damage is expected and must not
  count as failure.
- Prey that is winning. A hunter taking serious damage from a counterattacking target breaks off.

An abandoned target is remembered and avoided for a while, so the animal does not immediately
re-acquire the same hopeless quarry.

### Tactics

Four, and they are characters rather than difficulty tiers:

- **Solo** — direct pursuit. Fast, simple, and limited by what one body can hold down.
- **Pack** — a leader holds off while flankers work around the prey's far side, and the group
  commits together. A pack takes prey no member could. Members holding station are not failing;
  the system must not judge a flanker by its own damage output.
- **Swarm** — no roles, no retreat, strength purely in numbers. A swarm's reach is what makes it
  dangerous to things far larger than itself.
- **Ambush** — build concealment while moving slowly, then a short burst of speed and damage. An
  ambusher that has pounced is visible again. Lying in wait without approaching *is* the tactic,
  so an ambusher must never be timed out for failing to close.

### Defence is a group behaviour

A pack or swarm that is attacked, or that spots a hunter closing on one of its own, calls the
others in — and the response overrides hunger, so even a fed member joins. A lone member with too
few allies flees instead. This is what makes a colony something that answers a raider rather than
being eaten one at a time, and it is the same mechanism that lets a faction's creatures answer an
attack on its structures.

A specialist that only ever eats one thing keeps that restriction while defending itself, and so
flees an attacker it is not allowed to eat rather than hunting down its own predator.

## Fear

Fear accumulates with a threat's proximity and decays with a period of heightened vigilance after
it leaves. Above a threshold, the animal responds in its species' way:

| Response | Character |
|---|---|
| Flee | run, faster the more frightened |
| Freeze | stop and hope to go unnoticed |
| Panic | erratic bursts — fast, unpredictable, exhausting |
| Defensive | stand ground, in a group |

Fleeing is terrain-aware in the same species-specific way everything else is: a frightened fish
treats *land* as the hazard and does not bolt ashore. A semi-aquatic animal caught in the open
biases its flight toward water its pursuer will not enter — otherwise it is simply in a race it
always loses.

Stamina bounds all of it. A prey animal can burst, then tires, then recovers, so a chase has a
shape instead of being decided at the first instant.

## Living and dying

Creatures age and die of old age. They starve if they cannot eat, drown or suffocate in the wrong
element after a grace period, and die of venom, of faction weapons, and of being eaten. Every one
of those leaves a corpse.

Breeding requires being fed, being rested, being mature, being off cooldown, and standing
somewhere the species can breed. Some species must be on particular ground to breed at all —
which is what makes a semi-aquatic animal genuinely amphibious rather than a land animal that
tolerates water: it feeds at sea, hauls out to breed, and returns. That rhythm falls out of two
thresholds sitting in the right order relative to each other, not out of a script.

**Crowding is the brake, and it is local.** A species stops breeding where it is already dense,
graded rather than cliff-edged, per species and per place. A global brake shared by all species is
not a brake at all — see [07-simulation-contract.md](07-simulation-contract.md).

## What the roster is for

The species list is not a collection; each entry exists to do a job in the world.

- **Generalists** are the baseline food web — the animals a player sees in the first minute, whose
  numbers are the world's pulse.
- **Biome specialists** give each region its own cast, and are the reason a player can tell where
  they are without looking at the ground. A desert with the same animals as a forest is not a
  desert.
- **Specialists with hard diet restrictions** are load-bearing. They keep one part of the web from
  being eaten flat by generalists, and they are the species most likely to reveal a broken system,
  because they have nowhere else to go.
- **Deep water is a refuge by construction.** Almost nothing follows prey into it. That reservoir
  is what lets a cropped shoal recover and is a deliberate structural feature of the web, not an
  accident of who happens to swim.
- **Factions** are covered in [04-factions.md](04-factions.md).

New species are judged by what they add to this list of jobs. "A biome has only three animals" is
a reason; "it would be cool" is not.
