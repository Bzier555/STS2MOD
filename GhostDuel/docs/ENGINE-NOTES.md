# Engine notes — verified facts

## 0. Live-test findings (observed running, not read from source)

**2026-09-02, first live click of the debug button:** `AbstractModel`'s constructor
(`AbstractModel.cs:70-78`) implicitly makes the *first* construction of any given type its one
canonical instance, and throws `DuplicateModelException` — "Trying to create a duplicate canonical
model... Don't call constructors on models! Use ModelDb instead." — on any second construction of
that type. `GhostDuelEncounter` was being built with `new GhostDuelEncounter()` in `DebugBoot`; this
threw on the very first click, inside `EnterRoomDebug`'s argument list, *after* `SetUpNewSingleplayer`
and `SetCurrentScene(NRun.Create(...))` had already run — which is exactly why the symptom was a
correctly-headed but otherwise empty grey run screen with combat never loading.

First fix attempt was wrong and made things worse: calling `ModelDb.Inject(typeof(GhostDuelEncounter))`
from `Mod.Initialize()` turned a click-time failure into a **boot-time fatal error**. Confirmed from a
second live run's stack trace: `ModelDb.Init()` (`ModelDb.cs:389-398`, called from
`OneTimeInitialization.ExecuteEssential()` during `NGame.GameStartup()`) already walks
`AllAbstractModelSubtypes` and unconditionally `Activator.CreateInstance`s every one it finds — **it
auto-discovers mod-defined `AbstractModel` subtypes with no registration call needed**, since mods
finish loading (`ModManager.ReadSteamMods()`, during `OneTimeInitialization.ExecuteVeryEarly()`)
strictly before `Init()` runs. `Inject` (`ModelDb.cs:400-412`) constructs its type immediately, at mod
load time — *before* `Init()`'s own scan reaches it — so `Init()`'s later unconditional construction
of the same type (it has no `Contains()` guard, unlike `Inject`) throws the identical
`DuplicateModelException`, now during boot instead of on click, and the whole game fails to start.

**Confirmed working fix:** do nothing at registration time — `new GhostDuelEncounter()` is still
wrong (never construct a model directly), but no explicit `Inject`/registration call is needed either.
Just retrieve the instance `ModelDb.Init()` already created, through the matching
`ModelDb.<Category><T>()` accessor (`ModelDb.Encounter<GhostDuelEncounter>()`, mirroring
`ModelDb.Character<T>()`/`ModelDb.Card<T>()`/etc.), the same way native code reads any other model.

A second, compounding bug: because the exception left `GhostSession.Current` set (combat never
started, so `CombatManager.CombatEnded` never fired to dispose it), every subsequent click of the
debug button failed immediately with "A GhostSession is already active" instead of retrying — fixed
by having the debug entry point dispose a stale session itself before starting a fresh one
(`DebugBoot.StartDebugRunAsync`), since that's a legitimate case for a dev/test button specifically
(a normal gameplay path should still never see `Begin()` called twice without disposal in between).

**2026-09-02, third live attempt (booted, entered combat room, but the human had no energy counter or
hand):** `NullReferenceException` at `CombatState.Contains(AbstractModel)`, reached via
`CombatState.IterateHookListeners()` → `Hook.ModifyShuffleOrder` → `CardPile.RandomizeOrderInternal`
→ `Player.PopulateCombatState`, itself called from the P5-patched `CombatManager.SetUpCombat`. Traced
to the actual field: `CombatState.Contains`'s `CardModel` branch (`CombatState.cs:593`) reads
`cardModel.Owner.IsActiveForHooks` with no null check. `CardModel.Owner`'s own doc comment
(`CardModel.cs:328-330`) already warns it "will technically be null... in certain edge-case timing
moments" — this was one.

Root cause, confirmed by reading `RunState.CreateShared` (`RunState.cs:319-334`): a card's `Owner` is
**not** set by `CardPile.AddInternal` (verified: no `Owner` assignment anywhere in `CardPile.cs`) —
it is set exactly once, by `RunState.AddCard(card, owner)` (`RunState.cs:404-411`), called from
`CreateShared`'s per-player loop `foreach (Player player in players) { player.RunState = runState;
foreach (CardModel card in player.Deck.Cards) runState.AddCard(card, player); }`. That loop only runs
for players passed into `RunState.CreateForNewRun`/`CreateForTest` — and the Ghost, by design, is
never one of them (it isn't part of the run). `Player.CreateForNewRun`'s own doc comment already said
so plainly: "these models will not work properly until the player is added to a RunState and/or
CombatState" (`Player.cs:278-280`) — read at the time, but not connected to a concrete failure until
this trace. Relics and potions do **not** have this gap: both have their `Owner` set directly inside
`Player`'s own population methods (`Player.cs:451,648`), independent of `RunState`. Cards are the one
exception, because a card's canonical/shared template has no owner at all until something explicitly
claims it.

**Confirmed fix:** `GhostPlayerFactory.JoinRun(Player, RunState)` — set `ghostPlayer.RunState =
runState` (its setter allows exactly one transition away from the `NullRunState.Instance` default,
`Player.cs:87-101`) and call `runState.AddCard(card, ghostPlayer)` for every card in
`ghostPlayer.Deck.Cards`, mirroring `CreateShared`'s loop exactly. Called from `DebugBoot` right after
`RunState.CreateForNewRun` returns. `Player.RunState`'s own doc comment says `NullRunState` "should
always behave appropriately" as a fallback (`Player.cs:83-86`) — that held right up until a card's
`Owner` was read, which needs the real join.

**2026-09-02, fourth live attempt (combat entered, human's turn worked, but no Ghost was visible and
ending the human's turn hung):** the Ghost's own turn actually ran — log showed `TURN-START`, `HAND`
drew 5, `LEGAL`/`SPENT` for its first Strike — then `NullReferenceException` inside
`CardModel.OnPlayWrapper` → `CardPileCmd.AddDuringManualCardPlay` →
`CardPileCmd.CreateCardNodeAndUpdateVisuals`. Traced to the exact line
(`CardPileCmd.cs:691-694`):

```csharp
if (!owningPlayerIsLocal)
{
    nCard.Position = NCombatRoom.Instance.GetCreatureNode(card.Owner.Creature).IntentContainer.GlobalPosition;
}
```

`owningPlayerIsLocal` is false for the Ghost (its `NetId` is nowhere near the local client's), so this
branch runs and calls `GetCreatureNode(ghostCreature)` — which returned `null`, matching the visible
symptom exactly (no Ghost sprite on screen at all, not just on the wrong side). Root cause: **P5 was
targeting the wrong method.** Read `CombatRoom.StartCombat` in full (`CombatRoom.cs:197-231`) — it
calls `NRun.Instance?.SetCurrentRoom(NCombatRoom.Create(this, CombatRoomMode.ActiveCombat))` at
`:219`, which is what actually builds enemy visual nodes from `CombatState.Enemies` at that moment,
and only *afterward*, at `:225`, calls `CombatManager.Instance.SetUpCombat(CombatState)`. P5 was
adding the Ghost inside `SetUpCombat` — early enough to populate its combat state correctly (§0
above), but one native call too late for `NCombatRoom.Create` to ever have known the Ghost existed, so
no node was ever created for it. **Confirmed fix:** moved P5 to a prefix on `CombatRoom.StartCombat`
itself (private, `async Task` — a plain prefix works regardless, no async-wrapping needed since
nothing needs to run *after* it), so the Ghost is in `CombatState.Enemies` before node creation, not
just before combat-state population. See `ARCHITECTURE.md` §4 P5 for the up-to-date patch table entry
— this is its second revision, both driven by a concrete live/read finding, not a guess.

**2026-09-02, fifth live attempt (played several rounds; reported: attacks that deal unblocked
damage to the Ghost leave the card stuck in the Play pile and vanish from the deck entirely; Defends
and fully-blocked Strikes discard normally; Bash's Vulnerable never appears on the Ghost and
subsequent Strikes still read 6 instead of 9):** both are the same root cause. Confirmed from the
log: `System.InvalidOperationException: Player with ID <ghost netId> not found in player stats for
this run history! We have 1`, raised from `MapPointHistoryEntry.GetEntry(ulong)`
(`MapPointHistoryEntry.cs:47-55`, throws when `playerId` isn't in `PlayerStats`) and reached from
`CreatureCmd.Damage`'s per-hit loop (`CreatureCmd.cs:330-335`):
```csharp
if (damage > 0) {
    ...
    MapPointHistoryEntry mapPointHistoryEntry = receiver.Player?.RunState.CurrentMapPointHistoryEntry;
    if (mapPointHistoryEntry != null) {
        mapPointHistoryEntry.GetEntry(receiver.Player.NetId).DamageTaken += item.UnblockedDamage;
    }
}
```
— gated on `damage > 0`, i.e. only when unblocked damage actually landed, exactly matching the
reported Defend/fully-blocked-Strike vs. unblocked-Strike split. The exception faults the entire
`GameAction` (log: `GameAction PlayCardAction card: CARD.BASH ... completed with exception:
System.AggregateException`), which explains both symptoms as one:
- **Card stuck in Play pile / vanishes from the deck**: `OnPlayWrapper`'s result-pile step
  (moving the card out of `PileType.Play`) never runs, because the method faulted earlier in the same
  card's `OnPlay` — the card is orphaned in Play forever.
- **Bash's Vulnerable never applies**: `Bash.OnPlay` (`Bash.cs:29-36`) calls `DamageCmd.Attack(...)`
  *then* `PowerCmd.Apply<VulnerablePower>(...)` on the next line. The damage call is what throws, so
  the Vulnerable-application line is never reached at all — not a Vulnerable-specific bug, a
  downstream casualty of the same exception.

This is the exact scenario the first pass's `EnemyGhostRunHistoryPatch` already existed to prevent
(POSTMORTEM.md salvage list, read early in this project but not ported into M1's patch set — a real
gap in the initial plan, not a new engine discovery). **Confirmed fix:** P8 (`EnginePatches.cs`) — a
prefix on `MapPointHistoryEntry.GetEntry` that returns `GhostSession.HistoryEntry` (a detached
`PlayerMapPointHistoryEntry`, never serialized, never part of the real run) whenever `playerId` is the
Ghost's, instead of letting the native lookup throw.

Also confirmed the same live session: flipping the whole `NCreature.Scale` (P6, prior fix) mirrors
the health bar and damage-number VFX along with the sprite, since `%HealthBar` is a sibling of
`Visuals` under the same `NCreature` node, not a child of it. Fixed by flipping only
`NCreature.Visuals.Scale` (`NCreatureVisuals.cs:18`, a plain `Node2D`) instead of the whole node.

## M2 — read from source, implementation done, live gate not yet observed

Facts below are read from source (legitimate for this file) to build M2's card pool and turn
controller. Nothing in this section has been observed running — that is M2's exit gate, still
pending. Do not read "confirmed" here as "the M2 gate passed."

**`CardSelectCmd.Selector` is the seam `ARCHITECTURE.md` §6 already named.** Some cards prompt for a
card-grid choice outside of targeting — e.g. `Armaments`' base version, "pick 1 card in hand to
upgrade" (`Armaments.cs:34`, `CardSelectCmd.FromHandForUpgrade`). Read `CardSelectCmd.cs:771-813` in
full: `FromHandForUpgrade` answers directly from the option list with no prompt at all when there are
≤1 eligible cards (`:783-785`); otherwise, if the static `Selector` is non-null, it calls
`Selector.GetSelectedCards(list, min, max)` (`:787-789`) — only falling through to the real
UI/network flow (`PlayerChoiceSynchronizer`, `:792+`) when `Selector` is null. `Selector` is a
stack (`CardSelectCmd.cs:105`, `_selectorStack`), pushed/popped via `CardSelectCmd.PushSelector`
(returns an `IDisposable`, `:158-161`) — used by tests and `AutoSlayer`. `GhostCardSelector` (new,
`src/Ghost/Choosers/`) answers `options.Take(maxSelect)` — the same "leftmost, deterministic" policy
`LeftToRightChooser` already uses for card play — and `GhostTurnController` pushes it for exactly the
duration of the Ghost's own turn, popped via `using`.

**Ghost Block clearing needs no code — confirmed by reading `CombatManager.StartTurn`
(`CombatManager.cs:422-524`) in full.** `creaturesStartingTurn = _state.CreaturesOnCurrentSide` when
it is *any* side's turn, Ghost included when `CurrentSide == Enemy`; `creature.AfterTurnStart(side)`
(`Creature.cs:681-692`) calls `await ClearBlock()` unconditionally except on the human's very first
turn (`side == CombatSide.Player && TurnNumber == 1`) — so for the Ghost (`side == Enemy` always),
Block clears every single time this runs, before `Creature.TakeTurn`/our P2 patch ever fires. All of
`BeforeTurnStart`, `Hook.BeforeSideTurnStart`/`AfterSideTurnStart`, and `Hook.AfterBlockCleared`
likewise run over `creaturesStartingTurn` with no `IsPlayer`/`IsMonster` gate anywhere in this method
— this whole native pass already "just works" for an enemy-side `Player`, exactly the outcome
`ARCHITECTURE.md` §1's invariant predicts. Live verification is still part of M2's gate (a
Barricade-suppressed clear, in particular, needs to be watched for).

**`GhostTurnController` now mirrors `SetupPlayerTurn`'s full hook sequence**, not just the M1 subset:
`Hook.ShouldPlayerResetEnergy` → `ResetEnergy`/`AddMaxEnergyToCurrent` → `Hook.AfterEnergyReset` →
`Hook.BeforeHandDraw` → `Hook.ModifyHandDraw` (hand-size relics) → `CardPileCmd.Draw` →
`Hook.AfterPlayerTurnStart`, and end-of-turn now mirrors `FlushPlayerHand` exactly including Retain
(`card.ShouldRetainThisTurn`, `Hook.ShouldFlush`, `Hook.AfterFlush`) — all per `CombatManager.cs`
citations already in ENGINE-NOTES.md §9, just not wired in until M2 needed them.

**Injectable deck/relics use only native commands**, confirmed by reading each: `CardPileCmd.
RemoveFromDeck`/`Add(card, PileType.Deck)` for cards (the latter's own `Deck`-pile branch,
`CardPileCmd.cs:379-396`, is what a real card reward uses too — including the
`MapPointHistoryEntry`-touching line P8 already covers), `RelicCmd.Remove`/`Obtain` for relics
(`RelicCmd.cs`, doc comments literally say "Only `RelicCmd.Obtain`/`Remove` should be calling this"
about the lower-level `Player.AddRelicInternal`/`RemoveRelicInternal`). `RunState.CreateCard
(canonicalCard, owner)` supplies a correctly-owned card for an arbitrary runtime `Type` via
`ModelDb.GetById<CardModel>(ModelDb.GetId(type))` — no generic-per-card-type method needed. Must run
after `GhostPlayerFactory.JoinRun`, since `CardPileCmd.RemoveFromDeck` dereferences
`card.Owner.RunState` directly (`CardPileCmd.cs:60`) and the stock deck's `Owner` is only set by
`JoinRun`.

M2's 20-card/5-relic pool (`src/Debug/M2Deck.cs`) and its selection rationale — multi-hit, X-cost,
non-`AnyEnemy` targeting, Exhaust, self-damage, Strength/Dexterity scaling (and a deliberate
`ValueProp.Unpowered` negative control, `StoneArmor`/`PlatingPower`), on-card-play/on-damage-taken/
on-block-gained triggers — is recorded in that file's own doc comment, sourced from
`IroncladCardPool.cs`/`IroncladRelicPool.cs`/`SharedRelicPool.cs`.

**2026-09-02, live M2 test found a real bug, not covered by the fixed 20-card list's own testing —
`JuggernautPower.AfterBlockGained`** (`JuggernautPower.cs:17-29`) reads `base.CombatState.
HittableEnemies` (`CombatState.cs:142`: `Enemies.Where(IsHittable)`) to pick "a random enemy" to
damage whenever its owner gains Block. `CombatState.Enemies` is literally `_enemies` (Side-partitioned,
`CombatState.cs:65`), correct in vanilla play only because Juggernaut's owner is always a Player-side
creature there. With the Ghost as owner (Side==Enemy itself), the same read resolves to "creatures on
the Ghost's own side" — itself, the only one — so the damage has nowhere valid to land. Grepped
`MegaCrit.Sts2.Core.Models.Powers`/`.Relics` for the same `.HittableEnemies`/`.Enemies`/`.Allies`
pattern: 19+ classes read `HittableEnemies` directly, 5 more read `Enemies`/`Allies` directly — this
is systemic, not a Juggernaut-specific defect, and patching `JuggernautPower` itself would be exactly
the per-card special-casing CLAUDE.md forbids.

**Attempted fix (P9, `EnginePatches.cs`) — tried, then reverted the same day.** Made
`CombatState.Allies`/`Enemies` actor-relative, but only while
`CombatManager.IsExecutingCardOrPotionEffect(ghostPlayer)` is true — a native per-player
nesting-depth counter, incremented/decremented in a `finally` for the exact duration of that player's
own `OnPlay`/`OnUse` body (`CombatManager.cs:305-313,321-342`, doc comment: "True while a ... effect
body is currently executing for player, including nested auto-plays"). Reusing this existing native
tracker — rather than adding a mod-owned `AsyncLocal` scope around the whole turn — was meant to be
the narrower adaptation ARCHITECTURE.md §5 asks for when "too many sites conflate the two meanings":
it only answers "is the Ghost's own effect on the stack right now," and by construction cannot outlive
that effect (the depth counter is native-owned and decremented in the native `finally` regardless of
what the mod does), unlike the first pass's leaked `AsyncLocal` (POSTMORTEM F8). `HittableEnemies`
needed no patch of its own: its getter body calls `this.Enemies` internally, and Harmony patches the
compiled getter method, so same-class callers see the patched result too.

**Reverted: the very next live test showed no Ghost attack damaged the human at all — worse than the
bug it fixed.** This included plain single-target Strikes, which per a full read of
`AttackCommand.Execute`/`GetPossibleTargets()` (`AttackCommand.cs:157-176,513-658`) never consult
`Allies`/`Enemies`/`HittableEnemies` for a single-targeted attack at all — `GetPossibleTargets()`'s
`IsSingleTargeted` branch returns the explicitly-passed target directly. `CreatureCmd.Damage`,
`DamageCmd.cs`, and `AttackCommand.cs` were each read in full (not just grepped) looking for the
mechanism and none reference `Allies`/`Enemies`/`HittableEnemies` at any point in the single-target
path; `CombatState.IterateHookListeners()` (which `Hook.ModifyDamage` walks to reach every relic/power)
was re-confirmed to use the raw `_allies`/`_enemies` fields directly, not the patched properties, so
hook *dispatch* itself is not the cause either. The actual mechanism was not found before the decision
to revert — most likely some individual `Hook.ModifyDamage` listener (a Power/Relic override) reacting
to the flipped view mid-resolution, but this was not confirmed. **Decision: revert P9 entirely rather
than ship a fix with an unknown, worse failure mode than the bug it was meant to solve.** Restored
state: Juggernaut (and the ~19 similarly-shaped classes) are back to "known, not yet fixed" — no
`Allies`/`Enemies` patch exists. If this is revisited, the investigation should start from
`Hook.ModifyDamage`'s actual per-listener dispatch (not just its own body) and test with a MINIMAL
deck (one attack card, no other Powers/Relics) to isolate which specific listener reacts badly to the
flip, rather than reasoning from the full random-pool deck this was discovered under.

**2026-09-02, second attempt (P10) — narrower, not yet confirmed live.** Re-examined the audit: all
17 affected classes read `CombatState.HittableEnemies` specifically, never `Allies`/`Enemies` directly.
Confirmed by direct read that `HittableEnemies` is not on the real damage-resolution path at all — the
only core-engine read of it, `Hook.cs:1531-1548` (inside the `ModifyDamage` family), is gated behind
`target == null && previewMode == CardPreviewMode.MultiCreatureTargeting`, a UI hover-preview branch;
`CreatureCmd.Damage` calls `Hook.ModifyDamage` with `CardPreviewMode.None` (confirmed during the P8
investigation), so this branch never executes during actual resolution. `CreatureCmd.cs`/
`DamageCmd.cs`/`AttackCommand.cs` do not reference `HittableEnemies` at all (grepped directly, zero
hits in all three). The reverted P9 additionally patched `Allies` and `Enemies`, both read far more
broadly across the engine (`WhisperingEarring`, `GalvanicPower`, `VitalSparkPower`, a
`Creature.Kill`/`KillWithoutCheckingWinCondition` cleanup check at `CreatureCmd.cs:523`, per Q2) than
just these 17 classes — patching only what the 17 classes actually read removes that entire unexamined
surface. P10 (`EnginePatches.cs`) patches `CombatState.HittableEnemies`'s getter alone, using the same
`CombatManager.IsExecutingCardOrPotionEffect` guard as before, computing the flipped result directly
from the already-public `CombatState.Creatures` (no reflection needed this time — simpler than the
reverted attempt, not just narrower). `M2Deck.RelicTypes` now includes `RedMask` (swapped in for
`Shuriken`) as this fix's direct, isolated live test case; the rest of the 17 known cases stay excluded
from `BuildRandomDeck` until each is separately confirmed, per the evidence-discipline rule that a
fix's reasoning is not the same thing as an observation of it working.

**M2 debug harness got a third button, "random"** (`M2Deck.BuildRandomDeck`, `DebugMenuButton.cs`):
samples the *entire* Ironclad card pool (`ModelDb.CardPool<IroncladCardPool>().AllCards`, 87 cards)
and Ironclad + shared relic pools each click, specifically because the fixed 20-card list is what
already needed expanding to catch this class of bug — the more of the ~19-24 similarly-shaped
classes get exercised over repeated runs, the more of this bug family surface before M3.

**2026-09-02, full audit of the pattern (not a guess — every one of Ironclad's 87 cards and the 126
combined Ironclad + shared relics was checked against the actual class file):** 7 cards and 10 relics
confirmed affected.

- **Cards, functional (damage/debuff lands on the wrong side):** `Thunderclap.cs:33` (Vulnerable via
  `HittableEnemies`, while its own damage on line 30 correctly uses `TargetingAllOpponents` — same
  method, one correct pattern and one broken one side by side), `Juggernaut` (already documented
  above), `Inferno.cs:48` (retaliation damage via `HittableEnemies`).
- **Cards, cosmetic only (no gameplay effect, just VFX/UI on the wrong side):** `Stomp.cs:31`,
  `Conflagration.cs:32-36` (both spawn ground-effect VFX from `HittableEnemies`; actual damage
  correctly uses `TargetingAllOpponents`), `Dismantle.cs:18` (a "glow gold" UI hint checks
  `HittableEnemies` instead of `cardPlay.Target`; the real hit-count logic is unaffected).
- **Cards, narrow edge case:** `Hellraiser.cs:45` (`HittableEnemies.All(IsInfinite)` gates an
  auto-play cap for infinite-HP test encounters — main behavior unaffected).
- **Relics, all functional, identical shape** (a lifecycle hook — `AfterCardExhausted`,
  `AfterSideTurnStart`, `BeforeSideTurnStart`, `AfterPlayerTurnStart`, or `AfterCardPlayed` — reads
  `…CombatState.HittableEnemies` directly as a damage/debuff target): `CharonsAshes.cs:23`,
  `StoneCalendar.cs:96`, `ScreamingFlagon.cs:26`, `RedMask.cs:28`, `ParryingShield.cs:28`,
  `MercuryHourglass.cs:23`, `LetterOpener.cs:118`, `Kusarigama.cs:115`, `FestivePopper.cs:27-28`,
  `BagOfMarbles.cs:28`.

All 17 are now listed in `M2Deck.KnownEnemiesBugCards`/`KnownEnemiesBugRelics` and excluded from
`BuildRandomDeck`'s sampling pool — `Juggernaut` was also swapped out of the fixed `CardTypes` list
(replaced with `Anger`) now that it's confirmed broken rather than merely suspected. This does not fix
the underlying bug (the reverted P9's getters are still unpatched) — it only keeps future random test
runs from repeatedly re-discovering the same seven-plus-ten already-understood cases instead of
surfacing new ones. **2026-09-02 live confirmation**: a later random test drew `RedMask`, and its
Weak application landed on the Ghost instead of the human, exactly matching this audit's prediction —
no new information, just confirms the audit was correct.

**2026-09-02, same test session, a genuinely new and more significant bug — `CombatManager.
SetupPlayerTurn` runs for the Ghost during the human's own turn, not just its own.** Reported
symptom: `CrimsonMantlePower` (owned only by the Ghost in this run — confirmed via the deck-config log
line and `PILE Ghost: ... CRIMSON_MANTLE` entries, the human never had a copy) fired its self-damage/
Block twice per round instead of once. Traced to `CombatManager.cs:446`:
```csharp
playersStartingTurn = (state2.CurrentSide != CombatSide.Player) ? new List<Player>()
    : (_state?.Players.ToList() ?? new List<Player>());
