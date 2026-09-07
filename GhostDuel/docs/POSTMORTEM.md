# First-pass postmortem — `LegacyAscension` v0.9.4

Source reviewed: `../STS2Ghostmod/` — 8,482 lines of C# across 59 files, 19 released ZIPs
(v0.1.0 → v0.9.4), 9 design documents, a 288-line "verifier".

The first pass is not a failure of ideas. The central architectural bet is correct and worth keeping.
It is a failure of **sequencing and of evidence discipline**: a large, interdependent feature set was
built, documented in the present tense, and released nineteen times on top of a core loop that had
never once been observed working.

---

## 1. What the first pass got right — keep all of this

1. **The enemy-side `Player` model.** One real `Player` whose `Creature.Side == CombatSide.Enemy`
   owns HP, Block, powers, relics, energy, piles, orbs and pets. There is no Monster proxy and no
   state mirroring. This is verified-sound against the decompiled engine (see
   [`ENGINE-NOTES.md`](ENGINE-NOTES.md)) and it is the only design that satisfies "every card and
   relic works." v0.9.0 removed an earlier hidden-Player-plus-visible-Monster proxy; do not go back.
2. **`AsyncLocal` construction scope for the side flip.** A depth-counted scope around
   `Player.FromSerializable` with a postfix on the `Creature(Player,int,int)` constructor is a
   narrow, correct way to place a Player on the enemy side without touching normal construction.
3. **Correctly identified engine incompatibilities.** Three patches are genuinely required and
   correctly targeted; the reasons are confirmed in the decompiled source:
   `Creature.TakeTurn`, `Creature.AfterAddedToRoom`, `CombatManager.AfterCreatureAdded` all
   unconditionally dereference `Monster` for an enemy-side creature.
4. **The delayed-resolution duel concept.** Direct damage queues, everything else resolves
   immediately. This is the right way to make two player-style turns legible in an intent-based game.
5. **Damage packet grouping.** Separate attack cards aggregate into one hit; repeated hits from a
   single card stay multi-hit. `GhostDamagePlan` (114 lines) is small, pure, and worth porting.
6. **Vulnerable-in-effect-order.** Capturing Vulnerable at the moment each damage effect fires — so
   Bash queues 8 and a following Strike queues 9 — is the correct semantics and was reasoned out
   properly.
7. **A stripped debug encounter as the validation target.** v0.9.4 finally did the right thing by
   reducing to a stock Ironclad and a left-to-right controller. That instinct arrives eight
   milestones too late, but it is the correct instinct.
8. **Refusal to substitute missing modded content.** `LegacyGhostStore.FindMissingContent` reports
   missing card/relic/character ids and blocks the encounter rather than silently swapping cards.

---

## 2. Process and documentation failures

### F1 — Docs assert unvalidated behavior as fact

`README.md` opens with ~40 bullets under **"Implemented behavior"**, including "Ghost turns enter the
native Start, AutoPrePlay, Play, AutoPostPlay, End, and None player phases", "Buffs and debuffs
resolve immediately through the native power system", "Vulnerable is captured in native card-effect
order, so Bash stays 8 while a following Strike becomes 9."

`CURRENT_PROGRESS_AND_IRONCLAD_BASELINE.md` then states: *"In all in-game builds tested before
v0.9.4, the Ghost did not actually play its hand cards."*

Every one of those forty bullets sits downstream of a card actually being played. None of them can
be true. `PLAYER_VS_GHOST_MECHANICS.md`, `Enemy_Side_Player_Ghost_Architecture.md` and
`Ghost_Duel_Combat_Damage_Status_Timing.md` are all written in the same confident present tense
("Status: implemented in v0.9.4"). The result is a documentation set that cannot be used to plan,
because it does not distinguish *written* from *working*.

### F2 — The verifier manufactures false confidence

`verification/Program.cs` runs on every build and prints
`PASS: enemy-side real Player architecture, ... verified.` What it actually checks:

- JSON manifest fields, including `version == "v0.9.4"` — a string compare.
- `Harmony.PatchAll` succeeds and `patched.Count >= 27` — that patch *targets resolve*, not that
  patches are *correct*.
