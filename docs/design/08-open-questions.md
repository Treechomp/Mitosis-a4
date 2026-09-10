# 08 — Open design questions

*Last updated: 2026-09-10*

Decisions that have not been made. Each states the question, why it is hard, and what is already
known — not a recommendation. When one is settled, the answer moves into the document it belongs to
and the entry is deleted — unless the closure itself is what stops the question being reopened from
intuition, in which case the entry stays, marked settled, and says where the answer and its
arithmetic live.

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

The mechanism does bias toward a front: a share of every terraformer's acts works the ground
directly underfoot rather than a random tile in reach, which is what lets a converter hold and
extend an edge instead of speckling its neighbourhood. The split, and the rates behind it, are in
[`../implementation/README.md`](../implementation/README.md) under V7. Whether that reads as a front at world scale has never
been looked at. The soak runner can now write a biome map at the start and end of a long run, which
is the instrument for looking.

The reading is agreed here in advance, so that it is not fitted to whatever the maps turn out to
show:

1. **A domain boundary can be pointed at on the end-of-run map.** Then R1 is not a mechanism
   problem — the front exists and the player has no way to see it while playing, which is V1 in the
   presentation section below, and different work.
2. **The change is scattered rather than concentrated around structures.** Then there is no front,
   despite the underfoot bias, and the mechanism is what has to change.
3. **The late map looks like the early one.** Then the problem is not terraforming speed but early
   equilibrium — C2 — and R1 half solves itself.

### R2 — The pacing rework

The faction layer does not fit the target run length, and it misses in two opposite directions:
faction combat resolves far too fast, while faction terraforming may be too slow to read at all. A
single scale factor cannot correct both, so the pass is a reshaping and not a retiming. The
creature layer measures out about right and is not what has to move.

What this does to the Shroomer is no longer part of the question. Its lifespan now follows the
growth it achieved, which is a settled decision recorded in
[06-run-and-progression.md](06-run-and-progression.md).

### R3 — How is the Faeling's ecosystem domain measured and shown?

Its domain is the wild it has strengthened. Whether that is population counts, the condition of
those populations, the ground they hold, or a combination is open — as is how a player reads it
without a spreadsheet. The other two factions' domains have an obvious visual (converted ground,
standing structures); this one does not yet.

Now partly constrained. Since numbers are set by breeding ground (C2, C5), the honest measure is
**per habitat rather than per world**: a species filling the breeding ground it has is a domain the
player can see on the map and can act on, where a raw headcount is neither. This does not make the
domain unbounded — a species is still capped by its class share, so strengthening the wild moves
populations towards their ceilings and cannot push them past. Whether that ceiling should itself
respond to the faction is a separate question and is not assumed here.

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

### C2 — Should food bind herbivore numbers? **Settled: no.**

Kept rather than deleted because the intuitive answer is the wrong one and the question will
otherwise be asked again.

**What decided it was arithmetic, not preference.** The grazing economy was read out of the registry
and it is about a hundredfold away from binding: the entire herbivore ceiling would feed from a
fraction of one per cent of the map. Grazing is therefore not a population limiter and cannot be
made into one by tuning around the edges. The working is in [../changelog.md](../changelog.md), with
the commit it was read at.

**What limits numbers instead** is reproduction — a per-species carrying capacity read off the
seed's terrain, with breeding grounds and fertility-coupled births inside it
([03-creatures.md](03-creatures.md)). That keeps composition a property of the species rather than
of the engine, which is what [07-simulation-contract.md](07-simulation-contract.md) requires.

That is a statement about *numbers*, and it is no longer also a statement about survival. An animal
on ground that cannot feed it starves, by design — the rule that rejected starvation outright has
been replaced, and the distinction it was missing is in
[03-creatures.md](03-creatures.md).

**What the economy is for instead** is migration: a herd should move because the land it stands on
can no longer support it. It does not do that today, and making it do so is C4.

**One correction, from building the forage mechanism and measuring it.** Food does not bind numbers
through hunger. It reaches the birth rate directly, because the design requires an animal to be well
fed before it may breed, so food *is* a lever on population — a much shorter one than starvation,
acting on births. That does not reopen the question, but it is why forage values move composition
and why they cannot settle it (C5).

**Nothing starved in any run measured while answering this**, which read at the time as the design
working. It was not: it meant ground quality had no consequence anywhere, so being driven off good
land cost an animal nothing. That is now a defect to fix rather than a property to preserve.

The original complaint — the prey base hits its ceiling early and is a flat line for the rest of the
run — is not answered by this and is not a food question. It belongs to whatever varies a population
over a run: predation, seasons, faction pressure.

### C3 — What does per-individual variation actually do? **Mechanism built; the rates are open.**

Its purpose was already settled: the medium the player writes into, and the thing that carries
between runs. The mechanism now exists — a child descends from its parent rather than from the
species, on a leash of regression toward the species value plus a hard band no lineage may leave
([03-creatures.md](03-creatures.md)). Mutation is possible without a player writing anything, which
is what makes the medium worth writing into: a world that only changes when pushed is a world where
the player is the only author.

