# 05 — The player

*Last updated: 2026-09-07*

## Who you are

**One creature of one faction.** Not a camera, not a commander, not a spirit. You have a body that
lives in the world under the same rules as every other body: the same terrain resistance, the same
element barriers, the same mortality.

You are not, however, an ordinary member of your species. Your body **develops** over a run and
along paths chosen in advance — a tougher Sectid, a Faeling built as a direct guardian rather than
as a caretaker of the wild. What you are not is *free*: everything you become is paid for out of
your faction's own resources, so the strong individual and the strong faction are the same budget
spent two ways.

## The squeeze this design has to resolve

The world runs at a fixed rate everywhere, containing thousands of creatures. You are one of them,
and you move at creature speed.

- If your actions are **individual**, they are statistically irrelevant.
- If your actions are **global** — orders, spells, faction-wide spending — you stop being a
  creature and the pillar in [01-vision.md](01-vision.md) is gone.

The resolution: **your body stays local; your influence does not.** You are slow, so your reach is
not distance — it is **persistence**. What you do keeps acting after you have walked away.

## The one mechanism, three targets

Every way you reach past your own body is the same act: **writing something durable into a
locality.** What differs between factions is *what* you write into.

| | **Anchor** — the object | **Signal** — the invitation | **Parameters** — the lasting write |
|---|---|---|---|
| **Shroomer** | mycelium heart | the wet ground and mycelium you leave behind is itself the invitation — other Shroomers follow it | the traits of spores produced near your heart: **defence against reach** |
| **Sectid** | nest | a site you have cleared of danger, marked as good ground for a nest; pheromone that makes you worth following | pheromone strength and budget, developed along your path |
| **Faeling** | crystal | *(none of its own — the write below is the signal)* | the traits of **independent species**, raised so they hold out longer against faction pressure |

Three consequences of them being one mechanism rather than three:

1. **It is one system with three data targets**, not three systems. That matters for keeping the
   codebase honest: a per-faction implementation would drift into three sets of rules.
2. **It gives per-individual trait variation a purpose.** The capacity for creatures to differ from
   their species baseline exists in the data model and is used by nothing. It becomes the medium
   the player writes in.
3. **It makes the meta-progression coherent.** What persists between runs is what you wrote and
   what then *survived* — selection, not decree. In-run you never edit a species by fiat; you play,
   and the world keeps what worked. See [06-run-and-progression.md](06-run-and-progression.md).

## The shared verbs

All three player-creatures do the same four things. Character comes from the body doing them.

**Go.** Terrain is species-specific, so movement is a decision, not a traversal tax: as a Shroomer
you barely move and every step is a commitment; as a Sectid you are fast and cannot cross water; as
a Faeling you go where you like and the only question is whether being there is worth it.

**Feed.** Stay alive on your faction's diet — ground fertility, prey, or (for a Faeling) not at
all. Your hunger is the clock on everything else.

**Fight.** Your faction's combat form, and they are not interchangeable: an aura and thorns that
grow with you, a bite that only matters alongside others, a bolt that gets stronger the longer your
lineage survives.

**Shape.** Push the ground under you in your faction's direction. Slow, cumulative, and the reason
that *being somewhere for a long time* is itself an action.

## The economy: you and the faction share one purse

Everything you develop — abilities, path steps, the pheromone budget, the strength of what you
write — is paid for out of **the same pool that feeds your faction**: food carried to the nest,
mycelium mass, crystal power.

This is the central minute-to-minute decision, and it is the same decision in all three factions
wearing three costumes: *this larva, or a stronger me?* A player who invests entirely in themselves
commands a weak faction; one who invests entirely in the faction is a weak individual in a world
that kills individuals.

It also disarms the obvious balance problem by construction. A Faeling raising the whole ecosystem
against its rivals is spending crystal power — the same power that is its own strength and its own
respawn. **The ecosystem-buffing Faeling is a weaker, more mortal Faeling.** No extra limiter is
needed.

## The horde, and why it is not an order

A Sectid can develop pheromones and travel with a growing swarm. This is the closest the design
comes to a command interface, so it is bounded three ways at once:

1. **The horde keeps its own drives.** Your pheromone raises how attractive you are to follow;
   hunger, fear and its own hunting still win. You lead a hungry swarm to prey — you do not lead it
   into empty country. You set direction; you do not issue orders.
2. **The horde eats.** As many follow as you can feed. A large horde strips its surroundings and
   forces you to move.
3. **Its size is capped by your path.** Pheromone budget is a development choice competing with
   everything else, and a player may reasonably ignore the horde entirely and build their own body.

At the top of the pheromone path sits the one exception: **marking a target**, which converges the
swarm on it. It is deliberately the last step, because it is the single moment the faction does
what you say. It is a **spend**, not a passive: it costs pheromone budget per use and lasts a
limited time. It must not bypass the eligibility rules that govern any hunt — mass, element,
dietary restriction — because the faction's own defensive reflex already relaxes some of those, and
a player mark that relaxed more would outrank the faction's instinct for self-preservation.

The same shape generalises to the other factions as an **(open)** proposal: a costly forced spore
wave with chosen traits, a costly pulse of reinforcement to the wild. One moment per faction where
it does what you say, priced.

## Clocks — what makes standing still bad

- **Your body.** Hunger and mortality. Immediate and legible, and it ticks differently per faction:
  for a Shroomer hunger is growth fuel rather than a threat, and a Faeling does not eat at all.
- **Rivals' progress.** Ground you do not take is ground somebody else takes. This is the clock
  that currently runs too quietly to be felt — making rival advance *visible* is its own piece of
  work, and until it is done this clock does not really exist.
- **The victory timer — Faelings only.** See [06-run-and-progression.md](06-run-and-progression.md).

## Death

You respawn at one of your faction's anchors: the nearest nest, the heart of your territory, your
own crystal. Where you come back is therefore something the world decides and something rivals can
take from you.

**Respawning is paid for by the anchor, out of the faction pool** — the nest spends food, the heart
loses substance, the crystal loses power. Your death is a faction setback, which is what completes
the self/faction tension: the more you have invested in yourself, the more expensive it is to lose
you.

How many times you can come back is a fixed number set at the start of the run, and deliberately
**not** the number of surviving anchors. Anchor counts are a consequence of world generation and
get retuned; if they doubled as continuations, a generation fix would silently be a difficulty
change.

Two independent ways to lose:

- **You run out of continuations.** You played badly or unluckily; the faction survives you.
- **Your faction runs out of anchors.** There is nowhere to put you. The faction lost and you went
  with it.

The second is the one the design cares about — the only pressure that makes the faction's fortunes
genuinely yours. It also means you can be doing well personally and still be losing.

## What accumulates, and why it is a chain rather than four scores

Four things grow over a run, and they are not four progress bars. They are a production chain:

```
knowledge  →  personal power  →  faction strength  →  domain
(where to    (surviving being   (what your acting    (what strength
 act)         there)             produces)            converts into,
                                                      and what the goal
                                                      measures)
```

Read as separate scores they overlap — terrain converted and faction strength move together for two
of the three factions. Read as a chain, each one is the input to the next, and a player who is
strong in one and weak in the previous knows exactly what is wrong.

**Knowledge** is the one that is not a resource. It is what you have worked out about this world —
where a species lives, who is winning, where the rival's anchors are — and it pays out by making
your other three choices better, plus by unlocking things between runs.

## What exists today

The player is a **development placeholder**, and the gap is worth stating plainly because it is the
whole of the work ahead: the current player entity has no species, no faction, no diet, no
mortality and no verbs. It is unclamped, which makes it many times faster than any creature, and
its capabilities are observation tools built for answering "why is this creature doing that" —
inspect drives, highlight and cycle a species, detach the camera, pause, step, paint terrain.

Some of that (inspection) is a plausible ancestor of a real feature. Most of it is scaffolding.
The divergences between this document and the code are tracked in
[`../implementation/README.md`](../implementation/README.md).

## Open

Listed in [08-open-questions.md](08-open-questions.md): what control actually feels like for a body
with drives of its own, how a faction is chosen, whether the top-tier "does what you say" ability
generalises beyond the Sectid, and what the player is entitled to see.