- Reflection existence checks that restate the code: `GhostRuntime` has a method named
  `PerformSimpleDebugTurnAsync`; `LegacyGhostMonster` does not exist; a shader constant contains the
  substring `vec3(luminance)`; six `.aiconfig` files exist on disk.
- A handful of genuinely useful pure-function tests on `GhostDamagePlan` and packet display math.

Only the last category has value. The rest are tautologies that pass whether or not the mod works,
and they were reported as verification for nineteen releases.

### F3 — The specs contradict each other and were never reconciled

| Question | `Legacy_Ascension_STS2_Mod_Spec.md` / `Codex_Ghost_AI_Integration.md` | `README.md` / verifier |
| --- | --- | --- |
| Ghost max HP | `SavedMaxHp × 5.0` ("83 → 415") | "the exact saved value" |
| Ghost damage | `× 1.25` at the output layer; AI must value scaled output | "no custom multiplier" |
| Ghost Block | `× 2.0` at the output layer | "exact native calculated values" |
| Weak/Vuln/Frail decay | end of the **applying** side's turn | after the **afflicted** side's turn |

The verifier actively asserts that no field named `*HpMultiplier` / `*DamageMultiplier` /
`*BlockMultiplier` exists — locking in the reversal — while the spec's Definition of Done still
requires output scaling and the AI brief still requires the planner to reason about it. Nothing
records that the decision changed or why. Balance is now undefined.

### F4 — Scope ran nine milestones ahead of the core loop

The spec's own Phase 1 is "Hard-Coded Ghost … prove player attack accumulation, Ghost hand, Ghost AI
turn, mirrored card display, Ghost block, delayed Ghost intent, player damage resolution." Phase 1
was never cleared. Built anyway:

Necrobinder Osty as a native pet with `DieForYouPower` Soul Link routing and owner-death cleanup ·
Defect orb queue with Lightning passive/evoke damage and deferred hit VFX · Regent Stars via Divine
Right · multiplayer Ghost parties with party-size matching and host authority · a full grayscale
`canvas_item` shader with Necrobinder moving-texture exemptions · custom main-menu buttons ·
`Neow.GenerateInitialOptions` patching to restore starting-relic choices · a JSON progression store
with schema versioning · ~4,600 lines of imported semantic AI with six per-character tuning files.

Each of those depends on the unproven loop. When the loop failed there were 38 Harmony patches and
twenty interacting subsystems in which to look for the cause.

### F5 — Diagnostics arrived at v0.9.4

`CURRENT_PROGRESS` correctly names the four failure buckets a log would distinguish:

```text
No hand entry       -> debug draw/pile population problem
No Attempting entry -> legality or target-validation problem
No Spent entry      -> resource-spending problem
Spent but no Played -> OnPlayWrapper/card-effect problem
```

That logging exists only in `PerformSimpleDebugTurnAsync`, added in the nineteenth release. Eighteen
releases were shipped with no way to tell where the turn died.

### F6 — No scripted path to the failure

Reproducing the bug requires launching the client, clicking a custom menu button, picking Ironclad,
entering the first room and ending a turn — by hand, every time. The game ships a non-interactive
harness (`AutoSlayer`, `TestMode`, `NonInteractiveMode`, `FastModeType.Instant`, `ICardSelector`);
none of it was used.

---

## 3. Engineering failures in the code

### F7 — Core combat views are patched unconditionally, for every combat in every run

`EnemySideGhostPatches.cs` postfixes the getters for `CombatState.Players`, `PlayerCreatures`,
`Allies`, `Enemies` and `HittableEnemies`. `Players` and `PlayerCreatures` **have no
`LegacyEncounterContext.IsActive` guard at all** — they rewrite and reallocate a `List<>` on every
access, in every combat, whether or not a Ghost exists, whether or not the mode is even enabled:

