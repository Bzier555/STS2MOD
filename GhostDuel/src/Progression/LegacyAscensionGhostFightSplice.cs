using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GhostDuel.Debug;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using GhostDuel.Ai;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Progression;

/// <summary>
/// M7c (PLAN.md): the Act-3-boss-victory to Ghost-fight splice — the highest-risk, least-precedented
/// piece of M7, built only after a dedicated research pass (2026-09-04) confirmed every step against
/// source rather than assuming.
///
/// Confirmed: <c>RunManager.EnterNextAct()</c> (<c>RunManager.cs:1209-1238</c>) is reached exactly
/// once after the *true* final boss (a double-boss Act 3 at Ascension 10 never reaches it after the
/// first of the two — <c>NRewardsScreen.cs:546-565</c> diverts back to the map instead of voting to
/// advance, confirmed by a second research pass, so no extra ascension-aware guard is needed here).
/// On that one call, with <c>CurrentActIndex &gt;= Acts.Count - 1</c> and
/// <c>!CurrentRoom.IsVictoryRoom</c>, the native body enters <c>TheArchitect</c>'s narrative
/// (confirmed pure VFX, no real combat) — this patch replaces that one call's body entirely (skip +
/// <see cref="HarmonyReversePatch"/>, the established P5/P13 shape) with: heal the party, build/
/// restore the Ghost, redirect into a real <c>CombatRoom</c> using the same confirmed-safe sequential-
/// transition path any ordinary room-to-room move already uses (not the nested-room-stacking API,
/// which is documented for the different event-nested-combat shape), wait for that fight to end, and
/// — only on a human win — call through to the true original <c>EnterNextAct()</c> so the run
/// proceeds into <c>TheArchitect</c>/victory exactly as it always would have. A human loss needs no
/// special handling: the native death/run-loss pipeline already owns ending the run
/// (<c>COMBAT-RULES.md</c> §9), and this method simply never calls through to continue the "you won"
/// path.
/// </summary>
internal static class LegacyAscensionGhostFightSplice
{
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterNextAct))]
    private static class Prefix_InjectGhostFight
    {
        [HarmonyPrefix]
        private static bool Prefix(RunManager __instance, ref Task __result)
        {
            RunState? runState = __instance.DebugOnlyGetState();
            if (runState is null)
            {
                return true;
            }
            LegacyAscensionModifier? modifier = runState.Modifiers.OfType<LegacyAscensionModifier>().FirstOrDefault();
            if (modifier is null
                || modifier.FoughtActGhostThisAct
                || runState.CurrentActIndex < runState.Acts.Count - 1
                || runState.CurrentRoom is not { IsVictoryRoom: false })
            {
                return true;
            }

            GhostLog.Info($"LegacyAscensionGhostFightSplice: final-act boss defeated at ladder level A{modifier.LadderLevel} — injecting the Ghost fight before the Architect scene.");
            __result = InjectGhostFightThenContinue(__instance, runState, modifier);
            return false;
        }

        private static async Task InjectGhostFightThenContinue(RunManager instance, RunState runState, LegacyAscensionModifier modifier)
        {
            // 2026-09-04: confirmed live — the very first attempt at this got stuck silently after
            // the heal (no exception anywhere in ghostduel.log or godot.log, no P5 "Ghost added to
            // CombatState" line either, meaning StartCombat was never even reached). Wrapping the
            // whole thing in a try/catch here is a deliberate, permanent addition, not a band-aid:
            // this method's Task becomes RunManager.EnterNextAct()'s own return value (via the
            // prefix's `ref Task __result`), and whatever ultimately awaits that — a native
            // async-void Godot signal handler, most likely — may swallow an unobserved exception
            // with no log at all. Never let a bug in this splice fail *silently* again.
            try
            {
                await InjectGhostFightThenContinueUnsafe(instance, runState, modifier);
            }
            catch (Exception ex)
            {
                GhostLog.Error("LegacyAscensionGhostFightSplice: unhandled exception while injecting the Ghost fight — the run may be stuck. See below for the real cause.", ex);
            }
        }

        private static async Task InjectGhostFightThenContinueUnsafe(RunManager instance, RunState runState, LegacyAscensionModifier modifier)
        {
            // 2026-09-08 (live incident: a joining client's screen got stuck on "Waiting for other
            // players..." over the old boss-fight background). Root cause, confirmed by reading
            // ActChangeSynchronizer.MoveToNextAct (native, untouched by this mod): it calls
            // `TaskHelper.RunSafely(RunManager.Instance.EnterNextAct())` — NOT awaited — then
            // immediately checks `NOverlayStack.Instance?.Peek() is NRewardsScreen` to decide whether to
            // call `HideWaitingForPlayersScreen()`. An `async Task` method runs synchronously up to its
            // first `await` before control returns to its caller, so whatever this method (reached via
            // the Harmony prefix that replaces the real EnterNextAct) does before ITS first real await
            // races that same native post-call check. The human-heal loop below is that first await —
            // but only if `missing > 0m`; a player already at full HP skips it entirely and races
            // straight through to `NOverlayStack.Instance?.Clear()` further down, synchronously,
            // *before* the native check ever runs — so it finds something other than NRewardsScreen on
            // top and silently never dismisses the waiting overlay, stranding that peer. A forced yield
            // here, unconditionally, guarantees the native caller's own post-call check always wins this
            // race regardless of whether anyone happens to need healing.
            await Task.Yield();

            Player human = runState.Players[0];
            foreach (Player player in runState.Players)
            {
                decimal missing = player.Creature.MaxHp - player.Creature.CurrentHp;
                if (missing > 0m)
                {
                    await CreatureCmd.Heal(player.Creature, missing);
                }
            }

            Player? ghostPlayer = BuildGhost(modifier.LadderLevel, human.Character);
            if (ghostPlayer is null)
            {
                // Snapshot missing/corrupt/references content that no longer exists — per
                // docs/PROGRESSION.md §3, never substitute or guess. The safest available fallback
                // that doesn't strand the player mid-run is to skip this level's Ghost fight (logged
                // loudly) and let the run finish normally, rather than hang or crash here.
                GhostLog.Error($"LegacyAscensionGhostFightSplice: could not build the A{modifier.LadderLevel - 1} Ghost; skipping this level's Ghost fight and continuing to the Architect scene.");
                await Original(instance);
                return;
            }

            // Confirmed live 2026-09-04: on a retry (save+continue) after a previous attempt got
            // stuck before ever disposing its session, GhostSession.Begin throws immediately
            // ("a GhostSession is already active") — silently, per the try/catch above, which is
            // exactly how the second attempt's log went straight from this line to nothing. Same
            // defensive disposal LegacyAscensionEntry.Start/RealFlowGhostDuelEntry.Start already use.
            if (GhostSession.Current is not null)
            {
                GhostLog.Warn("LegacyAscensionGhostFightSplice: a previous GhostSession was still active — disposing it and starting fresh.");
                GhostSession.Current.Dispose();
            }

            GhostPlayerFactory.JoinRun(ghostPlayer, runState);
            // 2026-09-08 (user request, mirroring the multiplayer splice's identical fix): heal the
            // restored Ghost to full before the fight, the same courtesy already given to the real
            // human above — its snapshot can legitimately have CurrentHp < MaxHp (the human who earned
            // it may have won their own Act-3 boss fight while still damaged). Reads Creature.MaxHp —
            // the Ghost's own live, already-restored value (Player.FromSerializable sets it directly
            // from the snapshot's MaxHp, confirmed by reading Player.cs), never CharacterModel
            // .StartingHp, which would silently downgrade an earned max-HP increase back to the base
            // character's stock value.
            decimal ghostMissing = ghostPlayer.Creature.MaxHp - ghostPlayer.Creature.CurrentHp;
            if (ghostMissing > 0m)
            {
                await CreatureCmd.Heal(ghostPlayer.Creature, ghostMissing);
            }
            GhostSession session = GhostSession.Begin(ghostPlayer, new WeightedRandomGhostChooser());

            // Mirrors EnterNextAct's own choreography for its "enter a new room" branch
            // (RunManager.cs:1225-1231: RoomFadeOut -> ClearScreens -> EnterRoom -> FadeIn) rather
            // than calling EnterRoom bare — this prefix replaces that entire branch, so nothing else
            // provides these steps. ClearScreens itself is private, but its full body is three plain
            // calls to already-public singletons (RunManager.cs:968-976), reproduced directly here
            // rather than reflected, since there's nothing private left to reach through.
            if (!MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn)
            {
                await NGame.Instance!.Transition.RoomFadeOut();
            }
            NOverlayStack.Instance?.Clear();
            NCapstoneContainer.Instance?.Close();
            NMapScreen.Instance?.Close(animateOut: false);

            TaskCompletionSource<object?> combatEndedSource = new();
            void OnCombatEnded(CombatRoom room) => combatEndedSource.TrySetResult(null);
            CombatManager.Instance.CombatEnded += OnCombatEnded;
            try
            {
                // Confirmed live 2026-09-04 (multiplayer test — see the multiplayer splice's own
                // matching note): the normal room-entry flow this splice bypasses
                // (RunManager.cs:824-830) calls CombatReplayWriter.RecordInitialState(ToSave(null))
                // before ever entering a room — skipping it here left ChecksumTracker (which every
                // multiplayer combat's first CombatManager.StartTurn call exercises, via
                // GenerateChecksum -> ObtainAndTrackChecksum) throwing "RecordInitialState must be
                // called first" the instant the fight began, silently (caught by
                // TaskHelper.LogTaskExceptions) aborting CombatManager.StartCombatInternal partway
                // through — which is why neither side had Energy or could play a card. Mirrored here
                // exactly, same gate.
                if (instance.CombatReplayWriter.IsEnabled)
                {
                    instance.CombatReplayWriter.RecordInitialState(instance.ToSave(null));
                }

                // Confirmed live 2026-09-04: constructing CombatRoom directly with the canonical
                // (shared, immutable) encounter instance throws "Canonical model ... used in
                // incorrect place" — silently, per the try/catch above. RunManager.CreateRoom
                // (RunManager.cs:878) always calls .ToMutable() on whatever encounter it passes to
                // CombatRoom's constructor; RealFlowGhostDuelEntry's own substitution never needed
                // to do this itself only because CreateRoom applies .ToMutable() to *its* return
                // value regardless of source. Calling the constructor directly, as here, means doing
                // it ourselves.
                CombatRoom ghostFightRoom = new(ModelDb.Encounter<GhostDuelEncounter>().ToMutable(), runState) { ShouldResumeParentEventAfterCombat = false };
                await instance.EnterRoom(ghostFightRoom);
                // See GhostPlayerFactory.FireAfterRoomEnteredForGhosts' own doc comment: the native
                // call this same EnterRoom already made (CombatRoom.cs:228) only ever reaches the real
                // human's own relics (e.g. Vajra), never the Ghost's — this is the mod-side equivalent.
                await GhostPlayerFactory.FireAfterRoomEnteredForGhosts(session.Party, ghostFightRoom);
                if (!MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn)
                {
                    await NGame.Instance!.Transition.RoomFadeIn();
                }
                await combatEndedSource.Task;
            }
            finally
            {
                CombatManager.Instance.CombatEnded -= OnCombatEnded;
            }

            bool humanWon = ghostPlayer.Creature.IsDead;
            GhostLog.Info($"LegacyAscensionGhostFightSplice: Ghost fight ended, humanWon={humanWon}.");
            if (!humanWon)
            {
                // The human died; the native death/run-loss pipeline already owns ending the run
                // (COMBAT-RULES.md §9) as a side effect of that death having just happened. Nothing
                // further to do — in particular, never call through to continue toward the "you won"
                // Architect scene.
                return;
            }

            // No separate "advance the ladder" step: GhostSnapshotStore.GetMaxSelectableLevel()
            // derives the next-selectable level purely from which snapshot files exist, so writing
            // this one file (or refusing to, on a replay of an already-won level) is the whole of it.
            GhostSnapshotStore.Save(modifier.LadderLevel, human.ToSerializable());
            modifier.FoughtActGhostThisAct = true;
            await Original(instance);
        }

        private static Player? BuildGhost(int ladderLevel, CharacterModel humanCharacter)
        {
            if (ladderLevel == 0)
            {
                return GhostPlayerFactory.CreateDebugGhost(humanCharacter);
            }
            GhostSnapshotFileStore.LoadResult load = GhostSnapshotStore.Load(ladderLevel - 1);
            if (!load.Success || load.Player is null)
            {
                GhostLog.Error($"LegacyAscensionGhostFightSplice: {load.Error}");
                return null;
            }
            return GhostPlayerFactory.CreateGhostFromSnapshot(load.Player);
        }

        [HarmonyReversePatch]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task Original(RunManager instance)
        {
            throw new NotImplementedException("LegacyAscensionGhostFightSplice: replaced by HarmonyReversePatch at patch time - should never actually run.");
        }
    }
}
