using System.Linq;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using GhostDuel.Multiplayer.Messages;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Multiplayer;

/// <summary>
/// PLAN.md M9f: host-side half of the reconnect resync — see <see cref="GhostPartyResyncMessage"/>'s
/// own doc comment for this feature's deliberately narrowed scope. <c>RunLobby
/// .HandleClientRejoinRequestMessage</c> is confirmed (by direct read, not guessed) as the exact
/// point the host sends its native rejoin response and marks the peer ready for broadcasting — this
/// postfix piggybacks a Ghost-specific resync onto that same moment, to the same specific rejoining
/// peer, only when a Ghost session is actually active.
/// </summary>
internal static class GhostPartyReconnectSync
{
    [HarmonyPatch(typeof(RunLobby), "HandleClientRejoinRequestMessage")]
    private static class Postfix_SendGhostPartyResyncToRejoiningClient
    {
        [HarmonyPostfix]
        private static void Postfix(ulong senderId)
        {
            if (GhostSession.Current is not { } session)
            {
                return;
            }

            GhostPartyResyncMessage message = new()
            {
                ghostNetIds = session.Party.Select(m => m.Player.NetId).ToArray(),
                currentHp = session.Party.Select(m => (int)m.Player.Creature.CurrentHp).ToArray(),
                block = session.Party.Select(m => (int)m.Player.Creature.Block).ToArray(),
            };
            RunManager.Instance.NetService.SendMessage(message, senderId);
            GhostLog.ReconnectGhostResync("sent", $"to={senderId} party=[{string.Join(", ", session.Party.Select(m => $"{m.Player.NetId}:hp={m.Player.Creature.CurrentHp}/blk={m.Player.Creature.Block}"))}]");
        }
    }
}
