# Architecture

Facts cited here are established in [`ENGINE-NOTES.md`](ENGINE-NOTES.md). Anything not traceable
there is a design choice or an expectation, and is marked as such.

---

## 1. The one invariant

> **The Ghost is a single real `Player` whose `Creature.Side == CombatSide.Enemy`. That creature is
> the only gameplay entity. Nothing about a card, relic, power or class resource is reimplemented
> mod-side.**

It owns HP, max HP, Block, powers, debuffs, relics, energy, Stars, every card pile, orbs, pets and
class state. It is the creature the human targets, the creature damage resolves against, the creature
whose death ends combat, and the `Owner` of every card it plays.

No hidden Player. No visible Monster. No mirroring, no HP copy, no Block transfer. The first pass
removed a proxy design at v0.9.0 and was right to; do not reintroduce one.

```text
CombatState
├── _allies   (CombatSide.Player)   human Player creature(s)
└── _enemies  (CombatSide.Enemy)    Ghost Player creature(s) + Ghost-owned pets
```

**Test for any proposed change:** if it makes the mod special-case a particular card, relic or
character, it is wrong. The mod's job is to make the *situation* legal, not to emulate content.

---

## 2. Why this works

Three verified engine properties carry the design:

1. `CombatState.AddCreature` routes by `Side`, so a Player creature can occupy the enemy list.
2. `CombatState.IterateHookListeners()` walks `_enemies` too and, for a creature with a `Player`,
   registers its relics, potions, orbs and all pile cards. **Ghost relics and cards get ordinary
   combat hooks for free.**
3. `CardModel.IsValidTarget` compares `target.Side != Owner.Creature.Side`. Player card targeting is
   already owner-relative.

Together these mean the mod is not building a combat system. It is removing three monster
assumptions and supplying a controller.

---

## 3. Construction

```text
GhostConstructionScope.Begin()            AsyncLocal depth counter
  Player.FromSerializable(snapshot)       (or Player.CreateForNewRun<T> in debug)
    -> Creature(Player,int,int) ctor
       -> Harmony postfix: if scope active, Side = Enemy
  scope disposed
```

Port `GhostConstructionScope` verbatim from the first pass. It is correct and minimal.

Setting `Side` requires writing a get-only property's backing field. Accept that one reflection
dependency; it is the single unavoidable one. **Isolate it in one file with a startup assertion that
the field still exists**, so a game update fails loudly at load rather than mysteriously in combat.

Debug construction: `Player.CreateForNewRun<Ironclad>(UnlockState.all, netId)` inside the scope
gives stock HP, energy, deck and starting relic. The first pass created it *outside* the scope, then
round-tripped through `ToSerializable`/`FromSerializable` to get the side flip; construct inside the
scope directly instead.

Assign a collision-free `NetId` (the first pass used `ulong.MaxValue - 1024 - index`). Keep that.

---

## 4. The patch set — target five to start, no numeric ceiling (2026-09-02)

