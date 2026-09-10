# 04 — The three factions

*Last updated: 2026-09-10*

The factions are the world's plot engine. Ordinary animals produce a steady state; the factions
produce a *direction* — ground changing hands, fronts advancing, a region that looked settled
turning into something else. They are also the only part of the world the player belongs to.

## The one difference that generates all the others

Each faction converts a different input into growth:

| | **Shroomer** | **Sectid** | **Faeling** |
|---|---|---|---|
| **Spends** | ground fertility | prey | nothing it can run out of |
| **Earns** | body mass, then spores | food, then brood | personal power |
| **Grows by** | occupying more ground | building more nests | getting individually stronger |
| **Pushes ground** | wetter | drier | back toward the middle |
| **Anchor** | a territory | an object | an object |
| **Dies of** | drought, crowding | famine | being few |
| **Numbers** | many, slow, immobile-ish | very many, fast, fragile | few, mobile, durable |

Everything below follows from that row. Nothing about the factions is a stat advantage; they are
three different answers to "how do you turn a world into more of yourself".

## Shroomer — the occupier

A fungal organism that grows in place. It feeds on the fertility of the ground it stands on,
grows continuously toward a large mature form, and spreads spores onto wet ground where they take
root as new Shroomers.

**Its weapon is the ground.** It makes ground wetter, and wet ground is where it can spread. A
bloom therefore paints the terrain it needs ahead of itself.

**Its trap is the same ground.** Growing strips fertility, and its own terraforming turns fertile
land into barren swamp. A bloom exhausts the ground it stands on and must advance into fresh
country — a pasture-destroying, self-limiting front rather than an expanding blanket. On its own
swamp it survives but stays too hungry to keep spreading.

**Defence scales with age.** A newborn Shroomer is harmless. A mature one has a wide damaging
aura and thorns that punish whatever bites it — and the thorns hurt *large* attackers most, in
proportion to the body that impaled itself. That is deliberate: flat thorns punish exactly the
wrong attacker, since a swarm of small mouths pays the toll on every one of its many bites while a
single large predator pays it once per heavy blow. Scaled thorns make a mature bloom a problem for
big solitary hunters and keep the swarm as its designated counter.

**Its counters are environmental, not martial.** Crowding kills — a dense mat thins itself into
advancing fronts rather than a solid immortal field. Drought kills — a mature Shroomer on ground
too dry for it bleeds out. This is what makes rival terraforming a genuine weapon: drying a bloom's
ground kills the elders that nothing can out-fight. And a handful of animals eat spores and young
Shroomers as food, so a bloom is cropped from below while it is still vulnerable.

**Its anchor is a territory, not a building.** A Shroomer colony has no object to break — it is a
spread of bodies over ground it has made wet. So its anchor is modelled as a *heart* of claimed
ground, which survives only while both the ground around it stays wet enough and enough living
Shroomers remain nearby. That gives rivals two genuinely different attacks on one target: dry the
ground from outside the bloom and never trade a blow, or go in and kill the bodies. A bloom that
loses its heart founds another once it has regrown.

The design point is that **a bloom defends the ground it stands on**. Attacking a territory where
its owners are not is the shape of the drying attack; standing inside it and drying is a losing
race against the Shroomers' own wetting.

## Sectid — the colony

Insects that cannot graze. Everything they eat, they kill, and everything they build is paid for
out of what they carry home.

**The economy is the faction.** A kill is chopped and carried to the nearest nest; enough food
starts a larva; enough larvae advance the nest; a mature nest with surplus founds more nests
nearby, or sends out a distant colony. The whole faction is one conversion rate — prey into
brood — and every design question about Sectids is a question about that rate.

**Numbers are the weapon.** A single Sectid is nothing. A swarm's effective strength scales with
how many are present, which lets it bring down prey far larger than any of them and makes it the
natural counter to a fortified Shroomer bloom. For the pile-on to work at all, the swarm must be
able to physically reach the target — a spacing rule that holds them just outside biting distance
turns a swarm into a crowd of spectators.

**Feeding is split.** The killer carries the colony's share home while its packmates eat in the
field. Without this the whole swarm ferries tiny loads back and forth and starves in transit.