```csharp
[HarmonyPatch(typeof(CombatState), nameof(CombatState.Players), MethodType.Getter)]
private static void Filter(CombatState __instance, ref IReadOnlyList<Player> __result)
{
    Player? actor = GhostRuntimeService.CurrentGhostCardOwner;
    CombatSide side = actor?.Creature.CombatState == __instance ? actor.Creature.Side : CombatSide.Player;
    __result = EnemyGhostCombatViews.RawSide(__instance, side)
        .Where(creature => creature.Player is not null).Select(creature => creature.Player!).ToList();
}
```

`CombatState.Players` is read on hot paths — `CardModel.CanPlay`, `SetPhaseForAllPlayers`,
`IterateHookListeners` sizing, monster HP scaling. Both getters also reach the private `_allies` /
`_enemies` fields by reflected name. A mod that is inert outside its own mode must not do this.

### F8 — `AsyncLocal` view rewriting plus fire-and-forget tasks

`BeginGhostCardExecution` sets an `AsyncLocal<Player?>`; while it is set, `Allies`/`Enemies`/
`Players`/`PlayerCreatures` change meaning for *anything* running in that logical context. Card
presentation is then launched as `_ = TaskHelper.RunSafely(GhostCardPresentation.ShowAsync(...))`,
which captures that context and outlives the scope. The set of code that observes an inverted combat
state is therefore not statically knowable. This is the hardest part of the first pass to reason
about and a strong suspect for nondeterministic behavior.

### F9 — The Ghost is never positioned on the enemy side of the screen

Confirmed in the engine (`NCombatRoom.cs:722`): `AddCreature` puts **any** `creature.IsPlayer` node
into `_allyContainer`, and only sets a position when `creature.SlotName != null`.
`LegacyGhostEncounter` generates no monsters and therefore no slots, so a Ghost creature's
`SlotName` is null and it is never positioned. The mod's postfix then reparents it with
`keepGlobalTransform: true`, which *preserves the ally-side position it was just given*.

Expected on screen: the Ghost renders on the player's side, plausibly on top of the human character.
No enemy slot marker is ever consulted. Add a slot to the encounter, or set the position explicitly
from the enemy container's marker.

### F10 — Hardcoded screen coordinates

`GhostCardPresentation` places the card preview at `GlobalPosition = (1510, 360)` and tweens to
`(1080, 430)`, with `Scale = 0.28 → 0.72`. Anchored to nothing; wrong at any resolution but the
author's.

### F11 — Six-plus dependencies on compiler-generated and private names

`<Side>k__BackingField`, `<NetId>k__BackingField`, `<Modifiers>k__BackingField`, `CombatState._allies`,
`CombatState._enemies`, `NCombatRoom._enemyContainer`, `NCustomRunModifiersList._modifierTickboxes`,
`NSubmenuButton._locKeyPrefix`, `NSubmenu._stack`. Each breaks silently on a game update — and
`Creature.Side` is a get-only property in the decompiled source, i.e. the engine treats it as
immutable and code may cache decisions made from it.

### F12 — Two divergent turn controllers, so the tested one proves nothing

`GhostRuntime` contains both `PerformTurnAsync` (the real path: native energy hooks, `Hook.BeforeHandDraw`,
`Hook.ModifyHandDraw`, Innate/bottom-of-pile handling, `CardPileCmd.Draw`, auto-pre/post-play model
hook passes, orb hooks, `FinishHandAsync` with Ethereal/retain) and `PerformSimpleDebugTurnAsync`
(the debug path: `combat.ResetEnergy()`, a hand-rolled `DrawSimpleDebugHand` using
`DrawPile.RemoveInternal` / `Hand.AddInternal`, a hand-rolled discard flush, no hooks at all).

The debug path deliberately bypasses `CardPileCmd`, every pile-change hook, and every lifecycle
hook — which are exactly the surfaces the real path needs. Clearing the debug gate would not have
told anyone whether the real path works. Two controllers also means two places for every future fix.

### F13 — `DamageResult` is fabricated

`DelayedDamagePatch` skips the single authoritative `CreatureCmd.Damage` overload and returns a
synthetic result so that cards which inspect damage keep working:

```csharp
internal static DamageResult CreateDeferredResult(PendingDamagePacket packet) => new(packet.Target, packet.Props)
{
    BlockedDamage = Math.Max(0, (int)packet.AmountForDisplay)
};
```

`BlockedDamage` is set to the entire queued amount purely so that `TotalDamage` reads back the right
number. Any card that asks about unblocked damage, HP actually lost, whether the target died, or hit
count receives a lie. This is load-bearing for the whole delayed-damage design and is the most
likely source of subtle per-card breakage once cards beyond Strike/Defend/Bash are in play.

### F14 — The queued-damage math is lossy and identity-matched

`CreateDamagePacket` runs `Hook.ModifyDamage` at play time, then *divides out* the current Weak
multiplier to recover a "pre-Weak base" (`lockedBase = playTimeModified / weak`) so Weak can be
re-applied late. That division loses precision, is undefined if a multiplier reaches 0, and silently
assumes Weak is the only modifier that may change between play and resolution.

At resolution, `QueuedDamageModifierPatch` suppresses the second `Hook.ModifyDamage` pass by matching
a tuple of `(target, dealer, amount, props, cardSource)` — an ambient identity check that can both
false-positive on a coincidentally identical nested hit and false-negative if anything nudges the
amount.

### F15 — Global static mode state with leaky lifetime

`LegacyEncounterContext` is process-global static state (`SourceRun`, `_ghostParty`, `IsActive`,
`IsDebugEncounter`). `IsActive` arms the global damage-capture patch. `End()` is only reached from
`ProceedFromTerminalRewardsScreen` and `RunManager.OnEnded`; abandoning a run, quitting to menu, or
any error path leaves the capture patch armed for the next combat. `GhostRuntimeService` adds two
static dictionaries plus three `AsyncLocal`s with the same lifetime problem.

### F16 — Mode entry is UI surgery held together by a static flag

`LegacyGameModePatches.cs` duplicates `NSubmenuButton` nodes via `Duplicate()`, nulls
`_locKeyPrefix` by reflection, then manually lays out five buttons with hardcoded pixel gaps
(`const float gap = 12f`) and a `buttons.Single(b => b.Name == "StandardButton")` lookup that throws
if any other mod touches that menu. Mode selection is a `static PendingLegacyMode _pendingMode`
consumed in a `StartNewSingleplayerRun` prefix; backing out of character select leaks it, and the
mitigation is to hook the three stock buttons' `Released` signals to clear it.

Debug mode is also entangled with the shipping mode: it is a `CustomModifierModel` with
`[SavedProperty]` fields, forces `GameMode.Custom`, and needs `Neow.GenerateInitialOptions` patched
plus two `NCustomRunModifiersList` patches to stay hidden.

### F17 — The imported AI is 4,600 lines of dead weight in the only mode being tested

`src/AdaptedAi/` is 40 files under namespace `AITeammate.Scripts` (the upstream namespace was never
renamed), including a 675-line combat config, a 591-line card/potion config, a 549-line scorer, a
546-line card resolver and a 503-line beam planner, plus six `.aiconfig` files. The verifier asserts
those types and files exist. The debug path never calls any of it. It was imported before the thing
it decides for could run.

### F18 — Per-decision AI cost

`GhostAiController.ChooseNextCard` constructs a fresh `CardResolver` and four repositories, resolves
every card in hand, rebuilds enemy/power/relic dictionaries, reloads the character config, and runs
a beam plan — **once per card played**, up to `MaxCardPlaysPerTurn = 40` times per turn. It also
keys card identity on `RuntimeHelpers.GetHashCode(card)`, an opaque value that cannot be correlated
with anything else and is meaningless in a log.

### F19 — God objects with no test seam

`GhostRuntime` is 828 lines mixing turn lifecycle, AI orchestration, damage queueing, Godot intent
node creation, card-preview animation and raw pile manipulation. `GhostRuntimeService` is 344 lines
of static mutable registries. Nothing can be exercised without a live Godot combat room, which is
precisely why the verifier could only test string constants.