| # | Target | Kind | Why |
| --- | --- | --- | --- |
| P0 | `NMainMenu._Ready` | postfix | M0 debug harness. **Revised 2026-09-02**: originally a `NGame.GameStartup` prefix reading a `--ghostduel-debug` command-line flag; in practice there was no reliable way to pass that flag through the Steam launch path being used to test, so this now duplicates one existing button in the main menu's `%MainMenuTextButtons` list (anchored to that container's own layout, no hardcoded coordinates — POSTMORTEM F16 is the cautionary tale for doing more than one plain button) and wires it to the same `DebugBoot.StartDebugRunAsync` the flag used to call. Always adds the button when the main menu loads — this one is not mode-guarded the way P1-P7 are, since a permanently-visible dev button *is* the point; it should be pulled before any public release per PLAN.md D4. |
| P1 | `Creature(Player,int,int)` ctor | postfix | Side flip inside `GhostConstructionScope`. |
| P2 | `Creature.TakeTurn` | prefix | Native version throws for non-monsters (`ENGINE-NOTES.md` §2). Run the Ghost controller. |
| P3 | `Creature.AfterAddedToRoom` | prefix | Native version dereferences null `Monster` (`ENGINE-NOTES.md` §2). |
| P4 | `CombatManager.AfterCreatureAdded` | prefix | Native version calls `Monster.RollMove`, confirmed NPE at `CombatManager.cs:865` (`ENGINE-NOTES.md` §7 Q2). |
| P5 | `CombatRoom.StartCombat` | prefix (+ `HarmonyReversePatch`, conditionally) | Insert the Ghost into `CombatState` before combat nodes exist. Went through two revisions, both driven by concrete evidence rather than guesses: originally planned as a `CombatRoom.EnterInternal` prefix, moved to `CombatManager.SetUpCombat` because `EnterInternal`'s own player-population step is guarded by `Players.Count == 0` (`CombatRoom.cs:128`) and adding the Ghost there first would make that guard skip the *human's* population. Then moved again, confirmed live (ENGINE-NOTES.md §0): `SetUpCombat` is early enough for combat-state population but too late for node creation, because `CombatRoom.StartCombat` calls `NCombatRoom.Create(...)` — which builds enemy nodes from whatever is in `CombatState.Enemies` at that moment — *before* it calls `SetUpCombat` (`CombatRoom.cs:197-231`, order confirmed by direct read: monster generation → `NCombatRoom.Create` at `:219` → `SetUpCombat` at `:225`). `StartCombat` (private, `async Task`) is early enough for both: everything `SetUpCombat` does (`ResetCombatState`/`PopulateCombatState`/`NetCombatCardDb.StartCombat`/per-creature `AddCreature`) still includes the Ghost, and so does node creation. **Revised 2026-09-03 (M8.5)**: also the injection point for a pending random deck (`GhostSession.PendingRandomDeck`, set by `RealFlowGhostDuelEntry`) — the earliest async native method downstream of that entry's synchronous `PullNextEncounter` prefix, and confirmed to run before `SetUpCombat` shuffles the deck. When a pending deck exists, skips straight to a `HarmonyReversePatch`-based async chain (same technique as P13): await `GhostPlayerFactory.ConfigureDeckAsync`, then call the true original. |
| P6 | `NCombatRoom.AddCreature` | postfix | Container choice is `IsPlayer`-based, not `Side`-based (`ENGINE-NOTES.md` §7 Q4) — a Ghost is *always* parented into `_allyContainer` by the native method with no slot-data workaround possible. Reparent into `_enemyContainer` after the native call. Required for the M1 gate's "visible on the enemy side" requirement (POSTMORTEM F9). **Revised 2026-09-03 (Necrobinder), confirmed live**: the same container check (`creature.IsPlayer \|\| creature.PetOwner != null`) also unconditionally ally-sides any pet, so a Ghost-owned pet (Osty) was left in the ally container with no reparent, at the wrong screen position. Extended to also reparent a pet whose `PetOwner` is the Ghost's `Player`. **Revised again 2026-09-03, confirmed live**: three more fixes landed in this same postfix, since it is the one point that runs after the *entire* native `AddCreature` body (including its own mid-body side effects) has finished — (1) desaturates the Ghost's (and its pets') sprite to grayscale via the engine's own `res://materials/vfx/hsv.tres` shader material (same one `NCreatureVisuals.SetScaleAndHue` and `NCharacterSelectButton` already use, just `s`=0 instead of shifting `h`), per explicit user request; (2) resets `NSelectionReticle`'s private `_cancelToken` (reflection, `NSelectionReticleCancelTokenAccess`) after each `Reparent` call — `Reparent` fires `_ExitTree` on every descendant including this per-creature targeting reticle, which cancels that token and makes `OnDeselect()` a permanent no-op from then on, leaving the four-corner targeting box stuck visible after the first card target selection; (3) mirrors a pet's X position offset around its (already correctly repositioned) owner — the native mid-body pet-layout loop's offset formula assumes the ally-side "owner faces right" convention and has no side-awareness, so a Ghost-owned pet landed on the wrong side of the mirrored Ghost sprite. |
| P7 | `Creature.PrepareForNextTurn` | prefix | Defaults `rollNewMove: true` and unconditionally calls `Monster.RollMove(...)`. `CombatManager.cs:480-483` calls it with that default for every enemy-side creature at the start of *every human turn* — confirmed NPE for the Ghost, not merely a presentation gap as this doc originally assumed (`ENGINE-NOTES.md` §2 correction). |
| P8 | `MapPointHistoryEntry.GetEntry` | prefix | Throws for any player id absent from the run's history stats — confirmed live: the Ghost hits this the moment it takes unblocked damage (`CreatureCmd.cs:331-335`), which faults the whole `PlayCardAction` and strands the triggering card in the Play pile (ENGINE-NOTES.md §0, fifth live attempt). Supplies `GhostSession.HistoryEntry`, a detached row, instead of the real lookup. |
| P9 | `CombatManager.SetupPlayerTurn` | prefix | `CombatState.Players` is `IsPlayer`-derived, not Side-filtered (ENGINE-NOTES.md §7 Q2), so `playersStartingTurn` (`CombatManager.cs:446`) already includes the Ghost whenever it's the *human's* turn — confirmed live: this gave the Ghost a full spurious extra energy-reset/hand-draw/hook pass during the human's own turn, on top of the real one `GhostTurnController` runs during the Ghost's turn, observed as `CrimsonMantlePower` firing twice per round (ENGINE-NOTES.md §0, sixth live attempt). Corrects §9's original (wrong) claim that this native method "does not run automatically for the Ghost" — it does, just at the wrong time; skip it for the Ghost specifically. |
| P10 | `CombatState.HittableEnemies` | postfix | Second, narrower attempt at the bug the original (reverted) P9 tried to fix. All 17 audited cases (ENGINE-NOTES.md §0) read `HittableEnemies` specifically, and it is confirmed off the real damage-resolution path (`Hook.cs:1531-1548`'s only native read is gated behind a UI-preview-only condition never hit during actual play; `CreatureCmd.cs`/`DamageCmd.cs`/`AttackCommand.cs` don't reference it at all) — unlike `Allies`/`Enemies`, which are read far more broadly and were the reverted attempt's likely (never confirmed) failure point. **Revised 2026-09-02**: confirmed live that the original guard alone (`CombatManager.IsExecutingCardOrPotionEffect` only) did not fix `RedMask` — see P11; guard now also accepts `GhostHookOwnerScope.Current`. |
| P11 | `HookPlayerChoiceContext` ctor (postfix, push) + `AbstractModel.InvokeExecutionFinished` (prefix, pop) | postfix/prefix | **Not yet confirmed live.** Fills the gap P10 alone left open: `RedMask.BeforeSideTurnStart` (and any relic/power hook of the same shape) is not a card/potion effect, so `IsExecutingCardOrPotionEffect` never observes it. `Hook.cs`'s three non-card/potion hook dispatchers (`BeforeSideTurnStart`, `AfterDeath`, `AfterDiedToDoom`) all construct one `HookPlayerChoiceContext` per listener, whose constructor already resolves "owner `Player`" generically for every model kind (`HookPlayerChoiceContext.cs:70-89`) — pushed/popped into `GhostHookOwnerScope` (`ConditionalWeakTable`-keyed `AsyncLocal`, correct under nesting), read by P10's guard. Content-agnostic: covers RedMask's shape without naming it. |
| P12 | `PowerCmd.Apply`'s compiler-generated `MoveNext` | transpiler | Confirmed live 2026-09-02 (Stone Armor now grants the correct 4 Plated). `CombatState.Players.Count` (`IsPlayer`-derived, ENGINE-NOTES.md §7 Q2) counts the Ghost as a second "player," so the native multiplayer-scaling gate (`PowerCmd.cs:128`) misreads a Ghost Duel as 2-human co-op — was granting `PlatingPower`/`StoneArmor` 12 Plated instead of 4 (`((2-1)*2+1)*4=12` vs. the correct `((1-1)*2+1)*4=4`). Not per-power — any `ShouldScaleInMultiplayer` power would inflate the same way, so the fix targets the one gate all of them funnel through, located via `AsyncStateMachineAttribute` (fails loudly at patch time on a game update, not silently in combat) and matched by `MethodInfo` operand, not IL offsets. Known not fixed: `PlatingPower.AfterApplied`'s separate `RunState.Players.Count`-derived decay rate (ENGINE-NOTES.md §0). |
| P13 | `Hook.AfterSideTurnStart` | prefix + `HarmonyReversePatch` | Confirmed live 2026-09-02: a Ghost with `Candelabra` (turn-2-only, +2 energy) correctly started round 2 with 5 energy (base 3 + 2), confirmed from the log's own `SPENT`/X-cost numbers. Confirmed by full read of `CombatManager.StartTurn` (`:440-604`): this hook fires unconditionally for the starting side *before* the method's enemy-turn branch (which reaches `Creature.TakeTurn`/P2), but a human's energy reset (`SetupPlayerTurn`, `:509-519`) runs *before* it — so a turn-N energy relic's grant lands after a human's reset but was landing *before* the Ghost's (since `GhostTurnController` used to reset only once `RunTurnAsync` ran, later). Was silently discarding `VeryHotCocoa`'s +4 via the Ghost's own `ResetEnergy()` (absolute assignment) every round. `GhostTurnController.SetupTurn` (extracted from `RunTurnAsync`) now runs from a prefix here instead, restoring the human's relative order; a `HarmonyReversePatch` stub lets the prefix call through to the true original afterward without recursing into itself. |