```
— when it's the *human's* turn (`CurrentSide == Player`), `playersStartingTurn = _state.Players.ToList()`.
Per Q2 (§7), `CombatState.Players` is `IsPlayer`-derived, not Side-filtered — it already includes the
Ghost. `CombatManager.cs:508-519` then calls `SetupPlayerTurn(item5, ...)` (the private method at
`:629-676`, full body: `Hook.ShouldPlayerResetEnergy` → `ResetEnergy`/`AddMaxEnergyToCurrent` →
`Hook.AfterEnergyReset` → `Hook.BeforeHandDraw` → `Hook.ModifyHandDraw` → `CardPileCmd.Draw(...,
fromHandDraw: true)` → `Hook.AfterPlayerTurnStart`) **for every entry in that list — including the
Ghost, during the human's turn.** This is the exact same native method `ENGINE-NOTES.md` §9 already
documented and that `GhostTurnController` deliberately re-implements for the Ghost's own turn — but §9's
own conclusion ("this does NOT run automatically for the Ghost") was wrong: it never cross-checked
whether `_state.Players` excludes the Ghost, and per Q2 it does not. The Ghost was getting this full
setup — energy reset, a real 5-card draw, every associated hook — twice every round: once spuriously
during the human's turn (native), once correctly during its own (`GhostTurnController`). Likely a
contributing factor to earlier hard-to-pin-down deck-cycling oddities beyond the specific
already-fixed `RunState`/`MapPointHistoryEntry` bugs, though not confirmed as their sole cause.

**Confirmed fix:** P9 (`EnginePatches.cs`, reusing the slot number freed by the reverted actor-relative
`Allies`/`Enemies` patch — unrelated fix, same number) — a prefix on `CombatManager.SetupPlayerTurn`
that no-ops (`Task.CompletedTask`) when `player` is the Ghost, letting the human's own call through
unchanged. Nothing is lost: `GhostTurnController` already runs the identical sequence at the Ghost's
actual turn start.

**2026-09-02, next test round: P10 alone did NOT fix RedMask — still misapplied Weak to the Ghost.**
Confirmed by reading `RedMask.cs:23-30` again with this specific question in mind:
`RedMask.BeforeSideTurnStart` is a relic *lifecycle hook*, not a card/potion effect — it evaluates
`combatState.HittableEnemies` as a plain argument expression, synchronously, before its own method
even reaches its first `await`. P10's guard, `CombatManager.IsExecutingCardOrPotionEffect(ghostPlayer)`,
is scoped (by the native implementation, `CombatManager.cs:305-313,321-342`) to exactly a
`CardModel.OnPlay`/`PotionModel.OnUse` body — RedMask's hook runs completely outside that scope, so
the guard was false the entire time and P10's postfix never fired. This is not the same mistake as the
reverted original P9 (that was patching an over-broad property; this is a correctly-scoped patch with
an under-broad *guard condition* — the target was right, the "is the Ghost the actor" detector wasn't).

Read `Hook.cs:1144-1158` (`Hook.BeforeSideTurnStart`, the dispatcher RedMask's hook actually runs
under) and the two other call sites with the identical shape (`Hook.cs:451-461` `AfterDeath`,
`Hook.cs:485-495` `AfterDiedToDoom`): all three construct one
`HookPlayerChoiceContext(model, netId, combatState, GameActionType.Combat)` per listener, then await
that listener's returned `Task` and call `model.InvokeExecutionFinished()`. `HookPlayerChoiceContext`'s
constructor (`HookPlayerChoiceContext.cs:70-89`) already resolves "owner `Player`" for every model kind
(`CardModel`/`RelicModel`/`PotionModel`/`AfflictionModel`/`EnchantmentModel`/`PowerModel`) into its own
public `Owner` property — a single, non-content-specific place where "who does this hook belong to" is
knowable, for every relic/power hook that isn't a card/potion play, not just RedMask's.

**Confirmed fix (not yet re-verified live at time of writing):** `GhostHookOwnerScope`
(`src/Engine/GhostHookOwnerScope.cs`, new) — a `ConditionalWeakTable`-backed save/restore around an
`AsyncLocal<Creature?>`, keyed per-model so correctly nested if a hook ever indirectly triggers another
hook dispatch. P11 (`EnginePatches.cs`) pushes it from a postfix on `HookPlayerChoiceContext`'s
constructor (reading its own `Owner.Creature`, no re-derivation) and pops it from a prefix on
`AbstractModel.InvokeExecutionFinished` (called at least once per listener regardless of which of the
three dispatch loops or `HookPlayerChoiceContext.ExecuteTaskThenInvokeExecutionFinished`'s internal
call triggers it first — `Pop` is a no-op the second time, by construction). P10's guard now accepts
either the original card/potion-effect flag *or* `GhostHookOwnerScope.Current == ghostCreature`.

**2026-09-02, same test round, a separate and previously unreported bug — StoneArmor granted the
Ghost 12 Plated Armor instead of the correct 4.** Confirmed by reading `PlatingPower.cs` in full:
`ShouldScaleInMultiplayer => true` (`:23`) and `GetScaledAmountForMultiplayer` (`:85-88`) returns
`((combatState.Players.Count - 1) * 2 + 1) * amount`. The gate that invokes it,
`PowerCmd.Apply` (`PowerCmd.cs:128`): `if (combatState.Players.Count > 1 && (target.IsPrimaryEnemy ||
target.IsSecondaryEnemy) && power.ShouldScaleInMultiplayer)`. In this mod, `CombatState.Players` is
`IsPlayer`-derived (Q2, §7) and counts the Ghost as a second "player" even though there is only one
real human — `combatState.Players.Count == 2` here, so `((2-1)*2+1) * 4 = 12`, exactly the reported
value; the correct value with a true player count of 1 is `((1-1)*2+1) * 4 = 4`. This is the
previously-flagged, deliberately-deferred "MP-headcount scaling" risk (`ArtifactPower`, `BufferPower`,
`PlatingPower`, `SkittishPower`, `SlipperyPower` all read `Players.Count` similarly) now confirmed with
a live failing observation for `PlatingPower` specifically. Not a per-power bug: every power with
`ShouldScaleInMultiplayer => true` would be inflated the same way if it were ever applied to either
side in this combat, since there is genuinely no real second human in a Ghost Duel — patching
`PlatingPower` by name would be exactly the content-by-id special-casing CLAUDE.md forbids.

**Confirmed fix (not yet re-verified live at time of writing):** P12 (`EnginePatches.cs`) — `PowerCmd
.Apply` is `async Task` (compiled to a state-machine `MoveNext`, located via
`MethodInfo.GetCustomAttribute<AsyncStateMachineAttribute>().StateMachineType` rather than a guessed
compiler-generated name, so a game update that changes this fails loudly at Harmony-patch time, not
silently in combat); a transpiler matches the existing `callvirt` to `PowerModel
.ShouldScaleInMultiplayer`'s getter by `MethodInfo` operand (stable across state-machine IL rewriting,
unlike raw offsets) and ANDs in a zero-argument, session-only guard (`GhostSession.Current is null`)
immediately after it. This is the single non-content-specific chokepoint every multiplayer-scaling
decision funnels through, regardless of which power; touches nothing else `CombatState.Players`'s many
other legitimate readers (hand draw, turn setup) depend on, and does not require an `AsyncLocal`
redefinition of `Players` itself (CLAUDE.md: "patch the consuming call site" for side-relative
adaptation). **Known NOT fixed by P12:** `PlatingPower.AfterApplied` (`:29-36`) separately sets its
own per-turn decrement from `RunState.Players.Count` unconditionally (outside the `PowerCmd.Apply`
gate entirely) — Stone Armor's block will still decay 2/turn instead of 1/turn in a Ghost Duel. No
common non-per-power chokepoint was found for this secondary consequence; noted here as known,
not yet fixed, rather than silently missed.

**2026-09-02, next test round: P10/P11/P12 confirmed working (Stone Armor granted the correct 4
Plated), but a new question raised — a Ghost with `VeryHotCocoa` (+4 energy, turn 1 only) played only
2 of 5 drawn cards on round 1.** Traced from `ghostduel.log`'s own numbers, not a guess: `SPENT`
logs `energySpent` (`GhostLog.cs:77-78`), not remaining energy. Round 1: `UNMOVABLE` spent 2, then the
X-cost `WHIRLWIND` (which spends whatever remains as its own cost) spent 1, and the following `STATE`
line showed `energy=0`. That means energy right before Whirlwind was 1, so starting energy was
`2 + 1 = 3` — base Ironclad energy, no `VeryHotCocoa` bonus at all; the relic never actually granted
anything (2 cards played was a correct greedy-left-to-right response to only 3 energy, not the
algorithm mis-choosing — the bug is upstream of the chooser).

Read `VeryHotCocoa.cs:21-28` (`AfterSideTurnStart`, gated on `TurnNumber <= 1`) and confirmed its
dispatcher, `Hook.AfterSideTurnStart` (`Hook.cs:1163-1175`), is called from
`CombatManager.StartTurn` at `CombatManager.cs:522` — **unconditionally for whichever side is
starting, before the method's own branch into the enemy-turn path** (`:588-604`,
`IsEnemyTurnStarted = true; ...; await ExecuteEnemyTurn(...)`) that eventually reaches
`Creature.TakeTurn` (P2). For a human, the native per-player `SetupPlayerTurn` loop
(`CombatManager.cs:509-519`, hard-gated to `CurrentSide == Player`) does the energy reset *before*
line 522, so a turn-1 energy relic's grant lands on top of an already-reset pool. That per-player loop
is never reached at all when it's the *Enemy's* turn — so nothing reset the Ghost's energy before line
522 fired. `GhostTurnController` used to do that reset itself, but only from `RunTurnAsync`, invoked
via `Creature.TakeTurn` — which line 522 always precedes. Its `ResetEnergy()` call
(`PlayerCombatState.cs:162-165`: `Energy = MaxEnergy`, an absolute assignment, not additive) silently
discarded whatever `VeryHotCocoa` had just granted, every time, on every round — not merely round 1
(the bonus is gated to round 1, but the discard mechanism runs every round regardless; it just only
ever had something to discard on round 1 in this test).

**Confirmed fix (not yet re-verified live at time of writing):** `GhostTurnController.SetupTurn`
(`src/Ghost/GhostTurnController.cs`, extracted from the top of `RunTurnAsync`) now runs from P13
(`EnginePatches.cs`) — a prefix on `Hook.AfterSideTurnStart` itself, the one place a human's and the
Ghost's sequencing diverge, rather than special-casing `VeryHotCocoa`. The prefix cannot simply run
its own setup then call `Hook.AfterSideTurnStart` again by name — that would recurse into the same
patch — so it uses a `HarmonyReversePatch` stub (`Original`, rewritten by Harmony at patch time into
the true unpatched method body) to call through safely. This technique is more exotic than any prior
patch in this set and has only been confirmed to compile, not yet to apply/run correctly — the next
live test must specifically check `godot.log` for a clean mod load (the same class of failure P12 hit
with `AmbiguousMatchException` is possible here too, from a different cause) before trusting the
in-game energy behavior.

**2026-09-02, next test round: mod loaded clean, P10/P11/P12 held up, P13 confirmed live, and one
genuinely new bug found — the Ghost kept "playing" a card against an already-dead target for the
rest of its turn.** Two separate questions this round, both answered from the log's own numbers:

*Candelabra/CaptainsWheel — checked, not a bug.* Neither logs its own hook firing directly, so this
was reconstructed from energy/block deltas around exact-match-gated relics (`Candelabra.cs:25`:
`TurnNumber == 2`; `CaptainsWheel.cs:22`: `TurnNumber == 3`, both exact equality, not `<=`, so each can
fire at most once ever). Round 2's only play, `CASCADE` (X-cost, spends all remaining energy), logged
`SPENT ... energy=5` — base Ironclad energy is 3, so exactly `3 + 2` (Candelabra's amount). Round 3's
first `STATE` line (after `DEMON_FORM`, which grants Strength, not Block) showed `ghostBlock=18` —
exactly `CaptainsWheel`'s amount, via `AfterBlockCleared` firing at turn start before any card is
played. Rounds 3-5 show no further unexplained energy/block jumps of either amount. Both relics fired
exactly once, at the correct turn number, matching their source exactly — this **confirms P13 live**
(Candelabra's grant landing correctly is exactly the class of bug P13 fixed) and finds no new issue.

*A separate, more serious bug: round 5 played `CASCADE` roughly 40 times in under 30ms of log time,
every play against a target already at 0 HP, until `MaxPlaysPerTurn` tripped.* Root cause, confirmed
by reading `CombatManager.cs` in full: `CombatManager.Instance.IsInProgress` (`:153`, a plain
`private set` property) only flips false once `CheckWinCondition()` (`:1046-1059`) actually runs and
calls `EndCombatInternal()` — nothing about a kill itself sets it as a side effect. The two native
paths that call a card's play logic each call `CheckWinCondition()` themselves right after: a human's
`PlayCardAction` runs through `ActionExecutor`, which calls it after every completed `GameAction`
(`ActionExecutor.cs:170`); a real monster's own turn is driven by `ExecuteEnemyTurn`
(`CombatManager.cs:1061-1089`), which calls it once after `enemy.TakeTurn()` returns (`:1084`). That
second path is what P2 hooks into (`Creature.TakeTurn` → `GhostTurnController.RunTurnAsync`) — but it
only checks *after the whole turn*, which is fine for a real monster's handful of fixed moves (killing
the player one move early just means the rest of a short scripted sequence plays out against a corpse,
invisible in practice) and was never adequate for a play loop that can run up to `MaxPlaysPerTurn`
(40) iterations in one turn. `GhostTurnController` never called `CheckWinCondition()` itself at any
finer grain, so a kill mid-turn went completely unnoticed until the loop's own safety guard tripped.

**Confirmed fix (not yet re-verified live at time of writing):** `GhostTurnController.RunTurnAsync`
now calls `await CombatManager.Instance.CheckWinCondition()` after every card play, breaking out of
the loop immediately (skipping `FlushHand`, since combat has already torn itself down) if it returns
true or `IsInProgress` is now false — mirroring exactly what both native paths already do, just at the
finer per-card grain our longer play loop needs. Not a Harmony patch — this is mod-owned turn-controller
code omitting a step the native wrappers normally provide, not an engine assumption to route around.

**2026-09-02, RedMask confirmed fixed live (P10/P11 hold up) — and one more bug found the same
round: `PlatingPower`'s Block grant firing twice per round, once at the end of the human's turn too.**
Initially reported as a possible decay-timing question (buff/debuff decay is M6 scope, not M2) —
checked, and it is not: `PlatingPower.AfterSideTurnStart` (the actual decay/decrement logic,
`PlatingPower.cs:70-83`) already reads a correctly Side-filtered participants list (confirmed earlier,
same as every other `AfterSideTurnStart`/`BeforeSideTurnStart` caller). The double-fire is a different
hook entirely — `PlatingPower.BeforeSideTurnEndEarly` (`PlatingPower.cs:61-68`), which *grants* Block
each turn and checks only `participants.Contains(base.Owner)`, no side filter of its own. Root cause,
confirmed by reading `CombatManager.cs` in full: `EndPlayerTurnPhaseOneInternal` and
`EndPlayerTurnPhaseTwoInternal` (both hard-gated to `CurrentSide == Player` — i.e. only ever run
during the *human's* turn-end) each build `playersEndingTurn` from `_state.Players.ToList()`
(`:1158`, `:1288`) — the same `IsPlayer`-derived, not Side-filtered, list P9 already found and fixed
for turn *start* (§7 Q2) — and pass it straight through as `participants` to `Hook.BeforeTurnEnd`
(`:1179`)/`Hook.AfterTurnEnd` (`:1307`). That list includes the Ghost even though it is unambiguously
not the Ghost's turn ending. The Ghost's own real turn-end (`EndEnemyTurnInternal`, `:1248-1257`)
builds its list from `_state.CreaturesOnCurrentSide` — genuinely Side-filtered — so that call was
already correct; the bug was specifically the *human*-turn-end path spuriously including the Ghost.

**Confirmed fix (not yet re-verified live at time of writing):** P14 (`EnginePatches.cs`) — a prefix
on both `Hook.BeforeTurnEnd` and `Hook.AfterTurnEnd` (same signature, same bug, patched together via
`TargetMethods()`) that filters the Ghost's creature out of `participants` whenever `side ==
CombatSide.Player`. Fixes the whole class of "Ghost-owned relic/power reacts to the human's turn-end
as if it were its own" generically — the same P9 philosophy applied to turn-end instead of turn-start
— rather than special-casing `PlatingPower`.

**2026-09-02/03, M3 in progress: the game auto-updated twice in the same evening, the second update
reverting the first's API shape.** Confirmed by `sts2.dll`'s modified timestamp moving *twice* within
one session (23:12 build deployed clean; game re-updated to a DLL timestamped 23:52; re-decompiling
that DLL showed the changes below had reverted back to their original shape). Re-ran
`../STS2Ghostmod/.tools/ilspycmd.exe -p -o sts2-src <new sts2.dll>` after *each* update per this file's
own standing instruction, and re-verified every citation against whichever DLL was current before
fixing anything — twice, not once. **The user has now paused Steam auto-updates**, so the 23:52 DLL
(the game's standard/default release build) is the fixed target going forward, and everything below
reflects that final, stable target — not the intermediate one.

First update (in effect from roughly 22:37-23:52): `EncounterModel.IsDebugEncounter` was temporarily
removed (mod's override was deleted to match — harmless either way since nothing else reads it and the
current build restored it as `virtual bool => false`, so no override is needed regardless);
`Hook.BeforeTurnEnd`/`Hook.AfterTurnEnd` were temporarily renamed to `Hook.BeforeSideTurnEnd`/
`Hook.AfterSideTurnEnd` (same body/order/signature, name only); `Hook.ModifyDamage`,
`CreatureCmd.Damage`'s `cardSource`-taking overloads, and every `AbstractModel.ModifyDamage
{Additive,Multiplicative,Cap}` override (including `WeakPower`'s) temporarily gained a `CardPlay?
cardPlay` parameter (`CardPlay`, a per-card-play-instance context object distinguishing e.g. a
Replay's 1st vs 2nd play of the same card, still exists as a type used elsewhere — e.g.
`CreatureCmd.GainBlock` — just not threaded into the damage-modifier hooks in the current build).

**Second update (23:52, current, stable target) reverted all three of the above to their original
names/signatures** — confirmed directly from the freshly re-decompiled source, not assumed: `Hook.cs`
has `BeforeTurnEnd`/`AfterTurnEnd` again (`Hook.cs:1232,1267`), `CreatureCmd.Damage`'s ten overloads
are all back to their original parameter lists with no `CardPlay` (`CreatureCmd.cs:96-240`), and
`WeakPower.ModifyDamageMultiplicative`/`AbstractModel`'s base declaration are both back to the
original 5-parameter shape (no `cardPlay`). P14, P17, `QueuedDamagePacket`, and `GhostDamageQueue`
were all reverted to match — net effect, the mod's code is now back to exactly what it would have been
had the intermediate update never happened, confirmed against the actual DLL rather than assumed.

This whole two-update episode is exactly the scenario this file's environment note exists for —
nothing was assumed frozen, and nothing was fixed against a remembered diff without re-reading the
current source first. **Not yet re-verified live**: the churn landed in the middle of building M3's
queued-damage core (P15-P17, below), so a live test now needs to distinguish "leftover engine-update
fallout" from "the brand-new queueing mechanic's own bug" — both were live possibilities before the
revert; only the latter should remain now that the target build is confirmed stable and paused.

**2026-09-03, M3 queued-damage core confirmed live for the Ghost's own attacks** (the user observed
the Ghost's damage queuing to its next turn instead of landing immediately). Extended the same
session: P17 broadened from "Ghost's own attacks only" to **both directions** (COMBAT-RULES.md §3),
so the human's damage against the Ghost now queues too — resolved at the *end* of the Ghost's turn
(`GhostTurnController.RunTurnAsync`, after the play loop, before `FlushHand`) rather than the *start*
of the Ghost's own (`SetupTurn`), matching §2's asymmetric step ordering (Ghost's queued attack
resolves at step 1 of its own next turn; the human's resolves at step 5, after the Ghost has already
had its turn to react with Block/buffs). The gate condition changed from `dealer ==
session.GhostPlayer.Creature` to `dealer.CombatState == session.GhostPlayer.Creature.CombatState` —
scoped to this Ghost Duel's own combat, not to a specific side, since it's a plain 1v1 and "either
participant" needs no further disambiguation. **Not yet confirmed live.**

Also added a presentation layer (`GhostDamageIndicatorPresenter`, `src/Presentation/`) showing a
queued-damage number above whichever side currently has a pending packet (COMBAT-RULES.md §8).
Confirmed by full read: `NCreature.UpdateIntent` throws outright for a `Player`-backed creature
(`Creature.Monster == null`), and every native intent call site is gated on `IsMonster`/`IsEnemy` — so
the real `AbstractIntent`/`NIntent`/`MonsterModel.NextMove` system cannot be reused for either the
Ghost or the human. What every `NCreature` genuinely has, monster or player alike, is an
unconditionally-assigned `IntentContainer` (`%Intents`), already anchored by native code to a real
`Marker2D` (`"IntentPos"`) — a plain `Label` parented into that same container, one per side, is the
narrowest way to show a number without touching monster-only machinery or hardcoding a position
(POSTMORTEM F10). Wired as a `GhostDamageQueue.Changed` event subscriber owned by `GhostSession`
(created/disposed with the session), per ARCHITECTURE.md §8's "presentation is a subscriber to a plain
event, never a detached task inheriting an ambient scope" rule (POSTMORTEM F8) — every entry point
catches and logs its own exceptions so a presentation bug cannot affect the logical turn. **Not yet
confirmed live** — this is a plain `Label`, not the polished sprite/animation an `NIntent` gets; good
enough to verify the mechanism works, revisit visuals once confirmed.

**2026-09-03, live test: queueing itself confirmed working both directions (log shows correct
`QUEUED`/`RESOLVED` pairs for both `Ghost#` and `Human#`), but no damage indicator ever appeared for
either side.** Root cause, confirmed by reading `NCreature.cs` in full: native `ExecuteEnemyTurn`
(`CombatManager.cs:1079`) calls `NCreature.PerformIntent()` on every enemy-side creature — Ghost
included — immediately before that creature's turn starts. `PerformIntent()`
(`NCreature.cs:641-653`) sets `IntentContainer.Modulate`'s alpha to 0 (instantly in Fast Mode, or via
a 0.4s fade otherwise) to hide the previous turn's monster intent icons. Nothing ever restores it for
a `Player`-backed creature: the only restore path, `RevealIntents()` (`NCreature.cs:667-674`), is
reached exclusively through the Monster-only `Creature.PrepareForNextTurn`/`Monster.RollMove` chain
(ENGINE-NOTES.md's earlier intent-system research). Since a child's rendered alpha in Godot is its own
Modulate multiplied by every ancestor's, a label parented inside `IntentContainer` inherits that
zeroed alpha regardless of its own settings — invisible from the moment it's created, every single
Ghost turn, with no code path that ever un-hides it. (The human's indicator failing too, with no
equivalent per-turn hide call reaching it, was not independently root-caused — most likely the same
class of issue if `%Intents`' scene-authored baseline Modulate starts transparent for every creature
and only monsters' own reveal path ever raises it; not confirmed since no `.tscn` source is available
to inspect directly.)

**Confirmed fix (not yet re-verified live at time of writing):** stopped parenting the indicator label
into `IntentContainer` at all. `IntentContainer.GlobalPosition` is still read for placement (that part
was never the problem — it's correctly anchored to a real `Marker2D` by native code), but the label is
now parented directly under the `NCreature` node itself — a sibling of the manipulated subtree, not a
descendant of it — with `TopLevel = true` so its own transform is independent of whatever animation
Modulate/scale/position tweens `NCreature` itself might be running.

**2026-09-03, live test: icon confirmed correct (real attack-intent sprite), but position and sizing
were both wrong** — the number rendered detached from either creature, near unrelated background
scenery. Root cause of *this* attempt: the custom `Control`+`TopLevel=true`+manual-half-size-offset
scheme was a guess at replicating native layout, not a reuse of it. Per the user's explicit direction
("look at how this icon is handled for enemies in a normal combat and copy that implementation"),
switched to the **real native `NIntent` node** instead of any custom Control:

- `NIntent` (`NIntent.cs`, scene `res://scenes/combat/intent.tscn`) is the actual class a monster's
  intent icon is. `NCreature.UpdateIntent` — the usual way one gets created — throws for a
  `Player`-backed creature, but that method's own body is just two lines per intent:
  `NIntent.Create(startTime)` then `IntentContainer.AddChildSafely(nIntent)`, followed by
  `nIntent.UpdateIntent(intent, targets, owner)`. Calling those same lines directly, skipping only the
  `Monster == null` throw, gets the exact same node with its own correct built-in layout/sizing/
  positioning/bob-animation/frame-cycling — no manual position math needed at all, which is what was
  wrong with the previous attempt.
- `AbstractIntent.GetIntentLabel`/`AttackIntent.GetTotalDamage` are both genuinely `virtual`
  (confirmed by reading `AbstractIntent.cs`/`AttackIntent.cs` directly) — but `AttackIntent
  .GetSingleDamage` (what `SingleAttackIntent`'s own `GetIntentLabel` calls) is **not** virtual and
  always re-runs `Hook.ModifyDamage` fresh against the local player with `ValueProp.Move` and no card
  source. Correct for a real monster (whose base move value has no modifiers baked in), wrong here:
  this mod's queued packets already have Vulnerable locked in from card-play time, so reusing
  `SingleAttackIntent` directly would silently re-apply Vulnerable a second time, live. New class
  `GhostQueuedAttackIntent : AttackIntent` (`src/Presentation/`) overrides `GetTotalDamage`/
  `GetIntentLabel` directly with the packet total already correctly computed, bypassing the recompute
  entirely rather than fighting it. (Known, not-yet-fixed minor gap: `AttackIntent
  .GetIntentDescription`, used only for the hover tooltip body, is not overridden and still calls
  `GetSingleDamage` — so the tooltip's own number would still be wrong if anyone hovers it. The main
  displayed label is unaffected.)
- `NIntent` parented into `IntentContainer` for the Ghost still hits the exact Modulate-zeroing
  problem the *previous* attempt worked around by avoiding the container — `NIntent._Ready()` only
  manages its own internal `_intentHolder`'s Modulate, never its parent `IntentContainer`'s. Fixed by
  explicitly resetting `IntentContainer.Modulate = Colors.White` whenever this creature has something
  queued to show, right before creating/reusing its `NIntent` — safe for the Ghost, since
  `PerformIntent`'s fade always runs *before* the Ghost's own cards can queue anything that turn,
  never after; harmless for the human, since nothing else native ever touches its `IntentContainer`.
- Live refresh (the Weak-application display bug from the previous round) no longer needs a separate
  `CombatStateChanged` subscription of this mod's own: `NIntent._EnterTree()` already subscribes to
  that same native event itself and calls its own `UpdateVisuals()`, which re-invokes
  `GhostQueuedAttackIntent`'s `Func<int>` closure — itself reading `GhostDamageQueue
  .TotalPendingFor(dealer)` fresh every call — so it stays live for free once wired up correctly.

**Not yet confirmed live.**

**2026-09-02, M3 queued-damage core (P15-P17) — first working slice, Ghost's own damage only, not
yet confirmed live.** Per the user's explicit decision to pull `COMBAT-RULES.md` §2-§5's real queued-
damage duel loop forward into M3 (rather than faking a retrospective-only intent display — see
`ARCHITECTURE.md`'s M3 section for the full reasoning and the three research passes that grounded the
design). Confirmed via full reads of `CreatureCmd.Damage`, `Hook.ModifyDamage`/`ModifyDamageInternal`,
`AttackCommand.cs`, and `WeakPower.cs`:

- No engine seam exists to split "compute a damage number" from "apply it" — `CreatureCmd.Damage`
  (`CreatureCmd.cs:258-...`, the 7-parameter `cardSource`+`cardPlay` overload after the game update)
  fuses `Hook.ModifyDamage` → `AfterModifyingDamageAmount` → `BeforeDamageReceived` (confirmed
  non-idempotent: `ThornsPower.BeforeDamageReceived` deals real retaliation damage here) → Block
  subtraction → HP loss → kill-check → VFX → history, inline, with `Creature.DamageBlockInternal`/
  `LoseHpInternal`'s own doc comments warning against calling them directly.
- The one genuinely pure, repeatedly-safe-to-call function is `Hook.ModifyDamage` itself — the same
  function used both for real resolution and for the native card-hover damage preview
  (`DamageVar.cs`/`CalculatedDamageVar.cs`).
- `WeakPower.ModifyDamageMultiplicative` is independently callable and pure: `if (dealer !=
  base.Owner) return 1m;` gates it to its own owner's attacks only — so its contribution can be read
  in isolation, `dealer.GetPower<WeakPower>()?.ModifyDamageMultiplicative(...)`, without going through
  the full modifier fold.

Design (`src/Duel/`): a `QueuedDamagePacket` locks its amount at card-play time via `Hook.ModifyDamage`
while a new scope, `GhostWeakSuppressionScope` (P15, a postfix forcing `WeakPower
.ModifyDamageMultiplicative` to return 1 while active), excludes Weak specifically — honestly
capturing Vulnerable/Strength/everything else as of that moment ("Vulnerable captured in card-effect
order", §5) without ever folding Weak in, rather than baking it in and later dividing it out
(POSTMORTEM F14's exact mistake). Weak is re-read live, multiplied in fresh, every time
`QueuedDamagePacket.DisplayAmount` is queried (both at future intent-display time and at resolution).
P17 (a prefix on `CreatureCmd.Damage`'s core overload, scoped narrowly to card-sourced, non-self Ghost
damage — relic/power/orb/pet-sourced direct damage is explicitly deferred until a concrete card proves
the gap) queues instead of resolving. P16 (a prefix on `Hook.ModifyDamage`, active only while
resolving a queued packet via a second scope, `GhostQueuedResolutionScope`) stops the real
`CreatureCmd.Damage` call used at resolution from re-applying modifiers already baked into
`LockedAmount` — the "explicit token carried through the resolution call" `COMBAT-RULES.md` §5 asks
for, instead of the old mod's ambient tuple-matching (POSTMORTEM F14). Resolution is wired into
`GhostTurnController.SetupTurn` (already the correct point in the sequence, per P13): the Ghost's own
queued packets from last turn resolve there, before energy reset/hand draw, matching `COMBAT-RULES.md`
§2's "the attack the Ghost queued last turn resolves against the human" as the first thing that
happens on the Ghost's turn. The human's own queueing (§2's asymmetric resolution timing) and all
presentation (intent nodes, the human's own queued-attack display) are follow-on steps, not yet built.

**2026-09-03, M6: Red Mask's Weak on the human outlived its own turn by a full round — root-caused
to a native grace-tick asymmetry, not a mod bug in the usual sense.** Reported: Weak, applied to the
human at the very start of the Ghost's turn (Red Mask, `BeforeSideTurnStart`, `TurnNumber<=1`),
should decay by the end of that same Ghost turn but instead persisted through the human's entire next
turn too — a one-turn debuff with a two-turn effect. Confirmed directly from `PowerModel.cs:242-245`'s
own doc comment: `SkipNextDurationTick` "enables the behavior of duration-type powers (Vulnerable,
Weak, etc.) ticking down at the end of the *monster* side turn, but skipping the first tick if a
*monster* applied the power to the player." `PowerCmd.Apply` sets this flag unconditionally whenever
`target.Side == CombatSide.Player` (`PowerCmd.cs:144-147`), assuming any debuff landing on the human
came from a monster mid-fight and deserves one full grace turn before its first decay check.
`WeakPower`/`VulnerablePower`/`FrailPower` (confirmed identical shape in all three, read in full):
`Type => PowerType.Debuff`, `StackType => PowerStackType.Counter`, and `AfterSideTurnEnd`: `if (side
== CombatSide.Enemy) PowerCmd.TickDownDuration(this);` — one fixed native checkpoint (the *Enemy*
side's own turn end) for decay, regardless of who applied or holds the stack; `TickDownDuration`
(`PowerCmd.cs:190-200`) itself: `if (power.SkipNextDurationTick) { power.SkipNextDurationTick = false;
} else { await Decrement(power); }` — the grace consumes exactly one decay opportunity, no more.

Since Red Mask applies Weak at the very *start* of the Ghost's own turn — immediately before that
same turn's decay checkpoint — the grace tick (meant to buy the human a full turn before a
monster-applied debuff can decay) instead swallows the Ghost's own turn-end tick entirely, so the
real first decay lands a full round later than intended. The asymmetry: this grace is granted only
for `target.Side == Player`; a debuff landing on the *Ghost* (e.g. Vulnerable from the human's Bash)
never receives it and already decays "on schedule" at that same fixed checkpoint — which is the
inconsistency the user asked to remove ("make similar stacked debuffs consistent").

**Decision (2026-09-03, the user, overriding COMBAT-RULES.md §6's earlier (b) recommendation): rule
(a)** — decay at the end of the *applying* side's turn. For every debuff a Ghost-owned effect can
apply, the existing native checkpoint (Enemy side turn end) already *is* the applying side's turn
end, so implementing rule (a) here means removing the human-only grace tick, not building new decay
timing. **Confirmed fix (not yet re-verified live at time of writing):** P18 (`EnginePatches.cs`) — a
postfix on `PowerCmd.Apply` (disambiguated from the generic `Apply<T>` overload by explicit parameter
types, per the P12 precedent) resets `power.SkipNextDurationTick = false` whenever `power.Type ==
PowerType.Debuff` and `target` belongs to this Ghost Duel's own combat. No power named by class —
applies uniformly to Weak, Vulnerable, Frail, and any future debuff sharing this shape. Everything
past that one flag (the real decrement, removal at zero stacks, VFX) stays entirely native.

Explicitly out of scope for this fix, left open for M6's own gate: Poison/Doom source-owned timing
(untouched, no live evidence either way), and the "expiry cannot retroactively alter a committed
attack" property (not separately verified — though P18's fix only changes *when* a decay tick fires,
not the queued-damage packet math itself, so no new retroactivity risk is expected from this change
specifically).

**2026-09-03, M8 kickoff: debug harness made character-agnostic (P19), not yet confirmed live.**
Researched whether any live "currently selected character" state exists at `NMainMenu._Ready` time
(where the debug buttons are added) that could be read instead of the hardcoded `Ironclad` generic
argument `DebugBoot`/`GhostPlayerFactory` used through M1-M6. Confirmed by full read: no such state
exists. `NCharacterSelectScreen`'s own selection lives inside a `StartRunLobby` object
(`_lobby.LocalPlayer.character`) that is not constructed until `InitializeSingleplayer()` runs — only
reachable from the Play button handler (`NMainMenu.SingleplayerButtonPressed`), never during main-menu
boot; `_lobby` is explicitly nulled on every close, so even a prior visit this session leaves nothing
behind. `ProgressState`/`SerializableProgress` (the save files) have no "last selected character"
field either — grepped the whole tree for that shape, zero matches. The engine's own fallback for a
fresh lobby player is a hardcoded default (`StartRunLobby.TryAddPlayerInFirstAvailableSlot:796`,
`ModelDb.Character<Ironclad>()`) until `SetLocalCharacter` overrides it.

**Confirmed fix:** since nothing to *read* exists, *track* it instead — a postfix on
`NCharacterSelectScreen.SelectCharacter(NCharacterSelectButton, CharacterModel)` (`:801`, public, the
one method every character-portrait click funnels through, confirmed by tracing the real new-run flow
click → `SelectCharacter` → `_lobby.SetLocalCharacter` → `NCharacterSelectScreen.BeginRun` →
`NGame.StartNewSingleplayerRun(CharacterModel, ...)`) stashes the picked `CharacterModel` into
`SelectedCharacterTracker.Current` (P19), defaulting to Ironclad exactly like the engine's own
fallback. `DebugBoot`/`GhostPlayerFactory` now read this and call the non-generic
`Player.CreateForNewRun(CharacterModel character, UnlockState unlockState, ulong netId)`
(`Player.cs:301` — confirmed real; the generic `CreateForNewRun<T>` is a thin wrapper around this one,
`Player.cs:286-289`) instead of a compile-time `<Ironclad>` argument. `M2Deck`'s fixed/random decks
are unchanged (Ironclad-specific content, per CLAUDE.md's no-content-emulation-for-other-classes
premise) — `DebugBoot` now skips that injection when the selected character isn't Ironclad and runs
with that character's own real stock deck instead, which is still a valid M1-style regression check.

**2026-09-03, M8 multi-character testing (Silent/Regent confirmed clean; Necrobinder/Defect found real
gaps) — Defect's orb bugs fixed, not yet confirmed live.** Two separate confirmed issues:

- **Orb damage never queued.** Confirmed live (`ghostduel - Defect.log`): `ZAP` (channels a Lightning
  orb) logged `LEGAL`/`SPENT`/`PLAYED` but no `QUEUED` line — its damage resolved immediately. Traced:
  `LightningOrb.ApplyLightningDamage` (`LightningOrb.cs:58`) calls the 5-argument `CreatureCmd.Damage`
  overload, which forwards to the exact same 6-parameter chokepoint P17 already patches
  (`CreatureCmd.cs:141-144→240`) but with `cardSource` hardcoded to `null`. P17's own gate required
  `cardSource != null` — a deliberate, documented scope limit ("out of scope until a concrete
  card/relic proves the gap") that turned out to silently exempt *all* orb damage rather than
  narrowing to "card-sourced," contradicting COMBAT-RULES.md §3's own "Both directions" list, which
  already names orbs as queueable. **Fix:** dropped the `cardSource is null` clause. Nothing
  downstream needed changing — `QueuedDamagePacket.CardSource` was already nullable and
  `GhostDamageQueue.ResolvePendingFor` already forwards it straight back into the same
  null-tolerant overload.
- **Orb passive triggers (Frost's end-of-turn Block, etc.) never fire for the Ghost at all.** Not a
  side-check bug this time — confirmed by reading `FrostOrb.cs`/`OrbModel.cs`/`OrbQueue.cs` in full
  that the whole passive-trigger chain is side-agnostic by construction (scoped only by `OrbQueue
  ._owner`). The actual gap: `PlayerCombatState.OrbQueue.AfterTurnStart`/`.BeforeTurnEnd` each have
  exactly one call site in the entire engine, and both live *only* inside the `CurrentSide ==
  CombatSide.Player` branches of `CombatManager`'s turn-start block (`:526-538`) and
  `EndPlayerTurnPhaseOneInternal`→`DoTurnEnd` (`:1218`) — `EndEnemyTurnInternal`, the Ghost's own
  turn-end path, never calls either. Invisible in vanilla (a `Monster` has no `PlayerCombatState`);
  real once a `Player` occupies `Side.Enemy`. No native call exists to intercept/redirect, so there
  was nothing to patch — this is wired directly into `GhostTurnController` instead:
  `SetupTurn` now calls `OrbQueue.AfterTurnStart` after its existing turn-start sequence, and
  `RunTurnAsync` now calls `OrbQueue.BeforeTurnEnd` before resolving the human's queued packets
  (so a Frost Block gain applies before that hit lands, matching COMBAT-RULES.md §2 step 5). Known,
  accepted ordering deviation: native runs `AfterTurnStart` *after* `Hook.AfterSideTurnStart` finishes,
  but P13 already restructured `SetupTurn` to run *before* that hook's real body (the `VeryHotCocoa`
  fix), so this necessarily lands slightly earlier relative to it than a human's equivalent call does.

**Not yet confirmed live.**

**2026-09-03, live re-test: the Necrobinder hang from the previous round is confirmed fixed** (one of
the container/position/health-bar/pet-damage fixes above resolved it as a side effect — not
independently root-caused, but confirmed gone). Same re-test found the reported Defect orb-passive fix
was incomplete, and surfaced three new Necrobinder issues:

- **Orb passives fired at the end of BOTH sides' turns, not just their owner's.** Root cause,
  confirmed by reading `CombatManager.EndPlayerTurnPhaseOneInternal` in full (`CombatManager.cs:1143-
  1209`, hard-gated to the human's own turn end): it builds `playersEndingTurn` from
  `_state.Players.ToList()` (`:1158`) — the exact same `IsPlayer`-derived, not Side-filtered, list P9
  already found for turn-start and P14 for the turn-end hook dispatch — and calls `DoTurnEnd(item,
  ...)` for every entry (`:1191`), including the Ghost. `DoTurnEnd`'s (`:1216-1246`) first line is
  `player.PlayerCombatState.OrbQueue.BeforeTurnEnd(...)` — so the Ghost's own orb passives were firing
  once, spuriously, at the human's turn end (native, previously unpatched) and a second time,
  correctly, at the Ghost's own (the mod's own call, added the previous round). **Confirmed fix:** P22,
  a prefix on `DoTurnEnd` skipping it entirely for the Ghost (mirroring P9's exact shape) —
  `GhostTurnController` now replicates `DoTurnEnd`'s *full* body (not just the orb call: Ethereal-
  exhaust and `HasTurnEndInHandEffect` cards too, in native's own order, before the hand gets flushed)
  at the Ghost's actual turn end, so nothing native provided is lost. `Hook.BeforeFlush`/`Hook
  .AfterAutoPostPlayPhaseEntered` (`:1167`, `:1204` — this same method's other two per-player loops
  over the identical list) are likely the same bug shape, not yet confirmed broken for anything in the
  current test decks — noted, not patched speculatively.
- **Ghost's Osty pet sprite faced the wrong direction.** Confirmed: `PlayerCmd.AddPet<T>`
  (`PlayerCmd.cs:239`) sets a pet's `Side` to match its summoner's (`player.Creature.Side`) — for a
  normal human, Osty is genuinely ally-side, so its sprite is authored facing right (toward the enemy)
  the same convention as a Player character sprite, per P6's own existing doc comment. Sitting in the
  enemy container for the same reason as the Ghost's own sprite, it needs the identical flip — added
  to P6's pet-reparenting branch.
- **Ghost's Osty still has no health bar, and the Ghost keeps summoning a brand-new Osty instead of
  reinforcing the existing one (4 distinct Ostys after 4 turns).** Neither yet root-caused — a
  dedicated research pass is in progress. P21's fix is confirmed working for the Ghost's own creature
  but not for Osty specifically, and several plausible mechanisms (node-creation timing, `_isRemote
  PlayerOrPet` being re-set elsewhere, `PlayerCombatState` identity/`_pets` list tampering) have
  already been read and ruled out — see the follow-up entry once that research lands.

**2026-09-03, M8 Necrobinder: three confirmed bugs fixed, one hang not yet root-caused.** Playing the
Ghost's real Necrobinder deck, combat hung completely after the Ghost's `BODYGUARD` (summons the pet
`Osty`, a genuine `MonsterModel`) logged `LEGAL`/`SPENT` but never `PLAYED` — nothing further ever
logged. Separately, the user reported the Ghost's own Osty rendered at the wrong position with no
health bar (the human's own Osty, summoned identically, rendered correctly).

Confirmed, fixed:

- **Osty (any Ghost-owned pet) was left in the ally container, at the wrong screen position.**
  `NCombatRoom.AddCreature`'s container check (`creature.IsPlayer || creature.PetOwner != null`) — a
  sibling of the bug P6 already patches for the Ghost's own creature — unconditionally ally-sides any
  pet regardless of its owner's side; P6's original guard (`ReferenceEquals(creature,
  session.GhostPlayer.Creature)`) never matches a pet (a different `Creature` object), so Osty was
  never reparented. The position was doubly wrong, not just the container: the same native method's
  mid-combat pet-positioning code sets a pet's `Position` relative to its owner's own node `Position`
  — which, for the Ghost, is already expressed in the enemy container's coordinate frame by the time
  any pet is summoned (P6 already reparented the Ghost's own node at combat start, long before).
  Extended P6 to also reparent a pet whose `PetOwner` is the Ghost's `Player` — confirmed sufficient
  with **no position recompute needed**: the already-correct numeric `Position` value just needed
  reinterpreting in the matching frame, which a plain reparent (`keepGlobalTransform: false`, same as
  P6's existing call) does for free.
- **The Ghost's own health/Block display, and any pet's, defaults to hidden, only revealing on
  hover.** Root cause, confirmed by reading `NCreature._Ready()` in full: `_isRemotePlayerOrPet =
  (Entity.IsPlayer && !LocalContext.IsMe(Entity)) || (Entity.PetOwner != null &&
  !LocalContext.IsMe(Entity.PetOwner))` — logic authored for genuine co-op multiplayer ("don't clutter
  my screen with a remote ally's or their pet's bar"), misfiring because `LocalContext.IsMe` checks a
  creature's owning `Player.NetId` against the one real local human's, and the Ghost (and anything it
  owns) is never that, by design. New reflection accessor `NCreatureRemoteFlagAccess` (matching
  `CreatureSideAccess`'s established pattern) plus P21, a postfix on `NCreature._Ready()`, sets the
  private `_isRemotePlayerOrPet` field back to `false` and explicitly reveals the display for the
  Ghost's own creature and any pet it owns — deliberately not touching `LocalContext.IsMe`/`NetId`
  itself, which is read pervasively elsewhere for reasons unrelated to display (action-queue routing,
  choice-context ownership) and carries the same untraceable-regression risk the reverted original P9
  was reverted over.
- **Unleash's damage (dealt by Osty, not the Necrobinder itself) queued correctly but never
  resolved — silently never landing.** Confirmed: `Unleash.OnPlay` uses `AttackCommand.FromOsty`,
  setting `dealer` to the Osty `Creature`, not the owner's own creature. P17's queueing gate already
  matched this (`dealer.CombatState` still equals the Ghost's), but neither of `GhostTurnController`'s
  two `ResolvePendingFor` calls (`dealer == ghostPlayer.Creature` at turn start; `dealer == opponent`
  at turn end) reference-equal a pet, so an Osty-dealt packet matched neither and stayed queued
  forever — the *card* completed fully (`LEGAL`/`SPENT`/`PLAYED`/`STATE` all logged), but its damage
  silently never applied. Also explains a purely cosmetic side effect: the log line for this packet
  read `QUEUED Human#0 UNLEASH...` while the *Ghost* was playing Unleash — `dealer.Player` is null for
  a `Monster`-backed pet, tripping P17's `?? 0` fallback and its "not literally the Ghost's own
  creature, so must be Human" label logic. Fixed at the source: `GhostDamageQueue` now has a shared
  `BelongsTo(packet, dealer)` check (`ReferenceEquals(packet.Dealer, dealer)` OR
  `packet.Dealer.PetOwner == dealer.Player`), used by both `TotalPendingFor` and `ResolvePendingFor`,
  so a pet's damage resolves alongside its owner's own packets at the correct turn boundary. P17's log
  label also corrected (`DescribeDealer`, checks `PetOwner` before falling back) — cosmetic, but no
  longer misleading.

**Not confirmed:** the `BODYGUARD` hang itself. A dedicated research pass traced every `await` in
`Bodyguard.OnPlay` → `OstyCmd.Summon` → `PlayerCmd.AddPet<Osty>` → `CreatureCmd.Add` →
`CombatManager.AfterCreatureAdded` → `Hook.AfterCreatureAddedToCombat`/`PowerCmd.Apply<DieForYouPower>`/
`Hook.AfterSummon` and found no confirmed indefinite await (P4 already guards the one known
`Monster.RollMove` NPE site, though its guard doesn't cover Osty specifically — moot here since the
condition that would call `RollMove` requires `CurrentSide == Player`, false during the Ghost's own
turn). Two unconfirmed candidates, neither verified: (1) an unhandled exception somewhere in the
`Hook.AfterCreatureAddedToCombat`/`AfterSummon` fan-out specific to a relic/power in the actual
equipped decks (not enumerable without that data); (2) a genuinely unguarded null-deref at
`OstyCmd.cs:87-88` (`NCreature nCreature = NCombatRoom.Instance?.GetCreatureNode(osty); nCreature
.OstyScaleToSize(...)` — the second line isn't `?.`-guarded), flagged as low-confidence since the node
should normally be non-null by that point. Needs a live re-test with the three fixes above in place
(the health-bar/position fixes especially — it's possible what looked like a hang was partly a
silently-broken visual state) before chasing this further; not claiming it fixed.

**2026-09-03, M8: real-flow debug entry point (P20), not yet confirmed live.** The user pointed out
`DebugBoot`'s hand-built shortcut never shows a character-select screen at all (it constructs the
human `Player` directly with whatever `SelectedCharacterTracker.Current` already holds from an
*earlier* session's clicks, per P19) — for M8 they want a button that plays a genuinely real run
start (real character select, real Neow) and only substitutes the Ghost Duel once the player reaches
their actual first fight. Researched the real, non-debug room-entry flow in full (character select →
Embark → `NGame.StartNewSingleplayerRun` → `RunManager.EnterAct`/`EnterMapCoordInternal` →
`EnterMapPointInternal` → `CreateRoom` → `ActModel.PullNextEncounter`). Confirmed:

- `NMainMenu.SingleplayerButtonPressed` (`NMainMenu.cs:770-781`) — for a player with existing save
  data — reduces to two public calls: `SubmenuStack.GetSubmenuType<NCharacterSelectScreen>()
  .InitializeSingleplayer()` then `SubmenuStack.Push(screen)`. Reproducing exactly this from a debug
  button lands the player on the real screen with no simulated clicks needed for anything after that
  — they drive character pick and Embark themselves.
- Neow runs automatically as literally the first room only when `UnlockState
  .IsEpochRevealed<NeowEpoch>()` is true (`RunManager.cs:445-461,502-507,724-756` — otherwise the
  map's starting point is force-set to `Monster` and Neow is skipped). True on any established save;
  not unconditional, noted rather than assumed.
- `ActModel.PullNextEncounter(RoomType)` (`ActModel.cs:445-454`) is confirmed, by grep across the
  entire decompiled tree, to have **exactly one call site** in the whole engine — `RunManager.cs:878`,
  inside `CreateRoom`'s `Monster`/`Elite`/`Boss` branch, itself reached from the lazy, real room-entry
  path `EnterMapPointInternal` (as opposed to `EnterRoomDebug`, which passes an explicit model and
  bypasses this method entirely — the two paths converge afterward at the shared `EnterRoom`). This is
  the correct, narrowest possible interception point for "substitute the encounter for one specific
  upcoming room, then get out of the way."
- `RunManager`'s live `RunState` has exactly one public accessor, `DebugOnlyGetState()`
  (`RunManager.cs:1698-1704`, doc-commented "TEMPORARY... ONLY... IN TESTS" but technically
  unrestricted). `RunState.Players[0].Character` at the moment `PullNextEncounter` fires is the real
  human's actual character — the same `Player`/`RunState` objects flow unbroken from run creation
  through Neow and map traversal, confirmed no reconstruction happens in between.
- `GhostPlayerFactory.CreateDebugGhost`/`JoinRun` (already relied on by `DebugBoot`) operate purely
  through public `RunState`/`Player` API and don't care whether the `RunState` was hand-built or came
  from the real flow — confirmed no structural difference exists between the two (`RunState
  .CreateForNewRun` is the one factory both paths use).

**Confirmed fix, not yet re-verified live:** `RealFlowGhostDuelEntry` (`src/Debug/`) — `Start(NMainMenu)`
reproduces the two-call "Play" sequence above and arms a one-shot flag; P20, a prefix on
`ActModel.PullNextEncounter`, fires only when that flag is armed and `roomType == Monster` (so it
waits through Neow and any Rest/Shop/Event rooms, and never touches Elite/Boss, which share this
method with a different `roomType`), builds+joins the Ghost to the real `RunState`, begins
`GhostSession`, and substitutes `GhostDuelEncounter`. **Known, not-yet-solved limitation:** the real
flow autosaves on every room entry (`EnterMapPointInternal`, `shouldSave: true` unlike `DebugBoot`'s
`false`) — since neither `GhostSession` nor the Ghost `Player` are ever part of `RunState`/
`SerializableRun` by design, a save/reload mid-Ghost-fight started this way would not restore either.
Not addressed — this is a dev-only debug entry point, not a shipping feature.

**2026-09-02, mod failed to load after deploying P12 — confirmed from `godot.log`:**
`Harmony.PatchAll` threw `AmbiguousMatchException` inside P12's `TargetMethod()`:
`AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.Apply))` with no parameter-type list cannot
distinguish the intended overload from `PowerCmd.Apply<T>(PlayerChoiceContext,
IEnumerable<Creature>, decimal, Creature, CardModel, bool)`, a second, generic multi-creature-target
overload also named `Apply`. The game's mod loader caught the exception and logged "Finished mod
initialization" anyway, but `Harmony.PatchAll` had already aborted partway through its scan of the
assembly — meaning P12 never patched, and any other patch later in `Harmony`'s (unspecified, roughly
metadata-token-order) type enumeration may not have applied either. **Confirmed fix:** pass the exact
7-parameter list (`PlayerChoiceContext, PowerModel, Creature, decimal, Creature, CardModel, bool`) to
`AccessTools.Method` to disambiguate. Not yet re-confirmed live (needs a fresh game launch + log
check) that mod init now completes cleanly.

---

Everything here was read directly from the decompiled game at
`../STS2Ghostmod/.tools/sts2-src/` for build **0.107.1**. Paths below are relative to that
directory. Line numbers are from that decompilation and will drift; re-verify after a game update.

**Rule: nothing goes in this file unless it was read from source.** Inferences and expectations
belong in `ARCHITECTURE.md`, labelled as such.

---

## 1. An enemy-side Player creature is structurally legal

### Side routing works

`MegaCrit.Sts2.Core.Combat/CombatState.cs:221` — `AddPlayer(Player)` calls `AttachCreature` then
`AddCreature`. `AddCreature` (`:534`) routes purely on `creature.Side`:

```csharp
List<Creature> list = ((creature.Side == CombatSide.Player) ? _allies : _enemies);
```

So a `Player` whose `Creature.Side` is `Enemy` lands in `_enemies` and is exposed by
`CombatState.Enemies`. **No monster is required to occupy the enemy side.**

### It counts as a real enemy for combat end

`MegaCrit.Sts2.Core.Entities.Creatures/Creature.cs:252` — `IsPrimaryEnemy` is
`Side == CombatSide.Enemy && !IsSecondaryEnemy`, and `IsSecondaryEnemy` only checks for powers with
`OwnerIsSecondaryEnemy`. No monster check.

`MegaCrit.Sts2.Core.Combat/CombatManager.cs:~192` (combat-end condition) tests
`_state.Enemies.Any(e => e != null && e.IsAlive && e.IsPrimaryEnemy)`. An enemy-side Player
therefore keeps combat alive and its death ends combat normally. **Victory detection needs no
patch.**

### It gets its turn

`MegaCrit.Sts2.Core.Combat/CombatManager.cs:1061` — `ExecuteEnemyTurn` iterates
`_state.Enemies.ToList()`, awaits `NCreature.PerformIntent()`, then `await enemy.TakeTurn()`, then
`WaitForUnpause()` and `CheckWinCondition()`. The loop is side-based, not monster-based.

`NCreature.PerformIntent` (`MegaCrit.Sts2.Core.Nodes.Combat/NCreature.cs:641`) only animates
`IntentContainer` children and waits ~0.25–0.4s. Safe for a Player-backed node.

### Its relics, cards, powers and orbs are hook listeners

`MegaCrit.Sts2.Core.Combat/CombatState.cs:410` — `IterateHookListeners()` walks
`_allies` **and** `_enemies`. For a creature with a non-null `Player` it adds relics (skipping
melted), potions, orbs, and every card in every pile with its affliction and enchantment.

Gated on `player.IsActiveForHooks`, which is set to `Creature.IsAlive` in the `Player` constructor
(`Player.cs:272`) and in the deserializing path (`Player.cs:438`). It is `private set` and updated
nowhere else, so a restored Ghost is active. **Ghost relics and cards receive ordinary combat hooks
with no extra subscription.** This is the fact that makes the whole design work.

---

## 2. Exactly three engine sites assume enemies are monsters

These are the *only* places found that break for an enemy-side Player, and each needs one patch.

| Site | Code | Effect |
| --- | --- | --- |
| `Creature.cs:706` `TakeTurn()` | `if (!IsMonster \|\| Side != CombatSide.Enemy) throw new InvalidOperationException("Only enemy monsters can take automated turns."); if (!Monster.SpawnedThisTurn) await Monster.PerformMove();` | Throws immediately. Prefix must skip it and run the Ghost turn instead. |
| `Creature.cs:414` `AfterAddedToRoom()` | `if (Side == CombatSide.Enemy) await Monster.AfterAddedToRoom();` | `NullReferenceException` — `Monster` is null. Prefix must no-op for Ghosts. |
| `CombatManager.cs:860` `AfterCreatureAdded(Creature)` | `await creature.AfterAddedToRoom(); if (creature.IsEnemy && _state.CurrentSide == CombatSide.Player) creature.Monster.RollMove(...)` | `NullReferenceException` on `RollMove`. Prefix must no-op for Ghosts. |

> **Correction, 2026-09-02:** the line above was wrong — this is not merely a presentation concern.
> Read in full: `Creature.PrepareForNextTurn(IEnumerable<Creature> targets, bool rollNewMove =
> true)` (`Creature.cs:546-554`) calls `Monster.RollMove(targets2)` whenever `rollNewMove` is true,
> which is the **default**. `CombatManager.cs:480-483` calls it with the default every time the
> human's turn starts: `foreach (Creature enemy in _state.Enemies) enemy.PrepareForNextTurn
> (_state.PlayerCreatures);` — no `rollNewMove: false` override, unlike `CreatureCmd.Add`'s summon
> path (`CreatureCmd.cs:74`, which does pass `false`). This NPEs for the Ghost on **every human
> turn**, not just a rare summon path. It is P7 in `ARCHITECTURE.md` §4, a straightforward no-op
> prefix — M1 has no canned move to roll and no intent nodes yet (M3).

Nothing else in the enemy-turn path dereferences `Monster` (re-confirmed after the P7 correction
above — the only remaining candidates found were `CreatureCmd.Add`'s already-guarded call and this
one).

---

## 3. Room and combat setup order

`MegaCrit.Sts2.Core.Rooms/CombatRoom.cs:122` — `EnterInternal`:

```csharp
if (CombatState.Players.Count == 0)
    foreach (Player item in runState?.Players ?? Array.Empty<Player>())
        CombatState.AddPlayer(item);
...
await StartCombat(runState);   // or StartPreFinishedCombat
```

`StartCombat` then: generates monsters → preloads assets → `CreateCreature`/`AddCreature` per monster
→ `NCombatRoom.Create` → `CombatManager.SetUpCombat(CombatState)` → `Hook.AfterRoomEntered` →
`AfterCombatRoomLoaded()`.

`CombatManager.cs:350` — `SetUpCombat` iterates `state.Players` **twice**:

```csharp
foreach (Player player in state.Players) player.ResetCombatState();
foreach (Player player2 in state.Players) player2.PopulateCombatState(player2.RunState.Rng.Shuffle, state);
NetCombatCardDb.Instance.StartCombat(state.Players);
foreach (Creature creature in state.Creatures) AddCreature(creature);
```

**Consequences for the design.** Two mutually exclusive options; pick one and write it down:

- **(A)** Leave `CombatState.Players` unpatched, so `SetUpCombat` populates the Ghost's combat state
  natively along with the humans. Then find and fix the *specific* sites that mean "human party" by
  `Players` — rather than rewriting the property globally.
- **(B)** Exclude Ghosts from `Players` and populate their combat state manually. This is what the
  first pass did, and it means the mod must own `ResetCombatState`, `PopulateCombatState` and
  `NetCombatCardDb` registration forever.

`ARCHITECTURE.md` selects **(A)** and explains why.

Also note `Player.PopulateCombatState` (`MegaCrit.Sts2.Core.Entities.Players/Player.cs`) is small:
clone each deck card via `state.CloneCard`, set `DeckVersion`, `DrawPile.AddInternal`, then
`RandomizeOrderInternal`. `ResetCombatState` is just `PlayerCombatState = new PlayerCombatState(this)`.

---

## 4. The Ghost node lands on the ally side

`MegaCrit.Sts2.Core.Nodes.Rooms/NCombatRoom.cs:722` — `AddCreature(Creature)`:

```csharp
if (creature.IsPlayer || creature.PetOwner != null) _allyContainer.AddChildSafely(nCreature);
else                                                _enemyContainer.AddChildSafely(nCreature);
if (creature.SlotName != null) {
    if (EncounterSlots == null) throw new InvalidOperationException(...);
    nCreature.GlobalPosition = EncounterSlots.GetNode<Marker2D>(creature.SlotName).GlobalPosition;
}
```

Two facts follow:

1. Container choice is by `IsPlayer`, not by `Side`. A Ghost always starts in `_allyContainer`.
2. A creature with a null `SlotName` is **never positioned**. An encounter that generates no monsters
   has no slots, so a Ghost gets no position — reparenting with `keepGlobalTransform: true` then
   preserves the ally-side placement. See POSTMORTEM F9.

The fix must actually assign a position: give the encounter enemy slots and set `SlotName` on the
Ghost creature, or read the enemy container's marker and set `GlobalPosition` explicitly.

---

## 5. Card play — the exact native contract

`MegaCrit.Sts2.Core.Models/CardModel.cs`

### `CanPlay(out UnplayableReason, out AbstractModel?)` — `:1734`

Fails if: `CombatState` and `Owner.Creature.CombatState` are both null, or
`Owner.PlayerCombatState == null`; the `Unplayable` keyword; `!Owner.PlayerCombatState.HasEnoughResourcesFor(this, out reason)`;
`TargetType == AnyAlly && combatState.PlayerCreatures.Count(c => c.IsAlive) <= 1`;
`!Hook.ShouldPlay(...)`; `!IsPlayable`.

Note that `AnyAlly` legality reads `combatState.PlayerCreatures` — one of the properties that means
"the acting side's party" from a card's point of view.

### `IsValidTarget(Creature?)` — `:1771`

```csharp
if (target == null)  return TargetType != AnyEnemy && TargetType != AnyAlly;
if (!target.IsAlive) return false;
if (TargetType == AnyEnemy) return target.Side != Owner.Creature.Side;
if (TargetType == AnyAlly)  return target.Side == Owner.Creature.Side;
return false;   // every other TargetType with a non-null target
```

Two rules for the controller:

- Pass a target **only** for `AnyEnemy` and `AnyAlly`. Everything else (`Self`, `Osty`, `None`) must
  receive `null` or validation fails.
- Enemy/ally targeting is already **owner-side-relative**. `Strike`, `Defend` and `Bash` need no
  adaptation for a Ghost. This is why the design works without Ghost-specific card copies.

### `SpendResources()` — `:1816`

Returns `(energySpent, starsSpent)`; may convert excess energy to Stars when
`Hook.ShouldPayExcessEnergyCostWithStars` allows. Call it, then pass the values through in
`ResourceInfo` — do not compute costs mod-side.

### `OnPlayWrapper(PlayerChoiceContext, Creature? target, bool isAutoPlay, ResourceInfo, bool skipCardPileVisuals = false)` — `:1867`

Order of operations:

1. `choiceContext.PushModel(this)`, `await CombatManager.Instance.WaitForUnpause()`.
2. `CurrentTarget = target`, `CurrentPlayIndex = 0`.
3. `isAutoPlay == false` → `await CardPileCmd.AddDuringManualCardPlay(this)`;
   `true` → `CardPileCmd.Add(..., PileType.Play, ...)` plus a visual wait.
4. `Hook.ModifyCardPlayResultPileTypeAndPosition`, then `GeneratePlayCount`.
5. `CombatManager.Instance.BeginCardOrPotionEffect(Owner)` — a plain per-`Player` depth dictionary
   (`CombatManager.cs:321`); no membership requirement.
6. Per play index: power-card fly VFX or multi-play anim → `Hook.BeforeCardPlayed` →
   `History.CardPlayStarted` → `OnPlay` → enchantment `OnPlay` → affliction `OnPlay` →
   `History.CardPlayFinished` → `Hook.AfterCardPlayed`. Returns early at each step if
   `Owner.Creature.IsDead`.
7. Result pile: `None` → `RemoveFromCombat`, `Exhaust` → `CardCmd.Exhaust`, else `CardPileCmd.Add`.

`skipCardPileVisuals: true` only suppresses tweens/waits; all logic still runs.

`WaitForUnpause` (`CombatManager.cs`) loops only while the user has the game paused, and is skipped
entirely when `NonInteractiveMode.IsActive`.

---

## 6. Non-interactive / scripted play exists in the shipped game

- `MegaCrit.Sts2.Core.Helpers/NonInteractiveMode.cs` — `IsActive => TestMode.IsOn || AutoSlayerCheck()`.
- `MegaCrit.Sts2.Core.TestSupport/TestMode.cs` — `public static bool IsOn { get; set; }` (a public
  setter, plus `TurnOnInternal()`), `AssertOn`/`AssertOff`.
- `MegaCrit.Sts2.Core.TestSupport/ICardSelector.cs`, `TestCardSelector.cs` — the seam the first pass
  correctly used for card-grid prompts.
- `MegaCrit.Sts2.Core.AutoSlay/AutoSlayer.cs` + `AutoSlayConfig.cs` — a full scripted-run driver with
  per-room/screen handlers, a 30s watchdog and state dumps. It sets
  `NonInteractiveMode.AutoSlayerCheck = () => IsActive` (`AutoSlayer.cs:66`).
- `MegaCrit.Sts2.Core.Nodes/NGame.cs:663` — the built-in entry point is gated:

  ```csharp
  if (!IsReleaseGame() && CommandLineHelper.HasArg("autoslay")) { ... new AutoSlayer().Start(seed, logFile); }
  ```

  `IsReleaseGame()` is presumably true for the Steam build, so `--autoslay` is unavailable there —
  **verify this before relying on it.**
- `FastModeType.Instant` is a real preference value (`NGame.cs` downgrades it to `Fast` outside the
  editor on startup) and `NCreature.PerformIntent` short-circuits on it.

Practical upshot for M0: the mod can own its own `--ghostduel-debug` command-line argument, drive
mode selection and the first room programmatically, and set `FastMode` for the harness — reusing
`ICardSelector` and, if it proves usable, `AutoSlayer`. Do not flip `TestMode.IsOn` globally; it
changes engine behavior far beyond waits.

---

## 7. Open questions — answered 2026-09-02

### Q1. Does `NGame.IsReleaseGame()` return true in the Steam build? Is `--autoslay` reachable?

`NGame.IsReleaseGame()` (`MegaCrit.Sts2.Core.Nodes/NGame.cs:729-732`) is hardcoded:

```csharp
public static bool IsReleaseGame() => true;
```

No build define, env var or config input — always `true` in this compiled build. The `--autoslay`
gate at `NGame.cs:663` (`if (!IsReleaseGame() && CommandLineHelper.HasArg("autoslay"))`) is therefore
**dead code**, on every platform, not just Steam. `--autoslay` cannot be reached through
`NGame.GameStartup` in this binary. **Do not build the M0 debug harness on `--autoslay`.**

`MegaCrit.Sts2.Core.TestSupport/TestMode.cs` (42 lines): `public static bool IsOn { get; set; }`
(`:9`, a bare public setter), `IsOff` (`:15`), `AssertOn`/`AssertOff` (`:17-33`), `TurnOnInternal()`
(`:38-41`, doc comment: "NEVER CALL THIS. Only calls should be in NetCoreRunner and CiCoreRunner.").
Nothing stops a mod from setting `IsOn` directly, but it has broad side effects: `NetCombatCardDb.
StartCombat` (`NetCombatCardDb.cs:40-43`) and `NCombatRoom.Create` (`NCombatRoom.cs:330-333`, returns
`null` when `TestMode.IsOn`) both branch on it — setting it globally during real gameplay would
suppress visual combat-room creation. **Do not set `TestMode.IsOn` globally**, confirming the
original guidance in §6.

`MegaCrit.Sts2.Core.Helpers/NonInteractiveMode.cs` (21 lines, full file):

```csharp
public static class NonInteractiveMode
{
    public static Func<bool> AutoSlayerCheck { get; set; } = () => false;
    public static bool IsActive => !TestMode.IsOn ? AutoSlayerCheck() : true;
}
```

A settable delegate seam independent of `TestMode`. Available if a later milestone needs it; M0's
debug harness (§8 below) does not.

**Conclusion:** `--autoslay` is unreachable in this build. The M0 debug harness must not depend on it
or on `AutoSlayer` (see §8 — `AutoSlayer` is UI-click automation, not an API surface, and is far
heavier than needed anyway). Use a dedicated `--ghostduel-debug` flag patched into
`NGame.GameStartup` instead.

### Q2. Which engine call sites read `CombatState.Players` / `PlayerCreatures` / `Allies` and mean "the human party"?

Ground truth first — `CombatState.cs:60-80`:

```csharp
public IReadOnlyList<Creature> Allies => _allies;                                     // :60, Side-partitioned (via AddCreature)
public IReadOnlyList<Creature> Enemies => _enemies;                                    // :65
public IReadOnlyList<Creature> Creatures => _allies.Concat(_enemies).ToList();         // :70
public IReadOnlyList<Creature> PlayerCreatures => Creatures.Where(c => c.IsPlayer).ToList(); // :75
public IReadOnlyList<Player> Players => PlayerCreatures.Select(c => c.Player).ToList(); // :80
```

`Creature.IsPlayer => Player != null` (`Creature.cs:167`) — **not** Side-based. `Creature.IsEnemy =>
Side == CombatSide.Enemy` (`Creature.cs:243`). **`.Players`/`.PlayerCreatures` mean "every creature
with a `Player` reference," independent of `Side`. `.Allies`/`.Enemies` are the real Side partition.**
A Ghost (`Side = Enemy`, `Player != null`) is automatically excluded from `.Allies` and automatically
included in `.Players`/`.PlayerCreatures`. `IRunState.Players` (`RunState.cs:60`, field `_players`)
is a separate, run-scoped list set at run creation — a Ghost is not in it unless a mod adds it there.

Over 30 real hits were found. The ones that matter for M1:

**Hard blocker (confirmed crash):** `CombatManager.cs:865`, inside `AfterCreatureAdded`:

```csharp
if (creature.IsEnemy && _state.CurrentSide == CombatSide.Player)
    creature.Monster.RollMove(_state.Players.Select(p => p.Creature));
```

`creature.IsEnemy` is Side-based (true for the Ghost); `_state.CurrentSide` starts as
`CombatSide.Player` (`CombatState.cs:157`); `creature.Monster` is `null` for a Player-backed
creature. **This NullReferenceExceptions the first time a Ghost's creature is added to combat**,
confirming P4 in `ARCHITECTURE.md` §4 is load-bearing exactly as designed.

**Soft blockers, gated behind run type (need live confirmation, not yet observed):**
- `CombatManager.cs:780` `SetReadyToBeginEnemyTurn`: compares `_playersReadyToBeginEnemyTurn.Count`
  against `_state.Players.Count` — would deadlock waiting on the Ghost's readiness signal, but is
  bypassed entirely when `RunManager.Instance.NetService.Type == NetGameType.Singleplayer` (`:782`).
- `CombatManager.cs:804` `AllPlayersReadyToEndTurn`: same pattern, bypassed via
  `RunManager.Instance.IsSingleplayerOrFakeMultiplayer` (`:806,814`).

Both are neutralized *if* the debug run stays flagged Singleplayer — true for `RunManager.
SetUpNewSingleplayer` (§8), but worth watching in the M1 live-test log in case anything flips the run
to a different `NetGameType`.

**Silent correctness/cosmetic bugs, not crashes, deferred past M1 (no patch without a failing
observation, per `ARCHITECTURE.md` §4 rule 1):** MP-headcount scaling on `combatState.Players.Count`
in `ArtifactPower.cs:45`, `BufferPower.cs:36`, `PlatingPower.cs:87`, `SkittishPower.cs:86`,
`SlipperyPower.cs:43` (all M2+/M8 concerns, none of Strike/Defend/Bash/Burning Blood touch them) —
**plus one more site found 2026-09-02 while chasing a Vulnerable report**: `PowerCmd.Apply`
(`PowerCmd.cs:128`) applies `power.GetScaledAmountForMultiplayer(...)` whenever
`combatState.Players.Count > 1 && target.IsPrimaryEnemy && power.ShouldScaleInMultiplayer` — true for
*any* debuff applied to the Ghost, for *any* power that overrides `ShouldScaleInMultiplayer` (base
`PowerModel` default is `false`; `VulnerablePower` doesn't override it, so this specific report wasn't
this bug — but the next power that does override it, applied to the Ghost, will silently get an
amount scaled by `combatState.Players.Count` as if 2 *co-op* players were fighting one monster, not 1
player fighting a Ghost). Same root cause as Q2's list: `.Players` counts the Ghost. Worth a real fix
before any power with `ShouldScaleInMultiplayer => true` matters (M2+), not before; UI
player-count/row code (`NEndTurnButton.cs:431`, `NCombatUi.cs:529`, `NPlayerHand.cs:893`,
`NControllerCardPlay.cs:229`, `NCardPlay.cs:346`, `NCreature.cs:659`) that would render/target the
Ghost as if it were a second human player — cosmetic risk to watch for in the M1 live test, not
pre-patched.

**No save/serialization hits.** Save code operates on `runState.Players` only, unaffected by
combat-time Ghost membership.

**Conclusion:** Option (A) (§3) holds. `CombatState.Players`/`.PlayerCreatures` including the Ghost is
not a bug to patch around — it is *required* (see Q3) for the Ghost's cards to be playable at all.
The only crash is the already-planned P4. The two turn-readiness counts self-resolve under
Singleplayer. Nothing here adds to the M1 patch count beyond P1–P5.

### Q3. Does `NetCombatCardDb.StartCombat(state.Players)` need to see a Ghost's cards?

Yes, and it already does, automatically. `NetCombatCardDb.StartCombat` (`NetCombatCardDb.cs:33-59`)
IDs every card in every pile of every passed player (`:44-47`) and subscribes to future pile changes
(`:48-58`). `CombatManager.SetUpCombat` calls it with `state.Players` (`CombatManager.cs:372`), and
per Q2 that list is `IsPlayer`-derived, not Side-filtered — the Ghost is included automatically, no
adaptation needed.

This registration is required, not optional, in single-player too:
`PlayCardAction`'s constructor path calls `NetCombatCard.FromModel(cardModel)`
(`PlayCardAction.cs:49`) → `NetCombatCardDb.Instance.GetCardId(card)` (`NetCombatCard.cs:34`), which
throws `InvalidOperationException` (`NetCombatCardDb.cs:73-80`) if the card was never IDed. Every
card play, including the Ghost's own, goes through this lookup. **Conclusion:** no adaptation needed;
excluding the Ghost from `Players` (option B) would have broken card play outright.

### Q4. `_allyContainer` / `_enemyContainer` layout and enemy-side slot markers

`NCombatRoom._Ready()` resolves both via Godot unique-name scene lookups: `_allyContainer =
GetNode<Control>("%AllyContainer")` (`:345`), `_enemyContainer = GetNode<Control>("%EnemyContainer")`
(`:346`) — not `[Export]` fields, plain scene-tree lookups against `combat_room.tscn`.

`EncounterSlots` (`private Control? EncounterSlots { get; set; }`, `:302`) is assigned in
`CreateEnemyNodes()` only `if (_visuals.Encounter.HasScene)` (`:479-484`) — **null for any encounter
without a custom scene**, which is the common case, including a debug encounter with zero monsters.

Placement is **`IsPlayer`-based, not `Side`-based** — this is the fact that matters most:

```csharp
// NCombatRoom.cs:722-733 (AddCreature)
if (creature.IsPlayer || creature.PetOwner != null) { _allyContainer.AddChildSafely(nCreature); }
else                                                { _enemyContainer.AddChildSafely(nCreature); }
if (creature.SlotName != null) {
    if (EncounterSlots == null) throw new InvalidOperationException(...);
    nCreature.GlobalPosition = EncounterSlots.GetNode<Marker2D>(creature.SlotName).GlobalPosition;
}
```

A Ghost (`IsPlayer == true`) is **always** parented into `_allyContainer`, regardless of `Side`. This
holds even when `CreateEnemyNodes()` iterates `_visuals.Enemies` (Side-based, correctly includes the
Ghost) and calls `AddCreature` on it — `AddCreature`'s own `IsPlayer` check still routes it into
`_allyContainer`. There is **no fallback marker** for a slotless creature: if `SlotName == null`, the
slot-position block is skipped entirely and the node keeps its default `Position` (0,0 relative to
its container) until the later `PositionEnemies`/`PositionCreaturesWithSlots` pass — but the Ghost
never reaches that pass either, since it's not in `_enemyContainer`'s child list to begin with.
Separately, `PositionCreaturesWithSlots` (`:503-509`) calls `EncounterSlots.GetNode<Marker2D>
(slotName)` with no null-check, so a slotless creature would NPE there too *if* `EncounterSlots` were
non-null — moot for M1 since the debug encounter declares no custom scene.

**Conclusion — revises the assumption in §8/§9 of `ARCHITECTURE.md`.** Slot data alone cannot place
the Ghost on the enemy side, because container choice never consults `Side`. Achieving "visible on
the enemy side" (required by the M1 gate) needs one additional, narrowly-guarded patch on
`NCombatRoom.AddCreature` that reparents a Ghost's node into `_enemyContainer` after the native call
completes — mirroring the old mod's `EnemyGhostCreatureNodePlacementPatch` (salvageable shape, not
its broken `keepGlobalTransform: true`-only version, since here the container itself must change).
This is P6 in the M1 patch set (`ENGINE-NOTES.md` §2 lists P1-P4 from the original three-plus-one
crash sites; `EnginePatches.cs` in code numbers the full set P1-P6 including this one and the boot
hook, see `ARCHITECTURE.md` addendum below).

### Q5. Does `PlayerCombatState.Phase` gate `CanPlay`/`OnPlayWrapper`/hooks?

`PlayerTurnPhase` (`PlayerTurnPhase.cs:6-61`): `None, Start, AutoPrePlay, Play, AutoPostPlay, End`.
A whole-tree grep for `.Phase ==`/`.Phase !=` against a `PlayerCombatState`-typed value found exactly
six sites, none in `CardModel.CanPlay`/`OnPlayWrapper` (zero `Phase` occurrences in `CardModel.cs` at
all): four in `MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms/CombatRoomHandler.cs` (bot decision timing,
irrelevant to core rules) and two in `NHandCardHolder.cs:312,335` (`ShouldGlowGold`/`ShouldGlowRed`,
checked only *after* `CanPlay()` already returned true — purely cosmetic glow). `Phase` is set by
`CombatManager.SetPhaseForAllPlayers` (`:296-300`) and read by MP wire serialization and
`UnceasingTop.cs:29`'s own hook-gated relic logic (not a `CanPlay` gate).

**Conclusion:** A controller can leave a Ghost's `Phase` at its default/unmanaged value without
breaking `CanPlay`, `OnPlayWrapper` or hook dispatch. The only loss is cosmetic (no hand-glow) and a
desync from `CombatManager`'s own `SetPhaseForAllPlayers` sweep (which touches the Ghost regardless,
since it's in `_state.Players` per Q2, harmlessly). M1's controller does not drive `PlayerTurnPhase`
at all.

---

## 8. Starting a run and reaching first combat without menu UI

### The lower-level entry point already exists — `NSceneBootstrapper.StartNewRun()`

`MegaCrit.Sts2.Core.Nodes.Debug/NSceneBootstrapper.cs:83-139`, launched today via `--bootstrap`
(`NGame.cs:671-675`) with a developer-supplied `IBootstrapSettings`. Sequence (verbatim):

```csharp
RunState runState = RunState.CreateForNewRun(
    new [] { Player.CreateForNewRun(settings.Character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL) },
    acts, settings.Modifiers, GameMode.Standard, settings.Ascension, seed);
RunManager.Instance.SetUpNewSingleplayer(runState, settings.SaveRunHistory);
await PreloadManager.LoadRunAssets(new [] { settings.Character });
RunManager.Instance.Launch();
_game.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
await RunManager.Instance.SetActInternal(0);
RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(runState.RunLocation);
RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(runState.MapLocation);
await RunManager.Instance.EnterRoomDebug(settings.RoomType, MapPointType.Unassigned,
    settings.RoomType.IsCombatRoom() ? settings.Encounter.ToMutable() : null);
```

A mod does not need the `IBootstrapSettings` file indirection — it can call the same
`RunManager`/`RunState` calls directly with hard-coded values. `RunManager.EnterRoomDebug(RoomType,
MapPointType, AbstractModel?, bool)` (`RunManager.cs:990-1058`) is explicitly documented "Should only
be used in tests or dev commands, never in real flows" and, passed an `EncounterModel` as `model`,
bypasses the map/act encounter-pool selection entirely (`RunManager.cs:867-892`, `CreateRoom`).

### `Player.CreateForNewRun` and Ironclad's starting deck/relic

`Player.cs`:
- `CreateForNewRun<T>(UnlockState, ulong netId) where T : CharacterModel` (`:286-289`) — thin wrapper
  over `CreateForNewRun(ModelDb.Character<T>(), unlockState, netId)`.
- `CreateForNewRun(CharacterModel character, UnlockState unlockState, ulong netId)` (`:301-306`) —
  builds the `Player` from `character.StartingHp, character.MaxEnergy, character.StartingGold, 3
  (potion slots), character.BaseOrbSlotCount, new RelicGrabBag(), unlockState`, then calls
  `PopulateStartingInventory()` (`:330-346`), which populates deck, relics and potions from the
  `CharacterModel`. The caller supplies only `UnlockState` and a `netId` — HP/energy/deck/relic all
  come from the character class.

`MegaCrit.Sts2.Core.Models.Characters/Ironclad.cs`: `StartingHp => 80` (`:33`); starting deck
(`:43-55`, confirmed 5×`StrikeIronclad`, 4×`DefendIronclad`, 1×`Bash`); starting relic (`:57`,
`ModelDb.Relic<BurningBlood>()`). Matches the M1 debug-encounter table in `PLAN.md` exactly — no
mod-side deck construction needed, just `Player.CreateForNewRun<Ironclad>(...)`.

### Zero-monster encounter and combat-room entry

`CombatRoom.StartCombat` (`CombatRoom.cs:197-231`) calls `Encounter.GenerateMonstersWithSlots(...)`
then iterates `Encounter.MonstersWithSlots`, calling `CombatState.CreateCreature`/`AddCreature` per
entry. **If `MonstersWithSlots` is empty this loop is a no-op** — zero monsters needs no engine
change. `EncounterModel` (`MegaCrit.Sts2.Core.Models/EncounterModel.cs`) is `abstract class
EncounterModel : AbstractModel` with one abstract method, `GenerateMonsters()`
(`:250`, returns `IReadOnlyList<(MonsterModel, string?)>`), plus `AllPossibleMonsters` (`:155`). The
existing debug pattern to copy is `MockMonsterEncounter : EncounterModel`
(`MegaCrit.Sts2.Core.Models.Encounters.Mocks/MockMonsterEncounter.cs:7-19`, `IsDebugEncounter =>
true` at `:11`) — a `GhostDuelEncounter : EncounterModel` returning `Array.Empty<(MonsterModel,
string?)>()` from `GenerateMonsters()` is exactly this shape.

### `CombatManager.SetUpCombat` is the correct P5 target, not `CombatRoom.EnterInternal`

Read in full (`CombatManager.cs:350-378`). `public void SetUpCombat(CombatState state)` — synchronous,
public, single `if (_state != null) throw ...` guard, then: `foreach (Player player in state.Players)
player.ResetCombatState();` (`:364-367`) → `foreach (Player player2 in state.Players)
player2.PopulateCombatState(player2.RunState.Rng.Shuffle, state);` (`:368-371`) →
`NetCombatCardDb.Instance.StartCombat(state.Players);` (`:372`) → `foreach (Creature creature in
state.Creatures) AddCreature(creature);` (`:373-376`, this `AddCreature` is `CombatManager.
AddCreature`, `:841-854` — `Monster?.SetUpForCombat()`, `StateTracker.Subscribe`, distinct from
`CombatState.AddCreature` and `NCombatRoom.AddCreature`). A prefix that calls `state.AddPlayer
(ghostPlayer)` as its first statement puts the Ghost through every one of these native passes —
including `PopulateCombatState`, which clones `Character.StartingDeck` into the Ghost's real
`DrawPile` for this combat — for free.

Confirmed downstream: `AfterCombatRoomLoaded()` (`:380-385`) → `StartCombatInternal()` (`:387-416`)
→ `foreach (Creature creature in _state.Creatures) await AfterCreatureAdded(creature);` (`:394-398`)
— this is the real call site that reaches the P4 crash (`CombatManager.cs:865`,
`creature.Monster.RollMove`) for an **initial** combat creature, not just `CreatureCmd.Add`'s
mid-combat summon path (`CreatureCmd.cs:71`, the other caller). Confirms P4 fires for the Ghost given
P5 runs first.

### Boot hook point

`NGame.GameStartup()` (`NGame.cs:618-684`) dispatches:

```csharp
if (!IsReleaseGame() && CommandLineHelper.HasArg("autoslay")) { ... }   // :663-670, dead (Q1)
else if (CommandLineHelper.HasArg("bootstrap")) { ... }                  // :671-675
else if (StartOnMainMenu) { ... }                                        // :676-682
```

A Harmony **prefix** on `GameStartup`, mode-guarded on `CommandLineHelper.HasArg("ghostduel-debug")`
as its first statement, is the right shape: when the flag is present, run the mod's own bootstrap and
return `false` (skip the original body, so the main menu never loads); otherwise return `true`
(vanilla startup, untouched). This is the debug-harness patch, separate from `AutoSlayer`.

**`AutoSlayer` is not a low-level API** — `Start(seed, logFile)` (`AutoSlayer.cs:101-113`) drives a
full UI-click automation harness (`mainMenu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton")`
and real `UiHelper.Click(...)` calls, room/screen handler dispatch, `WaitHelper` polling). It never
calls `RunManager.SetUpNewSingleplayer`/`RunState.CreateForNewRun` directly and is far heavier than a
debug harness needs. **Do not reuse it** — call the `NSceneBootstrapper` primitives directly instead.

---

## 9. Native turn/card-play contract for a scripted controller

### Draw, discard, exhaust — `CardPileCmd` / `CardCmd`

`MegaCrit.Sts2.Core.Commands/CardPileCmd.cs`:
- `Draw(PlayerChoiceContext, decimal count, Player player, bool fromHandDraw = false)` (`:798-857`)
  — draws from `PileType.Draw.GetPile(player)` into `PileType.Hand.GetPile(player)`, auto-shuffles
  discard into draw when empty (`ShuffleIfNecessary`, `:838`), respects the hand cap
  (`CardPile.MaxCardsInHand`, `:818-823,844`). No caller-supplied source pile — implicit.
- `Add(CardModel, PileType, CardPilePosition position = Bottom, ...)` (`:259-266`) — generic pile
  move, used internally by everything else, including end-of-turn discard.

`MegaCrit.Sts2.Core.Commands/CardCmd.cs`:
- `Discard(PlayerChoiceContext, CardModel)` (`:147-150`) and the `IEnumerable<CardModel>` overload
  (`:157-160`) — use the batch overload for multi-card discard (see the method's own warning about
  Sly-style effect timing in a loop).
- `Exhaust(PlayerChoiceContext, CardModel, bool causedByEthereal = false, bool skipVisuals = false)`
  (`:237-246`).
- `AutoPlay(PlayerChoiceContext, CardModel, Creature? target, ...)` (`:51-131`) — **do not use this
  for the Ghost's normal card plays.** Its own doc comment says "Automatically play a card **for
  free**" (used for effects like Havoc/Duplication Potion); it builds `ResourceInfo { EnergySpent =
  0, StarsSpent = 0, ... }` (`:123-129`) regardless of the card's real cost. Using it for the Ghost
  would let every card play bypass energy entirely, breaking the M1 gate's affordability checks
  (items 5-8). The correct native path is `CardModel.CanPlay`/`SpendResources`/`OnPlayWrapper`
  directly (below) — the same path `PlayCardAction` uses for a human play.

`PileType` enum (`PileType.cs:7-37`): `None, Draw, Hand, Discard, Exhaust, Play, Deck`.

### The real per-card-play sequence — mirror `PlayCardAction.ExecuteAction()`

`PlayCardAction.cs:62-104` (human play), the sequence a Ghost controller should copy minus the
`GameAction`/network wrapping:

```csharp
if (!_card.CanPlay(out UnplayableReason _, out AbstractModel _) || !_card.IsValidTarget(target)) { Cancel(); return; }
(int energySpent, int starsSpent) = await _card.SpendResources();
ResourceInfo resources = new ResourceInfo {
    EnergySpent = energySpent, EnergyValue = energySpent,
    StarsSpent = starsSpent, StarValue = starsSpent
};
PlayerChoiceContext ctx = new GameActionPlayerChoiceContext(this); // Ghost: use BlockingPlayerChoiceContext instead
await _card.OnPlayWrapper(ctx, target, isAutoPlay: false, resources);
```

`CardModel.CanPlay(out UnplayableReason, out AbstractModel?)` (`CardModel.cs:1734-1764`) — confirmed
verbatim as originally documented in §5 above; `IsValidTarget` (`:1771-1794`) — confirmed
side-relative, no Ghost adaptation needed for `AnyEnemy`/`AnyAlly` cards.
`SpendResources()` (`:1816-1829`) — confirmed verbatim: computes energy/Stars cost itself (including
the excess-energy-to-Stars conversion via `Hook.ShouldPayExcessEnergyCostWithStars`), calls
`Owner.PlayerCombatState.LoseEnergy`/`LoseStars` internally. **Do not compute cost mod-side** — call
`SpendResources()` and use its return values directly, exactly as `PlayCardAction` does.

### `PlayerChoiceContext` — `BlockingPlayerChoiceContext` is a real engine type

`PlayerChoiceContext` (`MegaCrit.Sts2.Core.GameActions.Multiplayer/PlayerChoiceContext.cs:9-64`) is
`abstract class` with a model stack (`PushModel`/`PopModel`, `:39-59`) and two abstract members every
concrete context implements: `SignalPlayerChoiceBegun`/`SignalPlayerChoiceEnded` (`:61-63`). Four
concrete subclasses exist; the right one for a Ghost controller is
**`BlockingPlayerChoiceContext`** (`BlockingPlayerChoiceContext.cs:18-29`) — doc comment: "for when we
don't care if player choice blocks the task"; both signal methods are `Task.CompletedTask` no-ops
(`:20-28`). No `GameAction`/`ActionQueueSet` wiring needed. Instantiate one per Ghost turn (or per
decision — either is fine given the no-op body).

### Hand/pile properties

All on `PlayerCombatState` (`PlayerCombatState.cs`): `Hand` (`:60`), `DrawPile` (`:62`),
`DiscardPile` (`:64`), `ExhaustPile` (`:66`), `PlayPile` (`:68`), each a `CardPile`; `AllPiles`
(`:70-80`). `CardPile.Cards` is the ordered collection to enumerate left-to-right for "cards in hand."

### Start of turn: native `SetupPlayerTurn` must run for the Ghost's own turn *and* be suppressed during the human's — correction, see §0 M2 section

**Correction (2026-09-02, live finding — see §0's M2 section for the full writeup):** the original
version of this note said `SetupPlayerTurn` "does NOT run automatically for the Ghost" and concluded
there was "no public API to invoke it for an enemy-side player." That was wrong, and the error was a
research gap, not a re-read of different code: it never cross-checked whether the `_state.Players`
list `SetupPlayerTurn`'s caller iterates is actually filtered to the human side, and per Q2 (§7) it is
not — `CombatState.Players` is `IsPlayer`-derived, not Side-filtered, so it already contains the
Ghost. The real behavior, confirmed live via `CrimsonMantlePower` double-firing: `SetupPlayerTurn`
**does** get called for the Ghost natively — just at the wrong time (during the human's own turn,
alongside the human's own call), not never. P9 (`EnginePatches.cs`) now suppresses that spurious call
for the Ghost specifically, so the only `SetupPlayerTurn`-equivalent work it does happens through
`GhostTurnController`, at the Ghost's own turn start, as originally intended.

The real native "start player turn" method, `CombatManager.SetupPlayerTurn(Player, HookPlayerChoiceContext)`
(private, `CombatManager.cs:629-676`), does: `Hook.ShouldPlayerResetEnergy` → `ResetEnergy()`/
`AddMaxEnergyToCurrent()` (`:641-649`) → `Hook.AfterEnergyReset` (650) → `Hook.BeforeHandDraw` (652)
→ `handDraw = Hook.ModifyHandDraw(state, player, 5m, out modifiers)` (654, base hand size **5**) →
turn-1 Innate/enchantment reordering (657-672) → `await CardPileCmd.Draw(ctx, handDraw, player,
fromHandDraw: true)` (673) → `Hook.AfterPlayerTurnStart` (675).

It is called from `CombatManager.StartTurn()` for every entry in `playersStartingTurn`
(`CombatManager.cs:446`: `playersStartingTurn = (state2.CurrentSide != CombatSide.Player) ? new
List<Player>() : _state.Players.ToList();`, loop at `:509-519`) — which, per the correction above,
includes the Ghost whenever it's the *human's* turn too, not just the human. `GhostTurnController`
still owns the Ghost's own equivalent setup at the Ghost's actual turn start
(`PlayerCombatState.ResetEnergy()`, `PlayerCombatState.cs:162-165`, then `await CardPileCmd.Draw(ctx,
5m, ghostPlayer, fromHandDraw: true)`) — that part of the original design was always correct; P9 just
stops the native path from *also* doing it a second time, at the wrong moment.

Symmetrically, `CombatManager.EndPlayerTurnPhaseOneInternal`/`EndPlayerTurnPhaseTwoInternal`
(`:1143,1279`) both throw `InvalidOperationException` if `_state.CurrentSide != CombatSide.Player`
(checked at `:1150,1281`), and `PlayerCmd.EndTurn`/`EndPlayerTurnAction` route through the same
side-gated machinery. The Ghost controller must replicate end-of-turn hand flush directly, mirroring
`CombatManager.FlushPlayerHand` (`:1313-1347`): discard non-retained hand cards via `await
CardPileCmd.Add(cardsToFlush, PileType.Discard)` (`:1341`), then `player.PlayerCombatState.
EndOfTurnCleanup()` (`:1346`, itself calling `CardModel.EndOfTurnCleanup()` on every card in every
pile).

**Conclusion — this is exactly the shape `ARCHITECTURE.md` §6's turn skeleton already describes,
confirmed against source rather than assumed.** The controller owns: reset energy, draw 5, loop
plays via `CanPlay`/`SpendResources`/`OnPlayWrapper`, flush hand, `EndOfTurnCleanup()`. No new patch
is needed for any of this — `CombatManager`'s turn machinery simply never touches the enemy side, so
there's nothing to patch around, only native methods to call directly in the right order.

---

## 10. Damage resolution has no honest deferral seam — decides the M1/M4 split

`CreatureCmd.Damage(PlayerChoiceContext, IEnumerable<Creature> targets, decimal amount, ValueProp
props, Creature? dealer, CardModel? cardSource)` (`MegaCrit.Sts2.Core.Commands/CreatureCmd.cs:240-412`)
is the one method every attack card's damage routes through (confirmed: `AttackCommand` builds
targets/amount and calls into this family of overloads). Read in full — it is a single ~170-line
`async` method that, per target, in order: runs `Hook.ModifyDamage` or the multiplier (`:261`),
`Hook.BeforeDamageReceived` (`:263`), computes block via `creature.DamageBlockInternal` (`:265`),
runs `Hook.ModifyHpLost` twice around Osty redirection (`:266-270`), applies HP loss via
`creature.LoseHpInternal`/`LoseHpInternal` (`:271,285`), fires on-hit VFX/SFX/screen-shake
(`:300-368`), then after the target loop runs `Hook.AfterBlockBroken`, `Hook.AfterCurrentHpChanged`,
`Hook.AfterDamageGiven`, `Hook.AfterDamageReceived` and `Kill(killedCreatures)` (`:376-411`).

**There is no seam between "compute the damage" and "apply it."** Deferring HP application to a
later turn while keeping the result truthful would require either synthesizing a `DamageResult` to
return to callers that never actually ran this method (the fabrication `ARCHITECTURE.md` §7 and
`POSTMORTEM.md` F13 forbid — some cards, e.g. Blight Strike's Doom per `PLAN.md` M4, read the result
to decide their own follow-up effect) or reimplementing this method's ~170 lines of hook/VFX/kill
logic mod-side (forbidden as content reimplementation). Neither is acceptable under this project's
rules.

**Decision (2026-09-02, see `PLAN.md`'s M1 gate-correction note): M1 uses immediate, native damage
resolution.** Strike, Defend and Bash resolve for real, synchronously with their `OnPlayWrapper` call,
exactly like an ordinary creature attack — zero custom queueing code, zero new patches. Real
queueing/intent-as-forecast is an M4 problem, to be solved together with the honest `DamageResult`
design `ARCHITECTURE.md` already scheduled there. This also means `Duel/DamageQueue.cs` and
`Duel/IntentPublisher.cs` from `ARCHITECTURE.md` §9's module layout **do not exist yet** — they start
at M4 and M3 respectively, not M1.

---

## 11. M8 visual/lifecycle fixes and M8.5 (2026-09-03)

**`NCombatRoom.AddCreature`'s pet-layout loop re-hides what P21 just showed** (`NCombatRoom.cs:749-763`).
The affected-pet list is built with `!(c.Entity.Monster is Osty) || !LocalContext.IsMe(player)` — for
the Ghost, `LocalContext.IsMe(player)` is always false, so every Ghost-owned pet is unconditionally
swept in and gets `nCreature2.ToggleIsInteractable(on: false)` (`:762`) called on it, which sets
`NCreature._stateDisplay.Visible = false` directly (`NCreature.cs:889`) — this runs later in the same
original method body than P21's `_Ready()` postfix, so it silently undoes P21's fix every time any
pet is (re-)added. P6 now calls `petNode.ToggleIsInteractable(on: true)` after the full native method
returns to undo it.

**The same loop's offset formula has no side-awareness** (`NCombatRoom.cs:757-761`):
`nCreature2.Position = new Vector2(creatureNode.Position.X - 20f + num2 * num + nCreature2.Visuals
.Bounds.Size.X * 0.5f, ...)` — baked for the ally-side convention (owner faces right, pet sits
slightly toward the enemy). P6 now reflects the resulting X offset around the owner's own position
for a Ghost-owned pet, rather than recomputing the formula's internals (`num`/`num2`/`list` are local
to that method, unavailable from a postfix).

**`NCreature.OstyScaleToSize`** (`NCreature.cs:1096-1110`) tweens `Visuals.Scale` toward `Vector2.One
* num * Visuals.DefaultScale` — always uniformly positive, since `DefaultScale` (`NCreatureVisuals.cs
:204`) is one `float`, not a per-axis value. Called from `OstyCmd.Summon` (`OstyCmd.cs:88`, duration
0.75) and from `NCreature.cs:1055` at Osty's own death (duration 0.75, target size 0). For a
Ghost-owned Osty (X already negative per P6's flip), this visibly un-flips it mid-tween. P24 lets the
native tween run, then plays a second, independent tween (separate `CreateTween()` calls on the same
node run independently in Godot) that snaps the sign back after the same duration.

**`OstyCmd.Summon`'s revival lookup is Side-literal** (`OstyCmd.cs:48`): `combatState.Allies
.FirstOrDefault(c => c.Monster is Osty && c.PetOwner == summoner)` can never find a Ghost-owned Osty
(`Side == Enemy`). `DieForYouPower.ShouldCreatureBeRemovedFromCombatAfterDeath` returns false for
Osty's own death (confirmed by earlier research), so the corpse is never removed from combat or from
`PlayerCombatState._pets` — it's genuinely still there, just unreachable through this one read. Note
`summoner.IsOstyAlive` (line 49) uses a *different*, already-correct lookup: `Player.Osty =>
PlayerCombatState?.GetPet<Osty>()` → `Pets.FirstOrDefault(p => p.Monster is T)` (`PlayerCombatState.cs
:255-258`), no Side filter at all. P23 is a transpiler on this one method's compiled state machine
(located via `AsyncStateMachineAttribute`, same technique as P12) that duplicates the `CombatState`
receiver around the `Allies` read and concatenates `Enemies` into the search space — safe because the
downstream `PetOwner == summoner` filter already disambiguates by owner regardless of which list the
candidate came from.

**`NSelectionReticle`'s fade-out breaks permanently after one `Reparent` call** (`NSelectionReticle.cs
:63-109`). `_cancelToken` is a private, `readonly CancellationTokenSource` created once in the field
initializer; `_ExitTree()` (`:75-79`) cancels it; `OnDeselect()` (`:96-109`) is a no-op once
`_cancelToken.IsCancellationRequested` is true, and the token is never replaced. `Reparent(...)` fires
`_ExitTree()` on every descendant of the reparented node, including this per-creature targeting
reticle (a child of `NCreature`, `%SelectionReticle`) — an ordinary creature is never reparented
mid-life, so this native interaction never surfaces outside this mod. P6 resets the token via
reflection (`NSelectionReticleCancelTokenAccess`) right after each `Reparent` call it makes (Ghost's
own node and its pets).

**Grayscale reuses the engine's own HSV shader material**, confirmed via `NCreatureVisuals
.SetScaleAndHue` (`NCreatureVisuals.cs:277-298`): `PreloadManager.Cache.GetMaterial("res://materials
/vfx/hsv.tres")`, duplicated once per `SpineSprite.NormalMaterial`, with `h`/`s`/`v` shader
parameters — the same resource `NCharacterSelectButton` uses for its locked-character dimming
(`_hsv.SetShaderParameter(_s, 0.2f)` etc., confirmed by reading that class too). P6 duplicates this
exact pattern (`GetNormalMaterial()`/`Duplicate()`/`SetNormalMaterial(...)`) for the Ghost's own
sprite and its pets, setting `s` (saturation) to 0 instead of shifting `h`.

**`CombatManager.EndCombatInternal` revives dead players on victory over the same `IsPlayer`-derived
`CombatState.Players` list** (`CombatManager.cs:984-987`): `foreach (Player player in combatState
.Players) { await player.ReviveBeforeCombatEnd(); }`. `Player.ReviveBeforeCombatEnd` (`Player.cs
:821-827`): `if (Creature.IsDead) { await CreatureCmd.Heal(Creature, 1m); }` — a co-op safety net
("if your ally died but the party still won, don't leave them dead") that fires for the Ghost too,
since it's `IsPlayer`. This is the fifth confirmed consumer of this exact list shape this project has
found (P9, P12, P14, P22 are the other four). P25 skips it entirely for the Ghost, mirroring P9's
shape; the method's second loop (`player2.AfterCombatEnd()`, `:995-998`, power/Block teardown) is left
untouched since it's harmless for the Ghost too.

**`PowerModel.Applier` is already set on every power application** (`PowerCmd.cs:119`:
`power.Applier = applier;`, inside `PowerCmd.Apply`) — no new tracking is needed to know who applied
a given debuff instance. P26 reads `power.Applier?.Side` in place of `VulnerablePower`/`WeakPower`/
`FrailPower.AfterSideTurnEnd`'s hardcoded `side == CombatSide.Enemy` check (all three confirmed
identical shape by direct read). `PoisonPower` shares the identical `Type`/`StackType` shape
(confirmed: `PoisonPower.cs:17-21`) but is not touched — P26 targets the three duration-debuff classes
specifically via `TargetMethods()`, not a shared dispatch point, precisely so Poison/Doom's own
existing timing is unaffected.

**`CharacterModel.CardPool`/`RelicPool` are per-instance properties** (`CharacterModel.cs:92,94`:
`public abstract CardPoolModel CardPool { get; }` / `public abstract RelicPoolModel RelicPool { get; }`),
unlike `M2Deck`'s Ironclad-specific `ModelDb.CardPool<IroncladCardPool>()` static generic lookup — no
per-character pool-type mapping is needed to sample any character's own pool generically. Used by
`RandomDeckBuilder` (M8.5).

**`CombatRoom.StartCombat` is `private async Task StartCombat(IRunState? runState)`**
(`CombatRoom.cs:197`) — confirmed genuinely async (not fire-and-forget), and runs before
`CombatManager.SetUpCombat` shuffles a player's deck into its draw pile. This is what makes it usable
as P5's injection point for `GhostSession.PendingRandomDeck` (M8.5): the earliest async native method
downstream of `RealFlowGhostDuelEntry`'s synchronous `ActModel.PullNextEncounter` prefix, reached via
the same `HarmonyReversePatch` technique P13 already established for "await our own setup, then call
the true original."

---

## M9 research (2026-09-04)

**`NetService` is not a class — every "bare `NetService.X`" call site in decompiled source is an
unqualified reference to that *calling* class's own instance property named `NetService`.** Confirmed
by a throwaway `System.Reflection.MetadataLoadContext` inspection of the real `sts2.dll` (the plain-text
decompile dump under `.tools/sts2-src/` doesn't contain a type literally named `NetService` anywhere —
grepping the whole tree for `class NetService` returns nothing, which is itself the tell). Reflection
found the *member* `NetService` (type `INetGameService`) independently declared on `RunManager`,
`JoinFlow`, `ReactionSynchronizer`, `PeerInputSynchronizer`, `LoadRunLobby` and `StartRunLobby` — each
class's own methods read it unqualified because it's an instance member in scope, not a static global.

**The publicly reachable one for mod code is `RunManager.Instance.NetService`** (public instance
property, confirmed via the same reflection pass: getter `IsPublic=true`, `IsStatic=false`, type
`MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService`). `RunManager.Instance` is already the mod's own
established singleton-access pattern (`RunManager.Instance.DebugOnlyGetState()` etc.), so no new
reflection/access seam is needed beyond this one property.

**`INetGameService`'s full surface** (also confirmed via the same reflection pass, not decompiled
source): `Type` (`NetGameType`), `NetId` (`ulong`), `SendMessage<T>(T)`, `SendMessage<T>(T, ulong
peerId)`, `RegisterMessageHandler<T>(MessageHandlerDelegate<T>)`,
`UnregisterMessageHandler<T>(MessageHandlerDelegate<T>)`, `Update()`, `IsConnected`, `IsGameLoading`,
`Platform`, `Disconnect(...)`, `GetStatsForPeer(...)`, `SetGameLoading(...)`, `SetBufferMessages(...)`,
`GetRawLobbyIdentifier()`, and a `Disconnected` event — all `T : INetMessage`.

**Mod-defined `INetMessage` types are a genuine, intentional extension point, not a closed native
registry.** `MessageTypes.Initialize()` (`MessageTypes.cs:11-17`) builds its id table from
`INetMessageSubtypes.All` (native, source-generated) *plus*
`ReflectionHelper.GetSubtypesInMods<INetMessage>()` — explicitly discovering mod assemblies. `NetTypeCache`
(`NetTypeCache.cs:20`) assigns ids by sorting the combined list by `Type.Name` (ordinal string compare),
which is deterministic across independent processes with the same mod set installed — no load-order
dependency, no extra Harmony patch needed to "register" a new message type. Caveat: `SerializeMessage`
casts the id to a single `byte` (`NetMessageBus.cs:37`), so the combined native+all-mods id space is
capped at 256 distinct `INetMessage` types.

Used by `src/Multiplayer/Messages/*.cs` (M9a: `MultiplayerGhostLadderReportMessage`,
`MultiplayerGhostLadderAggregateMessage`, `MultiplayerGhostSnapshotReportMessage`) and
`src/Multiplayer/MultiplayerGhostLadderCoordinator.cs`, which calls `RunManager.Instance.NetService`
directly (aliased as a private `Net` property for brevity) rather than any bare `NetService` reference.

**Addendum to the `NetService` finding above — which instance to use depends on the phase.**
`RunManager.Instance.NetService` is only meaningful once an actual run exists; during the
lobby/character-select phase (before `RunManager.SetUpNewMultiplayer` runs) the correct instance is
`StartRunLobby.NetService` — also public (confirmed via the same reflection pass), reachable as
`NCharacterSelectScreen.Lobby.NetService` (`NCharacterSelectScreen.Lobby` is likewise a public
property, `StartRunLobby`). `MultiplayerGhostLadderCoordinator` (`src/Multiplayer/`) therefore takes
its `INetGameService` as an explicit constructor parameter rather than reaching for a global, and
`MultiplayerLegacyAscensionEntry` (M9b) supplies `screen.Lobby.NetService` from postfixes on
`NCharacterSelectScreen.InitializeMultiplayerAsHost`/`InitializeMultiplayerAsClient` (both confirmed
public, `NCharacterSelectScreen.cs:438,452`) — the exact symmetric pair reached by the host and by a
joining client respectively.

**`NMultiplayerHostSubmenu` mirrors `NSingleplayerSubmenu` almost exactly**: same child node names
(`StandardButton`/`DailyButton`/`CustomRunButton`, `NMultiplayerHostSubmenu.cs:143,146,149`), and its
own `StartHostAsync(GameMode, Control loadingOverlay, NSubmenuStack stack)` is `public static`
(`NMultiplayerHostSubmenu.cs:189`) — callable directly to reuse 100% of the native host-starting flow
(Steam-vs-ENet transport choice, error popups) rather than reimplementing any of it. Only
`_loadingOverlay` needed a new reflected accessor (`MultiplayerUiAccess.cs`) to supply that method's
second parameter; `_stack` reuses the already-existing `NSubmenuStackAccess` (generic to the `NSubmenu`
base class, no change needed).

---

## P29 (2026-09-04): `PlayerChoiceSynchronizer` crashes for a Ghost — not an M9 regression

Confirmed live via the "M8 real run" debug button (`RealFlowGhostDuelEntry`, unrelated to any M9 code
path): `GnarledHammer`'s `AfterObtained()` (an enchant-pick prompt, triggered the instant M8.5's random
deck configuration grants it) crashed with an unhandled `System.ArgumentOutOfRangeException`, silently
swallowed by the same "unobserved async Task exception" mechanism this project has hit before —
`godot.log`, not `ghostduel.log`, had the real stack trace.

