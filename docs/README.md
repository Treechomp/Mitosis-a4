# Mitosis — documentation

Mitosis is a top-down creature-sandbox game built in Godot 4.6.3 / C#. This folder holds two
kinds of document, kept deliberately apart, plus a history.

## The two layers

| | [`design/`](design/) | [`implementation/`](implementation/) |
|---|---|---|
| **Answers** | What is this, and why is it built this way? | What does the code do today, and where? |
| **Reads without** | the codebase | nothing — it names files, types and fields |
| **Contains** | intent, function, relationships, constraints, open questions | classes, methods, execution order, where each tuning value lives |
| **Contains no** | tuning constants, class names, measured run results | design rationale that isn't already in `design/` |
| **Goes stale when** | a design decision is reversed | code is renamed or restructured |
| **Priority** | primary | secondary |

The layers are not only separate, they check each other. `design/` states the target;
`implementation/` records, in its **Divergences** table, every place the code does not yet match
it. That table is the working backlog, and it only exists because there is a target picture to
measure against — which is the whole reason the design layer is primary.

[`changelog.md`](changelog.md) is the third thing: what was changed, when, in which commit, and
what result it produced. Every measured figure belongs there, stamped with its seed and commit —
nowhere else.

[`archive/`](archive/) holds superseded documents. They are kept for the reasoning they contain
and are wrong about the current code by definition; each carries a banner saying as of when it
was true.

## Why the split exists

A single reference document mixing intent with implementation was maintained for six months and
accumulated 31 verified contradictions against its own codebase (recorded in
[`archive/documentation-audit-2026-08.md`](archive/documentation-audit-2026-08.md)). The failure
was structural, not careless:

- **Claims that `grep` can falsify were mixed with claims that it cannot.** A reader had no way
  to tell which sentences needed re-checking after a code change, so none of them were.
- **Material accumulated where the work happened, not where the topic lives** — reef handling
  documented under *Hunting*, collision under *Hunting*, thorn defence inside a species table.
  Material filed away from its topic is material nobody rereads when the topic changes.
- **Counts and constants were restated in five files with no shared source**, so one code change
  needed nine coordinated edits and got two.

The split answers each: `design/` contains nothing checkable against source and therefore does
not rot; `implementation/` points at the authority rather than copying it; `changelog.md` gives
every measured number a date, a seed and a commit, so an expired figure is distinguishable from
a live one.

## Where to start

- New to the project → [`design/01-vision.md`](design/01-vision.md), then
  [`design/04-factions.md`](design/04-factions.md).
- Making a design decision → [`design/`](design/), and check
  [`design/08-open-questions.md`](design/08-open-questions.md) first in case it is already open.
- Changing code → the matching file in [`implementation/`](implementation/), then the source.
- Tuning balance → [`implementation/species-data.md`](implementation/species-data.md) for where
  the numbers live, [`changelog.md`](changelog.md) for what has already been tried.

## Rules for keeping this true

1. **A number that can be tuned does not appear in `design/`.** State the relation ("faster
   than", "only while", "cheaper for larger bodies"); the value lives in code, and
   `implementation/` says which field holds it.
2. **`implementation/` names, it does not copy.** Give the file, the type and the field. A field
   name stays correct until someone renames it, at which point the compiler complains; a copied
   value goes wrong silently.
3. **Every measurement is stamped or deleted.** Seed and commit, in `changelog.md`. An unstamped
   figure is not evidence.
4. **A document about a past state gets its banner the day it is written**, not the day it is
   archived.
5. **One timestamp per file, in the header.**
