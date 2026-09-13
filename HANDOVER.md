# Handover — next unit of work

*Written 2026-09-13 at `249847c`, branch `claude/lod-override-testing-rhwq4f`.*
*Supersede or delete this file when the unit it describes is done.*

## What just landed

`322eebe` + `249847c` — step A of the LOD divergence plan: the instrument was **scaled** before any
more curves were multiplied with it.

- **Floor control** (one tier against itself, one seed) reads `0.000000` at every sample and every
  scalar; the two fingerprint streams are byte-identical. The instrument is deterministic.
- **Ceiling control** (one tier against itself, two seeds — two worlds with nothing in common) is
  now recordable at all: the pair guard used to refuse a two-seed comparison unconditionally, and
  `--unrelated` is the deliberate opt-in. `--ceiling=<two streams>` writes the fidelity plateau as a
  fraction of it.
- The scalar was **split**: state (count, hunger, age) is the reading, position (centroid,
  dispersion) is diagnostic. The combined seven-component scalar is unchanged so earlier curves
  still compare, but it is mostly a position measurement.
- The curve is read in a **growth window and a plateau window**, not at one point.

**The finding that stops step B as written: the plateau has no resolution left.** Full against
Minimal plateaus at 126% (combined) and 110% (state) of the ceiling mean. A fidelity measure must
place "same world, coarser cadence" strictly below "different world entirely"; at plateau this one
places it above. That is D16's defect reappearing inside the instrument built to replace it, filed
as **D19**.

Only the **growth window** keeps headroom: 62% of ceiling combined, 64% state.

Figures with seed, tick count and commit: [`docs/changelog.md`](docs/changelog.md), section
"Scaling the divergence curve". Mechanism: `docs/implementation/tooling-and-tests.md`. Defect: D19
in `docs/implementation/README.md`.

## The next unit — one, and why it is this one

**Measure the fidelity curve's own seed-to-seed scatter in the GROWTH WINDOW, across the four
Full-against-{High,Medium,Low,Minimal} pairs, five seeds each.**

This is step B of the original plan, restricted to the growth window and re-aimed. Its question is
no longer "how much fidelity does each tier cost" but **"does the growth window have usable
resolution at all"** — which is the only thing that decides whether this instrument can ever gate.

It is next because:

- Every other move depends on its answer. If the growth-window separation between tiers survives
  the seed-to-seed scatter, the instrument gates on the growth window and the ladder collapse
  (step C) proceeds on a measured tolerance. If it does not, the instrument needs redesigning and
  step C stays blocked — redesigning first would be redesigning without knowing whether it is
  needed.
- The gap that has to survive is thin and already quantified: 3.5 ceiling-sd on the combined
  scalar, **1.6 on the state scalar**. The state scalar is the one worth gating and the one with
  the least room, so this is not a formality.
- It is cheap. See the runtime note below — this is roughly a minute of runs, not the hours the
  earlier handover assumed.

Report the between-seed spread beside the between-tier differences. If the spread is the same order
as the difference between tiers, the data settles nothing and that is the result.

## Settled — do not re-argue

- **Position is not gate material.** Centroid and dispersion carried 74% of the old scalar;
  dispersion is set by the seed, not the tier (Wolf: 6.75–40.51 across five Full seeds), and the
  centroid term divides by the two dispersions together, so a pair containing a tightly clumped
  pack reads as further apart. Diagnostic only.
- **The floor must stay exactly zero.** It is the precondition for reading anything else.
- **A tier must be pinned** by `SetLevelOverride` / `--tier`. The 128-tile scenario world never
  leaves Medium on its own — `LODSystem` says so. Do not "fix" that with distance thresholds.
- **No tolerance may be chosen rather than measured.** That is what step A existed to prevent.
- The combined scalar stays in the artefact for comparability. It is not the reading.

## Out of scope

- **Step C** (collapsing `LODLevel` to two tiers) — blocked until the growth window is shown to
  resolve. `SetLevelOverride` survives any collapse; that is already decided.
- **D2 and D3** — parked by decision.
- Any balance constant. `HabitatCapacity`, the metabolism question, a full-length soak.
- Re-litigating the plateau. It is measured and filed as D19.

## Runtime — read this before anything else

The container starts with **no .NET SDK and no Godot**, and the repo cannot be built or run without
both. `dot.net`, `builds.dotnet.microsoft.com` and `dotnetcli.azureedge.net` are all blocked by the
proxy. What works:

```
apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-8.0
curl -sSL -o godot.zip \
  https://github.com/godotengine/godot/releases/download/4.6.3-stable/Godot_v4.6.3-stable_mono_linux_x86_64.zip
unzip -q godot.zip     # binary: Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64
```

The `apt-get update` is required — the preloaded index 404s. Then `cd godot && dotnet build` is
clean, and `--headless --path godot --import` once before the first scene run.

**Runs are cheap: 3,000 ticks is ~1.2 s.** The earlier handover's "runs cost hours" no longer holds
and should not shape planning.

Also: the clone came up on a **stale ref** — the local branch sat 31 commits behind its own origin.
`git fetch origin <branch>` and fast-forward before reading anything, or you will study the wrong
tree.

A `--curve=` path that is not `res://` resolves against `godot/`, not the repository root.
