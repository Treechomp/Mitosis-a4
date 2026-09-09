# 03 — Creatures

*Last updated: 2026-09-09*

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

### Grazing is tuned for migration, not for population

The economy has two possible jobs and only ever does one of them. It is nowhere near limiting how
many animals there are — that is settled above, and the arithmetic is in
[../changelog.md](../changelog.md). What it is *for* is movement: **a herd should move because the
land it is standing on can no longer support it, and different herds should move differently.**

For that, a herd's consumption has to outweigh the regeneration of a *region*, not of a tile. Today
it outweighs neither: a grazer strips the tile under it in a second or two, but a herd's draw is
balanced by a patch barely larger than the herd, so the ground it left has recovered before it
comes back. That is why herd movement reads as shuffling rather than as a response to the land.
Raising the demand until a herd's draw balances a meadow rather than a dozen tiles is the change.

Two consequences, stated as consequences rather than as settled numbers:

- **It has to be paired with the birth brake above.** The same change that makes food drive
  migration also moves food towards binding population — and a population limited by hunger is the
  outcome this design rejects.
- **It puts the game in a regime it has never run in.** Herbivores have never been under food
  pressure, so this is measured across several seeds and not one: the invariant gate's own
  assertion is seed-dependent, so a single-seed result says nothing.

### Feeding niches are per species and per ground

A grazer still eats every kind of vegetation equally well. How fast an animal strips ground and how
much that feeds it are already its own; what neither depends on is *which* pasture it is standing
on. Vegetation is one flag over a list of terrain types plus a per-tile cap that applies the same to
everyone, so species differ in where they can comfortably *live* and never in what ground is worth
more to them — and every grazer in a region draws from one shared pool.

The decided shape is a **per-species, per-tile forage yield**: how much this ground is worth to this
animal, as another dimension of the same per-species terrain profile that already answers how fast
it moves here, whether it will enter at all, how quickly standing here wears on it, and how hidden
it is. Why this is the form:

- It is the architectural move that produced that profile in the first place — one resolver
  answering "how does this species relate to this ground", instead of special cases scattered
  through systems.
- It plugs into machinery that already exists. The wander logic already scores ground by how much
  food is left on it and walks a hungry animal toward the best it can see; weighting that by the
  species' own yield sends a deer to grass and a camel to scrub without any system naming either.
- **It replaces cutting fertility globally.** Reducing the world's grazeable area starves every
  species equally; giving each species a smaller and *different* pool separates them instead. Same
  lever, aimed better.

**The mechanism now exists and no species uses it**, which is deliberate. Switching it on was
measured, and the measurement changes the order of the work:

- **It does not fix composition, and it was never going to on its own.** Nothing starved in any
  run measured at the time, and the herbivore total did not move at all. What moves is *which* species hold the shared class
  ceiling, because a species that eats slightly worse is slightly less well fed, and being well fed
  is what the design requires before an animal may breed. Composition is settled by a race for one
  allowance, and forage only reweights the race. **The limit has to become per species first** —
  the breeding-ground work above — or every forage value is just a thumb on a scale that still has
  one pan.
- **Food binds reproduction long before it binds survival.** That is the missing half of "food does
  not bind herbivore numbers": it does not bind them through hunger, but it reaches the birth rate
  through the well-fed requirement, and that is a much shorter lever than starvation.

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
specialist whose entire diet is small game must never starve *by construction* — no species may be
built such that it cannot make a living even where its prey is abundant — and a world containing
only small prey must still be hunted normally. That is a rule about the roster; an individual
starving because it is somewhere it cannot feed is a different thing and is intended. Hunger flattens the preference: a desperate
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
not a brake at all — see [07-simulation-contract.md](07-simulation-contract.md). Note what it gates:
a birth, not a footstep. Nothing stops animals walking into ground that is already crowded, which is
why a lot of them can stand in a small place despite the brake.

### Numbers are limited at the point of birth

**How many of a species there are is a fact about the ground, and it is decided at birth.** Each
species has a carrying capacity read off the world the seed actually generated: the food flow of the
terrain it can feed on, divided among the species that contest that terrain in proportion to what
the ground is worth to each of them, divided again by what one animal takes. Births slow as a
species fills that capacity — graded, not a cliff, and per species, so crowding among deer says
nothing about whether a camel may breed.

The consequence is the point of it. **A world of steppe feeds the species that do well on steppe,
and the jungle species is rare there** — from the map, with no species named anywhere in a system
and nothing tuned per world. Two seeds are two different ecologies rather than the same roster at
two sizes.

The remaining question is not *whether* the ground decides but *how densely* — how many animals a
niche should hold. That is one number, it is a design choice rather than a derivation, and it is the
knob that says whether this world is teeming or sparse.

**Two older mechanisms sit inside this one.** Breeding grounds — a species may only breed while
standing on ground that suits it — remain the *place* constraint, and are what makes a semi-aquatic
animal genuinely amphibious rather than a land animal that tolerates water. Births coupled to local
fertility remain the *local* one: a population that has eaten its patch down stops replacing itself
there long before it starves.

Three reasons this is the shape.

*A shared ceiling is won by the fastest breeder.* The herbivore ceiling is shared by every
herbivore, so within the class the shortest cooldown and the earliest maturity take it — and the
roster has a clear winner on both. That is the same competitive exclusion the per-class budgets
removed *between* classes, still running *inside* each one, and it had already emptied a species out
of the roster. Per-species capacity replaces one shared count with as many independent pools as
there are species, which is the fix that already worked once.

*The engine must not be the limit.* A prey base pinned to its class allowance has had its size
chosen by a thread budget ([07-simulation-contract.md](07-simulation-contract.md)). Numbers set by
what the ground can feed leave that allowance untouched, which is what it is for.

*Numbers and survival are different questions, and hunger answers the second one.* Limiting at
birth decides **how many** there are. It does not decide **where** they can be, and it must not be
used to spare them. See below.

### A species thrives where its ground supports it, and nowhere else

**An animal on ground that cannot feed it starves.** A herd driven onto arid land by a predator, a
sounder pushed out of its forest by a rival faction's terraforming, a population that has eaten its
own patch bare — if there is no living to be made where they end up and they do not find their way
to somewhere there is, they die there. That is the intended outcome, not a failure to be tuned out.

This replaces an earlier rule that rejected starvation outright. That rule was written against a
real failure — equilibria reached by continuous die-off, which this project has had to undo more
than once — but it was the wrong generalisation, and it made hunger meaningless everywhere in order
to prevent a die-off in one place. The distinction that actually holds:

- **Starvation as the population limit is still rejected.** A world that holds its numbers by
  killing the surplus every generation is a world of corpses, and it puts the prey base on a
  boom-and-bust cycle the player can do nothing about. Reproduction sets the numbers.
- **Starvation as a local consequence is required.** It is what makes ground worth holding, what
  makes being driven off it a real loss, and what makes a faction's terraforming an act with
  victims rather than a change of colour. Without it, displacement costs nothing and the map is
  wallpaper.

Two things follow, and both are load-bearing:

**Being fed has to depend on where you are standing.** Ground that has been grazed down, or that
suits the species badly to begin with, must feed an animal *less* — not merely be somewhere it
prefers not to be. A preference an animal can ignore at no cost is not a pressure.

**Hunger has to move on the timescale of a run.** An animal that would take three runs to starve is
not under any pressure at all, whatever the ground is doing.

The species that are *meant* to be hardy stay hardy: a camel crossing bad country should be the one
that survives it. Hardiness is a species property, not a property of the world.

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
