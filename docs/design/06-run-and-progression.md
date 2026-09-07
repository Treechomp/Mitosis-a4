# 06 — The run, the goals, and progression

*Last updated: 2026-09-07*

## Three nested loops

| Scale | What you are doing | What pushes you |
|---|---|---|
| **Seconds** — being a creature | move, feed, fight, avoid | your own body |
| **Minutes** — shaping a locality | terraform, clear, mark, lead, place and feed an anchor, tune what your faction produces here | rivals' progress |
| **Tens of minutes** — the run | your locality compounds into faction domain; the goal is measured across the world | the goal's own clock (explicit only for Faelings) |

Each loop feeds the one below it and is paid for by it. The minute loop is where the game actually
lives: the second loop is survival and the run loop is scoring.

## Length: variable outcome, fixed design target

A run ends when a goal condition is met, not when a timer expires. There is no clock in the game.

There is, however, a number in this document, because **without a reference length no rate can be
tuned**: terraforming, growth, brood economy, fertility regrowth and every population ceiling are
all expressed relative to how long a run lasts.

> **Design target: the median run resolves in 60–120 minutes.** Finishable in one sitting, and long
> enough that a player who likes how their world turned out can keep going.

This target is a design decision, not a tuning value, which is why it lives here. Everything it
implies about specific rates lives in the implementation layer.

**The current build is tuned for roughly a sixth of that**, because its rates were set from
balance-run length rather than from play. Two consequences worth stating as design, not just as
numbers:

- **All faction-scale events resolve inside the first few percent of the intended run.** A siege, a
  bloom's collapse, the prey base filling its ceiling — all of it is over before the run has
  properly begun. Retuning is not a polish pass; it is the pacing pass the whole design now depends
  on.
- **Lifespans no longer exceed a run.** In particular, the Shroomer's lifespan was chosen precisely
  so that age would *never* check a bloom — leaving crowding and drought as its only limits. At the
  target length, age begins to bite, which changes the mechanic rather than its numbers. It must be
  decided deliberately: either lifespan is raised to preserve the original intent, or ageing becomes
  a third Shroomer limiter and crowding and drought are re-tuned around it.

A side effect worth keeping: at the target length a player watches **several full generations** of
the shorter-lived species. Generational drift becomes something you can see happening, which is the
raw material the progression layer below is built from.

## Winning: an asymmetric triple

Each faction wins at a different thing. This is the deepest layer of the asymmetry — not just
different means, but different reasons to play.

| Faction | Wins by | Character of the endgame |
|---|---|---|
| **Shroomer** | holding a share of the world in its own state | expansion, then defence of a front |
| **Sectid** | holding a number of standing colonies | expansion, then protection of infrastructure |
| **Faeling** | **neither rival's domain growing, for a sustained period** | containment, then a held stalemate |

The Faeling condition is the one that needed inventing, because elimination is the wrong goal for
it: wiping out two entrenched factions is disproportionate effort, and a keeper that wins by
conquest is not a keeper. Its win is that the factions have been **reduced to the level of
ordinary species in the ecosystem** — still present, no longer spreading. That is the faction's own
design stated as a victory condition rather than as a behaviour.

Failure is unchanged and is described in [05-player.md](05-player.md): out of continuations, or no
anchor left to return to.

### Domain — the number all three goals measure

A faction's domain is **terrain and anchors together**. Terrain says how much of the world you have
converted; anchors say how much of it you can hold. A faction needs both to count, which makes
"break their structures" and "undo their ground" two genuinely different and genuinely effective
strategies against the same target.

This is also the number the player has to be able to read. A goal nobody can see the progress of is
not a goal.

**The Faeling's domain is its ecosystem**: the independent species it has strengthened and the
ground they live on. This follows directly from its influence mechanism — the keeper's mark on the
world is a wild population that can hold its own. It is the only domain of the three that is not
measured in converted ground, and that asymmetry is correct: the other two measure how much of the
world they have made theirs, and the Faeling measures how much of it is still nobody's but
strong enough to stay that way.

It also fixes the endgame problem the condition otherwise has. "Nothing expanded for a while" is a
negative state that a player can only wait for; "the wild is thriving and holding" is something
they can see grow.

## Progression within a run

Four things accumulate, as a chain rather than four scores — knowledge → personal power → faction
strength → domain. [05-player.md](05-player.md) covers how they interlock and how personal power is
paid for out of the faction's purse.

The important property is that **the chain has one purse and two ends.** Every unit of faction
resource goes either into you or into the faction, and both eventually convert into domain, by
different routes and at different speeds. That is the strategic texture of a run.

## Progression between runs

Meta-progression exists. Its job is to make the next run *different*, and to make the last run have
left something behind.

### Unlocks

- **Faction abilities** — new structures, new behaviours, new ways of shaping ground. A faction you
  have played before can do things it could not do the first time.
- **World starting conditions** — kinds of world, biome arrangements, difficulty, opposing line-ups.
  The generator is already deeply parameterised, which makes this the cheapest meaningful content
  the project can produce.
- **Character paths** — real builds, not passive stat gains. A faster or tougher Sectid; a Faeling
  developed as a direct guardian *or* as a caretaker with more reach over the wild; equivalent forks
  for the Shroomer. Paths are what make a second run of the same faction a different game.

Paths are also why the player's body is deliberately **not** an ordinary member of its species. That
is a real cost — it weakens the "you are just another creature" claim — and it is accepted, because
a body that cannot become anything gives a run nothing to build toward.

### Species patterns carry over

The stronger idea, and the one that ties the whole design together: **the way a species turned out
in your world can persist into the next one.**

You do not edit a species by decree in-run. You play; your writes push traits in a locality; the
world runs; some of it survives and some does not. What carries over is the *outcome of selection*,
not a design decision — which keeps the emergence pillar intact while still letting a player feel
that their worlds have a history.

Mechanically it rests on the same per-individual trait variation the player writes into
([05-player.md](05-player.md)), which the data model already provides and nothing currently uses.

**(open)** How much persists, whether it is per species or per world lineage, and whether a player
can refuse an inheritance, are not decided. See [08-open-questions.md](08-open-questions.md).
