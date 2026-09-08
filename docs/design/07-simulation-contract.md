# 06 — The simulation contract

*Last updated: 2026-09-08*

Five promises the simulation has to keep. Each exists because breaking it broke the game in a way
that was hard to see and expensive to find, and each has cost real debugging time — so they are
recorded here as design constraints rather than as engineering notes.

---

## 1. The world advances at the same rate everywhere

The world contains far more creatures than can be thought about every tick, so distant creatures
think less often. That is a performance necessity and it is invisible to the player **only if it
changes nothing but cost.**

The rule is: **reduced detail gates decisions, never motion.** A creature far away decides less
often, and coasts on its last decision in between. It does not move more slowly, age more slowly,
starve more slowly or breed less.

Getting this wrong does not produce a visual artefact, it produces a different world. When motion
itself was throttled, distant herds crawled and distant hunts stalled, and how fast the world
advanced depended on where the player happened to be standing. When a growth rate was multiplied
to compensate for skipped ticks but applied on every tick anyway, one faction grew an order of
magnitude faster wherever the player was not — a faction bonus for being unobserved.

Two corollaries, both learned the hard way:

- **Compensation and gating are one mechanism.** Anything that scales a rate to make up for
  skipped thinking must also actually skip. Compensation without skipping is not compensation, it
  is multiplication.
- **The correctness question is answerable.** Run the same world at full detail and at reduced
  detail and compare the outcomes; anything that diverges beyond noise is either a defect or a
  documented, deliberate exception. This is a test the project keeps, not a principle it hopes for.

The intended end state is **two levels of detail, not a ladder of them**: full, and one compressed
level for everything the player is not near. The promise above is only ever about cost against
outcome, and nothing outside the player's vicinity is owed smoothness — a creature nobody is
watching does not need to move fluidly, it needs to arrive at the right place having done the right
things. Intermediate levels buy fluidity nobody sees, and each one is another place the rate rule
can be broken quietly. Collapsing to two is contingent on the compressed level actually producing
the same world as the full one; until that holds, the ladder is what keeps the error per step
small enough to live with.

---

## 2. Population limits are an engine constraint, never an ecological force

There is a ceiling on how many creatures can exist, because rendering and simulating them costs
something. That number must never be used to *shape* the world.

The failure it prevents is precise. A single global brake that suppresses every species' breeding
as the shared ceiling fills is not a brake — it is a selection filter. A species survives it only
while its birth rate beats its death rate by more than the brake's factor, so as the ceiling fills,
only the fastest breeders persist, and the winner keeps the brake clamped shut for everyone else.
The observed result was a world overwhelmingly made of the fastest breeders, in which predators
were neither starving nor search-limited: they were *birth*-limited by a mechanism their prey wins
by construction. (Figures, with seed and commit, in [../changelog.md](../changelog.md).)

The same defect has a second form. If the brake is applied to some reproduction paths and not
others, the unthrottled ones claim every freed slot immediately and their share can only ever rise.
That produces a monoculture that *relocates* when you patch one path, rather than resolving.

So:

- **Ceilings are per class** — prey base, hunters, factions — so crowding among rabbits says
  nothing about whether a wolf may breed.
- **Every path that creates a creature respects them.** One unbudgeted path recreates the ratchet.
- **A refusal is visible.** Hitting a ceiling is the engine deciding what the world contains, so it
  is counted and reported. Frequent refusals mean either the budget is wrong or the ecology is not
  binding — and that is the thing worth being able to read off a log.
- **The real limiters are ecological and local**: crowding where you stand, food where you stand,
  and being eaten.

---

## 3. The same seed produces the same run

Balance work on a system whose outcomes swing wildly between runs of the same world is not work,
it is anecdote. Every source of randomness in the simulation derives from the world seed, so a
result can be reproduced, bisected and argued about.

This is also what makes small, exactly-specified test worlds useful: a scenario with fixed terrain
and exact spawns, run to a stated tick count, produces the same numbers every time, so a change's
effect is the difference and not the noise.

---

## 4. A change must be visible in the world before it counts as done

Every mechanic is supposed to produce something a player could notice. The test for whether a
mechanic works is a change in what happens, not a change in a field.

Practically this means the world reports on itself — what died and of what cause, what was born,
what hunted and failed and why, which populations are moving where — and that a balance claim is
made against those reports rather than against intuition. A mechanic that cannot be observed
failing cannot be tuned.

It also means measurements are evidence about **one build of one world**. A figure without its
seed and its commit is not evidence, and the project has already had one headline result survive a
change that invalidated it. Measurements live in [../changelog.md](../changelog.md), stamped.

---

## 5. Events must be answerable

The world is allowed to be harsh. It is not allowed to resolve things before the player can react.

Concretely: a faction must not lose its infrastructure before it has built any. A raid should be
something a player sees coming and can answer — if a faction's structures are gone in the first
few minutes, it has not been beaten, it has been deleted, and every downstream measurement of that
faction is measuring a corpse.

This is enforced as a small set of whole-game assertions checked on a standard run: no structures
destroyed too early, populations of each faction above a floor, and outcomes that do not deviate
wildly between reruns of the same seed. They are a gate on the game being playable, not on the
code being correct.

---

## What this contract costs

All five are constraints on how systems may be written, and they are not free:

- Rate-scaled behaviour is harder to write correctly than un-gated behaviour.
- Per-class ceilings need every spawn path to know about them.
- Determinism forbids convenient global randomness.
- Self-reporting is code that produces no gameplay.
- Answerability limits how sharp early-game pressure can be.

They are worth it because each replaces a class of bug that is invisible from inside the code and
only shows up as "the world feels wrong", which is the most expensive kind of bug this project has.
