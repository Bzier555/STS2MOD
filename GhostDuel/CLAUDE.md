# Working rules for this repository

Read [`PLAN.md`](PLAN.md) before writing any code. Read
[`docs/POSTMORTEM.md`](docs/POSTMORTEM.md) before proposing an architecture — it records twenty
specific, already-diagnosed failures of the previous attempt at this exact mod.

## The premise you must not break

The Ghost is one real `Player` on `CombatSide.Enemy`. Every card, relic, power and class resource
works through the game's own systems. **If a change makes the mod special-case a card, relic or
character by id, the change is wrong.** The mod's job is to make the situation legal, not to emulate
content.

## Evidence discipline

1. **Never write documentation in the present tense for behavior you have not observed running.**
   This single failure is what made the previous attempt unrecoverable: forty "implemented behavior"
   bullets for a build whose core loop had never executed. Unobserved work is future tense or lives
   under "planned."
2. **`docs/ENGINE-NOTES.md` is for facts read from the decompiled source only**, cited by
   `file:line`. Inferences go in `ARCHITECTURE.md`, labelled as inferences. Never assert engine
   behavior from memory or from what a doc in `../STS2Ghostmod/` claims.
3. **A build that compiles and a verifier that passes are not evidence.** The previous attempt's
   verifier printed `PASS` for nineteen releases of a mod that did not work. An assertion that would
   pass on a broken mod is noise — delete it.
4. When reporting on a change: say what you observed, what you did not check, and what you assumed.
   If a gate check failed, say so with the log output.

## Before adding a Harmony patch

Answer all four, in the commit message or in `ENGINE-NOTES.md`:

1. What engine code breaks without it? Cite `file:line`.
2. Is the mode guard the first statement in the method?
3. Could a narrower target work — a specific call site instead of a property getter?
4. What is the total patch count now, and does this one stay independently justifiable on its own
   evidence? (No numeric ceiling — 2026-09-02 decision: the mod working correctly matters more than
   the patch count. Still answer questions 1-3 for every patch; don't use the removed ceiling as
   license to skip the discipline that answering them enforces.)

Never postfix a hot property getter to reallocate a collection. `CombatState.Players`, `Allies`,
`Enemies`, `PlayerCreatures`, `HittableEnemies` are read constantly by the engine.

Never use an `AsyncLocal` to change the meaning of an engine-wide property. If side-relative
adaptation is genuinely required, patch the consuming call site.

## Code shape

- No file over ~300 lines. Split by responsibility, not by line count.
- One controller. Behavior varies by injecting a chooser, never by forking the turn skeleton.
- No static mutable state except `Diagnostics` and one `GhostSession` with a defined lifetime and a
  single `Dispose`. Assert `GhostSession.Current is null` after every exit path.
- Each reflected private/compiler-generated name lives in exactly one file, with a load-time
  existence assertion so a game update fails loudly at load rather than mysteriously in combat.
- Prefer the native command (`CardPileCmd.Draw`, `CardCmd.Discard`, `card.SpendResources`,
  `card.OnPlayWrapper`) over manual state manipulation. If a native path throws for a Ghost, that is
  the finding — log it and fix it at its boundary. Do not hand-roll a bypass.
- Never fabricate an engine result object to satisfy a caller. If the truth is unavailable, that is
  a design problem to surface, not to paper over.
- No hardcoded screen coordinates. Anchor to nodes.

## Milestone discipline

Do not write code for a milestone whose predecessor's gate has not passed. Not scaffolding, not
"just the interfaces." If you believe a gate is unreachable as specified, say so and propose a
change to the gate — do not route around it.

Directories that must not exist before their milestone: `src/Ai/` (M5), `src/Progression/` (M7).

## Environment

- Game **0.107.1**, BaseLib **3.4.5+**, `net9.0`, Harmony.
- Decompiled source: `../STS2Ghostmod/.tools/sts2-src/` — regenerate with
  `../STS2Ghostmod/.tools/ilspycmd.exe` after a game update, then re-verify every `file:line` in
  `ENGINE-NOTES.md`.
- Old implementation, for salvage only: `../STS2Ghostmod/src/`. See POSTMORTEM §5 for what to port
  and what to leave.
- MIT-licensed AI references: `../STS2Ghostmod/.references/`. Preserve attribution and
  `THIRD_PARTY_LICENSES/` for anything adapted.
- No runtime dependency on Python, MCP, HTTP, external LLMs, ONNX, Publicizer or any external
  process. Everything runs in-process.

## Build

```powershell
dotnet build GhostDuel.csproj --configuration Release
```
