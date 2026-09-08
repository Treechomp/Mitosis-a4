# 06 — The run, the goals, and progression

*Last updated: 2026-09-08*

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

**The build does not miss this target uniformly, and where it misses matters more than by how
much.** Read off the creature tuning rather than assumed, the creature layer already sits about
right: the shorter-lived species live and breed through several turns of their cycle inside a
target-length run, which is what the progression layer below needs from them. The faction layer is
what does not fit, and it misses in two opposite directions at once — faction combat resolves far
faster than the run wants, while faction terraforming may be too slow to read at all. No single
scale factor corrects both, which is why a uniform retiming pass was proposed and then withdrawn.
The figures, and the reading they came from, are in [../changelog.md](../changelog.md).

- **Faction-scale events resolve inside the first few percent of the intended run.** A siege, a
  bloom's collapse, the prey base filling its ceiling — over before the run has properly begun.
  Retuning that is not a polish pass; it is the pacing pass the whole design depends on.
- **The Shroomer's lifespan follows the growth it achieved.** The original intent was that age
  would *never* check a bloom, leaving crowding and drought as its only limits, and at the target
  length age begins to bite. The resolution is neither of the two obvious ones: not raising the
  span to restore the old intent, and not bolting ageing on as a third flat limiter. **An
  individual that reaches maturity lives long; one that stays stunted dies young.** Growth only
  ever increases, so a span derived from it can only lengthen — an elder can never die because it
  shrank, and the span can be re-derived continuously instead of being fixed at maturity.

  The mechanism this produces is the point of it. A Shroomer on fertile ground grows and lives
  long; one on ground its own kind has exhausted grows poorly and dies young. A bloom strips the
  fertility beneath itself, so its own spores land on ground its parents ruined: **the core stops
  renewing** — its elders live out the span they earned and are not replaced — while the advancing
  edge, sown onto fresh ground, lives normally. The self-limiting front stops being an attrition
  constant and becomes demography.

  Two things follow, and they are consequences rather than settled values. Crowding attrition now
  shares its job with ageing and has to be retuned rather than assumed to still fit: crowding
  measures density, ageing measures fertility, and they are different limiters. And drying a
  bloom's ground gains a second channel, because dried ground now produces short-lived offspring
  and not merely fewer of them.

  Left open: whether maturity, and the thresholds that gate an elder's abilities, stay on an
  absolute scale or move with how much a given individual managed to grow.

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

### Durability — what makes an anchor hard to remove

Durability is not a constant bolted to a structure. **Each faction earns it from what it is already
good at**, so what makes a faction strong and what keeps its anchor standing are one investment
rather than two competing ones.

| Faction | Durability comes from | The decision it creates |
|---|---|---|
| **Shroomer** | extent — the heart holds more life the more ground the mycelium covers | spreading wide and being able to defend become the same investment, which is what an immobile faction needs |
| **Sectid** | numbers, and how tightly its nests are clustered | how far to range, and how many to leave at home |
| **Faeling** | crystal power, spent either on guardians among the wildlife or on a fast return to a threatened crystal | defending the anchor competes with everything else out of one purse |

Three properties fall out of this, and they are why the principle is worth more than a durability
number would be.

**Extent gives the Shroomer hit points and perimeter at the same time.** A heart already dies when
too little of the ground inside its reach is still moist, so a wide domain is simultaneously more
life to cut through and more boundary to dry out. A bloom cannot be strong against both attacks at
once. That is exactly the "two genuinely different and genuinely effective strategies against the
same target" the domain section above claims, and which a flat durability value did not deliver.

**Distance is the Sectid's identity.** It tolerates nearly any ground except water, so range is a
choice for it and a constraint for the other two — the Shroomer cannot leave, the Faeling need not.
It pays for that reach per individual: a Sectid far from home is fragile, and is not at home
defending the nest.

**A fast return revives a deleted mechanic, and its failure mode with it.** Keeper travel between
crystals was removed because keepers that relocate assemble, and assembled keepers sweep the map
unchallenged. Paying for it out of crystal power is a credible brake — assembling then costs
exactly what assembling is for — but the failure returns with the mechanism and has to be
re-measured rather than assumed away.

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