**Root cause**: `PlayerChoiceSynchronizer.ReserveChoiceId(Player)` (`PlayerChoiceSynchronizer.cs:69-80`)
calls `IPlayerCollection.GetPlayerSlotIndex(player)`, whose own doc comment states it returns `-1` "if
the player is not in Players" (`RunState.GetPlayerSlotIndex(Player)`, `RunState.cs:354-357`:
`Players.IndexOf(player)`), then indexes `List<uint>` with that value directly, with no negative-index
guard. The Ghost is deliberately never added to `RunState.Players` — this is the same root cause as
P8/P9/P12/P14/P22/P25 (a Ghost missing from some native per-player collection), a new call site, not
GnarledHammer-specific: the class's own doc comment names Survivor/Discovery/Toolbox's card-pick
prompts as the same mechanism, all equally exposed. `GetChoiceId` (`PlayerChoiceSynchronizer.cs:198-206`,
called from `ValidateChoiceId`, in turn called from `SyncLocalChoice` and `WaitForRemoteChoice`) has
the identical unguarded-negative-index bug, confirmed by direct read — fixed alongside the first, not
left for the next crash to find.

Fixed by P29 (`EnginePatches.cs`): `ReserveChoiceId` returns a fixed dummy id for a Ghost;
`ValidateChoiceId` always succeeds for a Ghost — both the same "detached value instead of the real
lookup" shape P8 already established. `SyncLocalChoice`'s own `_netService.SendMessage(...)` afterward
is left untouched (a no-op in singleplayer; a correct broadcast in real multiplayer).