**Famine is survivable, deliberately.** A pure consumer faction dies wholesale when prey is
scarce, which makes it a coin flip rather than a faction. So a Sectid that finds nothing to hunt
retreats to its nest and goes dormant: it costs almost nothing to keep alive, does not register
as a threat to anything, and wakes when prey comes near. A minimal colony rides out a trough as a
defensive posture instead of wandering off to die.

**Terraforming comes from the nest, not the body.** Sectids move too fast to leave an imprint by
walking. Instead the nest dries its surroundings each time a larva hatches, so the colony's
influence accumulates in one place and is visibly tied to the colony's success.

**Its anchor is the nest** — a real object that can be walked up to and broken, taking the brood
inside with it.

## Faeling — the keeper

Few, crystal-born, and the only faction that is not trying to win.

**It exists to suppress whoever is winning.** A Faeling periodically judges which rival is locally
over-dominant and turns its attention there — attacking that faction's members by preference and
restoring the ground the winner depends on. Today that is usually the Shroomer bloom; it flips to
the Sectids automatically if they surge. The role is an anti-dominance balancer, which means the
Faeling's success condition is a *balanced world*, not a Faeling world.

**Its power is personal and inherited.** Kills and restorative terraforming make an individual
stronger. When it dies, part of that power passes to its crystal, and the crystal produces a
successor carrying it — so a lineage that has been fighting for a long time is meaningfully
stronger than a fresh one. This is the only compounding advantage in the game, and it is given to
the faction that cannot grow by breeding.

**It fights at range and avoids what it cannot survive.** A keeper never duels a mature Shroomer
whose aura outreaches it — those are left to be killed by drying their ground. Instead it holds a
standoff ring outside the bloom and works on the substrate. Containment and collapse, not a
suicide charge.

**Its scarcity is the design.** A keeper must be able to *see* the whole world's balance without
having to *be* everywhere: perception and presence are different things, and buying perception
with bodies turns a judge into an army. A faction of a few keepers that understand the map is what
the design describes; a hundred immortal raiders is not.

**Its anchor is its crystal** — one crystal, one Faeling. Break the crystal and that lineage ends;
the Faeling fights on but will never come back.

**Its fortress is the wild around the crystal (provisional).** A keeper's crystal is the one thing
it cannot afford to lose and the one thing it cannot stand next to permanently, because a keeper
that guards its crystal is not judging the world. So it defends the crystal with what is already
there: **guardians are recruited from the local wildlife, not summoned.** A keeper strengthens
animals that live near its crystal and they defend the ground they already live on.

Three reasons this is the shape rather than a summoned garrison or a way to arrive at a fight:

- **It moves no keepers.** Crystal-to-crystal relocation was built and deleted because keepers that
  relocate assemble, and assembled keepers sweep (F1 in
  [08-open-questions.md](08-open-questions.md)). A fortress cannot revive that failure, because
  nothing about it travels. It answers the reach problem the opposite way: the keeper's region
  defends itself while the keeper's attention is elsewhere.
- **It is the faction's own verb.** Strengthening the wild is what a Faeling already does. This is
  that verb aimed at a place instead of at a balance, so it adds a use for an existing mechanic
  rather than a mechanic.
- **It does not buy presence with bodies.** Guardians are stationary and local; they do not extend
  where a keeper can act, only what survives while it is away. The scarcity rule above stays intact
  — a keeper still cannot be everywhere, it can only leave somewhere defended.

Two bounds keep it from becoming the strongest thing in the game, and neither is an added limiter:

- **It is paid out of the purse.** Guardians cost crystal power — the same pool as the keeper's own
  strength and its own respawn ([05-player.md](05-player.md)). A keeper with a fortress is a weaker,
  more mortal keeper.
- **It draws down the thing it protects.** Recruiting does not create animals. A guardian is a wild
  animal that has stopped behaving like one, taken out of the population it belonged to, and the
  local wild is what a Faeling's domain is measured by (R3 in
  [08-open-questions.md](08-open-questions.md)). Fortifying heavily is spending the
  ecosystem to defend the ecosystem, which is a decision rather than an accumulation.

Provisional: how a guardian is chosen, whether the change is permanent, and what happens to a
guardian when its crystal breaks are not decided.

**Its crystal has three taps, and they compete (provisional).** Crystal power is limited and comes
back slowly, and it is meant to be spent on three things:

- **Growing the keeper faster** — the compounding one. Power spent here makes every later kill and
  every later restoration worth more.
