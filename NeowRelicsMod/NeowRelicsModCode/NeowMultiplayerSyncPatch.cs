using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;

namespace NeowRelicsMod.NeowRelicsModCode;

// NeowModifierBlessingPatch adds an extra page (the blessing choice) after a player's last run
// modifier is picked. That page is applied asynchronously. In multiplayer, each peer mirrors every
// other player's Neow event locally and applies their choices as network messages arrive
// (EventSynchronizer.ChooseOptionForEvent), which rejects an option index that's out of range for
// the CurrentOptions it currently has. Without this patch, a peer could receive "blessing chosen"
// before it had finished locally applying "last modifier chosen" (since that page swap hasn't
// awaited yet), and reject the choice. This patch makes a peer finish applying a player's pending
// Neow choice before applying that same player's next one, so the extra page can never be skipped.
[HarmonyPatch(typeof(EventSynchronizer), "HandleEventOptionChosenMessage", typeof(OptionIndexChosenMessage), typeof(ulong))]
public static class NeowMultiplayerSyncPatch
{
    private sealed class Queue
    {
        public Task Tail = Task.CompletedTask;
    }

    private static readonly ConditionalWeakTable<EventSynchronizer, Queue> QueueBySynchronizer = new();

    private static bool Prefix(EventSynchronizer __instance, OptionIndexChosenMessage message, ulong senderId)
    {
        if (message.type != OptionIndexType.Event)
            return true;

        Player? player = __instance._playerCollection.GetPlayer(senderId);
        if (player is null || __instance.GetEventForPlayer(player) is not Neow)
            return true;

        Queue queue = QueueBySynchronizer.GetOrCreateValue(__instance);
        lock (queue)
        {
            queue.Tail = ApplyOnceUnblocked(__instance, queue.Tail, player, (int)message.optionIndex);
        }

        return false;
    }

    private static async Task ApplyOnceUnblocked(EventSynchronizer synchronizer, Task previousInQueue, Player player, int optionIndex)
    {
        try
        {
            await previousInQueue;
            await WaitForPendingOptionTasks(synchronizer);

            if (synchronizer.GetEventForPlayer(player).IsFinished)
                return;

            synchronizer.ChooseOptionForEvent(player, optionIndex);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[NeowRelicsMod] Failed to apply queued Neow choice for player {player.NetId} option {optionIndex}: {ex}");
        }
    }

    private static async Task WaitForPendingOptionTasks(EventSynchronizer synchronizer)
    {
        // Bounded so a bug elsewhere (a task that reschedules itself forever) can't hang this chain.
        for (int i = 0; i < 20; i++)
        {
            Task[] unfinished = synchronizer._pendingOptionTasks.Where(t => !t.IsCompleted).ToArray();
            if (unfinished.Length == 0)
                return;

            await Task.WhenAll(unfinished);
        }
    }
}