**This was latent since M1/M2, not introduced by M9** — no test deck before this session's M8.5 random
roll happened to include GnarledHammer (or Survivor/Discovery/Toolbox). Worth checking for during any
future live test: any card/relic whose selection prompt goes through `CardSelectCmd`'s
enchantment/discard/add-to-hand family, not just Armaments-style upgrade picks (already covered by
`GhostCardSelector`'s `CardSelectCmd.Selector` escape hatch, which this bug's call chain never even
reaches — the crash happens one step earlier, at ID reservation).

## P30 (2026-09-04): same family, `RewardsSetSynchronizer` this time — not fixable the same way as P29

Same test round, a different random relic (`Cauldron`, offers a bonus potion reward via
`AfterObtained()`): identical `ArgumentOutOfRangeException` shape, this time inside
`RewardsSetSynchronizer.GetRewardStateForPlayer(Player)` (`RewardsSetSynchronizer.cs:143-146`).

**Confirmed NOT fixable by patching the shared root** (`RunState.GetPlayerSlotIndex`, which both P29
and P30 trace back to): grepped every caller across the decompiled tree.
1. `_rewardStates` (and likely `TreasureRoomRelicSynchronizer._votes`, `MapSelectionSynchronizer
   ._votes`, `EventSynchronizer._playerVotes` — not individually confirmed, same shape by inspection)
   is a **fixed-size list, one entry per real player, allocated once at construction** — there is no
   index a root-level fix could return for the Ghost that wouldn't itself be a second out-of-range
   read, just on the other end.