### F20 — Progression store risks silent data loss

`LegacyGhostStore` serializes an entire `SerializableRun` per ascension level into
`user://legacy_ascension/profile_N/legacy_ascension.json` — unbounded growth, and far more than the
player build actually needs. On any `SchemaVersion` mismatch it logs and replaces `_cached` with a
fresh empty `StoreData`; the next `Save()` then overwrites the file, destroying every historical
Ghost. There is no migration and no backup.

---

## 4. Suspects for the original bug, ranked

The first pass never determined why the Ghost took an empty turn. From reading the code against the
engine, in order of likelihood:

1. **An exception inside `PerformTurnAsync` was swallowed.** `GhostRuntimeService.PerformTurnAsync`
   wraps the whole turn in `catch (Exception error)` and logs, then restores `Phase = None` and
   returns — which looks exactly like "took an empty turn and passed control back." Before v0.9.4
   there was no per-step logging to say how far it got. **Check the log for the single
   `Enemy-side Ghost turn failed for …` line first; the answer may already be there.**
2. **Empty hand.** `AttachToCombat` runs in the `CombatRoom.EnterInternal` *prefix*, before
   `CombatManager.SetUpCombat` — and `SetUpCombat` iterates `state.Players`, which the mod patches
   to exclude Ghosts, so nothing re-populates them. If `AddCard`/`CloneCard`/`PopulateCombatState`
   ordering is wrong, or the draw pile is empty, the hand snapshot is empty and every subsequent log
   line is absent.
3. **`CanPlay` returns false for everything.** It requires `Owner.PlayerCombatState != null`,
   sufficient resources, and `Hook.ShouldPlay`. If `ResetEnergy` did not take, or a relic hook
   objects, every card is skipped — with no `Attempting` line.
4. **A hang, not an empty turn.** `OnPlayWrapper` awaits `CombatManager.Instance.WaitForUnpause()`
   and `Cmd.CustomScaledWait`. If a `Tween`/`Cmd.Wait` in the fire-and-forget preview or in
   `NIntent` never completes, the turn stalls rather than finishes — visually similar.
5. **Node placement (F9) making a working turn look broken.** If the Ghost renders on the ally side
   with intents in the wrong container, cards could be playing correctly and invisibly.

Do not fix these in the old tree. Reproduce them in M1 of the new tree, where there are five patches
and one controller instead of thirty-eight and two.

---

## 5. Salvage list

**Port, largely as-is** (small, pure, or verified-necessary):

| From | Why |
| --- | --- |
| `GhostConstructionScope.cs` (32 lines) | Correct, minimal side-flip scope. |
| `GhostDamagePlan.cs` (114 lines) + its pure tests in `verification/Program.cs` | Correct grouping semantics; the one genuinely valuable test block. |
| `EnemyGhostCreatureConstructionPatch`, `EnemyGhostAfterAddedPatch`, `EnemyGhostManagerAfterAddedPatch`, `EnemyGhostAutomatedTurnPatch` | The four verified-necessary Monster-assumption patches. |
| `Sts2PathDiscovery.props` | Sound cross-platform game/BaseLib path discovery. Reuse verbatim. |
| `LegacyGhostStore.FindMissingContent` | Right policy for missing modded content. |
| The `[GhostDebug]` log-point taxonomy in `CURRENT_PROGRESS` §Diagnostic logging | Build this in at M1, not M19. |

**Rewrite:** the turn controller (one, not two); damage queueing (no fabricated `DamageResult`,
no divide-by-multiplier); combat-view adaptation (targeted and guarded, not global postfixes);
node placement (real enemy slot); mode entry (see [`PLAN.md`](../PLAN.md) M0).

**Do not port until its milestone:** everything in `src/AdaptedAi/`, `config/ai-behavior/`,
`GhostOstyPatches`, orb/Lightning handling, `SourceOwnedStatusService`, `GhostVisualPatches`,
`LegacyGhostStore`, `LegacyEncounterService`, `LegacyGameModePatches` menu construction,
`PlayerQueuedIntentService`.
