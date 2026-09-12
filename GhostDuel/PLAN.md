# Development plan

Ten milestones. Each has a **single exit gate** that must be *observed in a running game client*
before the next milestone begins. No milestone's code may be written before its predecessor's gate
is signed off.

This ordering is the entire lesson of the first pass. `../STS2Ghostmod` built M2 through M9 on top of
an M1 that never worked, then had thirty-eight Harmony patches and twenty subsystems to search when
the Ghost took an empty turn (see [`docs/POSTMORTEM.md`](docs/POSTMORTEM.md)).

---

## The three governing rules

**R1 — Evidence discipline.** Documentation states only what has been observed. Anything unobserved
is written in the future or conditional tense, or lives in a "planned" section. The first pass's
`README.md` listed forty implemented behaviors for a build whose core loop had never run once
(POSTMORTEM F1). Never again.

**R2 — Nothing without a gate.** A milestone's code is not written before the previous gate passes.
Not "started in parallel", not "just the scaffolding."

**R3 — Logs before features.** Every milestone's first commit adds the logging that will prove or
disprove its gate. The first pass added per-step logging in its nineteenth release (POSTMORTEM F5).

---

## M0 — Skeleton, harness, and the questions

Nothing about ghosts. Make the loop of *asking the game a question and getting an answer* fast.

**Tasks**

1. `.csproj` + manifest + `Sts2PathDiscovery.props` (port verbatim from the first pass — it is
   sound). Mod id decision confirmed (see [`README.md`](README.md)). One log line on init.
2. `GhostLog` — structured, always-on, categorized logging with a stable prefix. Log points named
   after what they prove, so absence is diagnostic. Port the first pass's taxonomy:
   ```text
   no HAND entry        -> draw / pile population
   no LEGAL entry       -> CanPlay or IsValidTarget
   no SPENT entry       -> resource spending
   SPENT but no PLAYED  -> OnPlayWrapper / card effect
   no TURN-END entry    -> hang inside the turn
   ```
3. **A scripted route to combat.** A one-click route to the debug encounter, so testing doesn't mean
   clicking through the normal menu/character-select/map flow each time. **Revised 2026-09-02**:
   originally planned as a `--ghostduel-debug` command-line flag; there was no reliable way to pass
   that flag through the Steam launch path being used, so this is instead one button added to the
   main menu (`ARCHITECTURE.md` §4 P0, `src/Debug/DebugMenuButton.cs`), wired directly to the same
   run-and-enter-combat code the flag would have called. See [`docs/ENGINE-NOTES.md`](docs/ENGINE-NOTES.md)
   §6 for the non-interactive seams that turned out not to be needed (`NonInteractiveMode`,
   `AutoSlayer` — the latter is UI-click automation, not an API surface). Do not flip `TestMode.IsOn`
   globally.
4. **Answer `ENGINE-NOTES.md` §7 open question 2:** enumerate every engine call site that reads
   `CombatState.Players` / `PlayerCreatures` / `Allies` and means "the human party" rather than "the
   acting side's party." This list decides whether `ARCHITECTURE.md` §5 option (A) holds. Write the
   answer into `ENGINE-NOTES.md`.
5. Answer open questions 1, 3, 4 and 5 the same way — by reading source, not guessing.
6. A verifier that only asserts things that can be false. Port the `GhostDamagePlan` pure tests;
   delete every tautology (POSTMORTEM F2). If an assertion would pass on a broken mod, it is noise.

**Exit gate.** One command launches the game, reaches the first combat, ends a human turn, and writes
a log file containing the mod's own turn-boundary entries. Round trip under a minute. All five open
questions answered in writing.

---

## M1 — An enemy-side Ironclad plays its cards ★

**This is the milestone. Everything else is downstream.** It is what
`../STS2Ghostmod/CURRENT_PROGRESS_AND_IRONCLAD_BASELINE.md` correctly identified as the objective,
nineteen releases late.

**Scope**

The debug encounter replaces the first combat of the run with:

| Property | Value |
| --- | --- |
| Character | Ironclad, `Player.CreateForNewRun<Ironclad>` inside `GhostConstructionScope` |
| Side | `CombatSide.Enemy` |
| HP / max HP | 80 (stock) |
| Energy | 3 per turn |
| Draw | 5 |
| Deck | 5 Strike, 4 Defend, 1 Bash |
| Starting relic | Burning Blood |
| Controller | `LeftToRightChooser` — one controller, no debug/real split |

Card values stay stock and native: Strike 1⚡/6 dmg, Defend 1⚡/5 Block, Bash 2⚡/8 dmg + 2 Vulnerable.

**Non-scope.** No AI, no scoring, no configs. No other character. No orbs, Stars, pets, Osty. No
shaders, grayscale, mirroring, entrance sequence. No ladder, snapshots, progression, save/load. No
multiplayer. No scaling. No status-expiry rules. Nothing under `src/Ai/`, `src/Progression/`, or
`src/Presentation/` beyond making the Ghost visible on the correct side.

**Patches: five to start.** P1–P5 from [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) §4, each with a
mode guard as its first statement. **2026-09-02: the numeric cap (originally ten) is removed** — the
mod working correctly matters more than the patch count, per direct user decision after a difficult
live-debugging session. Each addition beyond P1–P5 still requires a logged failing observation (or a
direct source-read finding, same evidentiary bar) recorded in `ENGINE-NOTES.md`, and still answers
`CLAUDE.md`'s four Harmony-patch questions — the discipline stays, only the ceiling is gone.

**Use native paths.** `CardPileCmd.Draw` for drawing, `CardCmd.Discard`/`Exhaust` for cleanup,
`card.SpendResources()` for cost, `card.OnPlayWrapper(...)` for the play. If a native path throws for
a Ghost, that is the finding — log it, fix it at its boundary. Do **not** hand-roll a bypass with
`AddInternal`/`RemoveInternal`; that is how the first pass ended up with two divergent controllers
(POSTMORTEM F12).

> **2026-09-02 gate correction.** Items 9–10 below originally read "queues" / "shows as an attack
> intent" / "resolves against the Block" — i.e. the full delayed-resolution duel from
> `COMBAT-RULES.md`. Implementing M1 revealed why that's wrong for this milestone: the only chokepoint
> all damage passes through, `CreatureCmd.Damage`
> (`MegaCrit.Sts2.Core.Commands/CreatureCmd.cs:96-412`), is one fused ~300-line method with no seam
> between computing damage and applying it — hooks, block, HP loss, VFX and kill-checking all happen
> inline. Deferring HP loss to a later turn would require either fabricating a `DamageResult` to hand
> back to callers (the exact mistake `ARCHITECTURE.md` §7 and `POSTMORTEM.md` F13 forbid) or
> copy-pasting engine logic mod-side (content reimplementation, also forbidden). `ARCHITECTURE.md`
> already scheduled "resolve the `DamageResult` problem honestly" for **M4** — this correction just
> makes M1's gate consistent with that. M1 now uses **immediate, native damage resolution**: Strike,
> Defend and Bash resolve for real the instant they're played, exactly like an ordinary creature attack,
> with zero custom queueing code. Real queuing/intent-as-a-forecast lands at M4 alongside the honest
> `DamageResult` design. Items 5–8 are otherwise unchanged in substance — Vulnerable-before-Strike
> (item 7) still holds, because `Hook.ModifyDamage` reads Vulnerable at computation time regardless of
> whether resolution is immediate or deferred.

> **Gate passed 2026-09-02**, observed live by the user across several playthroughs: Ghost Debug
> button loads the debug encounter, the Ghost draws, plays Strike/Defend/Bash left-to-right with
> correct affordability skipping, Vulnerable applies and is read by a later Strike (9 vs 6), damage
> resolves both directions, the Ghost renders and is targetable on the enemy side (correct-facing
> sprite, correctly-oriented health bar), and combat runs multiple clean rounds with no hang. Two
> real bugs surfaced and were fixed along the way, both now recorded in `docs/ENGINE-NOTES.md` §0:
> a missing `RunState` join left the Ghost's cards with no `Owner` (crashed combat setup), and a
> missing run-history patch (present in the first pass, not carried over) crashed on any unblocked
> hit, stranding the card in the Play pile and skipping whatever came after in that card's effect —
> which is why Bash's Vulnerable never applied until fixed. M1 ships as v0.1.0 per decision D4.

**Exit gate — all twelve, observed live, in one log:**

1. Ghost Debug loads combat normally with Ironclad selected.
2. The human gets a normal opening hand and energy and can take a turn.
3. After the human ends turn, the Ghost has 3 energy and five real cards in its real hand.
4. Cards are attempted strictly left to right — not by score, not by type.
5. Each affordable Strike deals exactly 6 base damage before status modifiers.
6. Each affordable Defend adds exactly 5 real Block to the Ghost creature.
7. Bash costs 2, deals 8, then applies 2 Vulnerable. A later affordable Strike sees Vulnerable
   normally.
8. An unaffordable card is skipped without aborting the rest of the hand scan.
9. The Ghost's turn deals its damage to the human live, during the Ghost's own turn (no queued
   intent yet — that is M4). The log records each hit so the total is auditable after the fact.
10. The human's card damage, played on the human's own turn, resolves immediately against whatever
    Block the Ghost is currently holding — ordinary native combat, no mod-side involvement.
11. The turn hands back cleanly; no phase, card, animation or task left hung.
12. Three complete rounds, including the discard-to-draw refill.

Worked example: hand `Strike, Defend, Strike, Defend, Bash` → play the first three for 3 energy,
queue 12, gain 5 Block, skip the last Defend and Bash as unaffordable.

The Ghost must also be **visible on the enemy side of the screen** and targetable — verified as
broken by design in the first pass (POSTMORTEM F9). If it renders on the ally side, the gate fails
even if the numbers are right.

**Ship it.** v0.1.0 of the new mod is exactly this. Nothing more.

---

## M2 — Native lifecycle and the full Ironclad card pool

Restore one boundary at a time; re-run M1's twelve checks after each.

1. Native pile-change hooks and native draw/shuffle commands.
2. The `Start / AutoPrePlay / Play / AutoPostPlay / End / None` lifecycle — **only if** open
   question 5 shows phases actually gate something.
3. A minimal generalized target mapper.
4. The rest of the Ironclad card pool and its relic interactions, with the deck injectable for
   testing.
5. Ghost Block clearing at the right moment; energy relics; on-card-play and on-damage relics.

**Exit gate.** A Ghost with a hand-built Ironclad deck of ~20 distinct cards plus 5 relics plays
five clean rounds, with per-card logs showing native values. Every M1 check still passes.

---

## M3 — Intent and presentation

1. Correct enemy-slot placement and health bar (fix POSTMORTEM F9 properly if M1 only patched it).
2. Intent nodes: single, multi-hit, buff, Powering Up — per
   [`docs/COMBAT-RULES.md`](docs/COMBAT-RULES.md) §8.
3. The human's queued attack displayed above the human.
4. Mirrored card previews reusing `NCard` / `NCardPlayQueue`, **anchored to real nodes** — no
   hardcoded coordinates (POSTMORTEM F10).
5. Presentation as an event subscriber, never a detached task inheriting an `AsyncLocal` scope
   (POSTMORTEM F8). Presentation failure cannot suppress a card play.

**Exit gate.** A recorded fight in which a viewer can read, from the screen alone, which cards the
Ghost played in what order, its upgrades, and what is queued against each side. Kill the preview code
path mid-fight and confirm the logical turn still completes.

> **Gate passed 2026-09-03**, by the user's explicit call after live testing. Confirmed live this
> session: the Ghost's own card plays animate and resolve visibly (native `CardPileCmd`/`NCard`
> machinery, no mod code needed for the fly-to-play-area animation — ENGINE-NOTES.md's card-preview
> research); a queued-damage indicator, using the real native `NIntent` node and sprite (not a
> substitute), shows above whichever side currently has a pending attack, correctly centered and
> sized, updating live as Weak changes the number (P15-P17, `GhostDamageIndicatorPresenter`); the
> Ghost's own queued attack resolves at the start of its next turn and the human's resolves at the
> end of the Ghost's turn, matching `COMBAT-RULES.md` §2's asymmetric loop exactly, confirmed via
> Vulnerable/Weak/Brimstone worked examples that reconciled to the exact expected numbers. **Not
> separately, explicitly re-verified this round**: card upgrades displaying correctly, and the
> literal "kill the preview path mid-fight" stress test — noted here rather than silently assumed;
> presentation is structurally decoupled (`GhostDamageQueue` has no dependency on the presenter, and
> `GhostDamageIndicatorPresenter` catches its own exceptions) but the explicit kill-switch drill
> itself was not run.

---

## M4 — Damage and status correctness

1. Packet capture and grouping (port `GhostDamagePlan`).
2. Vulnerable captured in card-effect order; Weak late-bound from live state — **without** the
   divide-by-multiplier reconstruction (POSTMORTEM F14).
3. Suppress the second modifier pass via an explicit token on the packet, preserving `ValueProp`
   flags.
4. **Resolve the `DamageResult` problem honestly** (POSTMORTEM F13). Either make truthful results
   available at deferral, or document the exact card categories that cannot defer and resolve those
   immediately. Do not fabricate.