| P14 | `Hook.BeforeTurnEnd` + `Hook.AfterTurnEnd` | prefix (`TargetMethods()`, both) | Confirmed live. Briefly retargeted to `Hook.BeforeSideTurnEnd`/`AfterSideTurnEnd` during a same-evening game update that renamed these, then reverted to the original names when a second update the same evening reverted the rename (same body/order/signature throughout — ENGINE-NOTES.md M3 section; Steam auto-updates now paused on this build). `PlatingPower`/Stone Armor's Block grant (`BeforeSideTurnEndEarly`) fired once correctly at the Ghost's own turn end and once more, spuriously, at the *human's* turn end. Root cause is the P9 bug's mirror image on turn-end: `EndPlayerTurnPhaseOneInternal`/`EndPlayerTurnPhaseTwoInternal` (both hard-gated to the human's own turn-end) build their hook-participants list from `CombatState.Players` (`IsPlayer`-derived, ENGINE-NOTES.md §7 Q2), so it already includes the Ghost regardless of whose turn is ending. Filters the Ghost out of `participants` at the one place both mis-scoped calls funnel through, fixing the whole class generically rather than special-casing `PlatingPower`. |
| P15 | `WeakPower.ModifyDamageMultiplicative` | postfix | Confirmed live 2026-09-03 (Weak's late-binding reconciled exactly across multiple worked examples — Red Mask, Brimstone/Strength traces). M3/M4's queued-damage core. Forces Weak's own multiplier to 1 while `GhostWeakSuppressionScope` is active, so a `QueuedDamagePacket.LockedAmount` can honestly exclude Weak — never baked in and later divided out (POSTMORTEM F14), genuinely never folded in for that one call. `WeakPower` is named directly because `COMBAT-RULES.md` §5 names Weak specifically as needing late-binding, not as per-content special-casing. |
| P16 | `Hook.ModifyDamage` | prefix | Confirmed live 2026-09-03 (no double-modifier-application observed across the same reconciled worked examples). Stops the real `CreatureCmd.Damage` call used to resolve a queued packet from re-applying modifiers already baked into `LockedAmount`, while `GhostQueuedResolutionScope` is active — the "explicit token carried through the resolution call" `COMBAT-RULES.md` §5 asks for, in place of the old mod's ambient tuple-matching (POSTMORTEM F14). |
| P17 | `CreatureCmd.Damage` (6-param, `cardSource`-taking overload) | prefix | Confirmed live both directions 2026-09-03. Queues card-sourced, non-self direct damage instead of resolving it immediately (COMBAT-RULES.md §2-§3) — **both directions** (§3), gated on `dealer.CombatState == the Ghost's CombatState` rather than a specific side, since this is a plain 1v1. Disambiguated from ten sibling `Damage` overloads by explicit parameter types. Relic/power/orb/pet-sourced direct damage is deliberately out of scope until a concrete card proves the gap. |
| P18 | `PowerCmd.Apply` (7-param overload) | postfix | **Not yet confirmed live.** M6's status-expiry fix. Confirmed live: Red Mask's Weak on the human persisted through the human's *second* turn instead of decaying after the Ghost's own — a native grace tick (`PowerModel.SkipNextDurationTick`, set by `PowerCmd.Apply` whenever `target.Side == Player`, `PowerCmd.cs:144-147`) meant for "a monster applied the power to the player" swallows the Ghost's own turn-end decay check when a Ghost-owned effect (Red Mask) applies the debuff at that same turn's start. `WeakPower`/`VulnerablePower`/`FrailPower` (confirmed identical shape) already tick down at one fixed checkpoint, the Enemy side's turn end, regardless of applier — for a Ghost-applied debuff that checkpoint already is "the applying side's turn end" (COMBAT-RULES.md §6, rule (a), confirmed 2026-09-03), so the fix is to stop granting the extra grace, not rebuild decay timing. Gated on `power.Type == PowerType.Debuff`, not any power class by name — applies uniformly to current or future debuffs sharing this shape. |
| P19 | `NCharacterSelectScreen.SelectCharacter` | postfix | Confirmed live (M8 gate, four characters). Debug-harness-only, not a gameplay patch (same category as P0). M8's "one character per sub-milestone" needs the debug buttons to build a run as whatever character is selected, not hardcoded Ironclad — but no live "currently selected character" state exists anywhere at main-menu-load time (confirmed by research: `NCharacterSelectScreen`'s own selection lives in a `StartRunLobby` object not constructed until the player has already opened that screen, and no save/prefs field records a "last selected character" either). Stashes the `CharacterModel` argument of the one method every character-portrait click funnels through into `SelectedCharacterTracker.Current` (`src/Debug/`), defaulting to Ironclad — mirroring exactly what the engine's own default-lobby-player logic does (`StartRunLobby.cs:796`) until the player picks something. `DebugBoot`/`GhostPlayerFactory` read this instead of a hardcoded `<Ironclad>` generic argument, using the non-generic `Player.CreateForNewRun(CharacterModel, ...)` overload (`Player.cs:301`, confirmed real — the generic one wraps it). `M2Deck`'s fixed/random decks stay Ironclad-specific content (unchanged — injecting Ironclad cards into another class's deck would be nonsensical); `DebugBoot` skips that injection for any other selected character and runs with that character's own real stock deck instead. |

Three mod-owned (non-Harmony) pieces are also worth recording alongside the patch table:
`GhostTurnController.RunTurnAsync` calls `CombatManager.Instance.CheckWinCondition()` after every card
play, not just once per whole turn (ENGINE-NOTES.md §0's "infinite Cascade against a dead target"
entry); `GhostTurnController.SetupTurn` resolves the Ghost's own pending queued-damage packets before
energy reset/hand draw, and `RunTurnAsync` resolves the human's pending packets after the play loop,
per `COMBAT-RULES.md` §2's asymmetric turn loop (both confirmed live 2026-09-03); and
`GhostDamageIndicatorPresenter` (`src/Presentation/`, confirmed live 2026-09-03, real `NIntent` node
and sprite) shows a queued-damage number above whichever side has a pending packet, subscribed to
`GhostDamageQueue.Changed` per §8 below.

| P20 | `ActModel.PullNextEncounter` | prefix | Confirmed live (M8 gate, four characters via the real flow). Debug-harness-only (same category as P0/P19), not a gameplay patch. `RealFlowGhostDuelEntry`'s (`src/Debug/`) real-flow debug entry point: plays a real run start (real character select, real Neow) instead of `DebugBoot`'s hand-built shortcut, then substitutes the Ghost Duel for the first normal-monster fight the player actually reaches. Confirmed by full-tree grep to be the single call site every non-debug room transition funnels an encounter through (`RunManager.cs:878`), distinct from and later converging with `EnterRoomDebug`'s explicit-model path. Gated on a one-shot armed flag (set when the debug button is clicked) and `roomType == RoomType.Monster`, so it waits unconsumed through Neow and any non-combat rooms and never touches Elite/Boss encounters, which share this method with a different `roomType`. Builds and joins the Ghost to the real `RunState` (via `RunManager.DebugOnlyGetState()`, the one public accessor to it) using the same `GhostPlayerFactory` calls `DebugBoot` already relies on — neither cares whether the `RunState` came from the real flow or a hand-built one. Known, not-yet-solved limitation: the real flow autosaves on every room entry (`shouldSave: true`, unlike `DebugBoot`'s `false`), and neither `GhostSession` nor the Ghost `Player` are ever part of `RunState`/`SerializableRun` by design — a save/reload mid-Ghost-fight started this way would not restore either. Dev-only tool, not addressed. |

| P21 | `NCreature._Ready` | postfix | Confirmed live (Necrobinder): the Ghost's own health/Block display, and its pet Osty's, were hidden by default, only revealing on hover (and hiding the human's own bar while doing so). Root cause: native `_Ready()` computes `_isRemotePlayerOrPet` from `LocalContext.IsMe(...)` — checking a creature's owning `Player.NetId` against the one real local human's — logic authored for "don't clutter my screen with a remote co-op teammate's bar," misfiring since the Ghost (and anything it owns) is never that local human by design. Fixes the private, no-setter `_isRemotePlayerOrPet` field directly (`NCreatureRemoteFlagAccess`, the established reflection-accessor pattern) rather than `LocalContext.IsMe`/`NetId` itself, which is read pervasively elsewhere for reasons unrelated to display — narrower target, lower blast radius. Only the Ghost's own creature and creatures it owns as pets are touched. |

| P22 | `CombatManager.DoTurnEnd` | prefix | Confirmed live. Same `playersEndingTurn` bug family as P9 (turn-start)/P14 (turn-end hook dispatch) — confirmed live as Defect's orb passives (Frost's Block) firing at the end of every turn instead of only their owner's. `EndPlayerTurnPhaseOneInternal` (hard-gated to the human's own turn end) builds `playersEndingTurn` from `_state.Players.ToList()` (`IsPlayer`-derived, includes the Ghost) and calls `DoTurnEnd` for every entry — so the Ghost's own `OrbQueue.BeforeTurnEnd` was firing once, spuriously, at the human's turn end (native), and a second time, correctly, at the Ghost's own (mod-added). Skipped entirely for the Ghost, mirroring P9's exact shape; `GhostTurnController`'s own end-of-turn sequence now replicates `DoTurnEnd`'s full body (OrbQueue, Ethereal exhaust, `OnTurnEndInHandEffect`) at the correct time instead. `Hook.BeforeFlush`/`Hook.AfterAutoPostPlayPhaseEntered` (same method, same list, two other per-player loops) are likely the same bug shape but not yet confirmed broken for anything in the current test decks — left as a known, undecided risk. |
| P23 | `OstyCmd.Summon`'s compiler-generated `MoveNext` | transpiler | Confirmed live 2026-09-03 — after the Ghost's Osty died once, every later Bodyguard play created a brand-new Osty instead of reviving the corpse (4+ duplicates observed by turn 4). Root cause: the method's revival lookup, `combatState.Allies.FirstOrDefault(c => c.Monster is Osty && c.PetOwner == summoner)`, reads the Side-literal `Allies` list, which can never contain a Ghost-owned creature. `DieForYouPower` deliberately keeps a dead Osty in combat/`_pets`, so the corpse is genuinely there to find — just unreachable through this one Side-literal read (`summoner.IsOstyAlive`'s own lookup, `Player.Osty` → `PlayerCombatState.GetPet<Osty>()`, is player-scoped and already finds it correctly). A transpiler duplicates the `CombatState` receiver around the one `Allies` read and concatenates `Enemies` into the search space — the downstream `PetOwner == summoner` filter already disambiguates correctly regardless of side, so no access to the `summoner` parameter (and no compiler-generated field name to guess) is needed. Considered and rejected: an ambient-scoped patch on `CombatState.Allies`'s getter itself — structurally the same shape as the reverted original P9. |
| P24 | `NCreature.OstyScaleToSize` | postfix | Confirmed live 2026-09-03 — the Ghost's Osty started combat correctly flipped (P6) but visibly un-flipped during its own summon/regrow scale tween. Root cause: the tween's target, `Vector2.One * num * Visuals.DefaultScale`, is always uniformly positive (`DefaultScale` is one `float`, not per-axis), so `TweenProperty` interpolates from the correct negative X toward a positive target, crossing zero. Reimplementing the native tween's own math was rejected (it also conditionally tweens `Position` and finishes via a private `UpdateBounds` callback); instead a second, independent tween (Godot tweens from separate `CreateTween()` calls run independently) waits the same duration and snaps the X sign back negative. Known limitation: a brief flash toward the wrong orientation still plays during the transition itself — only the end state is guaranteed correct. |
| P25 | `Player.ReviveBeforeCombatEnd` | prefix | Confirmed live 2026-09-03 — when the human won, the defeated Ghost was healed 1 HP and played its stand-up animation. Fifth confirmed consumer of the `IsPlayer`-derived, not Side-filtered, `CombatState.Players` shape (P9/P12/P14/P22 are the other four): `CombatManager.EndCombatInternal`'s `foreach (Player player in combatState.Players) { await player.ReviveBeforeCombatEnd(); }` includes the Ghost, and `ReviveBeforeCombatEnd` (`if (Creature.IsDead) { await CreatureCmd.Heal(Creature, 1m); }`) exists for genuine co-op — reviving a teammate who died but whose party still won. Skipped entirely for the Ghost, mirroring P9's exact shape; the same method's second loop (`AfterCombatEnd()`, power/Block teardown) is left untouched since running it for the Ghost too is harmless. |
| P26 | `VulnerablePower`/`WeakPower`/`FrailPower.AfterSideTurnEnd` (`TargetMethods()`, all three) | prefix | Not yet confirmed live. Revisits COMBAT-RULES.md §6 rule (a) per the user's 2026-09-03 clarification: decay must key off *who applied* the debuff, not a fixed side. All three tick down at one fixed native checkpoint (the Enemy/Ghost side's own turn end) regardless of applier — correct by coincidence for a Ghost-applied debuff (that checkpoint already is the applying side's turn end there), but a human-applied debuff on the Ghost was still decaying one full round late. Replaces the native `if (side == CombatSide.Enemy)` check with `power.Applier?.Side` (`PowerCmd.Apply` already sets `power.Applier`, `PowerCmd.cs:119` — no new tracking). Not a single patch on the shared `Hook.AfterSideTurnEnd` dispatch point, because `PoisonPower` shares the identical `Type`/`StackType` shape and must stay on its own existing afflicted-side periodic timing, per the user's explicit instruction. |

**Twenty-six patches total (P0 + P19 + P20 harness, P1–P18 + P21–P26 gameplay), three (P10, P18, P26)
not yet confirmed live.**
P6 through P18 were not
anticipated when this
table was first written; each is earned by a direct source read or a confirmed live failing
observation (ENGINE-NOTES.md §0), the same evidentiary bar the "failing observation" rule exists to
enforce (a concrete, cited engine fact, not a guess) — never a guess added in advance. The numeric cap
this table originally enforced (five target, ten hard) was removed 2026-09-02 by direct user decision:
correctness matters more than patch count. The four-question discipline before adding one (`CLAUDE.md`)
still applies to every patch regardless.

**A different, earlier patch was also numbered P9 the same day (2026-09-02), tried and reverted before
being reused above for the unrelated `SetupPlayerTurn` fix.** `CombatState.Allies`/`Enemies` are
Side-literal, not owner-relative — confirmed live via `JuggernautPower.AfterBlockGained` resolving "a
random enemy" to the Ghost itself (ENGINE-NOTES.md §0). A guarded postfix (active only while
`CombatManager.IsExecutingCardOrPotionEffect(ghostPlayer)`) was added to make both getters
actor-relative, but the very next live test showed a far worse regression: no Ghost attack — including
plain single-target Strikes, whose targeting never reads these properties at all
(`AttackCommand.GetPossibleTargets()`'s `IsSingleTargeted` branch returns the explicit target
directly) — damaged the human anymore. The exact mechanism was not pinned down before reverting;
`CreatureCmd.Damage`, `DamageCmd.cs` and `AttackCommand.cs` were read in full and none reference
`Allies`/`Enemies`/`HittableEnemies` directly, so the breakage is via some `Hook.ModifyDamage`
listener reacting to the flipped view in a way not yet traced. **Reverted rather than shipped broken**
— restoring the known-good M2 baseline was judged more important than keeping an unproven fix.
Juggernaut (and the ~19 similarly-shaped Power/Relic classes) go back to "known, not yet fixed." See
`ENGINE-NOTES.md` §0's M2 section for the full writeup, including what was ruled out.

