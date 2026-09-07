# 08 — Open design questions

*Last updated: 2026-09-07*

Decisions that have not been made. Each states the question, why it is hard, and what is already
known — not a recommendation. When one is settled, the answer moves into the document it belongs to
and the entry is deleted.

Known *defects* are not listed here; a thing that is broken is not a thing that is undecided. Nor
are **divergences** — places where the code does not yet do what this layer describes. Both live in
[`../implementation/README.md`](../implementation/README.md), and the divergence list is the
working backlog that this layer generates.

---

## Player

### P1 — What does control feel like?

Today the player steers a body directly. The design says the player is a creature under the same
rules as any other, and creatures have drives — hunger, fear, exhaustion — that a directly steered
body ignores. Somewhere between "a cursor with a hitbox" and "a creature you persuade" is the
intended answer, and it is not chosen.

Sharpened by the pheromone design: the Sectid horde explicitly *keeps its own drives and is only
given a direction*. If that is the right relationship between a player and a swarm, it is worth
asking whether it is also the right relationship between a player and their own body.

### P2 — How is a faction chosen, and is a run one faction?

All three are available from the start (faction access is not a meta unlock). Whether the choice is
made at run start, whether it can change mid-run, and whether a run could be played across factions
are undecided.

### P3 — Does the body leave a corpse?

Death costs the faction and returns you to an anchor. What the *body* does is unresolved: a corpse
that can be recovered would make dying a location problem as well as a resource one, and would sit
naturally alongside the existing carrion system — a Sectid dying with a full load, a Faeling's power
lying where it fell.

### P4 — Does the top-tier "does what you say" ability generalise?

The Sectid's pheromone path ends in target marking: one priced moment where the faction obeys. A
costly forced spore wave with chosen traits, and a costly pulse of reinforcement to the wild, are
the obvious equivalents — but whether every faction should *have* such a moment, or whether it is a
Sectid characteristic, is not decided.

### P5 — Is knowledge represented in the game at all?

Knowledge accumulates and is the first link in the chain, but it was deliberately not made the
in-run currency. Whether it exists as anything other than what is in the player's head — a filled
bestiary, a revealed map, a record of what you have seen — is undecided. It matters because it
decides whether *observing* is an activity or just a thing you do while walking.

---

## The run

### R1 — Rivals' progress has to become visible

The design names rivals' advance as one of the two clocks that make standing still bad. In the
current world it is nearly imperceptible: ground changes hands slowly and there is nothing that
reads as a front moving. **Until that is solved, the clock does not exist**, and the minute loop has
only hunger pushing it.

This is not a UI question first. It is a question of whether faction advance is *shaped* like
something visible — a front, a tide line, a spreading stain — or merely statistical.

### R2 — The pacing rework, and what it does to the Shroomer

Rates are set for roughly a sixth of the target run length, so a pacing pass is unavoidable. One
part of it is a mechanic decision rather than a number: the Shroomer's lifespan was chosen so that
**age would never check a bloom**, leaving crowding and drought as its only limits. At the target
length, age begins to bite. Either the lifespan is raised to preserve the original intent, or
ageing becomes a third limiter and the other two are retuned around it. Both are defensible; they
are different games for the Shroomer.

### R3 — How is the Faeling's ecosystem domain measured and shown?

Its domain is the wild it has strengthened. Whether that is population counts, the condition of
those populations, the ground they hold, or a combination is open — as is how a player reads it
without a spreadsheet. The other two factions' domains have an obvious visual (converted ground,
standing structures); this one does not yet.

### R4 — How much of a species pattern carries over?

Settled: what persists between runs is the outcome of selection, not a decree. Open: how much of
it, whether it is per species or per world lineage, whether it can be refused, and what stops a
long chain of runs from producing a roster that no longer resembles the designed one.

---

## Creatures & behaviour

### C1 — Priority ordering or weighted arbitration?

Drives are resolved by fixed priority with per-species thresholds
([03-creatures.md](03-creatures.md)). It is decidable and debuggable, and it cannot express "mildly
hungry and mildly afraid" as anything other than one of the two. A weighted scheme is more
expressive and much harder to reason about, and the project's worst behaviour bugs have all been
two drives fighting for one body.

