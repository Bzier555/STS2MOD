using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GhostDuel.Diagnostics;
using GhostDuel.Multiplayer.Messages;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace GhostDuel.Ghost;

/// <summary>
/// PLAN.md M9e: the host-authoritative Ghost-action broadcast — see the "design decision" section of
/// the M9 plan for why this is a small purpose-built bus rather than a reuse of the native, real-peer-
/// owned <c>ActionQueueSet</c>. Owned by <see cref="GhostSession"/> for the combat's lifetime (session-
/// scoped, not static — CLAUDE.md's code-shape rule).
///
/// <see cref="IsHost"/> is true for both <c>NetGameType.Host</c> and <c>NetGameType.Singleplayer</c> —
/// this is what keeps every M1-M8 singleplayer call site's behavior completely unchanged: a
/// singleplayer session decides via the chooser and executes immediately, exactly as before, with only
/// a harmless no-op <c>SendMessage</c> added (<c>NetSingleplayerGameService.SendMessage</c>/
/// <c>RegisterMessageHandler</c> are confirmed no-ops, ENGINE-NOTES.md's "M9 research" section) — the
/// new "wait for a broadcast instead of deciding" path only ever activates for <c>NetGameType.Client</c>,
/// a mode no existing tested scenario reaches.
///
/// <b>Revised 2026-09-05</b>: confirmed live (two-Ghost multiplayer test) that the original design —
/// one overwritable <c>TaskCompletionSource</c> per Ghost, registered fresh by each <see cref="AwaitNext"/>
/// call — drops an event outright if it arrives before the *next* <c>AwaitNext</c> call has registered
/// its own waiter. The host's own loop paces itself purely by how long its own local card execution
/// takes (<c>GhostTurnController.RunTurnAsync</c>'s host branch executes its own card immediately after
/// broadcasting) and never waits for a client to keep up, so this gap is not a rare edge case — any time
/// the host is even slightly faster than a client for one card, the *next* broadcast for that same
/// Ghost can win the race against that client's own re-entry into its `AwaitNext` loop. Confirmed from
/// `godot.log`: a joining client's log showed an end-turn event received and logged for one Ghost, but
/// that Ghost never logged `TURN-END` — its `RunTurnAsync` was left permanently awaiting a
/// `TaskCompletionSource` nothing would ever resolve, which also explains why a second Ghost's own
/// `TURN-START` never appeared at all: native `CombatManager.ExecuteEnemyTurn`'s per-creature loop
/// awaits `Creature.TakeTurn()` (P2's target) for one creature at a time, so a hung first Ghost blocks
/// the loop from ever reaching the second one, regardless of party size.
///
/// Fixed by replacing the single overwritable slot with an unbounded, per-Ghost FIFO channel
/// (<see cref="System.Threading.Channels.Channel{T}"/>) — a write from <see cref="OnEventReceived"/>
/// is never lost even when no one is currently reading, and a read from <see cref="AwaitNext"/>
/// immediately returns any already-buffered event before waiting for a new one. This is correct for
/// any number of Ghosts, not just two: each Ghost's own channel is created lazily, keyed by its NetId,
/// with no assumption about how many exist or in what order their turns run.
/// </summary>
internal sealed class GhostActionSync : IDisposable
{
    private readonly INetGameService _net;
    private uint _nextSequenceNumber;
    private readonly Dictionary<ulong, Channel<GhostCardPlayEvent>> _channelsByGhostNetId = new();
    private bool _disposed;

    public GhostActionSync(INetGameService net)
    {
        _net = net;
        _net.RegisterMessageHandler<GhostCardPlayEvent>(OnEventReceived);
    }

    public bool IsHost => _net.Type is NetGameType.Host or NetGameType.Singleplayer;

    /// <summary>Host only: broadcasts a decided card play. Every other client executes this exact
    /// event instead of deciding for itself; the host does not wait for its own broadcast (a host never
    /// receives its own message back — <c>NetHostGameService.SendMessage</c>'s broadcast overload only
    /// reaches <c>_connectedPeers</c>) and instead proceeds to execute the same choice locally right
    /// after sending, mirroring the native "enqueue locally and broadcast" pattern this project already
    /// uses elsewhere (<c>ActionQueueSynchronizer</c>, <c>MultiplayerGhostLadderCoordinator</c>).</summary>
    public uint BroadcastCardPlay(Player ghostPlayer, GhostChoice choice, int handIndex)
    {
        GhostCardPlayEvent evt = new()
        {
            sequenceNumber = ++_nextSequenceNumber,
            ghostNetId = ghostPlayer.NetId,
            isEndTurn = false,
            handIndex = handIndex,
            cardIdForValidation = choice.Card.Id.Entry,
            hasTarget = choice.Target is not null,
            targetNetId = choice.Target?.Player?.NetId ?? 0,
        };
        GhostLog.GhostBroadcast(evt.sequenceNumber, evt.ghostNetId, evt.cardIdForValidation ?? "?", choice.Target?.LogName);
        _net.SendMessage(evt);
        return evt.sequenceNumber;
    }

    /// <summary>Host only: broadcasts "this Ghost's turn has no more plays" so waiting clients know to
    /// stop rather than hang forever.</summary>
    public void BroadcastEndTurn(Player ghostPlayer)
    {
        GhostCardPlayEvent evt = new()
        {
            sequenceNumber = ++_nextSequenceNumber,
            ghostNetId = ghostPlayer.NetId,
            isEndTurn = true,
        };
        GhostLog.GhostBroadcast(evt.sequenceNumber, evt.ghostNetId, "<end-turn>", null);
        _net.SendMessage(evt);
    }

