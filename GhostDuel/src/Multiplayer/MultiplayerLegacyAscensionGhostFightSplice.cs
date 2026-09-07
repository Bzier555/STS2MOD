using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GhostDuel.Debug;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using GhostDuel.Progression;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Multiplayer;

/// <summary>
/// PLAN.md M9c: the multiplayer sibling of <c>GhostDuel.Progression.LegacyAscensionGhostFightSplice</c>
/// — same splice point and choreography (M7's own confirmed-live research into
/// <c>RunManager.EnterNextAct</c>, room-transition safety, and the <c>HarmonyReversePatch</c>
/// call-through technique all applies unchanged), but builds a whole <see cref="GhostPartyMember"/>
/// party — one Ghost per human, from that human's own reported multiplayer-ladder snapshot — instead
/// of one Ghost from one hardcoded <c>runState.Players[0]</c>.
///
/// <b>Unverified assumption, stated rather than silently assumed</b>: this prefix is expected to fire
/// independently on every connected client's own process, mirroring how P5 already populates
/// <c>CombatState</c> identically on every client in the already-tested singleplayer case. This is
/// only correct if every client's own <see cref="MultiplayerGhostLadderCoordinator"/> collected an
/// identical snapshot set (guaranteed by <c>MultiplayerGhostSnapshotReportMessage.ShouldBroadcast</c>
/// relaying every report to every peer) and the run's own modifiers/ascension level are already
/// synced by the native multiplayer layer (they are part of <c>RunState</c>, which real multiplayer
/// already keeps in sync for ordinary act progression). Not yet exercised with two real clients.
/// </summary>
internal static class MultiplayerLegacyAscensionGhostFightSplice
{
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterNextAct))]
    private static class Prefix_InjectGhostPartyFight
    {
        [HarmonyPrefix]
        private static bool Prefix(RunManager __instance, ref Task __result)
        {
            RunState? runState = __instance.DebugOnlyGetState();
            if (runState is null)
            {
                return true;
            }
            MultiplayerLegacyAscensionModifier? modifier = runState.Modifiers.OfType<MultiplayerLegacyAscensionModifier>().FirstOrDefault();
            if (modifier is null
                || modifier.FoughtActGhostThisAct
                || runState.CurrentActIndex < runState.Acts.Count - 1
                || runState.CurrentRoom is not { IsVictoryRoom: false })
            {
                return true;
            }

            GhostLog.Info($"MultiplayerLegacyAscensionGhostFightSplice: final-act boss defeated at multiplayer ladder A{modifier.LadderLevel} — injecting the Ghost-party fight before the Architect scene.");
            __result = InjectGhostPartyFightThenContinue(__instance, runState, modifier);
            return false;
        }

        private static async Task InjectGhostPartyFightThenContinue(RunManager instance, RunState runState, MultiplayerLegacyAscensionModifier modifier)
        {
            // Same permanent try/catch discipline as the singleplayer splice — a failure here must
            // never fail silently (see that file's own doc comment for the live incident that
            // established this).
            try
            {
                await InjectGhostPartyFightThenContinueUnsafe(instance, runState, modifier);
            }
            catch (Exception ex)
            {
                GhostLog.Error("MultiplayerLegacyAscensionGhostFightSplice: unhandled exception while injecting the Ghost-party fight — the run may be stuck. See below for the real cause.", ex);
            }
        }

        private static async Task InjectGhostPartyFightThenContinueUnsafe(RunManager instance, RunState runState, MultiplayerLegacyAscensionModifier modifier)
        {
            IReadOnlyList<Player> humans = runState.Players;
            foreach (Player player in humans)
            {
                decimal missing = player.Creature.MaxHp - player.Creature.CurrentHp;
                if (missing > 0m)
                {
                    await CreatureCmd.Heal(player.Creature, missing);
                }
            }

            List<GhostPartyMember>? party = BuildParty(modifier.LadderLevel, humans);
            if (party is null)
            {
                // BuildParty already logged exactly what was missing. Never substitute or guess
                // (docs/PROGRESSION.md §3) — skip this level's fight and continue normally.
                await Original(instance);
                return;
            }

            if (GhostSession.Current is not null)
            {
                GhostLog.Warn("MultiplayerLegacyAscensionGhostFightSplice: a previous GhostSession was still active — disposing it and starting fresh.");
                GhostSession.Current.Dispose();
            }

            foreach (GhostPartyMember member in party)
            {
                GhostPlayerFactory.JoinRun(member.Player, runState);
            }
            GhostSession session = GhostSession.Begin(party);
            GhostLog.Party($"A{modifier.LadderLevel}", $"[{string.Join(", ", party.Select(m => $"{m.Player.Character.Id}#{m.Player.NetId}"))}]");

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
                // Confirmed live 2026-09-04 (two-client test): neither side had Energy or could play
                // a card the first time this fight was reached. Root cause: the normal room-entry
                // flow this splice bypasses (RunManager.cs:824-830) calls
                // CombatReplayWriter.RecordInitialState(ToSave(null)) before ever entering a room —
                // skipping it here left ChecksumTracker (which every multiplayer combat's first
                // CombatManager.StartTurn call exercises, via GenerateChecksum ->
                // ObtainAndTrackChecksum) throwing "RecordInitialState must be called first" the
                // instant the fight began. Thrown from inside a fire-and-forget Task
                // (TaskHelper.LogTaskExceptions caught and logged it rather than crashing outright),
                // it silently aborted CombatManager.StartCombatInternal partway through — before
                // either side's turn setup (energy reset, hand draw wiring) had actually run.
                // Confirmed only ever exercised in multiplayer (ChecksumTracker.IsEnabled is
                // presumably false for a real singleplayer NetGameType), which is why the equivalent
                // singleplayer splice never needed live debugging to find this — fixed there anyway,
                // identically, since nothing about the gap is multiplayer-specific in principle.
                if (instance.CombatReplayWriter.IsEnabled)
                {
                    instance.CombatReplayWriter.RecordInitialState(instance.ToSave(null));
                }

                await instance.EnterRoom(new CombatRoom(ModelDb.Encounter<GhostDuelEncounter>().ToMutable(), runState) { ShouldResumeParentEventAfterCombat = false });
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

            bool partyWon = party.All(member => member.Player.Creature.IsDead);
            GhostLog.Info($"MultiplayerLegacyAscensionGhostFightSplice: Ghost-party fight ended, partyWon={partyWon}.");
            if (!partyWon)
            {
                // Some or all humans died; the native death/run-loss pipeline already owns ending
                // the run. Nothing further — in particular, never save a snapshot or continue toward
                // the "you won" Architect scene.
                return;
            }

            // Each client saves only its own local human's build to its own local multiplayer
            // ladder — never another human's, since that file lives on that human's own machine and
            // this process has no business writing to it. LocalContext.IsMe(Player) is the same
            // "which Player is this process's own local human" check the engine itself uses
            // pervasively (ENGINE-NOTES.md's "M9 research" section).
            Player? me = humans.FirstOrDefault(LocalContext.IsMe);
            if (me is not null)
            {
                MultiplayerGhostSnapshotStore.Save(modifier.LadderLevel, me.ToSerializable());
            }
            else
            {
                GhostLog.Warn("MultiplayerLegacyAscensionGhostFightSplice: could not identify the local human among this run's players; no snapshot saved for this client.");
            }

            modifier.FoughtActGhostThisAct = true;
            await Original(instance);
        }

        /// <summary>Builds one Ghost per human, in the human party's own stable order (matching
        /// COMBAT-RULES.md/PROGRESSION.md's "stable saved party order" requirement for free, since
        /// this is simply <paramref name="humans"/>' own order). Returns null (after logging exactly
        /// what's missing) if any single human's Ghost cannot be built — never a partial party.</summary>
        private static List<GhostPartyMember>? BuildParty(int ladderLevel, IReadOnlyList<Player> humans)
        {
            List<GhostPartyMember> party = new(humans.Count);
            for (int i = 0; i < humans.Count; i++)
            {
                Player human = humans[i];
                Player? ghost = BuildGhost(ladderLevel, human, i);
                if (ghost is null)
                {
                    return null;
                }
                party.Add(new GhostPartyMember(ghost, new Ai.WeightedRandomGhostChooser()));
            }
            return party;
        }

        private static Player? BuildGhost(int ladderLevel, Player human, int partyIndex)
        {
            if (ladderLevel == 0)
            {
                return GhostPlayerFactory.CreateDebugGhost(human.Character, partyIndex);
            }

            MultiplayerGhostLadderCoordinator? coordinator = MultiplayerLegacyAscensionEntry.Coordinator;
            if (coordinator is null || !coordinator.ReportedSnapshots.TryGetValue(human.NetId, out Messages.MultiplayerGhostSnapshotReportMessage report))
            {
                GhostLog.Error($"MultiplayerLegacyAscensionGhostFightSplice: no multiplayer-ladder snapshot report received for {human.Character.Id}#{human.NetId} at A{ladderLevel - 1}.");
                return null;
            }
            if (!report.hasPreviousGhost || report.player is not { } snapshot)
            {
                GhostLog.Error($"MultiplayerLegacyAscensionGhostFightSplice: {human.Character.Id}#{human.NetId} reported no previous Ghost for A{ladderLevel - 1}.");
                return null;
            }
            return GhostPlayerFactory.CreateGhostFromSnapshot(snapshot, partyIndex);
        }

        [HarmonyReversePatch]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task Original(RunManager instance)
        {
            throw new NotImplementedException("MultiplayerLegacyAscensionGhostFightSplice: replaced by HarmonyReversePatch at patch time - should never actually run.");
        }
    }
}