No damage-capture patch is in M1 — see §7 below and `ENGINE-NOTES.md` §10: `CreatureCmd.Damage` has no
seam for honest deferral, so M1 uses immediate native damage resolution and that patch moves to M4 as
originally planned. Expected additions in order: intent nodes (M3), `CreatureCmd.Damage` capture (M4).

### Patch rules

1. **Every patch's first statement is a mode guard.** If the Ghost mode is not active, return
   immediately — before any allocation, LINQ or reflection. A player with this mod installed and the
   mode unused must pay nothing. The first pass violated this on `CombatState.Players` and
   `PlayerCreatures` (POSTMORTEM F7).
2. **Never postfix a hot property getter to reallocate a list.** `CombatState.Players`, `Allies`,
   `Enemies`, `PlayerCreatures` and `HittableEnemies` are read constantly.
3. **No `AsyncLocal`-scoped rewriting of engine-wide views.** If side-relative adaptation is
   genuinely needed, patch the specific consuming call site (POSTMORTEM F8).
4. **One file per reflected private name**, each with a load-time existence assertion.

---

## 5. Combat-view adaptation — choose option (A)

`ENGINE-NOTES.md` §3 lays out the fork. **Take option (A): leave `CombatState.Players` unpatched.**

Rationale:

- `CombatManager.SetUpCombat` then calls `ResetCombatState` and `PopulateCombatState` on the Ghost
  natively, along with `NetCombatCardDb.StartCombat`. The mod stops owning combat-state population
  forever, which removes the first pass's most fragile ordering dependency.
