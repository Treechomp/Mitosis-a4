# 02 — The world

*Last updated: 2026-09-07*

## A world is a situation, not a level

Every run generates a new world from a seed. Nothing in it is placed by hand. This is a design
choice with a cost — no authored landmark, no crafted encounter — bought for one property: the
player and the designer are in the same position with respect to the world. Neither knows in
advance where the deserts are, and both have to read the map to find out.

A world is meant to read as a **situation**: this valley has a river and therefore a green
corridor through dry country; this coast is frozen and has no beach; this basin is flat and holds
its water and is therefore swamp. If a player cannot look at a region and say what kind of place
it is, generation has failed regardless of how varied the numbers are.

## Climate is made of coupled parts, not independent noise

The naive construction is three independent fields — height, wetness, warmth — combined by a
lookup table. It produces variety but not sense: rivers that run through deserts without wetting
them, swamps on hillsides, coasts identical at every latitude.

The design instead makes the parts feed each other:

- **Height comes before everything.** Mountain ranges rise as connected chains out of existing
  uplands rather than as isolated peaks, because a range is a feature a player can navigate by and
  a scatter of peaks is texture.
- **Water follows height, and then changes the climate.** Rivers are traced downhill from high
  ground to the sea, pooling into lakes where they get stuck and overflowing onward. Where they
  run, they wet the land around them — so a dry region with a river through it grows a green
  corridor, and a wet region grows marsh margins. A river reaching the sea spreads a delta fan.
- **Slope decides what water stays.** Hillsides shed moisture, flats hold it. Swamps settle into
  lowland basins; the same climate on a slope becomes forest or scrub.
- **Warmth comes from latitude, modified by altitude.** Cold at one pole, warm at the other,
  colder the higher you go — so mountain ranges carry their own climate up their sides.

The two-way coupling is the point. Hydrology is not decoration applied after biomes are chosen; it
is one of the inputs biomes are chosen from.

## Terrain has two jobs

**As a place**, terrain gives a region identity: forest, swamp, tundra, reef, desert. Identity is
what makes a world navigable from memory.

**As a surface**, terrain is what every creature is constantly negotiating. Each tile answers five
separate questions about each species, and the design keeps them separate on purpose:

| Question | Meaning | Example |
|---|---|---|
| **How fast can I cross it?** | grip, cost of travel | a shark is fast in deep water where a deer would flounder |
| **Can I be here at all?** | the element barrier | a fish on land, an insect in water |
| **Would I rather be elsewhere?** | preference — a *pull* | a shark patrols the deep but hunts the shelf freely |
| **How long can I stand it?** | tolerance — an *intolerance* | a herd grazes a marsh indefinitely; nothing lingers on a mountain |
| **How well does it hide me?** | concealment | a rabbit in forest, an arctic fox in snow |

**Preference and tolerance are different things and must never be collapsed.** Routing "I'd rather
be elsewhere" through "I can't stand it here" is what produced sharks permanently fleeing their
own ocean: a mild dislike accumulated without limit until it became an emergency. Tolerance settles
toward a level rather than piling up, so ground is either liveable forever or drives a creature
off promptly — and which one it is, is a property of the ground and the species, not of how long
they have been standing there.

## Elevation obstructs, it does not forbid

There is no impassable tile. Mountains and lava are traversable, just slow and punishing.
Difficulty comes from slope: climbing costs speed, and a sheer step is a hard block for anything
that cannot fly. The design prefers graded resistance over walls because a wall is a thing the
player learns once, while a slope is a thing they weigh every time.

The consequence is that the whole map is reachable, and that dangerous ground is a *decision*
rather than a boundary — a pressed animal will cross a river it normally avoids.

## The world edge is a boundary, not a cliff

A creature that reaches the map edge is turned back, and — just as importantly — stops *wanting*
to go there. Position clamping alone leaves animals pressed against the border indefinitely,
still trying. Both halves are needed: the barrier and the reason not to approach it.

## Fertility is an economy

Ground carries food. Grazers strip it; it regrows slowly. That single loop is what makes herds
move: a pasture eaten flat is worth no more than bare rock, and the animals on it feel that as
pressure to leave.

**Water carries food too** — plankton, not pasture. Only species that feed from water draw on it.
Without this, fish feed from an infinite supply and any pond fills solid with fish until something
eats them, which makes an isolated pond an impossible object. With it, a shoal eats its patch
down and has to move.

The gradient matters more than the amounts: reef is the richest water, the shelf next, rivers
thinner, the deep poorest. That gives shoals a reason to hold in the shallows **where their
predators can reach them**, instead of dispersing into water nothing can follow them into. Rich
water also breeds a shoal back quickly after predation and exhausted water barely breeds at all —
a brake that works with no predator present, which is exactly what a landlocked pond needs.

Decomposition closes the loop: a body that rots on the ground returns part of itself to the soil.

## Ground state is the contested resource

Faction terraforming moves a tile along a chain — drier, wetter, or back toward the middle:

```
dry  ←  Arid · Sand · Dirt · Shrubland · Grass · Forest · Wetland · Bog  →  wet
        (cold chain: Tundra · Steppe · Taiga · Forest — tropical: Savanna · Jungle)
```

One faction pushes wet, one pushes dry, one pulls both extremes back toward the middle. Because
every faction's survival is tied to a ground condition (see [04-factions.md](04-factions.md)),
moving a tile is an attack. This is the design's answer to "what do factions fight over": not
kills, not territory markers — **the moisture of the ground under each other's feet**.

Terraforming is deliberately slow relative to a run. A front should be something a player watches
advance, not a thing that flips.

## Landmarks

Small local overrides give a region incident: an oasis where a dry landmark meets water-fed
moisture, a clearing in deep forest, a permafrost patch in tundra, a cave mouth on a mountain
side. Their job is to make a place memorable and to give a reason to detour. They are rare on
purpose; a world of landmarks has none.

## What generation must guarantee

- Every biome the species roster needs exists somewhere on a default world, in a patch large
  enough to support a population. A species with nowhere to live is a bug in worldgen, not in the
  species.
- Rivers reach the sea. A severed river is a visible, immediate failure.
- The same seed produces the same world, always. Balance work is impossible otherwise.
- Regions are large enough to be recognised as regions. Fine-grained mottling reads as noise.
