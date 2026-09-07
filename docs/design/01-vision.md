# 01 — Vision & pillars

*Last updated: 2026-09-07*

## What Mitosis is

A top-down **creature sandbox**. A procedurally generated world is populated by animals that
hunt, graze, herd, flee, breed and die on their own, and by three rival factions that reshape the
ground itself in opposed directions. The player is one creature inside that world.

The world is not a backdrop that animates when observed. It runs at a fixed rate everywhere, all
the time, whether or not anyone is looking at it. A player who walks away from a hunt and comes
back finds it resolved, not paused.

## What it is not

**It is not a simulation of an ecosystem.** The ecology is a game mechanic — a lightweight
background process whose job is to keep the world alive and give the player something to react
to, in the same spirit as creature AI in a colony sim or god game. Its correctness criterion is
whether the world is interesting and legible, never whether it is biologically accurate. When
plausibility and playability disagree, playability wins and the disagreement is recorded rather
than argued.

**It is not a strategy game.** The player never issues orders to a population, never has a
build queue, never sees a resource bar for the faction. Influence is exerted by being present and
acting as a creature.

**It is not scripted drama.** There are no set pieces, no quest triggers, no authored events. What
happens is what the rules produce. The design's job is to make rules whose products are worth
watching.

## Pillars

### 1. The world runs without you

Everything the player can do, a creature can do. Everything a creature does, it does whether or
not the player is nearby. This is what makes the world feel like a place rather than a level, and
it is also the hardest constraint in the project — see
[07-simulation-contract.md](07-simulation-contract.md), which exists entirely to defend it.

### 2. Emergence from small rules

Behaviour comes from per-creature drives evaluated locally: a wolf does not know there is a herd,
it knows there is a deer nearby, a packmate nearby, and that it is hungry. Pack hunts, migrations,
population crashes and faction fronts are consequences, not features. A behaviour that has to be
scripted to appear is a signal that a rule is missing, not that a script is needed.

### 3. The ground is the stake

Most games about conflict contest territory as an abstraction — a flag, a control point. Here the
factions contest the **physical state of the tiles**: how wet the ground is, how fertile, what
grows there. A faction wins ground by changing what it is, and loses ground the same way. Every
faction's survival is tied to a ground condition it must maintain, which is what makes terrain the
battlefield rather than the arena.

### 4. Legibility over depth

A player who watches a creature must be able to work out why it did that. Systems are preferred
in the order: visible cause > plausible cause > correct cause. A mechanic that produces the right
population curve for a reason the player can never perceive is worth less than a cruder one they
can read off the screen.

### 5. Asymmetry by economy, not by stat block

The three factions differ in **what they spend and what they earn**, not in having bigger numbers.
One converts ground fertility into bodies, one converts kills into brood, one converts kills and
restoration into personal power. Their strengths, their counters and the player's verb inside each
all follow from that single difference. See [04-factions.md](04-factions.md).

## The player fantasy

You are one creature of one faction, in a world that does not need you. Alone you are small: a
single Sectid is prey, a young Shroomer is a snack, a Faeling is one body against a colony.

Your body is slow and local, so your reach is not distance — it is **persistence**. What you leave
behind keeps acting: an anchor placed, ground shaped, a site marked, the traits of what grows here
nudged. The faction's advance is a self-running process; you are the part of it that decides where
the next push happens.

And you are not free. Everything you become is paid for out of the same pool that feeds the faction,
so the strong individual and the strong faction are one budget spent two ways. That single tension
is the game.

A run ends when your faction reaches its goal — a different goal for each of the three — or when it
can no longer put you back on the map. See [05-player.md](05-player.md) and
[06-run-and-progression.md](06-run-and-progression.md).

## Genre reference points

Useful as shorthand for what the game feels like, not as features to copy:

- **Colony sims** — for the relationship between the player and a self-running population.
- **God games** — for the pleasure of nudging a system and watching consequences propagate.
- **Creature sandboxes / toyboxes** — for the idea that watching is a legitimate activity, and
  that the world should reward observation with understanding.

Explicitly *not* reference points: life-science simulators, ecology teaching tools, anything whose
appeal is accuracy.