- The cost is finding the specific engine sites that use `Players` to mean "the human party." That
  list is finite, discoverable by grep, and each site is a small targeted patch — versus a global
  property rewrite whose blast radius is every combat in the game.
- Under option (A), M1's controller may not need view adaptation *at all*: `Strike`, `Defend` and
  `Bash` only use owner-relative `IsValidTarget`. Confirm that empirically before writing any
  adaptation code.

Enumerating the `Players` call sites is task **M0-4**. If it turns out that too many sites conflate
the two meanings, revisit — but revisit with the list in hand, and write down the decision and the
reason (the first pass never did; see POSTMORTEM F3).

---

## 6. The turn controller — exactly one

**There is one controller.** The first pass's fatal structural mistake was maintaining a "debug"
controller alongside a "real" one, so the tested path shared almost nothing with the shipped path
(POSTMORTEM F12).

The controller is a small state machine with a pluggable **card chooser**:

```csharp
interface IGhostCardChooser
{
    // null == end turn. Choose from the live hand; the caller re-invokes after every play.
    GhostChoice? Choose(GhostTurnView view);
}
```

- **M1 chooser:** `LeftToRightChooser` — first hand card that passes `CanPlay` and `IsValidTarget`.
  Deterministic, ~30 lines, permanently retained as a regression mode.