2. `GetPlayerSlotIndex` is also read for **RNG seeding** (`Player.cs:326`, `EventModel.cs:238`,
   `Rng.cs:48`) at `Player` construction time — changing its return value for the Ghost would silently
   reseed `PlayerRng`, an untraceable regression risk across every already-verified M1-M8 scenario, for
   a fix that wouldn't even generalize per point 1.

Fixed at the actual call site instead (P30, `EnginePatches.cs`): `RewardsSetSynchronizer
.BeginRewardsSet` is skipped entirely for a Ghost (`Task.CompletedTask`, no tracking) — the same
"skip entirely, nothing real is lost" shape as P9/P22. A Ghost has no UI to take a reward through, so
this is the correct behavior, not a narrowed one. Explicit trade-off: the Ghost's M8.5 random-deck
grant never realizes a relic's bonus reward (e.g. Cauldron's potion) — acceptable for a debug-only
feature. M9's own Ghost-construction paths never call `ConfigureDeckAsync` and so never reach this.

**Both P29 and P30 are specifically about `ConfigureDeckAsync` (M8.5's debug random-relic grant)
programmatically triggering `AfterObtained()` outside of a real, interactive game session — unrelated
to M9.** More relics could plausibly surface further synchronizer classes in this same family
(`RestSiteSynchronizer`, `MapSelectionSynchronizer`, `EventSynchronizer`, `TreasureRoomRelicSynchronizer`
all share the identical `GetPlayerSlotIndex`-into-a-fixed-list shape, confirmed by inspection, not yet
individually hit or patched) if the random roll happens to include a relic whose `AfterObtained` uses
one of them. Each would need the same per-call-site treatment as P29/P30, not a shared fix, per the
finding above.

## P31 (2026-09-04): `NoxiousFumesPower` poisoned the Ghost itself — `AfterSideTurnStart` bypasses P10/P11's guards

Confirmed live (user report): a Ghost with `NoxiousFumesPower` applied its own poison stacks to
itself instead of the human. `NoxiousFumesPower.AfterSideTurnStart` (`NoxiousFumesPower.cs:34-43`)
reads `base.CombatState.HittableEnemies`, which is `Enemies.Where(IsHittable)`
(`CombatState.cs:142`), and `Enemies` is a fixed, non-relative collection — literally "whichever side
is not `CombatSide.Player`" (`CombatState.cs:65,237-238`) — always the Ghost's own side from the
Ghost's own perspective.

P10 (`EnginePatches.cs`) already flips `HittableEnemies` for a Ghost-owned reader, but only while
`CombatManager.IsExecutingCardOrPotionEffect` is true, or `GhostHookOwnerScope.Current` is set. P11
feeds that scope only from `HookPlayerChoiceContext`'s constructor
(`HookPlayerChoiceContext.cs:70-89`), which only three hooks construct: `BeforeSideTurnStart`,
`AfterDeath`, `AfterDiedToDoom` (`Hook.cs:1144-1158`, and the `AfterDeath`/`AfterDiedToDoom` sites).
`AfterSideTurnStart` and the `AfterSideTurnStartLate` loop nested inside it (`Hook.cs:1163-1175`) call
`model.AfterSideTurnStart(...)`/`model.AfterSideTurnStartLate(...)` **directly**, with no context
object at all — so neither of P10's guards was ever true for Noxious Fumes. Exactly the same gap
shape P11 already fixed for `RedMask.BeforeSideTurnStart` (this file's earlier P11 note), just on a
hook P11's three-method list doesn't cover.

**Cards audited for the same read, on the user's own suggestion**: `Shockwave.cs:37`,
`PiercingWail.cs` — both read `HittableEnemies` from inside `CardModel.OnPlay`, which runs while
`CombatManager.IsExecutingCardOrPotionEffect(ghostPlayer)` is true (set by the native
`OnPlayWrapper`/`StartCardOrPotionEffect` pairing, keyed by `Player` — confirmed by reading
`CombatManager.cs:305-340`, not by observing a live Shockwave/Piercing Wail play). P10's *first* guard
already covers this case; these should already be correct as of P10, but this is inferred from the
guard's own precondition, not confirmed by a live play of either card — worth a direct check next
session rather than treating this as settled.

**Fix** (folded into P13's existing reverse-patch wrapper around `Hook.AfterSideTurnStart`, not a new
patch registration — see that class's doc comment in `EnginePatches.cs`): P13 already brackets the
*entire* real method body (both loops) via `await Original(...)`, since it needed that shape already
for the `VeryHotCocoa` energy-timing fix. Push/pop `GhostHookOwnerScope` around that same
`await Original(...)`, using a new keyless `PushRaw`/`PopRaw` pair on `GhostHookOwnerScope`
(`GhostHookOwnerScope.cs`) rather than the model-keyed `Push`/`Pop` P11 uses — there is no per-listener
object to key off here, and reusing the model-keyed table across two independently-invoked mechanisms
that could nest would risk a lost restore (confirmed by tracing the `ConditionalWeakTable.AddOrUpdate`
semantics: a second push for the same key overwrites the first push's saved "restore to" value, so
only one of two pops would actually restore anything — evaluated and rejected while designing this
fix, not observed live).

**Q3 (narrower target considered):** a per-listener fix mirroring P11 exactly would need a transpiler
on `Hook.AfterSideTurnStart`'s compiler-generated async state machine (no constructor call exists here
to piggyback on, unlike P11's three methods). Rejected as more invasive than justified: both side-
turn-start hooks read so far that touch `Enemies`/`Allies`/`HittableEnemies` (`NoxiousFumesPower.cs:28`,
`RedMask.cs:25`) self-filter with `participants.Contains(Owner)` before touching any of those
collections, so a non-Ghost listener already returns before a whole-side (rather than per-listener)
scope flip could affect it. This is confirmed by reading those two classes, not by an exhaustive audit
of every `AfterSideTurnStart`/`AfterSideTurnStartLate` listener in the game — a listener that doesn't
self-filter this way would be a gap in this reasoning, not yet ruled out.

## Combat-rules rework (2026-09-04): two facts that meant no patch was needed

Researched while planning the human-immediate-damage/afflicted-side-decay rework (see `PLAN.md`'s
dated note and `COMBAT-RULES.md`'s file-level note). Both confirmed by reading the decompiled source
directly, not assumed from general STS knowledge.

**Block reset is already side-generic, not player-specific — so the Ghost's Block already persists
into the human's next turn with no mod involvement.** `CombatManager.StartTurn`
(`CombatManager.cs:492-499`) iterates `_state.CreaturesOnCurrentSide` (`CombatState.CreaturesOnCurrentSide`
→ `GetCreaturesOnSide(CurrentSide)`, `CombatState.cs:133`) and calls `Creature.AfterTurnStart(side)`
for every creature on the side whose turn is starting — a pure side-membership query, with no
`IsPlayer`/`IsMonster` branch anywhere in the loop. `Creature.AfterTurnStart` (`Creature.cs:681-692`)
calls `ClearBlock()` (`Creature.cs:718-728`, sets `Block = 0` via `Hook.ShouldClearBlock`) unless
`side == CombatSide.Player` and it's that player's first turn (a first-turn-only skip, irrelevant to
the Ghost). Since the Ghost is a `Player` sitting on `CombatSide.Enemy`, this loop already clears its
Block at the start of *its own* next turn — same as any monster — meaning it already persists through
the human's entire intervening turn today, unmodified. This loop runs at `CombatManager.cs:492-499`,
strictly *before* `Hook.AfterSideTurnStart` fires at `:522` (P13's own target), so it's not something
P13's reverse-patch wrapper needs to additionally trigger — it has already happened by the time P13's
code runs.

**`TemporaryStrengthPower` (Mangle-style Strength reduction) already decays at its holder's own turn
end, unmodified — the exact "afflicted-side" shape P26 was revised to match for Weak/Vulnerable/
Frail.** `TemporaryStrengthPower.AfterSideTurnEnd` (`TemporaryStrengthPower.cs:173-181`):
`if (participants.Contains(base.Owner)) { Flash(); await PowerCmd.Remove(this); await
PowerCmd.Apply<StrengthPower>(...); }` — ticks (via full removal + inverse-amount reapplication, not
`PowerCmd.TickDownDuration`) whenever the *holder* is a participant in the side whose turn is ending,
for *either* side, with no `Applier` tracking and no fixed `side == CombatSide.Enemy` gate at all. This
differs from `WeakPower`/`VulnerablePower`/`FrailPower.AfterSideTurnEnd` (`WeakPower.cs:48-54` and
siblings), which check a fixed `side == CombatSide.Enemy` regardless of holder or applier — the
inconsistency between these two native shapes is presumably why the mod ever needed P18/P26 for
Weak/Vulnerable/Frail in the first place, while Strength-reduction never needed an equivalent patch.

## P32 (2026-09-04): Ghost goes first — turn-order flip, citation chain

Per the user's request (see `PLAN.md`'s dated note and `COMBAT-RULES.md` §2's superseded-loop note).

**Root fact**: `CombatState`'s constructor hardcodes the opening side unconditionally —
`CombatState.cs:152-162`: `RoundNumber = 1; CurrentSide = CombatSide.Player;`. No ambush/acts-first
mechanic or per-encounter variation point exists anywhere in `CombatManager.cs`/`CombatState.cs`
(confirmed by search for `Ambush`/`ActsFirst`/`GoesFirst`/similar — no hits in the decompiled tree).
`CombatManager.SetUpCombat` (`CombatManager.cs:350-378`) never touches `CurrentSide`; `StartCombatInternal`
(`CombatManager.cs:387-420`) calls `StartTurn()` with no side argument, and `StartTurn`
(`CombatManager.cs:422-608`) simply reads whatever `_state.CurrentSide` already is.

**Safety check**: the one native setup-time reader of `CurrentSide` is `CombatManager.cs:865`, inside
`CombatManager.AfterCreatureAdded`, gated `IsEnemy && CurrentSide == CombatSide.Player` — this is the
exact check P4 (`EnginePatches.cs`, `P4_SkipMonsterMoveRoll`) already bypasses entirely for the Ghost
(a prefix returning `false`, skipping the native method body whenever the creature being added is a
Ghost). A Ghost Duel encounter has no real monsters, so the Ghost is the only creature where `IsEnemy`
is ever true during setup — meaning this check's truth value never actually matters for any creature
this patch could affect. Flipping `CurrentSide`'s initial value is therefore safe with respect to
every currently-known setup-time reader of it. (Not exhaustively verified: Neow/cutscene code was not
searched, but `CombatState` doesn't exist until a combat instance begins, well after any run-level Neow
flow — considered a very low residual risk, not fully ruled out.)

**Gate-ordering confirmation**: the most recent `ghostduel.log` shows `Session begin: party=[...]`
logged *before* `Ghost added to CombatState` (P5) on every run — so `GhostSession.Current` already
exists by the time any Ghost-Duel `CombatState` is constructed, making P32's `GhostSession.Current is
null` guard correctly scope the flip to Ghost-Duel combats only.

**Fix**: `P32_GhostGoesFirst` (`EnginePatches.cs`) — a Harmony postfix on `CombatState`'s constructor,
setting `__instance.CurrentSide = CombatSide.Enemy` when `GhostSession.Current is not null`. Everything
downstream is already side-order-agnostic: `GhostTurnController` reacts to whichever side is currently
active, not to a round number, and P13's `Hook.AfterSideTurnStart` wrapper gates on `side ==
CombatSide.Enemy` plus participants — neither cares whether this is round 1 or round 2.

## P33 (2026-09-04): Ghost-first broke every `PlayerCombatState.TurnNumber`-gated relic for the human

Confirmed live: Defect's Cracked Core (`CrackedCore.cs:29-38`, channels a Lightning orb via
`BeforeSideTurnStart` gated on `Owner.PlayerCombatState.TurnNumber <= 1`) never channeled its orb for
the human after P32.

**Root cause**: `PlayerCombatState.TurnNumber` (`PlayerCombatState.cs:37`, default `1`) is only ever
incremented by `IncrementTurnNumber()` (`PlayerCombatState.cs:157-160`), called exclusively from
`CombatManager.SwitchSides` (private, `CombatManager.cs:1387-1425`) — specifically only on the branch
where the side is transitioning *into* `CombatSide.Player`
(`CombatManager.cs:1404-1418`: `_state.CurrentSide = CombatSide.Player; ... foreach (Player item in
readOnlyList) { item.PlayerCombatState.IncrementTurnNumber(); }`, where `readOnlyList` is normally
`_state.Players`). The transitioning-out-of-Player branch (`CombatManager.cs:1398-1401`) does *not*
increment anything. Under native (Player-always-first) ordering, "transitioning into Player" only ever
happens *after* the player's own turn has already completed once — so the first such transition
correctly corresponds to the player's *second* turn beginning. With P32, the very first "transitioning
into Player" call now happens at the end of the *Ghost's* opening turn — before the human has taken
any turn at all — so this same increment fires one turn too early, permanently offsetting every real
human's `TurnNumber` by one for the rest of the fight.

Confirmed `CombatState.Players` includes the Ghost, not just real humans (this file's own earlier "M2
research" section, `IsPlayer`-derived) — so `SwitchSides`'s loop also calls `IncrementTurnNumber` on
the Ghost's own `PlayerCombatState` during this same transition, and that increment is *correct* (the
Ghost's first turn really did just complete) — the fix must suppress the bump only for real humans,
not skip the whole call.

**Fix, first attempt (confirmed live NOT to work)**: a Harmony prefix on
`PlayerCombatState.IncrementTurnNumber`, skipping the call for real humans while a scope opened by a
`SwitchSides` prefix was active (`GhostOpeningTurnScope`, since removed). Diagnostic logging added to
both patches showed the `SwitchSides` scope opening and closing exactly as expected, but the
`IncrementTurnNumber` prefix's own log line never printed even once — despite `TurnNumber` visibly
changing (1 → 2) across that exact call. The only explanation consistent with that evidence:
`IncrementTurnNumber`'s one-line body (`TurnNumber++`) gets inlined by the JIT directly into
`SwitchSides`'s own compiled code, so the method call Harmony patched was never actually reached at
runtime — a known failure mode for trivial one-line methods, which a prefix on the callee cannot
defend against no matter how correct its own logic is.

**Fix, revised — undo the mutation at its one caller instead of intercepting the callee**: a single
patch, `P33_SkipHumanTurnNumberBumpOnGhostOpeningTurn_SwitchSides` (`EnginePatches.cs`), on
`CombatManager.SwitchSides` itself. Its prefix, when this is the Ghost's opening-turn-end transition
(`CurrentSide == CombatSide.Enemy && RoundNumber == 1`, both still holding their pre-transition values
at this point), snapshots every real human's current `TurnNumber` into a `Dictionary<PlayerCombatState,
int>` carried as Harmony `__state` (`combatState.Players`, excluding the Ghost via
`GhostSession.IsGhostPlayer`). Its postfix restores each snapshotted value afterward, via
`PlayerCombatStateTurnNumberAccess.Set` (`src/Engine/PlayerCombatStateTurnNumberAccess.cs`) — a
reflected setter, since `TurnNumber` is `{ get; private set; }`. This is immune to the inlining problem
because it doesn't matter *how* `TurnNumber` changed during the call, only that its value needs
correcting afterward — no dependence on any specific method actually being invoked. The Ghost's own
`TurnNumber` is never touched by this patch at all, so it advances normally regardless.

Same-shape relics found by grep, not individually confirmed broken: `Bread`/`BlessedAntler`/
`FuneraryMask`/`IceCream`/`FestivePopper`/`HistoryCourse`/`LetterOpener`/`Pocketwatch`/`RadiantPearl`/
`ToastyMittens`/`VexingPuzzlebox`/`Toolbox` (all check `TurnNumber == 1`), `Candelabra`/`HornCleat`
(`==2`), `Chandelier`/`CaptainsWheel`/`SparklingRouge` (`==3`) — all should now be fixed by the same
general mechanism, but only Cracked Core was actually observed broken and re-verified.

Not verified: interaction with `Hook.ShouldTakeExtraTurn`/`_playersTakingExtraTurn`
(`CombatManager.cs:1360-1373`) if an extra-turn effect ever applies to the Ghost during its own opening
turn — no known deck exercises this.
