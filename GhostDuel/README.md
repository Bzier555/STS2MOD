# Ghost Duel (STS2)

A Slay the Spire 2 mod in which you fight an AI-piloted **Ghost** of a previous run's build.

The Ghost is not a scripted monster. It is a real `Player` instance placed on `CombatSide.Enemy`,
piloted by an algorithm, so that **every card and relic a player could have used in a run works for
the Ghost through the game's own systems**. That constraint is the whole point of the design: nothing
about a card or relic may be reimplemented mod-side.

> **Status: M1-M4 shipped, gates passed live; M5 and M7 deliberately deferred; M6 in progress; M8
> starting.** M1 (2026-09-02): a real Ironclad `Player` on `CombatSide.Enemy` draws, plays
> Strike/Defend/Bash left-to-right via native `CanPlay`/`SpendResources`/`OnPlayWrapper`, applies and
> reads Vulnerable correctly, renders and is targetable on the enemy side, and resolves several clean
> rounds with no hang. M2 (2026-09-02): a hand-built 20-card/5-relic Ironclad pool
> (`src/Debug/M2Deck.cs`) plays five clean rounds with the turn controller mirroring the native
> turn-start/flush hook sequence in full. M3/M4 (2026-09-03): the Ghost's own direct damage genuinely
> queues, resolving at the start of its next turn; the human's queues symmetrically, resolving at the
> end of the Ghost's turn; a queued-damage indicator (the real native `NIntent` node/sprite, not a
> substitute) shows above whichever side has a pending attack, live-updating as Weak changes the
> number; Vulnerable-captured-at-play-time and Weak-late-bound-at-resolution both confirmed against
> reconciled worked examples (`ARCHITECTURE.md` §4 P15-P17, `docs/ENGINE-NOTES.md` M3 section). **M5**
> (AI decision-making) and **M7** (snapshots/ladder) are deliberately skipped for now, by explicit user
> decision — not gate failures, see `PLAN.md`'s own notes on each. **M6** (status expiry) is in
> progress: a native grace-tick asymmetry that let a one-turn debuff (Weak, via Red Mask) persist for
> two turns is root-caused and fixed (P18), not yet re-confirmed live; Poison/Doom timing is untouched.
> See `docs/POSTMORTEM.md` for what the first pass never reached.

## Read in this order

| Document | What it is |
| --- | --- |
| [`PLAN.md`](PLAN.md) | **The plan.** Milestones M0–M9, each with an exit gate. Start here. |
| [`docs/POSTMORTEM.md`](docs/POSTMORTEM.md) | What the first pass got right, what it got wrong, what to reuse. |
| [`docs/ENGINE-NOTES.md`](docs/ENGINE-NOTES.md) | Facts verified against the decompiled game, with `file:line`. The only trusted source of engine behavior. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | The enemy-side `Player` design and the minimum patch set. |
| [`docs/COMBAT-RULES.md`](docs/COMBAT-RULES.md) | The delayed-resolution duel rules. Single source of truth for mechanics. |
| [`docs/AI.md`](docs/AI.md) | Ghost decision-making. Deferred until M5. |
| [`docs/PROGRESSION.md`](docs/PROGRESSION.md) | The Legacy Ascension ladder and snapshots. Deferred until M7. |
| [`CLAUDE.md`](CLAUDE.md) | Working rules for anyone (human or agent) writing code here. |

## The M1 target

One thing, working, observed in a real game client:

```text
Ghost Debug mode  ->  first combat of the run
  ->  enemy side holds a real Ironclad Player: 80 HP, 3 energy, 5 Strike / 4 Defend / 1 Bash, Burning Blood
  ->  human ends turn
  ->  Ghost draws 5 real cards into its real hand
  ->  a left-to-right controller plays them via native CanPlay / SpendResources / OnPlayWrapper
  ->  Defend produces real Block on the Ghost creature; Strike/Bash produce a real queued attack
  ->  turn hands back; three full rounds run clean
```

Nothing else. No AI, no other characters, no orbs, no pets, no ladder, no snapshots, no shaders.

## Environment

- Game build **0.107.1**, **BaseLib 3.4.5+**, `net9.0`, Harmony.
- Decompiled game source for reference: `../STS2Ghostmod/.tools/sts2-src/` (3,425 `.cs` files, produced with
  `../STS2Ghostmod/.tools/ilspycmd.exe`). Re-generate it after any game update.
- Reference AI projects (MIT): `../STS2Ghostmod/.references/sts2-ai-teammate/`, `../STS2Ghostmod/.references/sts2-combat-ai/`.

## Mod identity — confirm before M1

Proposed permanent `id` in the manifest: **`GhostDuel`**, display name **Ghost Duel**.

The id is baked into save paths and mod dependencies, so it should not change later even when the
Legacy Ascension ladder is layered on top (a display name can change freely). Speak up now if you
want to keep `LegacyAscension` as the id instead.
