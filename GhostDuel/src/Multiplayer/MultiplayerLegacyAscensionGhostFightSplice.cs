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
            // 2026-09-08 (live incident: a joining friend's screen got stuck on "Waiting for other
            // players..." over the old boss-fight background, while the host proceeded normally into
            // the Ghost fight). Root cause, confirmed by reading ActChangeSynchronizer.MoveToNextAct
            // (native, untouched by this mod — the multiplayer "everyone voted to advance" mechanism):
            // it calls `TaskHelper.RunSafely(RunManager.Instance.EnterNextAct())` — NOT awaited — then
            // immediately checks `NOverlayStack.Instance?.Peek() is NRewardsScreen` to decide whether to
            // call `HideWaitingForPlayersScreen()`. An `async Task` method runs synchronously up to its
            // first `await` before control returns to its caller, so whatever this method (reached via
            // the Harmony prefix that replaces the real EnterNextAct) does before ITS first real await
            // races that same native post-call check — on *each peer independently*, since
            // OnPlayerReady/MoveToNextAct run identically on every client once the replicated vote
            // action confirms everyone is ready. The human-heal loop below is that first await — but
            // only if `missing > 0m`; a player already at full HP skips it entirely and races straight
            // through to `NOverlayStack.Instance?.Clear()` further down, synchronously, *before* the
            // native check ever runs on that peer — so it finds something other than NRewardsScreen on
            // top and silently never dismisses the waiting overlay, stranding that one peer while
            // whichever peer *did* need healing (and therefore already yielded control back naturally)
            // proceeds normally. A forced yield here, unconditionally, guarantees the native caller's
            // own post-call check always wins this race on every peer, regardless of anyone's HP.
            await Task.Yield();

            IReadOnlyList<Player> humans = runState.Players;
            foreach (Player player in humans)
            {
                decimal missing = player.Creature.MaxHp - player.Creature.CurrentHp;
                if (missing > 0m)
                {
                    await CreatureCmd.Heal(player.Creature, missing);
                }
            }

            // 2026-09-08 (live incident: both players landed on the Architect scene with no fight at
            // all). Root cause, confirmed from a log showing none of MultiplayerLegacyAscensionEntry's
            // own arm/report lines that session: the run had been resumed via the "load"/reconnect join
            // path (restarting the lobby after the earlier stranded-overlay bug), which never touches
            // NCharacterSelectScreen — so the ladder coordinator was never attached and held no snapshot
            // reports at all. See MultiplayerLegacyAscensionEntry.EnsureSnapshotReportsAsync's own doc
            // comment for the full chain. This call is a no-op on the normal fresh-lobby path (the
            // coordinator already exists there, reported minutes ago at embark time per
            // Prefix_TagMultiplayerLegacyAscensionRun) and only does real work as a reconnect fallback.
            MultiplayerGhostLadderCoordinator coordinator = await MultiplayerLegacyAscensionEntry.EnsureSnapshotReportsAsync(
                instance.NetService, humans, modifier.LadderLevel, TimeSpan.FromSeconds(8));

            List<GhostPartyMember>? party = BuildParty(modifier.LadderLevel, humans, coordinator);
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
                // 2026-09-08 (user request): heal every restored Ghost to full before the fight, the
                // same courtesy already given to the real humans above — a Ghost's snapshot can
                // legitimately have CurrentHp < MaxHp (the human who earned it may have won their own
                // Act-3 boss fight while still damaged; only real humans got healed to full before that
                // fight, not their own eventual Ghost). Deliberately reads Creature.MaxHp — the Ghost's
                // own live, already-restored value (Player.FromSerializable's constructor sets it
                // directly from the snapshot's MaxHp, confirmed by reading Player.cs — e.g. 92 for a
                // run that grew past Ironclad's stock 80) — never CharacterModel.StartingHp, which
                // would silently downgrade an earned max-HP increase back to the base character's stock
                // value.
                Creature ghostCreature = member.Player.Creature;
                decimal ghostMissing = ghostCreature.MaxHp - ghostCreature.CurrentHp;
                if (ghostMissing > 0m)
                {
                    await CreatureCmd.Heal(ghostCreature, ghostMissing);
                }
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

                CombatRoom ghostFightRoom = new(ModelDb.Encounter<GhostDuelEncounter>().ToMutable(), runState) { ShouldResumeParentEventAfterCombat = false };
                await instance.EnterRoom(ghostFightRoom);
                // See GhostPlayerFactory.FireAfterRoomEnteredForGhosts' own doc comment: the native
                // call this same EnterRoom already made (CombatRoom.cs:228) only ever reaches the real
                // humans' own relics (e.g. Vajra), never a Ghost's — this is the mod-side equivalent for
                // the party.
                await GhostPlayerFactory.FireAfterRoomEnteredForGhosts(party, ghostFightRoom);
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
        private static List<GhostPartyMember>? BuildParty(int ladderLevel, IReadOnlyList<Player> humans, MultiplayerGhostLadderCoordinator coordinator)
        {
            List<GhostPartyMember> party = new(humans.Count);
            for (int i = 0; i < humans.Count; i++)
            {
                Player human = humans[i];
                Player? ghost = BuildGhost(ladderLevel, human, i, coordinator);
                if (ghost is null)
                {
                    return null;
                }
                party.Add(new GhostPartyMember(ghost, new Ai.WeightedRandomGhostChooser()));
            }
            return party;
        }

        private static Player? BuildGhost(int ladderLevel, Player human, int partyIndex, MultiplayerGhostLadderCoordinator coordinator)
        {
            if (ladderLevel == 0)
            {
                return GhostPlayerFactory.CreateDebugGhost(human.Character, partyIndex);
            }

            if (!coordinator.ReportedSnapshots.TryGetValue(human.NetId, out Messages.MultiplayerGhostSnapshotReportMessage report))
            {
                GhostLog.Error($"MultiplayerLegacyAscensionGhostFightSplice: no multiplayer-ladder snapshot report received for {human.Character.Id}#{human.NetId} at A{ladderLevel - 1}.");
                return null;
            }
            if (!report.hasPreviousGhost || report.player is not { } snapshot)
            {
                GhostLog.Error($"MultiplayerLegacyAscensionGhostFightSplice: {human.Character.Id}#{human.NetId} reported no previous Ghost for A{ladderLevel - 1}.");
                return null;
            }
            // 2026-09-07 (user report: "in Ascension 1 fight the ghosts appear to have base decks"):
            // logs the snapshot's own deck/relic count right before it's used to build the Ghost, so a
            // future log can distinguish "the reported snapshot was already a base deck" (root cause is
            // upstream — the report or the save that produced it) from "the report was fine but
            // something downstream of this line rebuilt a fresh Ghost instead" (root cause is here or
            // later). Cross-reference against this same human's own SNAPSHOT-SENT line.
            GhostLog.Info($"MultiplayerLegacyAscensionGhostFightSplice: building A{ladderLevel - 1} Ghost for {human.Character.Id}#{human.NetId} from reported snapshot (deckCount={snapshot.Deck.Count}, relicCount={snapshot.Relics.Count}).");
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
