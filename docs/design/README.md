# Design layer

*Last updated: 2026-09-09*

What Mitosis is meant to be, and why its systems are shaped the way they are. Everything here is
readable without opening the codebase, and nothing here can be verified — or falsified — by
reading source. That is the point: a design document that quotes the code becomes a second, worse
copy of the code.

## Contents

| Document | Answers |
|---|---|
| [01-vision.md](01-vision.md) | What kind of game is this, what is the player's experience meant to be, and what will it refuse to become |
| [02-world.md](02-world.md) | What a world is, why it is generated rather than authored, and why terrain is the thing worth fighting over |
| [03-creatures.md](03-creatures.md) | What a creature is, what drives it, and how a food web is meant to hold together |
| [04-factions.md](04-factions.md) | The three factions, their opposed economies, and the shape of the war between them |
| [05-player.md](05-player.md) | Who the player is, how their influence reaches past their body, what it costs |
| [06-run-and-progression.md](06-run-and-progression.md) | The loops, how long a run is, how each faction wins, what carries over |
| [07-simulation-contract.md](07-simulation-contract.md) | The promises the simulation must keep for any of the above to be true |
| [08-open-questions.md](08-open-questions.md) | Decisions that are genuinely not made yet |

## What belongs here

Function, intent, constraint, and the reasoning behind a choice. A system is described by **what
job it does in the game** and **what it guarantees**, not by how it is written.

- ✅ "A predator prefers prey that is worth the chase, but is never forbidden a small meal — a
  specialist whose whole diet is small game must not starve by construction."
- ❌ "`HuntingSystem` multiplies the candidate score by `1 / (nutrition / (MaxHunger × 0.25))`,
  clamped to 20×."

The first sentence stays true across every retuning of the second. If the second is reversed, the
first is what tells you whether that was a bug or a decision.

## What does not belong here

- Tuning constants, thresholds, radii, rates. State the relation, not the value.
- Class, file, method or field names.
- Measured run results. Those are evidence about one build and belong in
  [../changelog.md](../changelog.md), stamped with seed and commit.
- Task status, roadmaps, "done ✅" markers. What is built is a fact about the code.

## Status vocabulary

Design statements carry one of three markings. Anything unmarked is settled design.

- **(open)** — the decision has not been made. Listed in [08-open-questions.md](08-open-questions.md).
- **(provisional)** — decided, but on thin evidence; expected to be revisited.
- **(aspiration)** — the design intends this and the game does not do it yet. An aspiration is not
  a claim about the build.