- **M5 chooser:** the scoring/planning chooser.

Everything else in the turn — draw, energy, phases, hooks, queueing, intents, cleanup — is shared by
every chooser and exercised by M1.

### Turn skeleton — M1 (confirmed against source, `ENGINE-NOTES.md` §9-10)

`CombatManager`'s own turn machinery never touches an enemy-side `Player` (`SetupPlayerTurn` and both
`EndPlayerTurn*Internal` methods are hard-gated on `CurrentSide == CombatSide.Player`) — so there is
nothing to route around, only native primitives to call directly in the right order:

```text
1  ghostPlayer.PlayerCombatState.ResetEnergy()
2  await CardPileCmd.Draw(ctx, 5m, ghostPlayer, fromHandDraw: true)
3  loop, bounded:
     choice = chooser.Choose(view)                          null -> break
     card.CanPlay(out reason, out preventer)                skip if false, keep scanning
     card.IsValidTarget(target)
     (energySpent, starsSpent) = await card.SpendResources()
     resources = new ResourceInfo { EnergySpent = energySpent, EnergyValue = energySpent,
                                     StarsSpent = starsSpent, StarValue = starsSpent }
     await card.OnPlayWrapper(ctx, target, isAutoPlay: false, resources)   // resolves immediately, M1 — see §7
     re-derive the view                                      replan after every card
4  discard remaining non-retained hand cards: await CardPileCmd.Add(cardsToFlush, PileType.Discard)
5  ghostPlayer.PlayerCombatState.EndOfTurnCleanup()
```