    /// <summary>Client (non-host) only: waits for and resolves the next broadcast event for this
    /// specific Ghost against the currently-captured hand/combat state. Returns null for an end-turn
    /// event, matching <see cref="IGhostCardChooser"/>'s own "null means end turn" contract, so the
    /// rest of <see cref="GhostTurnController"/>'s loop needs no other branching.
    /// <b>Revised 2026-09-08</b> (user report: "the game engine was waiting for the ghost [Silent] to
    /// end its turn on the player's turn"): this used to await the channel with no timeout at all. If a
    /// broadcast for this Ghost is ever missed — a dropped packet, or (the most likely case, since
    /// M9f's own reconnect handling is explicitly diagnostic-only and does not replay missed turn
    /// events) a client reconnecting mid-Ghost-turn, with no way to receive whatever was already
    /// broadcast before it reconnected — this awaited forever. <see cref="GhostTurnController"/>'s own
    /// 30-second <c>TurnBudget</c> guard does not actually bound this: it is only re-checked *between*
    /// loop iterations, never around a single in-progress await, so a hang here never trips it. Because
    /// native <c>CombatManager.ExecuteEnemyTurn</c>'s per-creature loop awaits <c>Creature.TakeTurn()</c>
    /// (P2's target) for one creature at a time, a client stuck here never locally finishes processing
    /// "the Ghost's turn," even while the host and every other peer have already moved on to the human's
    /// turn — matching the reported symptom exactly. Now takes the caller's own remaining turn-budget
    /// window as an explicit timeout and gives up (logs and returns null, i.e. "end this Ghost's turn
    /// locally") rather than hanging forever; a missed broadcast can never be recovered from by waiting
    /// longer, so timing out is the only honest option (CLAUDE.md: surface what's unavailable rather
    /// than hang pretending it will arrive).</summary>
    public async Task<GhostChoice?> AwaitNext(Player ghostPlayer, GhostTurnView view, TimeSpan timeout)
    {
        GhostCardPlayEvent evt;
        using (CancellationTokenSource cts = new(timeout))
        {
            try
            {
                evt = await GetOrCreateChannel(ghostPlayer.NetId).Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                GhostLog.Error($"GhostActionSync: timed out after {timeout.TotalSeconds:F0}s waiting for the host's next broadcast for ghost {ghostPlayer.NetId} — a broadcast was missed (dropped message, or a mid-turn reconnect that can't replay history) and can never arrive now; ending this Ghost's turn locally rather than hanging forever.");
                return null;
            }
        }
        if (evt.isEndTurn)
        {
            return null;
        }

        IReadOnlyList<CardModel> hand = view.Hand;
        if (evt.handIndex < 0 || evt.handIndex >= hand.Count)
        {
            GhostLog.Error($"GhostActionSync: received hand index {evt.handIndex} out of range (hand size {hand.Count}) for ghost {evt.ghostNetId} — cannot execute, ending turn.");
            return null;
        }
        CardModel card = hand[evt.handIndex];
        if (!string.Equals(card.Id.Entry, evt.cardIdForValidation, StringComparison.Ordinal))
        {
            GhostLog.Error($"GhostActionSync: hand index {evt.handIndex} is {card.Id.Entry} locally but {evt.cardIdForValidation} on the host — hand desynced, cannot execute, ending turn.");
            return null;
        }

        Creature? target = null;
        if (evt.hasTarget)
        {
            target = ghostPlayer.Creature.CombatState?.PlayerCreatures.FirstOrDefault(c => c.Player?.NetId == evt.targetNetId);
            if (target is null)
            {
                GhostLog.Error($"GhostActionSync: could not resolve target NetId {evt.targetNetId} locally for ghost {evt.ghostNetId} — cannot execute, ending turn.");
                return null;
            }
        }

        return new GhostChoice(card, target);
    }

    private void OnEventReceived(GhostCardPlayEvent evt, ulong senderId)
    {
        // evt.cardIdForValidation round-trips as "" (not null) for an end-turn event — Serialize
        // writes `cardIdForValidation ?? string.Empty`, so Deserialize can never hand back null for
        // this field. Check isEndTurn explicitly rather than relying on `?? "<end-turn>"`, which would
        // never trigger.
        GhostLog.GhostExecute(evt.sequenceNumber, evt.ghostNetId, evt.isEndTurn ? "<end-turn>" : evt.cardIdForValidation ?? "?", evt.hasTarget ? evt.targetNetId.ToString() : null);
        // Never dropped even if AwaitNext hasn't (yet) been called again for this Ghost — see this
        // class's revised doc comment for why that gap is real and not just theoretical.
        GetOrCreateChannel(evt.ghostNetId).Writer.TryWrite(evt);
    }

    /// <summary>Lazily creates one unbounded FIFO channel per Ghost NetId — correct for any party
    /// size, since nothing here assumes how many Ghosts exist or which one calls this first.</summary>
    private Channel<GhostCardPlayEvent> GetOrCreateChannel(ulong ghostNetId)
    {
        if (!_channelsByGhostNetId.TryGetValue(ghostNetId, out Channel<GhostCardPlayEvent>? channel))
        {
            channel = Channel.CreateUnbounded<GhostCardPlayEvent>();
            _channelsByGhostNetId[ghostNetId] = channel;
        }
        return channel;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _net.UnregisterMessageHandler<GhostCardPlayEvent>(OnEventReceived);
        foreach (Channel<GhostCardPlayEvent> channel in _channelsByGhostNetId.Values)
        {
            channel.Writer.TryComplete();
        }
    }
}