Now also a player-facing question: the pheromone design adds "follow the player" as a competing
drive, and how it loses to hunger and fear is exactly an arbitration question.

### C2 — Should food bind herbivore numbers?

The prey base is currently limited by its class ceiling rather than by grazing — regrowth against
consumption leaves each grazer far more ground than it needs. That is the engine deciding the
world's composition, which [07-simulation-contract.md](07-simulation-contract.md) says it should not.

More pressing at the target run length: the prey base reaches its ceiling within the first few
percent of a run and then sits there for the rest of it. Whatever the answer, "the herbivore
population is a flat line for ninety minutes" is not it.

### C3 — What does per-individual variation actually do?

Its *purpose* is now settled: it is the medium the player writes into, and the thing that carries
between runs. The mechanism is not. How much an individual may differ from its species, how traits
pass to offspring, whether mutation is possible without a player writing it, and how a species
avoids drifting into something unrecognisable are all undecided.

---

## Factions

### F1 — How does a small faction reach what it can see?

Answered in part: the keeper's influence is not its body, so it does not need presence everywhere.
The reach problem is not gone, though — the ecosystem it strengthens is the ecosystem near it.
Crystal-to-crystal relocation was built and deleted, because keepers that relocate assemble and
assembled keepers sweep. Whether the answer is that a keeper only ever works its own region, and the
world census merely tells it *which* of its regions to work, is not decided.

### F2 — Is the keeper's raid role real?

Across four long runs, keepers landed not one blow on any structure. Every structure lost died to
another faction or to the environment. The raid mechanic this faction is designed around has never
been observed to execute, the cause was not established, and the leading hypothesis was invalidated
by a later change. **This needs re-measuring before it is reasoned about further** — see D4 in
[`../implementation/factions.md`](../implementation/factions.md).

### F3 — What is the Faeling's failure mode?

Shroomers die of drought and crowding; Sectids die of famine. A Faeling does not eat, does not
starve, cannot be hunted, respawns, and is meant to be individually stronger than the others. Its
only mortality is losing crystals.

Partly answered by the shared purse: strengthening the wild spends the same power that is its own
strength and its own respawn, so a generous keeper is a fragile one. Whether that is a sufficient
pressure, or whether the faction needs something that acts on the individual, is open.

### F4 — Are three factions the design, or the current count?

Nothing in the systems names a faction; a fourth would be data. Whether the three-way balance is the
point — each countering one and countered by another, with the third measuring the other two — or an
accident of history, matters for every faction-balance decision made from here.

---

## World

### W1 — Hex or square?

The world is stored as a square grid, hydrology traces flow across six offset-row neighbours, and
the renderer's half-row stagger — the thing that made the grid read as hex on screen — was removed.
The terrain currently has hex topology for water and square topology for everything else, and the
documentation papered over it rather than deciding. Hex neighbourhoods give rivers and movement
their character; a square grid is cheaper and simpler everywhere else.

### W2 — How big is a world for play?

Run length is settled; world size is not. At creature speed the whole map is now reachable within a
run, so the question is not "can the player get there" but "how much world should one run contain" —
and it trades directly against how visible a rival's advance is (R1) and how long the pacing pass
has to stretch every rate.

### W3 — Is a world persistent?

Progression between runs is settled; saving a *world* is not. Whether a run can be suspended and
resumed, and whether a world can be returned to, changes what the goal conditions and the
continuation counter mean.

---

## Presentation

### V1 — What is the player entitled to see?

There is no menu, no HUD, no map, no selection UI. Three things the design now *requires* to be
readable: your faction's domain and your rivals', progress toward your goal, and rivals' advance
(R1). Everything beyond that is a choice, and the "legibility over depth" pillar makes it a
consequential one.

### V2 — Which development tools become features?

Inspecting a creature and reading its drives is plausibly a real feature and is currently a debug
affordance — and it is the obvious candidate for whatever "knowledge" turns out to be (P5). Pausing,
single-stepping, speed control and terrain painting are almost certainly not. Which survive, and in
what form, decides whether the simulation must support them in a shipping build at all.