- **Guardians** — the placed one. Local wildlife strengthened into a defence for the ground around
  a crystal, as above.
- **Coming back whole** — the insurance one. A keeper's replacement is built from what its crystal
  kept of it, and how much of the original survives is what power buys. A depleted crystal still
  returns you, and still returns you quickly, but substantially less than you were.

Three things follow, and they are why this is recorded as one decision rather than three features:

- **Today crystal power buys nothing.** It raises growth and ranged damage passively, so these would
  be the faction's first spending decisions. One tap built alone is the only sink there is, and
  therefore always the correct spend — the choice only exists once all three do. **All three or
  none.**
- **Death degrading rather than blocking risks a spiral**: a weak crystal returns a diminished
  keeper, which dies more easily, which weakens the crystal. It needs a floor, or regeneration that
  outpaces the descent at the bottom, or the faction gains a third failure condition nobody
  designed.
- **It is a real trilemma, and that is the point** — provided no tap dominates. Growth compounds,
  guardians are place-bound, fidelity is insurance. They should be strongest in different
  situations, and if one is simply better the other two are decoration.

## The war

The three-way conflict is not a war of extermination and cannot be. Each faction has a counter and
each counter is held by a different rival:

```
        Shroomer ──wets ground, denies desert──▶ Sectid
            ▲                                      │
            │                                      │
      dries ground,                           swarms, breaks
      kills elders                             bodies & blooms
            │                                      │
            └────────────── Faeling ◀──────────────┘
                    suppresses whichever is ahead
```

- **Shroomer beats Sectid** by wetting the ground out from under a colony that needs it dry, and
  by growing past the size a small hunter can kill.
- **Sectid beats Shroomer** by mass — the one thing that can reliably kill a mature bloom — and by
  drying ground from its nests.
- **Faeling beats whoever is ahead**, by preference and by restoration, and loses to being
  outnumbered if it ever tries to beat both.

Structures matter because without them faction strength is a function of population, and that is
the one contest an elite faction can never win. A few Faelings that break nests and hearts are a
faction; a few Faelings that kill individual Sectids are a rounding error. So every faction has
something breakable, and destroying it is a real event with consequences — brood lost, a colony
ended, a lineage that can never return.

**A raid must be an event the player can see coming and answer.** A faction that loses its
infrastructure before it has built any has not been beaten, it has been deleted — see
[07-simulation-contract.md](07-simulation-contract.md).

## What the factions are for, in one line each

- **Shroomer** — makes the map change shape slowly and visibly, and gives the player ground to
  hold.
- **Sectid** — makes the map dangerous, and gives the player a reason to be somewhere at a
  particular moment.
- **Faeling** — stops either of the other two from being the answer, and gives the player a
  reason to care who is ahead.

## What each is like to play, and what it wins by

Full treatment in [05-player.md](05-player.md) and [06-run-and-progression.md](06-run-and-progression.md);
the summary belongs here because it is the same asymmetry seen from the player's side.

| | You write into | Your anchor survives by | You win by |
|---|---|---|---|
| **Shroomer** | the ground, and the traits of spores near your heart — defence against reach | extent: a wider mycelium is a tougher heart, and a longer edge to dry | holding a share of the world in your own state |
| **Sectid** | places (a cleared site marked as good ground for a nest) and behaviour (pheromone, and at the top of that path a target mark) | numbers and clustering, so ranging far is a real risk | holding a number of standing colonies |
| **Faeling** | other species — raising the wild so it holds out against faction pressure | crystal power, spent on guardians or on getting back fast | neither rival's domain growing, for a sustained period |

Durability is earned from what each faction is already good at rather than set per structure; the
principle and what falls out of it are in
[06-run-and-progression.md](06-run-and-progression.md).

The Faeling's win is the one that had to be invented rather than derived: eliminating two entrenched
factions is disproportionate effort, and a keeper that wins by conquest is not a keeper. It wins when
the factions have been **reduced to ordinary species in the ecosystem** — present, no longer
spreading. Its domain is correspondingly not converted ground but the wild itself: the independent
populations it has strengthened, and the country they hold.

Note what that does to the three-way diagram above. The Faeling is no longer only a suppressor; it
is the faction whose interest is aligned with everything in the world that is not a faction. The
food web stops being scenery to it.