No `PlayerTurnPhase` transitions (§ below), no queued-intent step (M1 uses immediate damage, §7), no
status-expiry step (M6). `ctx` is one `BlockingPlayerChoiceContext` for the whole turn.

Loop guards: max plays per turn, max repeated hand/energy/Block signature, and a wall-clock budget.
Exhausting a guard ends the turn — it never hangs.

**Do not use `CardCmd.AutoPlay`.** Its doc comment says "play a card for free" and it hard-codes
`ResourceInfo { EnergySpent = 0, StarsSpent = 0 }` regardless of real cost (`CardCmd.cs:123-129`,
confirmed by direct read) — using it would let the Ghost play unlimited free cards, breaking the M1
gate's affordability checks. Call `CanPlay`/`SpendResources`/`OnPlayWrapper` directly instead, exactly
as `PlayCardAction.ExecuteAction()` does for a human play (`ENGINE-NOTES.md` §9).

### `PlayerChoiceContext`

Confirmed: `BlockingPlayerChoiceContext` (`MegaCrit.Sts2.Core.GameActions.Multiplayer/
BlockingPlayerChoiceContext.cs:18-29`) is a real engine type, not something to build from scratch —
its doc comment is literally "for when we don't care if player choice blocks the task," and both
`SignalPlayerChoiceBegun`/`SignalPlayerChoiceEnded` are no-ops. Use one instance per Ghost turn. For
card-grid prompts (not needed by M1's Strike/Defend/Bash pool), scope an `ICardSelector`
(`MegaCrit.Sts2.Core.TestSupport.ICardSelector`) that answers deterministically from stable option
order when that need arises.

### Phases

Confirmed (`ENGINE-NOTES.md` §7 Q5): `PlayerCombatState.Phase` gates nothing in `CanPlay`,
`OnPlayWrapper` or hook dispatch — its only reads are AutoSlay bot-timing and a cosmetic hand-card
glow. **M1's controller does not drive `PlayerTurnPhase` at all.** Revisit only if a later milestone
needs the glow/AutoSlay integration specifically.

---

## 7. Damage — M1 is immediate, queueing is M4

`CreatureCmd.Damage` (`MegaCrit.Sts2.Core.Commands/CreatureCmd.cs:96-412`, read in full) is a single
fused method — hooks, block, HP loss, VFX, kill-checking all inline, no seam between computing damage
and applying it (`ENGINE-NOTES.md` §10). **Decision (2026-09-02): M1 resolves Strike/Defend/Bash
damage immediately and natively** — `OnPlayWrapper` runs to completion synchronously within the
Ghost's own turn, exactly like an ordinary creature attack. No queueing code, no new patch. The
delayed-resolution duel from [`COMBAT-RULES.md`](COMBAT-RULES.md) is the **M4** target design, decided
together with the honest `DamageResult` problem below — building the queue before that problem is
solved would force exactly the fabrication this project exists to avoid.

Two architectural constraints for **M4**, when the queue is actually built:

1. **Never fabricate a `DamageResult`.** The first pass returned a synthetic result with
   `BlockedDamage` stuffed to the full queued amount so `TotalDamage` read back correctly
   (POSTMORTEM F13). Any card that inspects unblocked damage, lethality or hit count then receives a
   lie. Design the deferral so the truthful answer is available, or accept a bounded, *documented*
   list of card categories that cannot be deferred and resolve those immediately.
2. **Do not reconstruct pre-modifier damage by division.** Storing `modified / weakMultiplier` to
   re-apply Weak later (POSTMORTEM F14) is lossy and breaks at a zero multiplier. Store what the
   engine gives you plus the identity of the modifiers you intend to re-evaluate, and re-evaluate
   from live state.

---

## 8. Presentation

Presentation is downstream, non-blocking, and never authoritative. But two pieces are load-bearing
for *judging* whether the mod works, so they are not optional polish:

- **Node placement.** Verified: `NCombatRoom.AddCreature` puts every `IsPlayer` node in
  `_allyContainer` regardless of `Side`, and slot data cannot redirect the container choice — only
  the position within whichever container it landed in (POSTMORTEM F9, confirmed in full at
  `ENGINE-NOTES.md` §7 Q4). M1 fixes this with P6 (§4): a postfix on `NCombatRoom.AddCreature` that
  reparents a Ghost's node into `_enemyContainer` — with `keepGlobalTransform: false` so it actually
  moves, unlike the first pass's reparent (POSTMORTEM F9).
- **Card previews.** Anchor to an actual node; never hardcode screen coordinates (POSTMORTEM F10).
  Reuse `NCard` and `NCardPlayQueue` — the first pass was right that the multiplayer remote-card
  presentation is the correct thing to mirror.

A preview failure must not affect logical execution. Achieve that by making presentation a
subscriber to a plain event the controller raises — not by launching detached
`TaskHelper.RunSafely(...)` work that inherits an `AsyncLocal` scope (POSTMORTEM F8).

Grayscale shaders, auras, mirrored sprites and the "AN ECHO REMAINS" entrance are M8.

---

## 9. Module layout

Each module is independently readable, and nothing above the line below it may depend on it.

```text
src/
  Mod.cs                        entry point, Harmony init, one log line
  Diagnostics/
    GhostLog.cs                 structured, always-on, per-step turn logging
  Engine/                       every reflected private name lives here
    CreatureSideAccess.cs       Creature.Side backing field + load assertion
    GhostConstructionScope.cs   ported verbatim
    EnginePatches.cs            P1-P6 (gameplay), each with a mode guard first
  Ghost/
    GhostSession.cs             M9c: GhostPartyMember + the party-shaped session (Party[0] aliases
                                 keep every M1-M8 call site unchanged); one GhostActionSync per session
    GhostActionSync.cs          M9e: host-authoritative Ghost-action broadcast — lives here, not
                                 src/Multiplayer/, so Multiplayer stays strictly downstream of Ghost
    GhostPlayerFactory.cs       construct + place the enemy-side Player; M9c: NetId pool per party slot
    GhostTurnController.cs      the one turn skeleton (§6) — immediate damage in M1; M9e: branches
                                 host-decides-and-broadcasts vs. client-awaits-and-executes
    GhostTurnView.cs            read-only snapshot handed to a chooser
    IGhostCardChooser.cs
    Choosers/LeftToRightChooser.cs  M9c: random target among 2+ valid enemies, was first-match
    Choosers/GhostCardSelector.cs  answers CardSelectCmd prompts (e.g. Armaments) deterministically, M2
                                 — M9e known gap: still decides locally, not host-broadcast
  Duel/                         M3 (IntentPublisher) / M4 (DamageQueue) — see §7. M9c:
                                 GhostDamageQueue gained ResolvePendingAgainst (target-filtered) and a
                                 (dealer, target) TotalPendingFor overload
  Multiplayer/                  M9. Compiles clean; not yet exercised live (PLAN.md's M9 note).
    Messages/                   INetMessage types — mod-defined types are auto-discovered
                                 (ReflectionHelper.GetSubtypesInMods, ENGINE-NOTES.md's "M9 research")
    MultiplayerGhostLadderCoordinator.cs   M9a: cross-lobby Min() aggregation of each human's own
                                 separate multiplayer ladder, mirroring the native multiplayer-ascension
                                 cap pattern
    MultiplayerLegacyAscensionEntry.cs     M9b: the 4th NMultiplayerHostSubmenu button
    MultiplayerLegacyAscensionModifier.cs  a separate type from the singleplayer modifier on purpose
    MultiplayerLegacyAscensionGhostFightSplice.cs   M9c: builds the whole Ghost party from every
                                 human's own reported snapshot
    MultiplayerDebugGhostDuelEntry.cs      the multiplayer debug-fight shortcut (5th button)
    GhostPartyReconnectSync.cs  M9f, deliberately narrowed — see PLAN.md's M9f note
    MultiplayerUiAccess.cs      reflected NMultiplayerHostSubmenu._loadingOverlay
  Debug/
    DebugEncounter.cs           the M1 stock-Ironclad zero-monster encounter (P5 target)
    DebugBoot.cs                shared bootstrap: build Players, start the run, enter combat
    DebugMenuButton.cs          P0 target: one button on the main menu, wired to DebugBoot
  Presentation/                 M2+, strictly downstream
  Progression/                  M7+
  Ai/                           M5. Compiles clean; not yet exercised live (PLAN.md's M5 note).
    Cards/                       ported semantic card layer (catalog, resolver, effect model) — MIT,
                                  ../STS2Ghostmod/STS2Ghostmod/src/AdaptedAi/Cards, namespace-only port
    Combat/                      ported scorer + beam-search line planner, adapted for a real 1v1 duel:
                                  DeterministicCombatContextBuilder is new (the salvage never shipped
                                  its own context-assembly step), and "kill potential" is reinterpreted
                                  as queued-damage setup, not a same-turn kill (COMBAT-RULES.md §2-§3)
    Config/                      per-character combat tuning + .aiconfig loader, trimmed of the
                                  salvage's reward/shop/event config (no such AI surface here)
    Contracts/                   legal-action DTOs, trimmed of UsePotion (no execution path yet)
    SemanticGhostChooser.cs      the real IGhostCardChooser; falls back to LeftToRightChooser on any
                                  exception anywhere in context-build or planning
    GhostScaling.cs              COMBAT-RULES.md §7's seam, wired neutral (1.0/1.0/1.0) — not decided
```

Hard limits: no file over ~300 lines; no static mutable state outside `Diagnostics` and one explicit
`GhostSession` object with a clearly defined lifetime and a single `Dispose`. `GhostRuntime` at 828
lines and `GhostRuntimeService` at 344 lines of static registries are what made the first pass
untestable (POSTMORTEM F19).

---

## 10. Session lifetime

One `GhostSession` object, created when the Ghost encounter is entered and disposed when it is left —
**including on every abort path**: run abandoned, quit to menu, human death, exception during setup,
combat cancelled.

`LegacyEncounterContext.IsActive` in the first pass armed a global damage-capture patch and was only
cleared on two happy-path callbacks (POSTMORTEM F15). Enumerate the exit paths first and write a test
that asserts the session is null after each.

The mode guard every patch checks is `GhostSession.Current is not null` — one field, one meaning.