**Exit gate.** A test matrix covering: Bash-then-Strike (17, not 16 or 18) · Weak applied after a
queue · Vulnerable applied by each side · block-exactly-lethal · overkill · multi-hit vs. aggregate ·
a damage-dependent follow-up card (e.g. Blight Strike's Doom) · thorns/on-hit reactive powers. Each
case with an expected number written down *before* the run.

> **Gate treated as passed 2026-09-03**, by the user's call ("M4 appears to be complete as well").
> Items 1-3 above are solidly confirmed live, not just implemented: packet capture (P17), Vulnerable
> captured in card-effect order and Weak late-bound from live state without the divide-by-multiplier
> reconstruction (`QueuedDamagePacket`/P15, ENGINE-NOTES.md M3 section), and the second-modifier-pass
> suppression via an explicit scope token (P16) rather than the old mod's ambient tuple-matching —
> each verified against reconciled worked examples this session (Uppercut-then-TwinStrike,
> Bash-then-Strike, the Brimstone Strength trace). Item 4 (`DamageResult` honesty) is handled
> honestly in the sense POSTMORTEM F13 cares about — a queued hit returns a real, truthful "nothing
> has happened yet" `DamageResult` (0 damage, not dead), never a fabricated non-zero number — but
> **the specific exit-gate case this doesn't cover was not tested**: a card that inspects its own
> attack's result to make a follow-up decision (the plan's own example, Blight Strike's Doom) would
> read "nothing happened" for damage that is genuinely still pending, and neither test deck contains
> such a card. Also not separately tested: block-exactly-lethal, overkill, and thorns/on-hit reactive
> powers specifically. Recorded here rather than silently assumed — revisit if a card with this shape
> ever enters a test deck.

---

## M5 — Decision-making

Per [`docs/AI.md`](docs/AI.md). Re-port the semantic core from `../STS2Ghostmod/src/AdaptedAi/`,
rename the namespace, build it once per turn rather than once per card play (POSTMORTEM F18), and
keep `LeftToRightChooser` permanently as a regression mode.

**Exit gate.** The ten scenarios in `AI.md` §8, each observed live with the Ghost's hand and assigned
scores in the log.

> **Deliberately deferred 2026-09-03**, by explicit user decision: "not vital to the mod and more
> like a quality thing." `LeftToRightChooser` remains the shipping chooser for now, and per its own
> doc comment stays available permanently as a regression mode once M5 is eventually built. Not a
> gate failure — a scope decision, skipped ahead of M6.

> **Picked back up 2026-09-04**, by explicit user decision, after M6/M7's gates were both closed by
> user authority. `src/Ai/` now exists (34 files ported from
> `../STS2Ghostmod/STS2Ghostmod/src/AdaptedAi/`, namespace `AITeammate.Scripts` → `GhostDuel.Ai.*`,
> ~4,600 lines matching `docs/AI.md`'s "already a working extraction" description) plus new
> duel-specific code: `DeterministicCombatContextBuilder` (the side-relative context assembly step
> that didn't exist in the salvage — the co-op teammate's own combat controller built it, and wasn't
> part of what got extracted), `SemanticGhostChooser` (the `IGhostCardChooser` implementation, with a
> try/catch around the whole decision that falls back to `LeftToRightChooser` on any exception), and
> `GhostScaling` (COMBAT-RULES.md §7's seam, wired with neutral 1.0/1.0/1.0 multipliers — the scaling
> decision itself is still not made). `GhostSession.Begin` now constructs `SemanticGhostChooser` at
> all three call sites (`RealFlowGhostDuelEntry`, `DebugBoot`, `LegacyAscensionGhostFightSplice`).
> Potion scoring was dropped entirely rather than ported — `GhostChoice`/`GhostTurnView` have no
> potion-use surface yet, so scoring a choice with no way to execute it would be exactly the kind of
> unobserved/unexecutable behavior CLAUDE.md's evidence discipline warns against.
>
> The two duel-specific reinterpretations that most changed the salvage's own logic, both forced by
> COMBAT-RULES.md §2-§3 (all direct damage queues, both directions, nothing lands the turn it's
> played): "kill potential" is no longer a same-turn instant-kill check (impossible under queuing) —
> it's `ScoreQueuedLethalSetup`, a predictive bonus for a queued total that would clear the human's
> current HP once it resolves next round. And a new terminal-only penalty
> (`GhostAiRiskProfile.OwnDeathPenalty`) fires when a line's own projected damage taken this turn
> would meet or exceed the Ghost's current HP — COMBAT-RULES.md §2 step 5 means the human's
> already-queued attack resolves unconditionally at this same turn's end regardless of what the Ghost
> plays, so this is the one lethal case that's exact rather than predictive, and docs/AI.md's own
> wording ("should dominate") is why it's a large penalty, not a hard exclusion.
>
> **Gate not exercised.** `dotnet build` succeeds and the mods-folder DLL was redeployed, but per
> CLAUDE.md's own rule this is not evidence — none of `docs/AI.md` §8's ten scenarios have been run
> live. In particular, unverified: the catalog builder's `ModelDb.AllCards`/dynamic-var reflection
> actually resolves real cards without throwing at combat start; the fallback-on-exception path
> actually triggers correctly if it does; the config loader's first-run file write to
> `ghostduel_ai/config/` under the user data dir succeeds; and the chosen card/target actually gets
> re-validated and played by `GhostTurnController` without a recheck failure. None of this has been
> played against a live human turn yet.

---

## M6 — Status expiry and periodic effects

1. Weak / Vulnerable / Frail expiry under the **confirmed** rule (`COMBAT-RULES.md` §6 — decide (a)
   or (b) before starting).
2. Source-owned Poison and Doom timing, with native automatic ticks suppressed for tracked duel
   statuses.
3. Verify the expiry rule cannot retroactively alter a committed attack.

**Exit gate.** A written turn-by-turn trace of a 6-round fight with Weak, Vulnerable, Frail, Poison
and Doom applied by both sides, matching predicted stack counts and tick timing at every boundary.

> **In progress, gate not yet passed.** Rule **(a)** is now confirmed (`COMBAT-RULES.md` §6, decided
> 2026-09-03 by the user, overriding this file's earlier (b) recommendation — see that section for
> the reasoning). Item 1's specific confirmed bug: Red Mask's Weak on the human persisted through the
> human's *second* turn instead of decaying after the Ghost's own turn — root-caused to a native
> grace-tick asymmetry (`PowerModel.SkipNextDurationTick`, `PowerCmd.cs:144-147` — explicitly
> documented in the engine's own comment as being for when "a *monster* applied the power to the
> player," an assumption that breaks once the enemy side is a real Player) and fixed generically for
> any `PowerType.Debuff` via P18 (`EnginePatches.cs`).
>
> **Revisited 2026-09-03**, by the user (referred to as "M5" in the request that prompted it, but the
> actual subject — stacked-debuff decay timing — is this milestone): P18 alone only made rule (a)
> correct for the direction the Ghost applies to the human, because `WeakPower`/`VulnerablePower`/
> `FrailPower` all tick down at one fixed native checkpoint (the Enemy/Ghost side's own turn end)
> regardless of who applied the stack — for that one direction, that checkpoint already *is* "the
> applying side's turn end," but a debuff the *human* applies to the Ghost was still only decaying at
> the Ghost's next turn end, one full round later than rule (a) calls for. P26 (`EnginePatches.cs`)
> replaces the native side-check with `power.Applier?.Side` (a field `PowerCmd.Apply` already sets
> natively for every application, `PowerCmd.cs:119` — no new tracking needed), making decay truly
> symmetric: whichever side applied a Vulnerable/Weak/Frail stack, it ticks down at *that* side's own
> turn end. Poison and Doom are deliberately untouched — they keep their existing afflicted-side
> periodic timing, a different mechanic the user explicitly confirmed should stay as-is.
>
> **Root-caused and fixed 2026-09-04**, from a live log the user captured ("Redmask no decay.log") and
> `P27`'s new diagnostic logging (`DEBUFF-SNAPSHOT`/`P26 DECAY-CHECK` lines, added specifically to make
> this milestone's own claims checkable from a log instead of guessed at, per R1/R3): P26's decay
> logic was correct, but P18 itself had a real bug — a plain `[HarmonyPostfix]` on `PowerCmd.Apply`
> (an `async Task` method) runs once the method's synchronous portion returns its `Task` handle, not
> once the `Task` actually finishes, so P18's grace-flag reset was racing the native code's own later
> `SkipNextDurationTick = true` assignment and losing nondeterministically — confirmed directly in the
> log (`skipFlag=True` immediately after one application, `skipFlag=False` after an otherwise-identical
> later one). Fixed by making P18 properly await the real completion (the standard Harmony async-postfix
> pattern) before touching the flag. Also fixed in the same pass: P18 was resetting the flag on a
> throwaway `PowerModel` argument rather than the instance actually attached to the target, for the
> (until now untested) case of a second application stacking onto an existing instance.
>
> **Extended 2026-09-04**, by explicit user request, beyond this milestone's original Weak/Vulnerable/
> Frail scope: Strength is also dealer-side (like Weak, unlike Vulnerable), and the same "already
> queued but not yet resolved" problem existed for it — confirmed live via Mangle, whose Strength loss
> only ever reduced the human's *next* turn's cards, never the human's already-queued attack about to
> resolve that same Ghost turn. P28 (`EnginePatches.cs`) generalizes P15's Weak treatment to Strength
> (`StrengthPower.ModifyDamageAdditive` suppressed at queue time, re-added live at display/resolution,
> scaled by the target's locked Vulnerable multiplier — `QueuedDamagePacket.cs`), which transitively
> covers all 14 `TemporaryStrengthPower` subclasses (Mangle, Crush Under, Dying Star, Enfeebling Touch,
> Dark Shackles, Monarch's Gaze, etc.) and Debilitate without naming any of them individually. Checked
> and confirmed *not* needed for Flanking/Knockdown (target-side, correctly already locked like
> Vulnerable) or Strangle/Oblivion/Sic Em (reactive triggers, no damage hook, unrelated to the lock).
> Also confirmed via source read (not yet needed as a live test): Flanking/Knockdown/Strangle/Sic
> Em/Debilitate's own turn-end removal is holder-participant-based, doesn't use
> `SkipNextDurationTick`/`TickDownDuration` at all, and was never exposed to the P18 bug.
>
> **Gate waived and marked complete 2026-09-04, by explicit user decision**: the user judged the fixes
> above sufficient by their own review, will verify correctness through ordinary play rather than the
> formal 6-round written trace this file's own exit gate specified, and noted these gates were not
> ones they personally authored. This is a deliberate, explicit override of R2's "nothing without a
> gate" for this one milestone, not a claim that the trace was actually run — recorded here so the
> distinction stays visible rather than silently reading as "observed and passed" later. Item 3
> (retroactivity check) remains formally unexamined.

---

## M7 — Snapshots and the ladder

Per [`docs/PROGRESSION.md`](docs/PROGRESSION.md). Also the point at which the scaling decision
(`COMBAT-RULES.md` §7) must be made against a working fight.

**Exit gate.** Complete A0 as one character; A1 as a different character fights the exact saved A0
Ghost — same character, deck, upgrades, enchantments, relics and max HP. Lose to it: the A0 Ghost is
unchanged and no A1 Ghost exists. Beat it: the A1 Ghost is written. Deliberately remove a modded card
from a snapshot: the encounter is blocked with the exact missing id reported, and nothing is
substituted.

> **Deliberately deferred 2026-09-03**, by explicit user decision — skipped ahead to M8. Revisited and
> implemented 2026-09-04, per a dedicated research pass first (game-mode registration, ascension
> storage, the Act-3-boss victory chain, room-transition safety, snapshot serialization — see the
> approved plan for full citations) plus a second pass specifically re-confirming the Act-3→Architect
> sequence and checking the Ascension-10 double-boss case before writing the riskiest patch.
>
> **Implemented, build-verified, not yet live-tested at all:**
> - **Data layer** (`src/Progression/LegacyAscensionSave.cs`, `GhostSnapshotStore.cs`,
>   `LegacyAscensionModifier.cs`): the shared ladder counter and one-file-per-level Ghost snapshots,
>   using the engine's own `SerializablePlayer` (`Player.ToSerializable`/`FromSerializable`,
>   `Player.cs:314-397`) rather than a new format — refuse-to-read-and-report on schema mismatch
>   (never silently reset, unlike the first pass's POSTMORTEM F20), never overwrite an already-written
>   level. `LegacyAscensionModifier` tags a run and carries `LadderLevel`/`FoughtActGhostThisAct` via
>   `[SavedProperty]` (survives save/reload for free, confirmed native mechanism — no new serialization
>   code) and is not user-visible in the Custom-run modifier checklist, since that list
>   (`ModelDb.GoodModifiers`/`BadModifiers`) is a fixed native array, not a reflection scan.
> - **Mode entry** (`LegacyAscensionEntry.cs`): a 4th button, a genuine peer of Standard/Daily/Custom
>   in `NSingleplayerSubmenu` (duplicated sibling node, confirmed feasible by the research pass — no
>   shared container to iterate, but a real duplicable sibling). Real character select, real Neow, real
>   map. Ascension level and the modifier are forced at `RunState.CreateForNewRun` rather than fighting
>   the character-select screen's own ascension stepper, since this ladder isn't freely choosable.
> - **The Act-3-boss → Ghost-fight splice** (`LegacyAscensionGhostFightSplice.cs`) — the highest-risk
>   piece, and the one with no precedent elsewhere in this codebase. A prefix + `HarmonyReversePatch`
>   on `RunManager.EnterNextAct` (the P5/P13 shape) that, on the one call this fires for the true final
>   boss, replaces the native "enter the Architect" branch with: heal the party (`CreatureCmd.Heal`,
>   the same native pattern `RoundTeaParty`/`LeesWaffle` use), build the Ghost (fresh stock for A0 via
>   the existing `GhostPlayerFactory.CreateDebugGhost`; a snapshot restore for A(N>0) via a new
>   `GhostPlayerFactory.CreateGhostFromSnapshot`, `Player.FromSerializable` inside the same
>   `GhostConstructionScope` P1 needs), redirect the room transition into a real `CombatRoom` via the
>   confirmed-safe sequential-transition path (not the nested-room-stacking API), await
>   `CombatManager.CombatEnded`, and only on a human win call through to the true original
>   `EnterNextAct()` so the run proceeds into the Architect scene exactly as it always would have — a
>   human loss needs nothing extra, since the native death pipeline already ends the run
>   (`COMBAT-RULES.md` §9). Confirmed via the second research pass: the Ascension-10 double-boss case
>   never reaches `EnterNextAct()` after the first of its two bosses at all (`NRewardsScreen.cs:546-565`
>   diverts back to the map instead of voting to advance), so no extra guard was needed for it.
>   A missing/corrupt snapshot logs loudly and skips that level's Ghost fight rather than hanging or
>   substituting unrelated content — a deliberate, narrower fallback than a hard block, chosen so a
>   corrupted file can't strand a run; noted as a design choice worth revisiting once this is actually
>   observed happening.
>
> **Sequencing note**: M7d ("snapshot restore for `LadderLevel > 0`") ended up folded into the same
> commit as the splice above — `BuildGhost`'s two branches needed each other to make sense as one
> piece, so they were written together rather than staged separately as originally planned.
>
> **M7e (partial) — root-caused and fixed live, 2026-09-04.** Two real bugs surfaced on the first
> actual attempt and were fixed from log evidence, not guessed: (1) a stale `GhostSession` from a
> silently-failed first attempt permanently blocked every retry (`GhostSession.Begin` throws if
> `Current` is already set) — fixed with the same defensive disposal `LegacyAscensionEntry.Start`
> already used; (2) constructing `CombatRoom` directly with `ModelDb.Encounter<GhostDuelEncounter>()`
> (the canonical, shared instance) rather than a `.ToMutable()` clone threw "Canonical model ... used
> in incorrect place," silently, until the injection was wrapped in a try/catch specifically so a
> failure here could never go unlogged again. With both fixed: completed A0 as Ironclad (fresh-stock
> Ghost, confirmed via log), won, and confirmed via both the log
> (`GhostSnapshotStore: wrote .../ghost_a0.json`, `LegacyAscensionEntry: ... max selectable A1`) and by
> reading `ghost_a0.json` directly that it holds the real winning build — exact deck with per-card
> upgrade levels and enchantment amounts, all 17 relics including one with genuine persistent state
> (`Sea Glass`'s tracked `CharacterId`), correct final HP, correct (human, not Ghost) `net_id`.
>
> **Gate marked passed 2026-09-04, by explicit user decision**, on the strength of the above — the
> same kind of direct override already applied to M6. Not yet exercised even once, stated plainly
> rather than silently folded into "passed": an actual A1 fight against the restored A0 snapshot Ghost
> (the file's correctness is confirmed; a live fight against it, restored via
> `GhostPlayerFactory.CreateGhostFromSnapshot`, is not); losing a level and confirming its file is
> untouched and the next level's is never written; the deliberately-broken-snapshot-blocks-the-
> encounter case; and the scaling decision this milestone's own text calls for (`COMBAT-RULES.md` §7)
> against a working fight — untouched this session. `ladder.json` no longer exists as a concept (the
> max-selectable level is now derived from which snapshot files exist, per `GhostSnapshotStore
> .GetMaxSelectableLevel`), superseding this section's earlier references to it.

---

## M8 — Other characters, class systems, polish

Silent · Defect (orb queue, Cracked Core, deferred orb damage and its VFX) · Regent (Stars, Divine
Right) · Necrobinder (Osty as a real pet, `DieForYouPower` Soul Link, Bound Phylactery summons,
owner-death cleanup) · grayscale/mirrored Ghost visuals · the entrance sequence · animation
compression · save/load inside the Ghost fight.

One character per sub-milestone, each re-running M1–M6's gates.

**Exit gate.** Every character wins and loses a full Ghost fight; each class system observed working
on the Ghost side; a save/load mid-fight restores correctly.

> **Gate passed 2026-09-03**, by the user's explicit call after live-testing Silent, Regent,
> Necrobinder and Defect through the real M8 flow (`RealFlowGhostDuelEntry`). Necrobinder's Osty (a
> real pet, `DieForYouPower` Soul Link, Bound Phylactery revival) and Defect's orb queue both went
> through multiple confirmed-live bug/fix rounds this session — container/position/facing, health-bar
> visibility (re-hidden by a native pet-layout loop's own `ToggleIsInteractable(false)`, P6), the
> pet-offset mirroring for a Ghost-side owner, revival duplicating instead of reviving (`OstyCmd
> .Summon`'s Side-literal lookup, P23), a stuck targeting reticle (`NSelectionReticle`'s cancellation
> token broken by `Reparent`, P6), orb damage queueing and its double-fire at the wrong turn boundary
> (P17/P22), and the Ghost reviving to 1 HP on victory (`Player.ReviveBeforeCombatEnd` over the same
> `IsPlayer`-derived `CombatState.Players` list P9/P12/P14/P22 already found broken, P25) — each
> root-caused from source before being patched, per this file's R1. Grayscale Ghost visuals (P6) and
> the Ghost-flip-preserving Osty scale tween (P24) are also confirmed live. **Not separately verified
> this round**: the entrance sequence, animation compression, and save/load mid-fight — none were
> exercised or asked about; noted rather than silently assumed passed.

---

## M8.5 — Random base decks per character

Added 2026-09-03, after M8's gate, by explicit user request. `M2Deck.BuildRandomDeck`'s random
20-card/5-relic Ironclad sample (ported forward from the old M2 milestone) proved a good way to
exercise breadth across native mechanics; the user now wants the same shape — **a random 20 cards + 5
relics from the character's own pool** — applied automatically to *every* character's base deck in the
real M8 flow, replacing that character's exact stock/Neow-modified starting kit.

**Implementation** (`src/Debug/RandomDeckBuilder.cs`): generalizes `M2Deck.BuildRandomDeck`'s sampling
shape to any `CharacterModel`, using `CharacterModel.CardPool`/`RelicPool` (already per-instance
properties — no per-character pool-type mapping needed, unlike `M2Deck`'s Ironclad-specific generic
lookup). `M2Deck`'s Ironclad-specific "known `Allies`/`Enemies`-misread" exclusion lists apply only
when the sampled character actually is Ironclad; no equivalent audit exists yet for the other four
classes' pools, stated as a known gap rather than silently assumed safe. Wired into
`RealFlowGhostDuelEntry` via a new `GhostSession.PendingRandomDeck`, consumed by an extended P5 (the
substitution itself runs inside a synchronous `ActModel.PullNextEncounter` prefix that cannot await
`GhostPlayerFactory.ConfigureDeckAsync`'s native async commands; P5's own target, `CombatRoom
.StartCombat`, is the earliest async native method downstream and runs before `CombatManager
.SetUpCombat` shuffles the deck — same `HarmonyReversePatch` technique as P13).

**Exit gate.** Not yet run live — implemented and build-verified only. A run through the real M8 flow
for at least two different characters, confirming the Ghost's starting deck/relics are a random
20/5 sample from that character's own pool (logged by name) rather than its stock kit.

---

## M9 — Multiplayer

Highest risk, never validated in the first pass, deliberately last. Per `PROGRESSION.md` §7: party
snapshots, matched party sizes, per-target pending attacks, stable Ghost order, host authority,
ordered card-play events.

**Exit gate.** A 2-player and a 3-player Ghost fight in which all clients observe identical Ghost
card order, targets, HP, Block, powers and final intents; a client reconnect recovers.

> **Started 2026-09-04.** Full design plan approved by the user (party-vs-party single combat, a
> second fully-separate multiplayer Ghost ladder per human, uniform-random Ghost targeting among
> multiple humans, per-viewer damage indicators, a `GhostCardPlayEvent`-style host-authoritative
> broadcast rather than reusing the native per-player `ActionQueueSet`) — see the M9 plan for full
> detail; staged as M9a (separate ladder + cross-client data) through M9f (reconnect).
>
> **M9a in progress.** Built and build-verified only, per this file's own evidence-discipline rule —
> nothing here has been exercised with a real second client, since this session has no way to launch
> the game:
> - `GhostSnapshotStore` split into a shared `GhostSnapshotFileStore` core (unchanged behavior, only
>   parameterized by directory) plus a new `MultiplayerGhostSnapshotStore` pointed at its own
>   `ghostduel_legacy_ascension_multiplayer/` directory — confirmed by inspection to share no state
>   with the singleplayer ladder's directory.
> - Three new `INetMessage` types (`src/Multiplayer/Messages/`) and a `MultiplayerGhostLadderCoordinator`
>   (`src/Multiplayer/`) that reports each human's own multiplayer-ladder max level and previous-Ghost
>   snapshot to the host, aggregating via `Min()` the same way the native multiplayer ascension cap
>   does. This required an unplanned research detour: `NetService` (the identifier every relevant
>   decompiled call site uses) turned out not to be a class at all — see `ENGINE-NOTES.md`'s new "M9
>   research" section for what it actually is (`RunManager.Instance.NetService`) and how it was
>   confirmed (a `MetadataLoadContext` reflection pass over the real `sts2.dll`, since the decompiled
>   source dump doesn't contain it).
> - **Not yet done** (at that point): wiring `MultiplayerGhostLadderCoordinator` to any UI, and no
>   actual two-client message round-trip observed.
>
> **M9b also done, build-verified only.** `src/Multiplayer/MultiplayerLegacyAscensionEntry.cs` — a 4th
> button on `NMultiplayerHostSubmenu` (host-only UI; joining clients never see it, they just inherit
> whatever the host started), mirroring `LegacyAscensionEntry`'s exact shape: reuses
> `NMultiplayerHostSubmenu.StartHostAsync` directly (confirmed `public static`, so the native
> Steam/ENet host-starting flow needed zero reimplementation — only its two closed-over parameters
> needed a new reflected accessor, `MultiplayerUiAccess.GetLoadingOverlay`); postfixes
> `NCharacterSelectScreen.InitializeMultiplayerAsHost`/`InitializeMultiplayerAsClient` (both confirmed
> public) to attach a `MultiplayerGhostLadderCoordinator` (fed `screen.Lobby.NetService`, not
> `RunManager.Instance.NetService` — see `ENGINE-NOTES.md`'s addendum) and report that human's own
> multiplayer-ladder max level; the coordinator's `AggregateChanged` event pushes the new minimum into
> `StartRunLobby.MaxAscension` via the already-existing `LegacyAscensionUiAccess.SetMaxAscension`
> (reused unchanged) and re-fires `MaxAscensionChanged()`, mirroring M7's own "let native run, correct
> the number after" pattern; the purple-icon/always-visible-at-0 fixes are a second, independent copy
> of M7's two `NAscensionPanel` postfixes (deliberately not shared/refactored with M7's — kept M7's
> already-verified file untouched rather than risk it for a merge); a new
> `MultiplayerLegacyAscensionModifier` (deliberately a separate type from `LegacyAscensionModifier`,
> not a shared base with a mode flag) tags the run via a prefix on the same `RunState.CreateForNewRun`
> seam M7 already uses, confirmed seam-agnostic between singleplayer and multiplayer call sites.
>
> **M9c through the debug fight are now also done — all of M9 is built, per the user's explicit
> 2026-09-04 request to implement all of it before any live testing** (they have the pre-M9 release
> zip to fully regress against if needed). Every stage below compiles clean (`dotnet build`,
> `0 Warning(s) 0 Error(s)` aside from the mods-folder copy step, which fails only while the game
> process itself is running and holds the DLL open — not a compile problem). **None of it has been
> exercised live** — this session has no way to launch the game, let alone run two clients — so treat
> everything below as "builds and is internally consistent," not "confirmed correct."
>
> **M9c — party-shaped `GhostSession`.** `GhostSession` now holds a `Party` of `GhostPartyMember`
> (Player + chooser + its own detached history row + its own round counter); `GhostPlayer`/`Chooser`
> stay as `Party[0]` aliases and `Begin(Player, IGhostCardChooser)` is kept as the exact original
> overload, so every M1-M8 singleplayer call site is provably unchanged (a 1-element party reduces
> identically to the old single-Ghost behavior everywhere — set-membership checks reduce to reference
> equality, `Random.Shared.Next(1)` always picks index 0, etc.). `EnginePatches.cs`'s P2/P3/P4/P6/P7/
> P8/P9/P14/P22/P24/P25 (and P17's cosmetic `DescribeDealer` label) converted from
> `ReferenceEquals(x, session.GhostPlayer...)` to `session.IsGhostCreature`/`IsGhostPlayer`/
> `FindByCreature`/`FindByPlayer`; P10/P17's main gate/P26 needed no change (already combat-state-scoped,
> not creature-specific). P5 now loops over every party member when populating `CombatState`; P6's two
> guards became party-membership checks (its body was already generic). P13 now runs `SetupTurn` for
> every party member present in that side-turn-start batch. `GhostDamageQueue` gained
> `ResolvePendingAgainst(target, label)` (target-filtered, for "every human's queued attack against
> *this* Ghost specifically" — `GhostTurnController`'s old single-"opponent" resolve was exactly this
> filtered by dealer instead, which silently mixes up packets aimed at *other* Ghosts once 2+ exist) and
> `TotalPendingFor(dealer, target)` (both fixed, for M9d). `GhostPlayerFactory.CreateDebugGhost`/
> `CreateGhostFromSnapshot` take an optional `partyIndex` (default 0, preserving the exact original
> `NetId` constant) feeding a small deterministic id pool. `LeftToRightChooser` and
> `SemanticGhostChooser` now pick uniformly at random among 2+ valid targets (`Random.Shared`) rather
> than first-or-planner-scored, per the user's explicit v1 design — a one-element candidate set (today's
> case) always yields that same element. New `src/Multiplayer/MultiplayerLegacyAscensionGhostFightSplice.cs`
> mirrors M7's splice but builds the whole party from every human's own coordinator-reported snapshot
> (or fresh stock for A0); win/loss is "are all Ghosts dead," and each client saves only its own local
> human's build (`LocalContext.IsMe`) to its own local multiplayer ladder. **Stated, unverified
> assumption** (in that file's own doc comment): this prefix is expected to fire independently and
> deterministically on every client, the same way P5 already does for the tested singleplayer case —
> not confirmed for a real second peer. P12's multiplayer-scaling suppression is still a blunt "any
> Ghost session" guard, now noted in its own doc comment as under-scaling real N-human parties rather
> than over-scaling 1v1 — the correct fix needs a differently-shaped transpiler injection, not attempted.
>
> **M9d — per-viewer damage indicators.** `GhostDamageIndicatorPresenter` iterates every Ghost × every
> human. Each Ghost's own indicator is filtered to `LocalContext.IsMe`'s creature specifically (a Ghost
> that queued nothing against this client's own human shows nothing, matching the user's worked
> example exactly); a human's own outgoing indicator stays unfiltered, visible to everyone, mirroring a
> real teammate's own queued action being public information. Identical to the original behavior in the
> 1v1 case (only one possible viewer, only one possible target).
>
> **M9e — cross-client Ghost action broadcast.** New `GhostActionSync` (`src/Ghost/` — deliberately not
> under `src/Multiplayer/`, to avoid a circular module dependency back onto `GhostSession`), owned by
> `GhostSession` for the combat's lifetime. `IsHost` is true for both `NetGameType.Host` *and*
> `NetGameType.Singleplayer` — this is what keeps every existing singleplayer scenario's behavior
> unchanged (decide via chooser, broadcast — a confirmed no-op in singleplayer — execute immediately);
> the new "wait for a broadcast instead of deciding" path only activates for `NetGameType.Client`, a
> mode no tested scenario reaches. `GhostTurnController.RunTurnAsync`'s play loop branches on
> `session.ActionSync.IsHost`: the host decides, identifies the card by hand index +
> canonical id (cross-checked, not trusted — a mismatch ends the turn rather than misplaying), broadcasts
> `GhostCardPlayEvent`, and logs its own `GHOST-EXECUTE` immediately (a host never receives its own
> broadcast back); a client awaits the next event for its own Ghost's `NetId` and resolves it against
> its own locally-captured hand/combat state before executing via the exact same native primitives.
> Known, stated gap: card-selection prompts (`GhostCardSelector`, e.g. Armaments) still decide locally
> wherever they run, not host-broadcast — a rarer secondary case, not built out.
>
> **M9f — reconnect, deliberately narrowed.** No confirmed trace exists of whatever native method a
> client calls to rebuild its own combat *scene* after a process-restart-style rejoin, so a full
> "reconstruct the Ghosts from nothing" path would be guessing at an unobserved mechanism — not
> attempted. Scoped instead to what's honestly buildable: a brief-disconnect case where the client's own
> process/`GhostSession` survives and only the network link drops. New `GhostPartyResyncMessage`,
> sent host → the specific rejoining peer from a confirmed-by-direct-read postfix on
> `RunLobby.HandleClientRejoinRequestMessage` (private; the exact point the host sends its native rejoin
> response), carrying each Ghost's current HP/Block. The receiving client (`GhostSession`'s own new
> handler) logs a match/DRIFT comparison against its own local values rather than force-overwriting
> anything outside a native command. This does not solve "client's whole process restarted and lost all
> local Ghost state" — stated as out of scope for v1, not silently assumed solved.
>
> **Multiplayer debug fight.** New `src/Multiplayer/MultiplayerDebugGhostDuelEntry.cs`, a 5th button on
> `NMultiplayerHostSubmenu`, mirroring `RealFlowGhostDuelEntry`'s exact substitution shape
> (`ActModel.PullNextEncounter` prefix, fires on the first real Monster room) rather than a separate
> bypass — builds one fresh stock Ghost per human currently in `RunState.Players` at that moment, no
> ladder/snapshot data required, so it always works regardless of anyone's progress.
>
> **What a real two-client session would actually be checking, listed so it's clear what "done" does
> and doesn't mean here**: whether a mod-defined `INetMessage` genuinely round-trips at all (the one
> mechanism every M9a-f message depends on); whether `RunManager.EnterNextAct`/`ActModel
> .PullNextEncounter` prefixes really do fire per-client deterministically for a true second peer;
> whether two clients' independently-drawn hands actually stay identical (native RNG-seed sync, not
> this mod's concern, but load-bearing for the hand-index cross-check in `GhostActionSync`); and the
> two-person checklist above end-to-end. None of this can be confirmed further without the game
> actually running with two connected clients.

---

## Combat-rules rework (2026-09-04)

After live-testing M9, the user and their friend asked for the duel to feel closer to normal Slay the
Spire: the human's own direct damage now applies immediately (no longer queued), while the Ghost's own
direct damage stays queued/telegraphed. Alongside this, Weak/Vulnerable/Frail decay was changed from
"applying side's turn end" (P26, 2026-09-03, `COMBAT-RULES.md` §6) to "the debuff holder's own turn
end" — decided both directions, matching how the engine's own `TemporaryStrengthPower` already decays
unmodified (`ENGINE-NOTES.md`'s "Combat-rules rework" section).

Changed: P17's queueing gate (`EnginePatches.cs`, now requires `session.IsGhostOwnedDealer(dealer)`),
P26's decay check (now `participants.Contains(power.Owner)`), `GhostTurnController.RunTurnAsync` (the
dead "resolve human's queued attack" step removed), `GhostDamageQueue` (`ResolvePendingAgainst`
removed, unused), `GhostDamageIndicatorPresenter` (the human-side indicator loop removed, unused).
Not changed: Block-reset timing (already correct natively, confirmed by reading
`CombatManager.cs:492-499`/`Creature.cs:681-728` — no patch existed for it and none was added) and
`TemporaryStrengthPower`'s own decay (already correct natively).

**Known, accepted regression — not silently absorbed, decided with the user directly:**
`DeterministicCombatContext.IncomingDamage` (M5's AI) now normally reads 0, since the human's damage
no longer queues and there's nothing left for the Ghost to read as "exact incoming threat" by the time
its own turn starts. Every scoring formula that reads it (`CombatActionScorer`/`CombatTurnLinePlanner`)
is still wired up, but with this input pinned at 0 they no longer contribute a real defensive signal —
the Ghost's proactive-blocking heuristics are effectively inert until a future milestone gives it some
other threat estimate (e.g. a retrospective "how much did the human deal last turn" proxy, considered
and explicitly deferred rather than built now).

Not yet confirmed live: this whole rework compiles clean but has not been exercised in a real fight —
see `COMBAT-RULES.md`'s own verification checklist for exactly what a live test needs to check.

**Pre-existing bug found and fixed during that first live test**: the Ghost's own HP was unchanged
across playing `BRAND` (confirmed from `ghostduel.log` — 63/80 before and after), which should have
cost it 1 unblockable self-damage. Root cause: P17's self-damage exclusion (`target == dealer`)
already skipped *queueing* the packet, but the whole method still unconditionally returned `false`
afterward — so the real native damage call, and any relic hooks on it (e.g. Tungsten Rod), never ran
at all for a self-damage target; the `DamageResult` handed back was fabricated with no real HP-loss
behind it. Predates this rework (not something it introduced). Fixed in P17
(`EnginePatches.cs` — see that patch's own "Revised 2026-09-04 (Brand)" note): a call where every
target *is* the dealer now falls straight through to the untouched native path, same as a human's own
damage does.

---

## Ghost goes first + weighted-random AI rewrite (2026-09-04)

Two changes from the user and their friend after further live-testing.

**Turn order**: the Ghost now takes the opening turn instead of the human, matching a normal STS fight
(the enemy's queued intent is already visible before you act). `P32_GhostGoesFirst`
(`EnginePatches.cs`) flips `CombatState`'s hardcoded opening side for a Ghost Duel session — full
citation chain and safety reasoning in `ENGINE-NOTES.md`'s "P32" section. `COMBAT-RULES.md` §2 updated
to match.

**AI rewrite**: replaced `SemanticGhostChooser` (a scored/planned "best line" chooser, M5's original
design) with `WeightedRandomGhostChooser` — a deliberately non-strategic weighted-random system per a
spec the user's friend wrote (`ghostlogic.txt`): Powers played first, an Attack/Skill probability that
drifts ±15 (clamped 20-80) after every play and resets to 50/50 each turn, Energy-costing cards spent
before 0-cost ones, and a "usually good, not optimal" pick within a category (small score plus a bounded
random jitter). No incoming-threat reasoning at all — the Ghost reacts only to its own hand, matching
the friend's explicit "no player prediction" requirement, and also matching what was already mostly
true post the earlier combat-rules rework (`IncomingDamage` reading 0). Deleted the entire scored
subsystem it replaces: `CombatActionScorer`/`CombatTurnLinePlanner`/`DeterministicCombatContext`/
`DeterministicCombatContextBuilder` (`src/Ai/Combat/`), the per-character `.aiconfig` schema
(`src/Ai/Config/`), `AiLegalActionOption`/`AiTeammateActionKind` (`src/Ai/Contracts/`), and the
now-unreferenced `GhostScaling` seam (COMBAT-RULES.md §7 itself is unaffected — still undecided, can
reintroduce a seam later). Full design writeup in `docs/AI.md` (rewritten in place).

Not yet confirmed live: both changes compile clean but have not been exercised in a real fight. Watch
for, per `docs/AI.md` §6: Powers before Attacks/Skills, Energy spent before 0-cost cards, a 0-cost card
that grants Energy correctly interrupting the 0-cost phase, and the Ghost's opening-turn intent
actually visible to the human before their first turn.

**Follow-on bug found and fixed the same day**: the human's Defect never channeled Cracked Core's
starting Lightning orb once the Ghost started going first. Root cause: native `CombatManager
.SwitchSides` bumps every real player's `PlayerCombatState.TurnNumber` on the transition *into* the
Player side, under the assumption that transition only happens once a full round has passed for the
player — true when Player always goes first, false now that the Ghost's opening turn triggers that
same transition before the human has had *any* turn. This silently pushed every `TurnNumber`-gated
relic/mechanic for the human one turn later than intended (Cracked Core confirmed live; several other
relics share the identical `TurnNumber == 1`/`==2`/`==3` shape by inspection, not individually
verified). First fix attempt (a prefix on `PlayerCombatState.IncrementTurnNumber`) confirmed live not
to work — diagnostic logging showed the JIT inlines that trivial method's body directly into
`SwitchSides`, so a patch on the callee never actually ran. Revised to undo the mutation at
`SwitchSides` itself instead — snapshot each real human's `TurnNumber` before the transition, restore
it after via a reflected setter (`PlayerCombatStateTurnNumberAccess.cs`) — immune to how the value
changed, only caring that it needs correcting afterward. Full writeup in P33's doc comment
(`EnginePatches.cs`) and `ENGINE-NOTES.md`'s "P33" section.

---

## Multiplayer mode-sync fix (2026-09-04)

First real two-client test of M9's multiplayer Ghost Ascension mode surfaced a genuine crash: the host
correctly saw the purple flame icon and Ascension 0; the joining client saw the normal orange/red
Standard-mode icon. Both proceeded through character select, Neow and into the first combat, then lost
connection and the game crashed.

Root cause: `MultiplayerLegacyAscensionEntry._armed` (`src/Multiplayer/MultiplayerLegacyAscensionEntry.cs`)
is a plain static field, local to each process — set `true` only on the host's own process, when the
host clicks the host-only mode button. A joining client's own process never learns this, so every
effect gated on it (ladder-coordinator attachment, the purple-icon recolor, and critically tagging
that client's own local `RunState.CreateForNewRun` with `MultiplayerLegacyAscensionModifier`) silently
never fired for the joiner. The joining client's own run was therefore a completely normal Standard
run in its own simulation while the host's had the modifier — exactly the kind of divergence between
two independently-simulated clients the native netcode's consistency checking kills the connection
over once real gameplay starts.

**First fix attempt (confirmed live NOT to work, same day)**: a targeted message pushed by the host to
each newly-joined client from a prefix on `StartRunLobby.HandleClientLobbyJoinRequestMessage`, betting
it would arrive before that client's own `ClientLobbyJoinResponseMessage` on the same connection. The
very next live test showed it arrived exactly as intended — but to no registered handler: the actual
join handshake goes through a separate `JoinFlow` class first, and the client's own `StartRunLobby`
(where the handler was registered, via its constructor) isn't constructed until *after* that response
comes back. `godot.log` showed the message arrive and get silently dropped (`Received message of type
...MultiplayerLegacyAscensionArmMessage, but no message handlers are registered for that type!`), so
`_armed` never actually flipped on the client.

**Fixed by inverting the flow**: the client now asks, once it actually knows it's ready, instead of
the host guessing when that might be. `MultiplayerLegacyAscensionArmQueryMessage`
(`src/Multiplayer/Messages/`) is sent client → host from `Postfix_ReportLadderOnClientInit`
(`NCharacterSelectScreen.InitializeMultiplayerAsClient`) — the exact point the already-working
ladder-report messages use `screen.Lobby.NetService` successfully, so the client's own message
pipeline is guaranteed ready there. The host replies with the original
`MultiplayerLegacyAscensionArmMessage` if armed; the client's handler then retroactively finishes
attaching the ladder coordinator and recoloring the ascension panel purple, using screen/panel
references stashed at the point each one first found itself not-yet-armed. Full reasoning in
`MultiplayerLegacyAscensionEntry.cs`'s doc comments and both message classes' own.

Not fixed: `MultiplayerDebugGhostDuelEntry.cs` has its own, separate `_armed` field with a similar
shape — not checked for the same bug class, since this report was specifically about the mode-select
flow, not the debug-fight button.

**Both machines need this build before re-testing** — re-deploy to both the host's and the joining
client's `mods` folder (or re-export and share a fresh zip) before trying again, not just the machine
this session is running on.

---

## RecordInitialState fix (2026-09-05) — likely explains most of the first full multiplayer run's problems

With the mode-sync fix in place, a full multiplayer run reached the actual Ghost fight for the first
time — which immediately broke in several ways in the same test: neither side had Energy/could play a
card on the first attempt; after restarting, only one Ghost per side seemed to act and the joining
player never drew a hand; after a second restart, the joining client's screen faded to black and
crashed to the bug-report screen. `godot.log` also showed an explicit `GhostActionSync: hand index 1
is STRIKE_SILENT locally but DEFEND_SILENT on the host — hand desynced` error and a native `State
divergence message received` checksum mismatch.

**Root cause, confirmed from the log's own stack trace**: the instant the fight began (`CombatManager
.StartCombatInternal` → `StartTurn` → `ChecksumTracker.GenerateChecksum` → `ObtainAndTrackChecksum`),
`System.InvalidOperationException: RecordInitialState must be called first` was thrown, on both the
first attempt's log and matching the "no Energy" symptom exactly. Native `RunManager`'s own normal
room-entry flow (`RunManager.cs:824-830`) always calls `CombatReplayWriter.RecordInitialState(ToSave
(null))` before entering any room — but both Ghost-fight splices (`LegacyAscensionGhostFightSplice.cs`,
`MultiplayerLegacyAscensionGhostFightSplice.cs`) bypass that whole flow with their own direct
`instance.EnterRoom(...)` call, and neither one called `RecordInitialState` itself. Thrown from inside
a fire-and-forget `Task` (caught and logged rather than crashing outright, by `TaskHelper
.LogTaskExceptions`), it silently aborted `StartCombatInternal` partway through — before turn setup
(Energy reset, hand-draw wiring) had actually run.

Confirmed only observable in multiplayer (`ChecksumTracker` is exercised on every `StartTurn`
specifically because two peers exist to keep in sync — presumably a no-op or never constructed in a
real singleplayer `NetGameType`), which is why this was never hit in all of M7's own singleplayer
testing despite the gap existing structurally in that splice too. Fixed identically in both splices
(mirroring native's own gate): `if (instance.CombatReplayWriter.IsEnabled) { instance
.CombatReplayWriter.RecordInitialState(instance.ToSave(null)); }`, right before the existing
`EnterRoom` call in each.

**Not yet confirmed whether this alone fixes everything else observed in the same test** — the hand
desync, the "only one Ghost acted" pattern, the garbled/missing Ghost names, and the black-screen
crash could plausibly all be downstream consequences of `StartCombatInternal` aborting inconsistently
between the host and the joining client (an interrupted setup diverging is a very plausible way to
get exactly this shape of symptom), or some could be independent bugs. Recommend re-testing with this
fix before investigating those further — background research into the "second Ghost" pattern was
already underway when this root cause was found and its results should be read in light of it, not
assumed to still apply if the retest comes back clean.

Two unrelated bugs also reported in the same message, under separate investigation: enemies visually
pushed too far right (with the last one clipping off-screen) in ordinary multi-monster fights that
have no Ghost involved at all; and a singleplayer crash on the very first card discard of a session,
which then behaves normally after a restart. Neither could be confirmed from this session's log
(P6, the mod's own enemy-repositioning patch, provably never ran before the reported multi-enemy
fight; the discard bug is singleplayer but this log is multiplayer start to finish) — still open,
need a matching log to make progress.

---

## GhostActionSync race condition fix (2026-09-05) — the actual "second Ghost never acts" root cause

Confirmed via a second background research pass that the "second Ghost never gets a turn" symptom is
**not** explained by the `RecordInitialState` fix above — the same exception did not recur in this
specific attempt's log window, so this needed its own root cause.

**Root cause, confirmed from `GhostActionSync.cs`**: the original design gave each Ghost exactly one
overwritable `TaskCompletionSource`, registered fresh every time `AwaitNext` was called (once per card,
in a loop). `OnEventReceived` could only resolve a *currently registered* one — if a broadcast for a
Ghost arrived before that Ghost's own next `AwaitNext` call had re-registered its waiter, the event was
silently dropped with no error. This is not a rare edge case: the host's own loop
(`GhostTurnController.RunTurnAsync`) paces itself purely by how long executing its own card takes and
never waits for a client to catch up, so any time the host is even slightly faster than a client for
one card, the next broadcast for that same Ghost can win the race.

Confirmed from `godot.log`: a joining client received and logged an end-turn event for its first Ghost,
but that Ghost never logged `TURN-END` — its `RunTurnAsync` was left permanently awaiting a
`TaskCompletionSource` that event should have resolved, had it not been dropped. Native
`CombatManager.ExecuteEnemyTurn`'s per-creature loop awaits `Creature.TakeTurn()` (P2's patch target)
one creature at a time, so a Ghost hung like this blocks the loop from ever reaching the *next* Ghost
at all — exactly matching "only one Ghost acted, the other never drew cards or had mana" and the
missing/garbled names (a creature whose turn never starts never gets whatever native step sets up its
displayed name either).

**Fixed** by replacing the single overwritable slot with an unbounded per-Ghost FIFO channel
(`System.Threading.Channels.Channel<GhostCardPlayEvent>`, `GhostActionSync.cs`) — a write is never lost
even when nothing is currently reading, and a read immediately drains any already-buffered event before
waiting for a new one. Lazily created per Ghost NetId with no assumption about how many Ghosts exist or
in what order their turns run, so this is correct for a party of any size, not just two. Also fixed in
passing: the log line for an end-turn event displayed a blank card id instead of `<end-turn>`, because
the event's `cardIdForValidation` round-trips over the network as `""` rather than `null` (`Serialize`
writes `?? string.Empty`) — checks `isEndTurn` explicitly now instead of relying on `??`, which could
never have triggered.

---

## Thorns fix, indicator refresh fix, M7 1-HP debug toggle (2026-09-04)

Three reported issues, fixed/added the same day.

**Thorns queued instead of hitting back immediately.** `ThornsPower.BeforeDamageReceived` fires as a
hook *inside* the same `CreatureCmd.Damage` call resolving the human's own (now-immediate) attack
landing on the Ghost — dealer = Ghost, target = the attacking human — so P17 was queuing it like any
other Ghost-dealt damage. Fixed generically, not as a Thorns special-case: P17
(`EnginePatches.cs`) now also requires `dealer.CombatState.CurrentSide == CombatSide.Enemy` before
queuing — Ghost-dealt damage only queues while it's genuinely the Ghost's own turn, since queuing only
ever existed to telegraph a card the Ghost chose to play on its own turn. Any Ghost-dealt damage during
the human's turn (Thorns today, any similarly-shaped reactive power later) now applies immediately.

**Ghost's queued-damage indicator didn't update when Weak was applied to it on the human's turn.**
`QueuedDamagePacket.DisplayAmount` already re-read the dealer's live Weak/Strength fresh on every read
— the number was never wrong, nothing ever told the presenter to *re-read* it, since applying/decaying
Weak or Strength doesn't touch `GhostDamageQueue`'s own pending list at all. Fixed at the one native
chokepoint every power amount-change funnels through, `PowerModel.SetAmount` (P34, `EnginePatches.cs`):
a postfix gated on `__instance is WeakPower or StrengthPower` (by kind, not by any specific card/relic)
calls the queue's new `NotifyDisplayChanged()` so the presenter re-renders with the live number.

**M7 debug toggle: force every real monster to 1 HP.** `DebugOneHpEnemiesToggle.cs` (`src/Debug/`) — a
persistent main-menu toggle (a new debug button, independent of any specific mode's entry point) that
patches `Creature.SetUniqueMonsterHpValue` to force 1 HP instead of rolling the normal range. That
method itself throws for a `Player`-backed creature, so this can never touch the Ghost or the human —
confirmed by reading it, not assumed. Deliberately does not touch the Ghost's own HP (the user's
explicit choice, over trivializing the duel too) — meant to be toggled on, then combined with the
game's own real "LEGACY ASCENSION" button, to blitz through Acts 1-3 across many runs/levels while
still exercising the actual duel normally each time.

Not yet confirmed live: all three compile clean but haven't been exercised in a real fight/run yet.

---

## Fistcuffs Block fix + Regent desaturation/orientation fix (2026-09-05)

Two bugs reported from the same M8 Regent fight.

**Fistcuffs gained no Block.** Root cause, confirmed by reading `DamageResult.cs`: P17
(`EnginePatches.cs`, queues the Ghost's own direct damage instead of resolving it immediately) hands
its caller back a bare `new DamageResult(target, props)` — `UnblockedDamage` defaults to `0`. That's
fine for a card whose own effects don't read the number back, but FISTCUFFS's "gain Block equal to
damage dealt" step reads it *synchronously*, in the same card-play — queuing only defers the *real*
resolution to later, not this card's own already-running effect chain, so it saw zero every time.
Fixed by populating `UnblockedDamage` with `QueuedDamagePacket.DisplayAmount` (the same pre-block
figure already shown to the player as the queued-damage intent) instead of an absolute zero — a
best-effort estimate, not the true post-resolution figure (this call site can't know the target's Block
at actual resolution time, which happens later at turn-start/turn-end). Documented in P17's own doc
comment as an estimate, not treated as fully solved; `BlockedDamage`/`WasFullyBlocked`/`WasTargetKilled`
are deliberately left at their safe defaults for the same reason (CLAUDE.md: surface what's unavailable
rather than fabricate it).

**Regent's held weapon stayed full-color and facing its original direction.** Root cause, confirmed by
reading `NRegentVfx.cs` (a monster-specific VFX helper script, not anything this mod wrote): some
characters render a hand-held item as its own separate native `"SpineSprite"` node
(`NRegentVfx._weapon`/`_weapon2`, wrapping `"Weapons/WeaponAnim1"`/`"WeaponAnim2"`) — entirely
independent of `node.Visuals.SpineBody`'s own material and transform, which is all P6's existing
`ApplyGrayscale` touched. Rather than special-case Regent by id (CLAUDE.md's core rule), `ApplyGrayscale`
now walks every descendant of the creature node for any node whose native class is `"SpineSprite"` (the
same identity check `NCreatureVisuals.cs:185` already uses) and desaturates each one found — a
character-agnostic fix that happens to catch Regent's extra weapon sprites without ever naming them.
Orientation: a sprite already inside `Visuals`' own subtree already inherited that node's existing
mirrored `Scale.X`, so flipping it again would cancel that back out — a sprite only gets its own flip
when found *outside* that subtree (checked via `Node.IsAncestorOf`, not by assuming a particular scene
shape). Not yet confirmed live: if Regent's weapon sprites turn out to sit *inside* `Visuals`' own
subtree after all, this fixes the desaturation but not the orientation — that would instead point to
Spine's IK/constraint system not respecting a naive parent-level mirror for a bone-attached held item,
a deeper native limitation this fix cannot reach and would need further live investigation.

---

## M10 — Visual UI overhaul (planned 2026-09-05; M10a and M10b both implemented same day, neither confirmed live)

Full design writeup, citations, and the research it's grounded in live in the plan file this was
drafted from; condensed here for the project's own permanent record.

**M10b — implemented, not yet confirmed live.** `GhostDeckViewerPresenter`
(`src/Presentation/GhostDeckViewerPresenter.cs`) attaches one plain, code-constructed `Button` per
Ghost (no new scene/texture ships with this mod — `"has_pck": false` — so this is text-labeled, not a
themed icon), opening `NMultiplayerPlayerExpandedState.Create(ghostPlayer)` via
`NCapstoneContainer.Instance.Open(...)` on click, guarded by `!NTargetManager.Instance.IsInSelection`
exactly like the native handler. One real timing wrinkle found and fixed during implementation, not
anticipated in the original plan: `GhostSession`'s own constructor runs *before* any Ghost has been
added to the combat room, so eagerly attaching (the way `GhostDamageIndicatorPresenter` is wired,
event-driven, never needing a node until damage actually queues) would find every node missing. Fixed
by calling `GhostSession.NotifyGhostCreatureNodeReady(ghostPlayer, node)` from inside P6
(`EnginePatches.cs`'s `P6_PlaceGhostOnEnemySide`) — the one place already guaranteed to hold a valid
node reference for that exact Ghost at that exact moment. Button position (offset from the Ghost's own
`IntentContainer`) is a first guess, not yet confirmed live to avoid overlapping the queued-damage
indicator.

**M10a — revised 2026-09-05 after a live test found it silently did nothing.** The user opened the
Compendium, saw the new button rendered correctly, clicked it, and observed no visible change at all.
`godot.log` showed the real cause: three swallowed `NullReferenceException`s logged from
`GhostLedgerScreen.Refresh()` at the exact moment Compendium opened. Root cause, confirmed by reading
`NCapstoneContainer.cs:130`: `NCapstoneContainer.Instance => NRun.Instance?.GlobalUi.CapstoneContainer`
only exists *during an active run* — the Compendium is a main-menu screen, so `NRun.Instance` is null
there and `NCapstoneContainer.Instance?.Open(screen)` silently no-opped every time (M10b's own use of
the same container is unaffected — it opens from inside a live Ghost fight, confirmed working by the
user's own testing). A second, independent bug compounded it: `NRunHistory.Create()` instantiates a
packed scene not yet part of any live `SceneTree` (`NRunHistory.cs:313`), so calling
`LoadDeck`/`LoadRelics` immediately, as the original constructor did, touched private fields those
methods only populate in their own `_Ready()` — that was the actual `NullReferenceException`.

Fixed by making `GhostLedgerScreen` (`src/Ui/GhostLedgerScreen.cs`) an `NSubmenu` instead of an
`ICapstoneScreen`, pushed via `NSubmenuStack.Push(NSubmenu)` — the same public, already-proven
mechanism `src/Progression/NSubmenuStackAccess.cs` gives `LegacyAscensionEntry` for its own
Standard/Daily/Custom-sibling button. Confirmed by reading `NMainMenuSubmenuStack.GetSubmenuType(Type)`
that every native submenu lazily spawns itself via the exact same two steps (construct,
`AddChildSafely` onto the stack, then `Push`) — no packed scene or hardcoded type-registration needed,
resolving the original "no generic push path" problem from a different angle than first planned. This
also fixes the readiness bug for free: the data refresh now happens from `OnSubmenuOpened()` (fired
only after the reparented `BackButton`/`DeckHistory`/`RelicHistory` subtree is already in the live tree
and has had its own `_Ready()` run) — the same convention `NRunHistory.OnSubmenuOpened` itself uses.
`GhostLedgerEntry` (`src/Ui/GhostLedgerEntry.cs`) still duplicates `%RunHistoryButton` (an
`NCompendiumBottomButton`, not the `NSubmenuButton` type other mod-added buttons use — its own label
lives at a plain child path `"Label"`, not a unique-name `%Title`, confirmed by reading
`NCompendiumBottomButton.cs` directly after an initial wrong guess) but now opens the screen via
`NSubmenuStackAccess.GetStack(compendium).Push(...)` instead of `NCapstoneContainer`. Not yet
re-confirmed live after this revision — needs another Compendium-opened pass (open, toggle ladder,
navigate, back out) before being considered done.

**M10b — two bugs fixed 2026-09-05 after the same live test.** The screen itself opened and showed
correct deck/relic data (confirmed working), but two things were wrong: the trigger was a plain
text-labeled `Button` ("poorly formatted" per the user), and the opened screen's title showed the
Ghost's raw numeric NetId instead of a name. Fixed the button by reusing `CharacterModel.IconTexture`
on a `TextureButton` — confirmed the exact same texture the native multiplayer party bar itself renders
for this purpose (`NMultiplayerPlayerState.cs:413`). Fixed the name by reaching into the opened screen
right after `NCapstoneContainer.Instance?.Open(screen)` returns (that call synchronously runs the
screen's own `_Ready()` before returning) and overwriting its name label — a one-field reflected
accessor (`GhostExpandedStatePlayerNameLabelAccess`, `src/Presentation/`), not a Harmony patch on the
widely-shared `PlatformUtil.GetPlayerName` helper that produced the bad value in the first place
(root cause: that helper can't resolve a Ghost's synthetic NetId to any real platform user, so it falls
back to `playerId.ToString()`). Shows "{Character} Ghost" (e.g. "The Ironclad Ghost") rather than
attributing the Ghost to a specific human's Steam identity — the latter would need new data tracking
(which human's snapshot became this party's Ghost), not implemented. Not yet re-confirmed live.

**Both revised again 2026-09-05, third pass, after the user tried the previous revision live.** M10a's
page now loaded (screenshot confirmed) but had no visible way to back out, and its ladder-toggle/prev-
next controls were plain default-themed `Button`s rather than matching the rest of the game's UI. Root
cause of the missing back button and the arrow-reparenting fix are both described in
`GhostLedgerScreen`'s own doc comment (`src/Ui/GhostLedgerScreen.cs`) — in short: this screen defaulted
to `Visible = true`, so `NSubmenuStack.Push`'s `screen.Visible = true` was a no-op write that never
raised `VisibilityChanged`, so the back button was never `Enable()`d out of the `Disable()`d state
`ConnectSignals()` leaves it in; fixed by starting `Visible = false`, matching every native submenu's
own construction. The ladder toggle and prev/next buttons are now a single reparented native
`LeftArrow`/`RightArrow` pair (`NRunHistoryArrowButton`, sourced from the same throwaway `NRunHistory`
instance) navigating one flat sequence across both ladders, replacing the separate toggle button
entirely. M10b's screenshot (confirmed correct deck/relic data, portrait icon working) showed two
remaining issues: the icon needed to sit at the right edge of the screen rather than float near the
Ghost's own sprite, and it needed to be desaturated like the Ghost itself. Fixed in
`GhostDeckViewerPresenter` (`src/Presentation/`): the button is now reparented under
`NRun.Instance.GlobalUi` (the same always-on-top container the native TopBar/RelicInventory/
CapstoneContainer live under) and anchored to its right edge via real Godot anchor fractions, with its
vertical position still taken from the Ghost's own `IntentContainer.GlobalPosition`; the icon's texture
now carries a duplicated `res://materials/vfx/hsv.tres` shader material with "s" zeroed — the exact same
technique `EnginePatches.cs`'s own `ApplyGrayscale` already uses on the Ghost's own sprite. Not yet
re-confirmed live.

**M10a fixed again 2026-09-05, fourth pass — the button had stopped doing anything at all.** Root
cause, confirmed by reading the setter: `_prevButton.IsLeft = true;` ran in `GhostLedgerScreen`'s own
constructor, but `_prevButton` (reparented from the still-never-`_Ready()`'d throwaway `NRunHistory`
instance, same as every other node this screen borrows) hadn't been added to a live tree yet —
`NRunHistoryArrowButton.IsLeft`'s setter unconditionally touches `_icon.FlipH`, a field only populated
in that button's own `_Ready()`, so the write threw a `NullReferenceException` straight out of the
constructor. `GhostLedgerEntry.Open` calls `new GhostLedgerScreen()` directly and uncaught, so this
aborted before `stack.Push(screen)` ever ran — indistinguishable from a dead button. Fixed by moving
that one assignment into a new `ConnectSignals()` override (fires from `_Ready()`, guaranteed to run
only after the whole reparented subtree has already had its own `_Ready()` execute).

**M10b refined again 2026-09-05 (icon size + multi-Ghost stacking), per direct user request.** Button
size increased 25% (44 → 55). More importantly: the previous revision derived the icon's vertical
position from the Ghost's own `IntentContainer.GlobalPosition` — correct for exactly one Ghost, but
every enemy-side creature (Ghosts included) stands on the same ground line, so a multiplayer party of
2-4 Ghosts would have resolved to nearly the same Y and stacked their icons on top of each other at the
same right-edge X. Replaced with a fixed vertical list in `GhostDeckViewerPresenter`: each Ghost gets a
slot ordinal (its attach order — deterministic per party, since `AttachButtonFor` fires once per Ghost
in the same order every session) stacked downward from the bottom of the real native `TopBar` node
(`NRun.Instance.GlobalUi.TopBar`), one `ButtonSize.Y` + fixed margin apart — anchored to a real node's
own position/size, not to any individual Ghost's own (potentially colliding) position. Not yet
re-confirmed live, including specifically with a real multi-Ghost party.

**M10a fixed again 2026-09-05, fifth pass — the compendium screen "worked" but the header text never
showed, and the back button did nothing.** Header root cause: `MegaLabel` asserts a per-instance "theme
font override" before its own `_Ready()` completes (`MegaLabelHelper.AssertThemeFontOverride`, guarding
a documented Godot engine bug around quit-time auto-sizing) — a scene-authored `MegaLabel` gets this for
free from the editor, but this screen's two hand-built `new MegaLabel()` instances never did, so both
threw `InvalidOperationException` the moment they entered the tree (confirmed live in `godot.log`; the
exceptions were harmlessly swallowed by Godot's own C#-exception-to-error boundary, which is why the
rest of the screen — deck/relic widgets, built from different types — still rendered while the header
text silently never did). Confirmed by grep that the native game never constructs a bare `MegaLabel` in
code anywhere. Fixed by reparenting an existing, already-configured header label instead
(`%GameModeLabel`, a `MegaRichTextLabel` — no such per-instance assertion, and it keeps whatever font
override the original scene gave it) in place of both hand-built labels. Back-button root cause: this
screen's own header row was added as a full-rect-anchored sibling starting at (0,0), overlapping the
reparented `BackButton`'s own native top-left position; being added later, it drew on top and won the
hit-test, so clicks aimed at the back button landed on this screen's own controls instead. Fixed by
offsetting the header row down by the back button's own real `Position`/`Size` (readable immediately
after scene instantiation, independent of `_Ready()`) plus a small margin.

**M10a fixed again 2026-09-05, sixth pass — the user reported the back button still broken and the
deck/relic cards no longer showing at all.** Two things going on. First: a running game process keeps
whatever mod assembly it already loaded — overwriting the DLL on disk mid-session has no effect until
the game itself is fully restarted (not just returning to the main menu) — so it's possible the "fifth
pass" fix was never actually exercised live yet if the same long-running process was still open from
before that fix was deployed; worth calling out explicitly since it cost real time to untangle from log
evidence. Second, independent of that: the fifth pass's own back-button-overlap fix read
`_reparentedBackButton.Size`/`.Position` to compute the header row's offset — but, like every other node
this screen reparents from the still-never-`_Ready()`'d throwaway source, a Control's `Size` is not
reliably valid before its own layout pass runs (the same class of bug that already caused the `IsLeft`
crash and the `MegaLabel` assertion earlier in this file). A degenerate offset here would push the
header row — and the deck/relic widgets inside it — far down the screen: invisible, not crashed,
matching "cards no longer show up" exactly. Replaced with a fixed, hand-tuned clearance constant,
removing the dependency on that node's own `Size` entirely.

**M10a, seventh pass (2026-09-05) — research, not another fix, per the user's explicit direction.**
Cards came back after the sixth pass, but the back button still didn't work. Read `NBestiary.cs`,
`NStatsScreen.cs`, and `NRunHistory.cs` in full and compared against `NBackButton.cs` itself. Confirmed:
none of those three native screens override `ConnectSignals()` — all rely purely on the inherited
`NSubmenu.ConnectSignals()`, exactly like `GhostLedgerScreen`. But `NBackButton` turned out to be far
more involved than a simple `Visible`-gated button: it computes its own show/hidden *positions* in its
own `_Ready()` from `GetWindow().ContentScaleSize` and its own anchor offsets, starts forced to its
hidden position, and only ever animates to its shown position via a 0.35s tween inside `OnEnable()` —
independent of the screen's own `Visible` property beyond triggering that one `Enable()` call. Nothing
in this reading definitively explains the reported failure (the mechanism reads as self-consistent
whether the button is reparented or not, since it positions itself from the window, not its immediate
parent) — so rather than risk another blind, possibly-regressing guess, this pass adds two
diagnostic-only log lines instead: one on the BackButton's own `Released` signal (confirms whether a
click is even reaching it) and one ~0.6s after `OnSubmenuOpened()` dumping its settled
position/enabled/visible state (past the show-tween's own 0.35s). No behavior change. Needs one more
live test, after a full restart, to read back what actually happened.

**M10a, eighth pass (2026-09-05) — the diagnostic answered its own question.** Log line:
`GhostLedgerScreen: BackButton settled state — IsEnabled=True, Visible=True, MouseFilter=Stop,
GlobalPosition=(-40, 726), ...`. The button was never a hit-test or `_stack.Pop()` problem — it's fully
enabled, visible, and accepting clicks, just sitting off the left edge of the screen at negative X, so
there was nothing there to click. Root cause: `NBackButton.OnWindowChange()` assigns `Position` from a
formula based purely on the window size and the button's own anchor offsets, but its show/hide tweens
animate `"global_position"` toward that same value — correct only if the button's parent screen sits at
`GlobalPosition == (0, 0)` exactly, matching whatever the original `NRunHistory` scene's own root offset
actually is (not verifiable without the .tscn itself). This screen's own plain, code-constructed root
evidently doesn't match closely enough. Fixed by correcting the one observable symptom directly: once
the show tween settles, if the button's real `GlobalPosition.X` is negative, mirror it back on-screen by
negating it — the actual observed magnitude, not a guessed constant. Not yet re-confirmed live.

**M10a, ninth pass (2026-09-05) — visible now, but still no click.** Screenshot confirmed the button
now sits correctly bottom-left, looking like a normal back button — but it still didn't respond, and the
diagnostic `Released`-signal log never fired even once, meaning the click never reached the button at
all. Root cause: the eighth pass revealed the button actually lives at the *bottom*-left of the screen
(`GlobalPosition.Y = 726`), not top-left as every earlier pass had assumed — so the fourth/fifth pass's
"push our own content below the back button" fix was solving a collision that was never the real one.
This screen's own `root` container (`AnchorBottom = 1`, spanning from the header down to the bottom of
the screen) fully overlaps the button's real, bottom-left position — and `root` was added to the tree
*after* the back button, so as the later sibling it draws on top and wins the click hit-test wherever the
two overlap — the same top-left overlap bug from two passes ago, recurring at the opposite corner. Fixed
by reordering: the back button is now added to the tree *after* `root`, so it is always the topmost
sibling regardless of where either one's rect actually lands. Not yet re-confirmed live.

**M10a confirmed working 2026-09-05 — the back button responds to clicks.** Same session, one polish
request: its left edge sat with a small gap from the window's true left edge (mirrored to the same
margin the eighth pass's off-screen bug was found at) rather than flush against it. Changed the
post-tween correction to pin `GlobalPosition.X` to exactly `0`. The diagnostic-only click-detection log
from the seventh pass is removed now that it answered its question.

**Test data added 2026-09-05, per explicit request.** `ghost_a1.json`-`ghost_a4.json` added to the solo
ladder's save directory alongside the one real, already-existing `ghost_a0.json` (left untouched, never
overwritten) with Silent/Defect/Necrobinder/Regent respectively; `ghost_a0.json`-`ghost_a4.json` added to
the multiplayer ladder's own (otherwise-empty) directory covering all five characters including
Ironclad. Each is a minimal but genuinely valid `SerializablePlayer` JSON, structured exactly like the
real `ghost_a0.json`: that character's own starting relic (confirmed via each character's own
`StartingRelics` override — `BurningBlood`/`RingOfTheSnake`/`CrackedCore`/`BoundPhylactery`/
`DivineRight`) plus 5 of their own Strike + 5 Defend cards (confirmed valid ids: every character has its
own dedicated `Strike<Character>`/`Defend<Character>` class, e.g. `StrikeRegent` -> `CARD.STRIKE_REGENT`
— the same class-name-to-id convention already confirmed live for `StrikeIronclad`/
`CARD.STRIKE_IRONCLAD`). The existing prev/next navigation (spanning both ladders as one flat sequence,
built across earlier passes) is the "ascension selector"-like feature requested — it already loads
whichever level/ladder the index points to; what was missing was simply something to browse.

**M10b fixed again 2026-09-05, third pass — the icon stopped showing at all after the previous pass'
multi-Ghost stacking fix.** Root cause: that fix anchored the icon's Y position off
`NRun.Instance.GlobalUi.TopBar` specifically — but `TopBar` is animated (confirmed elsewhere in this
exact codebase: `NMultiplayerPlayerExpandedState.AfterCapstoneOpened` calls `globalUi.TopBar.AnimHide()`/
`AnimShow()`), so its position at the exact moment P6 fires during combat setup isn't guaranteed to be
its resting, on-screen one — a hidden/mid-animation TopBar could silently place every button off-screen
with nothing logged (a positioning bug, not a crash). Replaced the anchor with `GlobalUi` itself (the
persistent, never-hidden root of all run UI) plus a fixed, hand-tuned clearance margin in place of
`TopBar.Size.Y`. Not yet re-confirmed live.

M1-M9 made the Ghost's mechanics legal. M10 is the first milestone purely about *visibility* into
data that already fully exists — no new combat mechanics, no new saved state, just native inspection
UI pointed at what the mod already tracks. Two features:

1. **M10a — Compendium page**: a new page, shaped like the native "previous runs" page
   (`NRunHistory`), for browsing Ghosts currently saved on either ladder. Strong reuse case: the native
   deck/relic display widgets (`NDeckHistory.LoadDeck`/`NRelicHistory.LoadRelics`) already take exactly
   the `(Player, IEnumerable<SerializableCard/Relic>)` shape `GhostSnapshotFileStore.Load(level)`
   already returns — no new conversion code needed. New page toggles between the solo
   (`GhostSnapshotStore`) and multiplayer (`MultiplayerGhostSnapshotStore`) ladders, since they must
   never be visually conflated (`docs/PROGRESSION.md` §7). Entry point: duplicate `RunHistoryButton`
   under `NCompendiumSubmenu`, same pattern as every other menu button this mod has already added.
2. **M10b — In-fight Ghost deck/relic viewer**: reuses `NMultiplayerPlayerExpandedState` — the native
   "view a co-op teammate's deck" screen — confirmed to work unmodified on a Ghost's own `Player`
   (reads live `.Relics`/`.Deck.Cards`, no `CombatSide`/`IsPlayer` check anywhere in it). The native
   trigger (the multiplayer party bar) won't include a Ghost (deliberately excluded from
   `RunState.Players`), so this needs one new small clickable node per Ghost, anchored to that Ghost's
   own creature node (a sibling presenter to `GhostDamageIndicatorPresenter`), guarded the same way the
   native click handler is (`!NTargetManager.Instance.IsInSelection`) so it can never compete with
   card-target-selection clicks. Correct for any party size by construction — one icon per
   `GhostPartyMember`, no hardcoded assumption of exactly one Ghost.

**Milestone-discipline flag**: M9's own gate (a clean two-client party fight) has not yet closed as of
this writing — this is almost entirely additive UI touching no combat-resolution code, so the risk to
M9 is low, but implementation should wait for the user's explicit go-ahead given M9 is still being
actively hardened.

New directory this milestone gates open: `src/Ui/`.

---

## Multiplayer live-test bug batch (2026-09-07)

Five reports from the same round of live multiplayer testing (four bugs, one feature request), handled
in this order: two fixed with source-grounded confidence, one UI request rebuilt rather than patched,
two left as diagnostics-only because neither could be root-caused from source alone.

**Fixed — Ghost AI randomness now uses the game's own seeded RNG.** `WeightedRandomGhostChooser` and
`LeftToRightChooser` drew every random decision (which power to play, Attack-vs-Skill weighting, jitter,
target selection) from `Random.Shared` — a per-process, wall-clock-seeded source that does not survive a
disconnect/process-restart of the same fight with the same results, per the user's explicit request that
Ghost AI randomness "be linked to seed state like everything else in the game." Confirmed by reading
`RunRngSet.cs`: `RunRngSet.MonsterAi` (`RunState.Rng.MonsterAi`) is the engine's own counter-based, seeded
stream specifically documented as "what moves each monster makes whenever there's randomness involved" —
already carried through save/reload and multiplayer sync like every other native RNG-driven decision, no
new tracking needed. Every `Random.Shared` call site in both choosers now draws from
`view.GhostPlayer.RunState.Rng.MonsterAi` instead. Does not change the two choosers' own determinism note
about client-side execution (still host-only, per M9e) — this is orthogonal: it's about the *same
process* producing the same continuation of "randomness" across a restart, not about cross-client
agreement (which was already handled by host-authoritative broadcast). Not yet confirmed live.

**Fixed — Tank/Guarded now affect an already-queued attack.** User report: "playing tank does not update
incoming damage indicators properly." Confirmed by reading `GuardedPower.cs`/`TankPower.cs` (both
`MultiplayerOnly` content, per `Tank.cs`): both are target-side `ModifyDamageMultiplicative` hooks, like
Vulnerable — but unlike Vulnerable (deliberately locked at queue time, COMBAT-RULES.md §5, since it
represents the *dealer's* own committed setup), Guarded/Tank exist specifically to be played *reactively*
by a teammate/self against an already-visible queued threat. Locking them at queue time like Vulnerable
meant they could never affect the attack they were played in response to — the card's entire purpose in
this delayed-queue duel. Generalized the existing Weak/Strength "suppress at queue time, re-read live at
display/resolution" pattern (P15/P28) to these two, target-side hooks this time: new
`GhostGuardedSuppressionScope`/`GhostTankSuppressionScope` (`src/Duel/`), new `P35`/`P36`
(`EnginePatches.cs`) suppressing each power's `ModifyDamageMultiplicative` while its scope is active,
both entered alongside the existing Weak/Strength scopes in P17's queue-time `Hook.ModifyDamage` call.
`QueuedDamagePacket.DisplayAmount` now also multiplies in the target's live Guarded and Tank factors —
confirmed correct by reading `Hook.ModifyDamageInternal` directly: the multiplicative pass is a plain
running product across every hook listener (`num *= num3`), so combining a third live multiplicative
factor the same way the second (Weak) already was is exactly what a fresh computation would produce.
P34's indicator-refresh gate extended from `WeakPower or StrengthPower` to also include `GuardedPower or
TankPower`, so applying either one now correctly nudges the presenter to re-render. Not yet confirmed
live.

**Rebuilt, not patched — Compendium ascension selector.** Two reports from the same screenshot: (1) "the
compendium screen does not allow for changing which ascension ghost is being viewed," with an explicit
request for "an ascension selector similar to what happens before a run starts," and (2) the screenshot
itself showed neither the header text nor the prev/next arrows rendering — only the deck/relic panels
and back button were visible. Root cause of (2) not independently re-confirmed live (no fresh log from
that session), but the prime suspect by inspection: the header label and both arrows shared one
`HBoxContainer`, and `NRunHistoryArrowButton` is exactly the class of native, pre-`_Ready()`,
reparented-from-a-throwaway-source widget that had already cost `GhostLedgerScreen` nine separate
debugging passes (M10a, above) — sizing/positioning not valid until its own layout pass runs. Rather than
risk a tenth pass on the same bug class, or gamble on reusing the real native `NAscensionPanel` (its
packed-scene resource path is not confirmed from the decompiled source alone, and it is normally only
ever instantiated as a character-select-screen child, not standalone), replaced the prev/next arrow pair
entirely with a row of plain, code-constructed `Button`s — one per saved (ladder, level), built once at
construction — the same proven-safe construction technique `GhostDeckViewerPresenter` already uses
successfully elsewhere in this mod. Directly satisfies "jump to any level, see them all at a glance,"
closer to the real ascension panel's own spirit than a one-at-a-time prev/next pair, while sidestepping
the "native widget not yet ready" bug class altogether. The header label also moved onto its own
dedicated full-width row (previously squeezed beside the two arrows) in case the missing text was a
clipping symptom rather than a rendering failure — cannot be fully distinguished from source reading
alone. Not yet confirmed live.

**Diagnostics only — two bugs not root-caused.** Per this file's own evidence discipline (R1: never
assert engine behavior from memory; a plausible story is not a finding), neither of these got a code fix
this session — only targeted logging, so the next reproduction can actually answer the question instead
of needing another guess:

- *"Multiplayer combat works correctly for one turn, then end turn button does not work to progress the
  fight."* Read `GhostActionSync.cs`, `GhostTurnController.cs`, and P32/P33 (`EnginePatches.cs`, the
  Ghost-goes-first turn-order patches) in full looking for anything gated to fire correctly once but not
  on a repeat — found nothing: P32/P33 are both deliberately scoped to the *first* Ghost-opening-turn
  transition only (`RoundNumber == 1`), not a recurring per-turn hook, and `GhostActionSync`'s per-Ghost
  channel (fixed 2026-09-05 for exactly this "works once, then hangs" shape) has no per-round reset logic
  that could regress the same way. No further hypothesis was strong enough to act on without a log — the
  existing log points (`GHOST-BROADCAST`/`GHOST-EXECUTE`/`TURN-START`/`TURN-END`, P33's own
  snapshot/restore lines) should already show exactly where the second turn stalls; what's needed next is
  the actual `ghostduel.log`/`godot.log` from a reproduction, not another guessed patch.
- *"In Ascension 1 fight the ghosts appear to have base decks"* (user's own hypothesis: "might be a
  problem relating to starting and ending a run on a different computer"). Read the full save → report →
  build chain (`GhostSnapshotFileStore.Save`, `MultiplayerGhostLadderCoordinator.BuildOwnSnapshotReport`/
  `RecordSnapshotReport`, `MultiplayerLegacyAscensionGhostFightSplice.BuildGhost`) — the "no previous
  Ghost" case is confirmed to log loudly and refuse to build a party (never silently substitutes a fresh
  stock deck, per PROGRESSION.md §3), so a genuinely base-looking deck must mean the *loaded* snapshot
  itself has one, not a silent fallback — but nothing read pins down whether that traces to the save
  (the file itself), the report (host/client mismatch), or a currently-unread step. Added deck/relic
  counts to three existing log points that were previously missing them entirely
  (`GhostSnapshotFileStore.Save`'s own line, `SnapshotSent`/`SnapshotReceived` in
  `MultiplayerGhostLadderCoordinator.cs`) plus a new one right before `CreateGhostFromSnapshot` is called
  in the splice — so the next test's log shows the count at all four points (saved → sent → received →
  built) and can localize exactly where it drops to 10.

**Fixed — a joining client never got its own `ghostduel.log`.** User question: "this should have debug
logs for host and joiners?" Confirmed by reading every `GhostLog.OpenLogFile()` call site: all of them
(`MultiplayerLegacyAscensionEntry.Start`, `MultiplayerDebugGhostDuelEntry.Start`, and the singleplayer
entries) sit behind a host-only or singleplayer-only button — a joining client never called any of them,
so it never had a dedicated log file; its `[GhostDuel]`-prefixed lines only ever reached the much noisier
native `godot.log` (still real, since `Log.Info`/`Warn`/`Error` fire unconditionally — just not isolated).
Fixed by opening the log file from `Postfix_ReportLadderOnClientInit` (`MultiplayerLegacyAscensionEntry.cs`)
— already fires for every joining client on every multiplayer character-select entry regardless of which
host mode was picked, so this covers a joining client for both multiplayer Ghost modes (Legacy Ascension
and the plain debug fight) without a second patch. Both bugs above need exactly this to make progress —
the end-turn hang was reported from the joining client's side, and the base-deck bug's new logging is
only useful if the client that reports/builds the affected Ghost actually has a log file to check.

---

## Regression: the RNG-determinism fix broke every multiplayer Ghost fight (2026-09-07)

A same-day live two-client test, using the build with the "AI randomness linked to seed state" fix
above, failed the A0 Ghost fight immediately after the Ghost's first turn ended — the human's turn never
usably started. Root-caused conclusively from the host's own `godot.log`, not guessed:

`[ERROR] State divergence detected! Checksum with ID 96 for client ... doesn't match host's! Context:
After enemy turn end.` — the native multiplayer consistency checker disconnected the client the instant
the Ghost's own turn-end checksum was computed. The log dumps both the host's ("LOCAL STATE DUMP") and
the client's ("REMOTE STATE DUMP") full state at the moment of divergence; a byte-for-byte diff of the
two found exactly one difference in the entire dump: `RNG counter MonsterAi: 39` (host) vs. `RNG counter
MonsterAi: 0` (client). Reproduced identically on a second attempt in the same session (different
checksum ID, same context, same single differing field).

Cause: `RunRngSet`'s per-stream counters (`RunState.Rng`) are part of the state every peer's checksum
must agree on — correct for *native* monster AI, whose decisions run identically and independently on
every peer as part of the ordinary lockstep simulation. But M9e's Ghost design is host-authoritative:
only the host ever calls a chooser's `Choose()`; every other client only replays the broadcast card/
target via `GhostActionSync.AwaitNext` and draws no randomness at all. Pointing the Ghost AI at
`RunState.Rng.MonsterAi` (this same day's earlier fix) meant only the host's copy of that shared,
checksummed counter ever advanced — guaranteeing a mismatch the moment a Ghost's turn involved any
random draw. `Random.Shared` (what it replaced) never had this problem only because it isn't tracked or
checksummed by anything.

Fixed by giving the Ghost AI its own standalone stream instead of a shared one: `GhostSession.AiRng`, a
plain `Rng(seed, name)` (confirmed via `Rng.cs` to be self-contained, registered in no `RunRngSet`
dictionary, and therefore invisible to the checksum) seeded from the run's own stable seed
(`RunState.Rng.Seed`) and owned by the session for the combat's lifetime. Both choosers
(`WeightedRandomGhostChooser`, `LeftToRightChooser`) now draw from `GhostSession.Current!.AiRng` instead.
This still delivers the reproducible-per-run half of the original request; the one honestly-stated gap
is that this counter isn't saved/restored across a full process restart the way checksummed streams are,
so a mid-fight disconnect that keeps the session alive continues the same sequence, but a full restart
reseeds from the run seed and starts over — a smaller, explicitly-documented regression versus the
multiplayer-breaking bug it replaces.

Plausibly explains the *original* "multiplayer combat works for one turn, then end turn does not work"
report from earlier the same day (before this RNG change existed) — the observed symptom (combat
apparently freezing right as the human's turn should start) is exactly what a checksum-divergence
disconnect looks like from the player's seat. Not asserted as confirmed, since that earlier report has
no matching log to check against; only this session's own reproduction is confirmed root-caused. Worth
specifically watching whether the end-turn symptom recurs on the next test now that this fix is in.

Not yet re-confirmed live — needs the same two-client A0 test repeated with this build.

**Important deployment note discovered while investigating**: the AiRng fix build above never actually
reached this machine's live `mods/GhostDuel/` folder before the next test — the game was running and
had the DLL file-locked when the build ran, the copy step failed, and the user's report of "seemingly
the same bug" turned out to be the *same unfixed build* still running, not a failure of the fix itself.
Confirmed by comparing file timestamps (`mods/GhostDuel/GhostDuel.dll` predated the fix; `godot.log` had
rotated several times since with no corresponding rebuild). Re-deployed successfully once the game was
closed. Lesson: always verify the mods-folder DLL's own timestamp/build after a deploy that reported a
locked-file warning, rather than assuming a later `dotnet build` silently succeeded.

---

## Legion of Bone summons an Osty for the Ghost too (2026-09-07)

User report: "legion of bones summoned osty's for the ghosts instead of only players." Confirmed by
reading `LegionOfBone.cs` (a `MultiplayerOnly` Necrobinder card, `TargetType.AllAllies`): its own
`OnPlay` computes its target set as `CombatState.PlayerCreatures.Where(c => c.IsAlive)` — every
`IsPlayer`-backed creature in the whole encounter, regardless of side — then summons an Osty for each
one via `OstyCmd.Summon`. Correct in vanilla co-op multiplayer, where no enemy is ever `Player`-backed;
wrong the instant a real `Player` sits on `CombatSide.Enemy` (this mod's entire premise). Exactly the
"`IsPlayer`-derived collection quietly means 'real human' until a Ghost exists" bug class already fixed
in several native code paths (P8-P14/P22/P25, ENGINE-NOTES.md §7 Q2) — just not yet found inside a
*card's own* logic.

Confirmed via a full read of every other `Cards`/`Relics`/`Powers` source file that `LegionOfBone` is
the *only* content item in the game using `.PlayerCreatures` directly for ally semantics — `TankPower
.AfterApplied` (the other "bless my allies" effect, checked for comparison) already uses the correct
side-relative `CombatState.GetTeammatesOf(Creature)` (`GetCreaturesOnSide(creature.Side)`). So this is a
genuine, isolated inconsistency in one native method, not a systemic pattern — a single targeted patch is
the right scope, not a broader rule change.

Per CLAUDE.md's Harmony-patch question 3 ("could a narrower target work — a specific call site instead
of a property getter"): `CombatState.PlayerCreatures` itself is one of the hot properties CLAUDE.md
explicitly forbids postfixing to reallocate, and changing its global meaning would also break this mod's
own correct uses of it elsewhere (both choosers' `AnyEnemy` target resolution relies on it including the
Ghost). Fixed instead with P37 (`EnginePatches.cs`): a prefix on `LegionOfBone.OnPlay` itself, replacing
its whole body only while a Ghost session is active — identical native calls (`CreatureCmd.TriggerAnim`,
`OstyCmd.Summon` per ally, the same `DynamicVars.Summon` amount) with the one substitution,
`GetTeammatesOf(owner)` (filtered to `IsPlayer` allies, matching `PlayerCreatures`' own original filter —
a teammate's own already-summoned Osty pet sitting on the same side doesn't get double-counted) in place
of the raw `PlayerCreatures` list. Not a content reimplementation: the effect itself (summon an Osty per
ally) is untouched; only which creatures count as "ally" is corrected, using the same helper the base
game's own equivalent effect already uses correctly.

Not yet confirmed live.

---

## Four fixes from a live A0 win + upcoming-A1 review (2026-09-08)

Same-day session: the previous night's AiRng fix held — a full multiplayer A0 fight completed cleanly
this time (`ghostduel.log`: both Ghosts played real turns, no state divergence, `partyWon=True`,
`ghost_a0.json` written with a real 32-card/23-relic Ironclad build). Four requests followed, aimed at
the *next* fight (A1) rather than a fresh crash in this one.

**Fixed — a client's `AwaitNext` could hang forever, with no log to confirm the exact trigger.** User
report: "the game engine was waiting for the ghost [Silent] to end its turn on the player's turn."
Checked every available log on this (host) machine for the actual incident — none showed it; the only
Ghost fight captured tonight won cleanly with no gap. Rather than invent a specific trigger with no
evidence, read `GhostActionSync.AwaitNext` end to end and found a real, general defect regardless of
cause: it awaited its per-Ghost channel with **no timeout at all**, and `GhostTurnController`'s 30-second
`TurnBudget` guard never actually bounds it — that guard is only re-checked *between* loop iterations,
never around one in-progress `await`. Native `CombatManager.ExecuteEnemyTurn` awaits `Creature.TakeTurn()`
(P2's target) one creature at a time, so a client stuck here never locally finishes "the Ghost's turn"
even once every other peer has moved on to the human's — matching the report exactly. The most likely
trigger is a missed broadcast (a dropped packet, or a client reconnecting mid-Ghost-turn with no way to
receive whatever was already broadcast before it reconnected — M9f's own reconnect handling is
explicitly diagnostic-only and doesn't replay history), but the fix doesn't depend on which one actually
happened: `AwaitNext` now takes the caller's own remaining turn-budget window as an explicit timeout and
gives up gracefully (logs, ends the Ghost's turn locally) rather than hanging forever, since a missed
broadcast can never arrive no matter how long the wait continues.

**Checked — the saved multiplayer ghost JSON.** `ghostduel_legacy_ascension_multiplayer/ghost_a0.json`
from tonight's win: structurally valid, 32 cards / 23 relics, `current_hp`/`max_hp` both 92 (a real
12-HP run-long gain over Ironclad's stock 80), all card/relic ids resolve. No errors found.

**Fixed — Ghosts weren't healed to full before a fight, only real humans were.** Confirmed by reading
`Player.FromSerializable`/`Player.cs:316,409-410`: a restored Ghost's `Creature.MaxHp`/`CurrentHp` are
already set directly from the snapshot's own saved values (e.g. 92, not Ironclad's stock 80) — that part
was already correct. But neither splice ever *healed* the Ghost — only the real humans, via a loop that
already existed. A Ghost's snapshot can legitimately have `CurrentHp < MaxHp` (the human who earned it
may have won their own Act-3 boss fight while still damaged), so without this the revived Ghost would
start its own fight partway hurt. Added the identical heal-to-full loop for every `GhostPartyMember`
in both `MultiplayerLegacyAscensionGhostFightSplice.cs` and `LegacyAscensionGhostFightSplice.cs`,
reading each Ghost's own live `Creature.MaxHp` — never `CharacterModel.StartingHp`, which would have
silently downgraded an earned max-HP increase back to the base character's stock value (exactly the
failure mode the user was worried about).

**Fixed — a Ghost's own room-entry relics (Vajra, confirmed picked up in tonight's run) never fired.**
Confirmed by reading `CombatRoom.cs:228` (`await Hook.AfterRoomEntered(runState, this)`, fired once when
the Ghost-fight's own `CombatRoom` is entered) and `Hook.AfterRoomEntered` → `RunState
.IterateHookListeners(null)` (`RunState.cs:545-570`): the native dispatch iterates `runState.Players`
only — real humans, by this mod's entire design never including a Ghost — so a Ghost's own copy of any
relic overriding `AfterRoomEntered` (confirmed by reading every `AbstractModel` subclass that does: only
`RelicModel`s — Vajra, BronzeScales, DivineRight, Gorget, Girya, and others; no card or potion currently
does) would silently never fire. Vajra specifically grants +1 Strength on entering a `CombatRoom` — not
a crash, but a Ghost carrying it would otherwise fight its whole match one Strength short of its own
build, forever. New `GhostPlayerFactory.FireAfterRoomEnteredForGhosts` fires each Ghost's own non-melted
relics against the fight's real `CombatRoom` instance right after both splices' `EnterRoom` call — a
narrow, call-site-specific fix (per CLAUDE.md's Harmony-patch question 3, applied here even though this
isn't a Harmony patch) rather than touching `RunState.Players`/`IterateHookListeners` themselves, both
read constantly elsewhere for things this fix must never affect.

All four not yet confirmed live — need the next A1 attempt (ideally one where a human wins their own
Act-3 fight while damaged, and where a Ghost's build includes Vajra, to actually exercise the last two).

---

## A joining client stranded on "Waiting for other players..." (2026-09-08)

Live A1 attempt, same day: the host correctly transitioned into the Ghost-party fight (Ghost turns
played out normally per `ghostduel.log`), but the joining friend's screen never left the old boss-fight
background — stuck on the native "Waiting for other players..." overlay. The host couldn't end their own
turn, most likely because native multiplayer synchronization was waiting on the stranded peer.

Root-caused **without needing a log from either machine** — a pure C# async-timing defect, confirmable
from reading native source alone. `ActChangeSynchronizer.MoveToNextAct()` (the real "all players voted to
advance" handler, entirely untouched by this mod, and confirmed to run independently and identically on
every peer once the replicated vote action lands) does exactly this:

```csharp
TaskHelper.RunSafely(RunManager.Instance.EnterNextAct());
if (NOverlayStack.Instance?.Peek() is NRewardsScreen nRewardsScreen)
    nRewardsScreen.HideWaitingForPlayersScreen();
```

`EnterNextAct()` is called *without being awaited*. Since an `async Task` method runs synchronously up to
its first real `await` before yielding control back to its caller, and this mod's Harmony prefix replaces
`EnterNextAct`'s entire body, whatever our own splice does before *its* first await runs inside that same
synchronous window — racing the native post-call check directly above. That first await used to be
`await CreatureCmd.Heal(...)` inside the human-heal loop — but only if `missing > 0m`. A player already at
full HP when they beat the boss skips it entirely, and execution races straight through building the
Ghost party into `NOverlayStack.Instance?.Clear()` a few lines later — synchronously, *before* the native
check ever runs on that peer. By the time `ActChangeSynchronizer` looks for `NRewardsScreen` at the top of
the overlay stack, it's already gone (we cleared it), so `HideWaitingForPlayersScreen()` is silently never
called on that one peer — while whichever peer *did* need healing naturally yielded control back first and
got the correct behavior. A per-peer coin flip based on nothing more meaningful than "were you at full HP
when the boss died."

Fixed by adding `await Task.Yield();` as the unconditional first statement of both splices'
`...ThenContinueUnsafe` methods (`LegacyAscensionGhostFightSplice.cs`, `MultiplayerLegacyAscensionGhostFightSplice.cs`)
— guarantees the native caller's own post-call check always runs first, on every peer, regardless of
anyone's HP. Applied to both the solo and multiplayer splice: the same native `ActChangeSynchronizer` path
is used in singleplayer too (with a single always-"ready" vote), so the identical race exists there,
just far less visible since there's no second peer left stranded — same "fix the identical gap
identically, since nothing about it is multiplayer-specific" reasoning this project already applies
elsewhere (see the `RecordInitialState` fix). Not yet confirmed live.

---

## A reconnected multiplayer run skips the Ghost fight entirely (2026-09-08)

**Report**: after the `Task.Yield()` fix above, a live retest ended worse — both players landed on the
Architect scene with no Ghost fight at all, host included. User: "this was working before, what
changed."

**Investigation**: the `Task.Yield()` fix was innocent — `ghostduel.log` (this host's dedicated file)
had not been written to since the *previous* test session, meaning tonight's attempt never got far
enough to even reach the splice's own logging past its very first line. `godot.log` (native, always
written) told the real story:

- `[GhostDuel] MultiplayerLegacyAscensionGhostFightSplice: final-act boss defeated ... injecting the
  Ghost-party fight` fired correctly — the splice *did* trigger.
- Immediately followed by `[ERROR] ... no multiplayer-ladder snapshot report received for
  CHARACTER.IRONCLAD#76561199064564977 at A0.` — `BuildParty` (`MultiplayerLegacyAscensionGhostFightSplice.cs`)
  found no snapshot report for either human and returned `null`, so the whole party fight was skipped
  for everyone, falling through to the real Architect scene.
- Zero other `[GhostDuel]` lines appear anywhere in tonight's `godot.log` before that — specifically
  none of `MultiplayerLegacyAscensionEntry.Start`'s "starting multiplayer Ghost-ascension host flow,"
  `Prefix_TagMultiplayerLegacyAscensionRun`'s "tagged multiplayer run," or either `LADDER-REPORT`/
  `SNAPSHOT-SENT` line. None of `MultiplayerLegacyAscensionEntry`'s own code ran this process at all.
- The join handshake at the top of tonight's log used `ClientLoadJoinRequestMessage`/
  `ClientLoadJoinResponseMessage` — the *resume-an-existing-run* path — not
  `ClientLobbyJoinRequestMessage` (fresh lobby/character-select join). This confirms the user's own
  account: they restarted the lobby (to work around the earlier stranded-overlay bug) and reconnected
  into the *already-embarked* A1 run rather than hosting/joining fresh through character select.
- Confirmed locally: `ghostduel_legacy_ascension_multiplayer/ghost_a0.json` exists on this machine
  (Ironclad, saved 2026-09-08T22:53:58Z) — the local snapshot file this human needs was there the whole
  time. The failure is purely in the *reporting* handshake, not the underlying data.

**Root cause**: `MultiplayerLegacyAscensionEntry._coordinator`/`_armed` are process-local static state,
populated only by Harmony postfixes on `NCharacterSelectScreen.InitializeMultiplayerAsHost`/
`InitializeMultiplayerAsClient` and consumed by `Prefix_TagMultiplayerLegacyAscensionRun`
(`RunState.CreateForNewRun`). None of that runs when a client reconnects into an already-embarked run —
`RunState.Modifiers` (loaded straight from the save) still carries the tagged
`MultiplayerLegacyAscensionModifier`, so the splice itself still fires correctly, but the coordinator
that's supposed to hold every human's previous-Ghost snapshot report was simply never built this
process, so `BuildGhost`'s lookup always misses.

**Fix**: `MultiplayerLegacyAscensionEntry.EnsureSnapshotReportsAsync` — if `_coordinator` is still
`null` when the splice needs it (the reconnect case), attach one now against `RunManager.Instance
.NetService` (confirmed in `ENGINE-NOTES.md`'s M9 research as the correct instance once a run actually
exists) and re-run this human's own `ReportOwnSnapshot`, then wait up to 8 seconds (polling every
200ms) for every human in the run to have a recorded report before proceeding. On the normal fresh-lobby
path this is a no-op — the coordinator already exists and already reported minutes earlier at embark
time — so nothing changes for the case that already worked. `BuildParty`/`BuildGhost` now take the
coordinator as an explicit parameter (returned by the ensure-call) instead of re-reading the static
`MultiplayerLegacyAscensionEntry.Coordinator` field, so there's no window where the two could
disagree. The `Task.Yield()` fix from the section above is unaffected and kept — it addresses a
different, later point in the same method and never got exercised by tonight's failure. Not yet
confirmed live.

---

## The joining client's own snapshot report never recorded itself (2026-09-08)

**Report**: next retest, a genuinely fresh lobby this time (not a reconnect) — host entered the Ghost
party fight normally, but the joining client landed on the Architect scene instead. Not the reconnect
gap above: this was a first attempt, arm/report flow all fired correctly.

**Investigation**: the host's own `ghostduel.log` told the whole story on its own —
`LADDER-REPORT`/arm-query-reply/`tagged multiplayer run` all fired normally, then both
`SNAPSHOT-RECV from=<host>` and `SNAPSHOT-RECV from=<client>` landed a full 11 minutes before the Act-3
boss died. `BuildGhost` on the host successfully built *both* party members and combat started clean —
the coordinator had every report it needed, on the host. The failure had to be specific to the client's
own process.

**Root cause**: `MultiplayerGhostLadderCoordinator.ReportOwnSnapshot` — the host branch always called
`RecordSnapshotReport(_net.NetId, report)` before broadcasting (recording its own report locally,
explicitly, because "a report the host originates locally never goes through [the incoming-relay]
path"), but the non-host (client) branch only ever called `_net.SendMessage(report)`, with no matching
local record. `ShouldBroadcast` only relays a message that arrives *from* a peer to *other* peers —
never back to the sender — so a joining client's own report about itself never loops back into that
client's own `ReportedSnapshots` dictionary. `BuildGhost` runs independently on every peer's own
process and needs an entry for every human, including that peer's own local human — so the client's
lookup for itself failed every single time, deterministically, while the host (who always self-records)
never had the problem. This was a latent, pre-existing gap in `ReportOwnSnapshot`, not something
introduced by either fix above — it just never got a clean shot to surface until both earlier bugs (the
stranded overlay, the "no fight for anyone" reconnect gap) stopped masking it.

**Fix**: `ReportOwnSnapshot` now calls `RecordSnapshotReport(_net.NetId, report)` unconditionally,
before broadcasting, regardless of host or client — collapsing what were two identical `SendMessage`
branches into one. Not yet confirmed live.

---

## Compendium redesign: mode toggle, ascension badges, ghostly background (2026-09-12)

**Request**: two screenshots — the current Compendium page (a flat "A0 A1 A2 A3 A4 MP A0" selector row
mixing both ladders) and the real pre-run ascension picker's per-level flame/number display — asking
for a Single Player / Multiplayer mode button, an ascension-level selector closer to that real picker
(without its flavor-text tooltip), and a greyed-out/ghostly background image of the character being
viewed.

**Research pass first** (`src/Ui/GhostLedgerScreen.cs` already has a nine-pass debugging history in
this exact file from reparenting native widgets before their own `_Ready()` runs — worth checking what
already exists before designing new UI):

1. The real `NAscensionPanel` flame icon's texture resource could not be found. Its node path
   (`%AscensionIcon`, `NAscensionPanel.cs:238`) and `h`/`v` shader-tint mechanism are confirmed (this
   mod's own `LegacyAscensionUiAccess` already reflects into it), but the actual image is authored in
   that panel's `.tscn` scene file — the decompiled source tree is C# only, no scenes. Reparenting the
   whole native panel standalone was already declined in the prior "ascension selector" revision for
   the same reason (untested outside its normal character-select-child context); this pass didn't
   reopen that gamble.
2. The desaturated/"ghostly" look already exists twice in this mod, confirmed working live:
   `EnginePatches.ApplyGrayscale` (the in-combat Ghost creature) and
   `GhostDeckViewerPresenter.CreateDesaturatedMaterial` (a plain `Control`) — both duplicate the
   engine's shared `res://materials/vfx/hsv.tres` material and zero its `"s"` shader parameter.
3. `CharacterModel.CharacterSelectIcon` (decompiled `CharacterModel.cs:150-152`) is a public
   `CompressedTexture2D` — a full character-art render keyed by character id — directly usable as a
   `TextureRect.Texture`, no new resource-loading code needed.

**Built**: `GhostLedgerScreen` now has a Single Player / Multiplayer mode toggle (two plain `Button`s,
`ToggleMode`) that switches which ladder's ascension badges show below it — replacing the old single
flat "both ladders mashed into one row" selector. Each level is a rounded, purple-tinted badge
(`Color.FromHsv(0.78, ...)`, the same hue `LegacyAscensionEntry`/`MultiplayerLegacyAscensionEntry`
already use for this mod's ascension theming) reading "A{level}", brighter with a gold border when
selected — a deliberate look-alike for the real flame icon, not a reuse of it, since that texture isn't
reachable (see finding 1). A full-screen `TextureRect` background shows the currently-viewed character's
`CharacterSelectIcon`, desaturated via the exact proven `hsv.tres`/`"s"=0` technique from finding 2, at
low alpha with a cool tint so foreground panels stay legible — added as this screen's first child
(before `root` and the back button) so it can never win a click hit-test, consistent with this file's
own hard-won "ninth pass" lesson about sibling draw/click order.

Built clean, deployed to the local mods folder. Not yet confirmed live — needs a real Compendium-opened
test after a full game restart.

**Live feedback, same day** (first real screenshot of the above): (1) background "way too zoomed in" —
`StretchMode = KeepAspectCovered` crops the character art to cover this screen's full-window rect, and a
portrait-oriented render covering a wide window crops down to an unrecognizable slice. Changed to
`KeepAspectCentered` (fits the whole image, letterboxed, never cropped). (2) "the background is a semi
transparent overlay from another image" — the low-alpha `TextureRect` had no opaque backing of its own,
so it was blending with whatever this screen's native parent already renders behind it, reading as two
images double-exposed. Fixed by adding a solid dark `ColorRect` behind the character texture (same child
position, before `root`/the back button) so the art only ever blends with a flat known color. (3) "I'd
really like the arrow and flame selector style instead of button" — replaced the per-level button row
with a single flame-badge-and-arrows selector (prev/next `Button`s either side of one purple badge
showing the current level), closer to the real ascension picker's step-one-at-a-time interaction than a
row listing every level at once. Built clean, deployed. Not yet confirmed live.

**Live feedback, next session**: a screenshot of the real character-select screen (Ironclad, full red
scene with a dragon silhouette) clarified the ask — that big scene-filling art, not the small square
roster-icon `CharacterSelectIcon` this screen had been showing (correctly identified by the user as "the
small images in the little buttons at the bottom" — that texture genuinely is the roster-icon asset, not
background art). Research pass confirmed `CharacterModel.CharacterSelectBg` (decompiled
`CharacterModel.cs:148`) is a **scene** path, not a texture — decompiled `NCharacterSelectScreen.cs:
817-826` shows the real screen consuming it via
`PreloadManager.Cache.GetScene(path).Instantiate<Control>(PackedScene.GenEditState.Disabled)`, added
into a dedicated `Control` container, with the previous occupant `RemoveChildSafely`/`QueueFreeSafely`'d
first — no `Initialize(...)` call or external signal wiring afterward. The two concrete bg-root scripts
found in decompiled source (`NCharacterSelectScreenBg`, `NRegentCharacterSelectBg`) depend only on their
own children/`GetTree().Root`, never on `NCharacterSelectScreen` specifically — a materially different,
lower-risk profile than `NAscensionPanel` (which needs an external `Initialize(mode)` call this mod has
twice now declined to gamble on). `GhostLedgerScreen.UpdateBackground` reproduces that exact native
sequence, applying the same desaturation/tint from the previous revision to the instantiated scene's own
root (Godot's `CanvasItem.Material`/`Modulate` inheritance cascades to whatever art nodes are actually
inside, without needing to know that hierarchy) and wrapping the whole thing in try/catch since no
per-character scene beyond those two scripts has been individually verified. Built clean, deployed. Not
yet confirmed live.

**Live feedback, next request**: (1) "make it opaque and greyscale ... ghost like" — the background's
low-alpha cool-tinted `Modulate` was muddying the desaturation rather than reading as clean grayscale.
Set to fully opaque neutral white (`Color(1,1,1,1)`), letting the existing zeroed-saturation shader alone
carry the "ghostly" read — the same way the in-combat Ghost creature itself is opaque grayscale, not
translucent. (2) "the ascension selector should be an exact copy of the ui from the run start menu" — a
materially bigger ask than the background-scene reuse above. Two further research passes (per this file's
own standing caution against reusing native widgets) established: the real `NAscensionPanel`'s arrow
clicks (`DecrementAscension`/`IncrementAscension` → `SetAscensionLevel`) mutate only the panel's own
local `Ascension` property and fire a public `AscensionLevelChanged` C# event — genuinely safe to
redirect to this screen's own level index, no coupling to any live `StartRunLobby`/`RunState`. But the
panel has no standalone packed scene of its own (unlike the background) — all 4 of its real host screens
only ever pull it from their own pre-baked scene by unique name (`%AscensionPanel`). Presented this
tradeoff to the user directly (materially higher risk than the background reuse) rather than deciding
unilaterally; user chose to proceed. `NMultiplayerLoadGameScreen` identified as the lightest-weight of
the 4 host screens (no character-button generation, no controller-manager signal hookups, no
exported-scene-field dependencies) — sourced via its own `Create()` factory, exactly mirroring how this
file already sources `BackButton`/`%DeckHistory`/`%RelicHistory` from a throwaway `NRunHistory`.
`Initialize(...)` deliberately never called (confirmed unnecessary for the arrow-click wiring, which
happens in the panel's own `_Ready()`; `Initialize` only sets an initial level/max, tints the icon, and
for Host/Singleplayer pushes two global hotkey bindings this read-only screen has no use for) —
`SetMaxAscension`/`SetAscensionLevel` instead kept in sync with this screen's own `_level` every
`Refresh()`, safe specifically because `Refresh()` only ever runs after `OnSubmenuOpened()`, by which
point the whole reparented subtree has already entered the live tree and had its own `_Ready()` run.
`Cleanup()` called defensively on `_ExitTree()` (confirmed safe/idempotent even though `Initialize` was
never called). The description sub-label is hidden (shows the real game's own canonical Ascension-N
flavor text, unrelated to and actively misleading for this screen's own saved-ladder levels), and the
icon is tinted with this mod's own established purple hue via the existing `LegacyAscensionUiAccess`
reflection helper, for visual consistency with the rest of this mod's Ghost-Ascension UI. Built clean,
deployed. Not yet confirmed live — this is the least-precedented reuse in this file's history and the
one most worth watching closely on the next real test.

**Live feedback, confirming both risky pieces at once**: a screenshot of a real A2 Defect run showed the
ascension panel rendering and looking correct (confirming the reparented-`NAscensionPanel` gamble paid
off) — but two problems. (1) The background (a lightning-effect Defect scene) rendered in full,
undesaturated color, not grayscale at all. Root cause: the previous revision's own doc comment claiming
"`CanvasItem.Material`/`Modulate` inheritance cascades down to children" was simply wrong —
`Modulate` does cascade in Godot, `Material` does not; setting `Material` only on the instantiated
scene's *root* `Control` never touched whatever texture/sprite nodes actually draw inside it. This mod
already had the correct fix for exactly this problem, just not applied here:
`EnginePatches.ApplyGrayscale` walks every `SpineSprite` descendant of the in-combat Ghost creature and
gives each one its own material directly, rather than assuming inheritance. `ApplyDesaturationRecursively`
reproduces that same walk generically (every `CanvasItem` descendant, since this background scene's
internal node types aren't known without a `.tscn`) rather than one specific native type. (2) "the
ascension selector ... needs to be moved into a better more central and lower location" — it had been
squeezed into a thin `HBoxContainer` row sharing space with the header/mode buttons, and a panel built
for a full character-select screen didn't fit there, rendering cramped and partly clipped (visible in the
screenshot as a single stray arrow near the top-left rather than the whole widget). Pulled out of `root`'s
document flow entirely into its own `CenterContainer` overlay, anchored to a fixed band of the screen
(62%-92% down) independent of how much deck/relic content sits above it, added after `root`/the back
button in child order for the same click-priority reason the back button itself already relies on. Built
clean, deployed. Not yet confirmed live.

**Live feedback, four bugs from one test (2026-09-13)**: screenshots across A0-A4 (solo) showed the
ascension panel rendering and paging correctly, confirming that gamble sound — but exposed real
regressions in everything touched since.

1. **Back button stopped responding.** Root cause: the new full-width ascension overlay (anchored
   62%-92% down, spanning the *entire* width) covered the same screen region the back button actually
   lives in (bottom-left), and — added *after* the back button — won every hit-test there, exactly the
   "later sibling wins hit-testing" lesson this file already learned twice before (the "fourth"/"ninth"
   passes), reintroduced by this session's own change. `MouseFilter.Pass` did not save it:  that filter
   only bubbles an unhandled event up to the control's own *parent*, not sideways to an earlier sibling
   drawn underneath — a genuine misunderstanding, now corrected in the code's own comment. Fixed by
   reordering the overlay *before* the back button (restoring the back button as the true topmost
   element) and, defensively, insetting the overlay's anchors well clear of that corner on both axes.

2. **Ascension panel invisible on the multiplayer tab.** Root cause: decompiled `NAscensionPanel
   .SetMaxAscension` does `base.Visible = maxAscension > 0` natively — with zero or one multiplayer
   snapshot saved, `SetMaxAscension(0)` hides the *whole panel*, not just its arrows. This mod already
   has a Harmony postfix for this exact native behavior (`LegacyAscensionEntry
   .Postfix_KeepPanelVisibleAtZero`), but it's gated behind that class's own `_armed` static field, never
   true in this screen's read-only browsing flow — so the existing fix never applied to this reparented
   instance. Fixed with the same one-line correction, applied locally after every `SetMaxAscension` call
   in this screen's own `Refresh()`.

3. **Background still not desaturated, and new glitches** — some backgrounds (a Defect scene) showed
   hard-edged overlapping geometric shapes and what looked like unrelated art bleeding through. Root
   cause: the previous revision's fix (walk every descendant, overwrite its `Material` directly) was
   actively harmful, not just occasionally ineffective — some descendant nodes already carry their own
   materials for legitimate masking/compositing/hover-skin effects (`NRegentCharacterSelectBg`'s own doc
   comment already flagged a hover-skin-swap mechanism), and overwriting those broke the composition
   rather than tinting it; other characters likely use `MegaSprite`/Spine art, where the correct target
   is `GetNormalMaterial`/`SetNormalMaterial`, not the plain `CanvasItem.Material` the walk assumed
   everywhere. Replaced entirely: the untouched background scene now renders into an offscreen
   `SubViewport` (nothing inside it is ever touched, so every internal material/mask/hover-effect keeps
   working exactly as authored), and that viewport's own composited output is displayed as one flat
   texture through a single `TextureRect`, with the desaturation material applied to only that one node —
   a single ordinary texture is exactly the case `GhostDeckViewerPresenter.CreateDesaturatedMaterial`
   already handles correctly elsewhere in this mod.

Built clean, deployed. Not yet confirmed live — three independent, previously-unexercised code paths
changed at once (panel visibility, background compositing, click ordering), each worth checking
individually on the next test rather than assuming all three landed together.

**Live feedback, confirming three of four fixes**: a fresh screenshot (grayscale Ironclad, mode buttons,
back button all correct) confirmed the desaturation, visibility, and click-order fixes above all landed.
Only the ascension panel's own position remained off. Rather than keep guessing coordinates blind, asked
the user directly what was wrong; they pointed back at their own original reference screenshot (the real
pre-run ascension picker) and said to match its actual layout. Measured that reference directly this
time instead of estimating from memory: the arrow/flame/arrow row sits at roughly x=32%-67% of screen
width (center ≈50% — genuinely screen-centered, not left-aligned under the description panel as guessed
two revisions ago) and y=70%-77% down — level with the back/confirm buttons' own height, but safely clear
of them horizontally since those live in the outer ~10% margins. `_ascensionOverlay`'s anchors tightened
to `AnchorLeft=0.3, AnchorRight=0.7, AnchorTop=0.68, AnchorBottom=0.8` to match. Built clean, deployed.
Not yet confirmed live.

**Second correction, same day**: user reported no visible change. A live screenshot comparison (their own
screenshot against the reference, both measured directly) showed the vertical read was already right
(~75% down, matching the reference's ~73-75%) — the "no different" report was because the first
correction's shift was real but small. The horizontal read was the actual miss: the reference image's
*icon itself* sits at ~36% of screen width, not ~50%. That 50% figure came from centering the whole
native row (icon + arrows + description text), but this screen hides that description box entirely (an
earlier explicit request to drop the flavor text) — so centering the remaining, much narrower icon+arrows
cluster on the screen's own midpoint put the icon at true center, well right of where it sits in the
reference. Recentered `_ascensionOverlay` at that same ~36% mark instead
(`AnchorLeft=0.16, AnchorRight=0.56`, vertical unchanged). Built clean, deployed. Not yet confirmed live.

**Size mismatch, same day**: user flagged a size difference too. Measuring both screenshots directly:
the reference's flame icon is ~4.2% of screen width; this screen's reparented panel renders at ~3.0% —
about 70% of the reference's size. `_ascensionPanel` is the same native scene either way, so this is
presumably an ancestor `Scale` present in whatever screen the reference was captured from that this
screen's own unscaled hierarchy doesn't reproduce. Corrected empirically from the measured ratio
(`AscensionPanelScale = 1.4`, i.e. `1 / 0.7`) rather than guessed, applied to `_ascensionPanel.Scale`
after its own layout pass (same 0.6s-after-open deferral already used for the back-button correction,
for the same reason — `Size` isn't reliable before then) with `PivotOffset` set to half its real `Size`
first so the scale expands from its visual center rather than its top-left corner.

**Leftover background box**: the same live report also flagged a semi-transparent rounded-rectangle
background still showing where the (deliberately hidden) description text used to sit — visibly not
centered on the now-narrower visible widget. Confirmed this cannot be part of
`HBoxContainer/AscensionDescription` (already hidden, description text included — Godot never renders
anything under a `Visible=false` Control) — must be a separate node with no C# field reference in
`NAscensionPanel.cs`, hence no name discoverable from decompiled source. Rather than guess at a name
with no evidence (this exact class of guess has already taken several rounds this session), added a
one-time diagnostic dump of `_ascensionPanel`'s live node tree (name/type/visible/position/size, once
per open) to `GhostLog`, mirroring this file's own "seventh pass" diagnostic-before-another-guess
precedent for the back button. Built clean, deployed. Not yet confirmed live — next test's log will name
the actual node so this can be fixed directly instead of guessed.

**Confirmed scale/position correct, one more report**: a live screenshot confirmed the scale (1.4x) and
recentered position (~36% horizontal) both landed correctly — icon now visibly matches the reference's
own proportions. Two remaining items: (1) the leftover background box is still visibly off-center from
the now-repositioned widget (expected — still waiting on the diagnostic log to name it before fixing).
(2) New report: "don't live resize it, make it right the first time the page loads" — the 0.6s timer
used for the scale/pivot correction (mirroring the back-button fix) let the panel visibly render at its
native size for half a second before snapping to the corrected scale. Root cause of needing *any* delay
was Size not being reliable until the panel's own layout pass runs — but that pass completes on the next
idle frame, not hundreds of milliseconds later; the 0.6s figure was borrowed from the back button's own
delay, which is really waiting on that button's separate 0.35s show *animation*, not layout. Switched
this correction to `Callable.From(...).CallDeferred()` (next idle frame, after layout but before the
frame is drawn) instead of a fixed timer, so the first frame the user ever sees already has the correct
scale. Built clean, deployed. Not yet confirmed live.

**Background box identified from the actual log (2026-09-13)**: rather than wait for the user to paste
the diagnostic dump, read `godot.log` directly (already have shell access to this machine) and found the
mystery node named plainly: a `NinePatchRect` called "Background", a sibling of `HBoxContainer` —
`Position=(14,5) Size=(601,118)` in the panel's own local, unscaled coordinates. It backs the *original*
icon+description card as a whole, sized for the full card width, entirely unrelated to
`HBoxContainer/AscensionDescription` (already correctly hidden). The same dump showed `HBoxContainer`
itself (the real icon+arrows) at `Position=(-120,-32) Size=(240,64)` — already centered exactly on the
panel's own local origin. Resized/repositioned "Background" to match that same centered span
(`Position=(-130,-40) Size=(260,80)`, a small margin around it) instead of guessing further.

This also explained the scale/pivot correction's own indirection: `_ascensionPanel`'s root `Size` reports
`(0,0)` unconditionally (confirmed in the same dump, taken well after layout) — never computed by any
container, so `PivotOffset = Size / 2` was always a no-op regardless of how long it waited. It happened
to look right only because `HBoxContainer` was already centered at local (0,0) anyway. Replaced with an
explicit `PivotOffset = Vector2.Zero` — same value, no longer accidental, and no longer dependent on a
`Size` that would never change — so the whole appearance setup (hide description, resize background,
tint icon, set pivot/scale) now runs synchronously in `SetUpAscensionPanelAppearance()` with no deferral
at all, and the now-answered diagnostic dump was removed. Also confirmed from the same data: the "number
not perfectly centered in the flame" and "icon possibly smaller" reports are native scene values
(`AscensionLevel` label offset `(-2,13)` inside a same-size `AscensionIcon`; native icon size 64×64,
which at the existing 1.4× scale renders at ~90px, matching the reference's own measured ~85px) —
neither introduced by this mod. Built clean, deployed. Not yet confirmed live.

**Polish pass (2026-09-13)**: screenshot confirmed the "Background" resize landed correctly — the card
now hugs the icon/arrows and reads as one cohesive unit. Two follow-up requests: "fix the flame
formatting" (the tight `-10/-8`-margin hug read as cramped, icon and arrows crowding the card's own
edges — widened to `Position=(-150,-50) Size=(300,100)` for real breathing room) and "make the single
player and multiplayer buttons better formatted" (they were plain, unthemed default Godot `Button`s).
Added `CreateModeButtonStyleBox`, a rounded StyleBoxFlat reusing this mod's own established
`AscensionHue` purple (the same hue already used for the level icon's own tint and, previously, the
now-removed look-alike badges) — darker/unfilled when unselected, brighter with a gold border when
selected — plus real content margins and a 10px gap between the two buttons, so the pair reads as a
deliberate themed toggle rather than two default widgets glued together. Built clean, deployed. Not yet
confirmed live.

**Flame/number overlap, same day**: a close-up screenshot showed the level number visibly overlapping
the flame glyph's own edges rather than sitting inside it — user: "just make the flame 25% bigger and
adjust the number field to be centered on that." Per the earlier diagnostic dump, `AscensionLevel` (the
number) is a *child* of `AscensionIcon` (the flame), both reporting the same native 64x64 `Size`, with
`AscensionLevel` at a slightly-off-center native `Position=(-2,13)`. Scaling `AscensionIcon` by a new
`AscensionIconExtraScale = 1.25` (on top of the whole panel's existing 1.4x) carries the number along
with it at the same factor since it's nested — preserves their native relative proportion, just bigger —
while separately resetting `AscensionLevel.Position` to `Vector2.Zero` (exact overlap, given the equal
sizes) corrects the native offset that was the actual source of the visible overlap. Built clean,
deployed. Not yet confirmed live.

**Correction, same day**: live feedback ("too high now") showed that zeroing the label's `Position` was
itself the mistake, not the fix — the flame glyph is visually bottom-heavy (wide base, tapering to a
point at top), so a number centered on the icon's *bounding box* sits above the flame's actual visual
center. The native `Position=(-2,13)` offset was already the correct compensation for that asymmetry, not
a bug — the real source of the original overlap complaint was the icon being too small relative to the
number (already fixed by `AscensionIconExtraScale`), not this offset. Reverted to the native value.
Built clean, deployed. Not yet confirmed live.

**Second correction, same day**: the reverted native `(-2,13)` then read as "too low" — the opposite
problem from the zeroed `(0,0)` attempt. Comparing a fresh vanilla "No Ascension" reference screenshot
against this screen's own render, both showing the same level-0 digit, confirmed the reference sits
close to centered within the flame's rounded body — nowhere near as low as the native offset placed it.
Since the two tried values (`0` and `13`) bracket the correct one from opposite directions, interpolated
roughly halfway (`y = 7`) rather than guessing a third arbitrary value. Built clean, deployed. Not yet
confirmed live.

---

## Standing quality bars

Applied at every milestone, checked at every gate:

- **Patch budget.** Every Harmony patch is justified in `ENGINE-NOTES.md` by a specific engine fact
  and guarded by a mode check as its first statement. A patch with no `file:line` justification gets
  deleted.
- **Inert when off.** With the mode unused, the mod allocates nothing and rewrites no engine
  property. Verify by profiling one ordinary combat with and without the mod loaded.
- **No content emulation.** If the mod special-cases a card, relic or character by id, it is wrong.
- **File size.** Nothing over ~300 lines. `GhostRuntime` reached 828 (POSTMORTEM F19).
- **Reflection.** Each reflected private name lives in one file with a load-time existence
  assertion, so a game update fails loudly at load.
- **Session lifetime.** One `GhostSession`, disposed on every exit path including aborts. Assert it
  is null after each.
- **One controller.** Choosers are pluggable; the turn skeleton is not duplicated.
- **Version discipline.** Ship a version only when its milestone gate passed. Record in the release
  notes what was *observed*, not what was written.
- **Doc sync.** A milestone is not done until `ENGINE-NOTES.md` records what was learned and the
  relevant spec section matches the code.

---

## Decisions needed from you

Blocking the milestone shown. None blocks M0 or M1.

| # | Decision | Blocks | Recommendation |
| --- | --- | --- | --- |
| D1 | Permanent mod `id` — `GhostDuel` or keep `LegacyAscension`? | M1 | `GhostDuel`; display name can change later, the id cannot |
| D2 | Status expiry rule (a) applying-side or (b) afflicted-side + after-queued-damage? | M6 | (b) — see `COMBAT-RULES.md` §6 |
| D3 | Output scaling for Ghost HP / damage / Block? | M7 | Build unscaled behind a `1.0` seam; decide against a working fight |
| D4 | Should M1 ship publicly as v0.1.0, or stay a local dev build until M3 makes it legible? | M1 | Ship it — a working core beats a pretty broken one |