What is open is **how fast, per trait**. Every heritable field carries its own mutation rate, and
the first set was chosen to preserve the spread a population already had rather than to express any
intent about what should evolve. The measurement that answers it is a drift table — per trait, per
species, which way selection pulled over a run and how hard — and it is in
[../changelog.md](../changelog.md). Read it before arguing about a rate.

Two things it should be read for specifically. **Reproduction traits** are the r/K filter this
project has removed twice: if cooldowns and thresholds drift downward across the board, selection is
rediscovering the fastest breeder and the leash is the only thing holding it. And **metabolism** —
if hunger decay or maximum hunger drift, the carrying-capacity model is computing from species
constants that no longer describe the population, and it will have to read a live mean instead.

### C4 — How much food pressure should a herd actually be under? **Partly answered by C5.**

C2 settled that grazing drives migration rather than numbers, and the arithmetic says by roughly how
much the demand has to rise for a herd's draw to balance a meadow rather than a dozen tiles. **That
figure is computed, not played.** Nobody has run the game at it, and it is the first time herbivores
would be under food pressure at all, so what it does to herd behaviour, to starvation deaths and to
the predator base is unknown.

Two things make it a real question rather than a tuning pass. It is measured across seeds or not at
all — the invariant gate's own assertion is seed-dependent. And it pushes food *towards* binding
numbers, which C2 rejects, so it is bounded from above by the birth brake it has to be paired with:
the right value is the largest one that still leaves reproduction, not hunger, deciding how many
there are.

The pairing has since happened: what a species extracts was raised, and the birth brake it needed
arrived with it (C5). D17 followed, and with it the half this question was really about — hunger now
tracks the ground. Animals on contested or marginal habitat sit visibly hungrier than animals on
ground they hold alone, they starve where they cannot make a living, and no species is lost to it.

**What is left is one number.** The world is still close to full of food in the average, because at
equilibrium total grazing pressure is fixed by how many animals the world is sized to hold, not by
how fast any one of them eats — raising a consume rate raises the draw per feeding tick and lowers
the time spent feeding by the same factor. The efficiency knob is what actually moves it: a species
that needs *fuller* ground to hold its condition draws more for the same hunger and wears the world
harder. The remaining question is how hard, which is the same trade as C5's density and should be
settled with it: more visible grazing means animals that need better ground, which means more of
them starve.

### C5 — How densely packed should a niche be? **The mechanism is settled; the number is not.**

**Settled: habitat is derived, not authored.** A species' capacity is read off the world the seed
generated — the food flow of the ground it can feed on, split among the species contesting that
ground in proportion to what it is worth to each of them, divided by what one animal takes. Nothing
is listed per species and nothing is tuned per world; the terrain decides, which is what makes two
seeds two ecologies rather than one roster at two sizes. It closed V8: the class allowance is no
longer touched and the engine refuses no births.

**Open: the density.** One number sets how many animals a unit of habitat holds, and it is a design
choice rather than a derivation — the difference between a teeming world and a sparse one. It is
also doing a second job it should eventually stop doing: it absorbs the gap between what an animal
takes while it is feeding and what it takes averaged over a run. Those are different quantities and
the second is much smaller, so the number is currently part ecology and part fudge. Separating them
means fixing D17 first.

Still open and no longer blocking anything: whether the *place* constraint should generalise — today
a species may be required to stand on particular ground to breed, and only some are — and what
happens to a species whose breeding ground a player or a faction destroys, since a limit that can be
driven to zero is an extinction mechanism rather than a cap.

---

## Factions

### F1 — How does a small faction reach what it can see?

Answered in part: the keeper's influence is not its body, so it does not need presence everywhere.
The reach problem is not gone, though — the ecosystem it strengthens is the ecosystem near it.
Crystal-to-crystal relocation was built and deleted, because keepers that relocate assemble and
assembled keepers sweep. Whether the answer is that a keeper only ever works its own region, and the
world census merely tells it *which* of its regions to work, is not decided.

The keeper's fortress ([04-factions.md](04-factions.md), provisional) answers half of it in the
opposite direction — not reaching further, but leaving a region defended while the keeper's
attention is elsewhere. It moves nothing, so it cannot revive the failure above. It says nothing
about where a keeper can *act*.

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

### F5 — Does extent-derived heart health run away?

Anchor durability is earned per faction, and the Shroomer earns it from extent: a wider mycelium
makes a tougher heart ([06-run-and-progression.md](06-run-and-progression.md)). The obvious failure
is positive feedback — a bigger bloom is harder to stop, so it gets bigger.

Three counterweights are already settled, and are expected to hold it: drought, the perimeter that
grows with the extent and has to be kept moist, and growth-derived lifespan stopping the core from
renewing itself on ground it has exhausted. That they are *sufficient* is a prediction rather than
a measurement, and it is the shape of prediction this project has been wrong about before.

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
