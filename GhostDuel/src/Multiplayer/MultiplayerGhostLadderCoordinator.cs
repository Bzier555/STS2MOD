using System;
using System.Collections.Generic;
using System.Linq;
using GhostDuel.Diagnostics;
using GhostDuel.Multiplayer.Messages;
using GhostDuel.Progression;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace GhostDuel.Multiplayer;

/// <summary>
/// PLAN.md M9a: collects every connected human's own multiplayer-ladder progress (never the
/// singleplayer one) and aggregates it the same way the native multiplayer ascension cap does —
/// <c>Min()</c> across everyone who has reported, recomputed and rebroadcast on every new report,
/// mirroring <c>StartRunLobby.UpdateMaxMultiplayerAscension()</c> (<c>StartRunLobby.cs:287-299</c>).
///
/// Wired by M9b's <c>MultiplayerLegacyAscensionEntry</c>, one instance per lobby attempt (deliberately
/// not static mutable state, per CLAUDE.md's code-shape rule) — created when the mode is armed,
/// disposed on disarm or embark, the same lifetime discipline <c>GhostSession</c> already uses for the
/// combat-side equivalent.
///
/// Takes its <see cref="INetGameService"/> explicitly rather than reaching for
/// <c>RunManager.Instance.NetService</c>: during the lobby/character-select phase (before a run
/// exists) the correct instance is <c>StartRunLobby.NetService</c>
/// (<c>NCharacterSelectScreen.Lobby.NetService</c>, confirmed public via the same reflection pass as
/// <c>RunManager.NetService</c> — see <c>ENGINE-NOTES.md</c>'s "M9 research" section), not whatever
/// <c>RunManager.Instance</c> currently holds, which is only meaningful once a run has actually begun.
///
/// The host handles its own report and its own broadcast locally rather than over the wire — confirmed
/// necessary by reading <c>NetHostGameService.SendMessage(T)</c> (broadcast overload): it only sends to
/// <c>_connectedPeers</c>, it never invokes the host's own registered handlers for a message the host
/// itself originates. Native code (e.g. <c>ActionQueueSynchronizer.EnqueueAction</c>) always pairs a
/// broadcast with an explicit local-handling step for exactly this reason; this class does the same.
/// </summary>
internal sealed class MultiplayerGhostLadderCoordinator : IDisposable
{
    private readonly INetGameService _net;
    private readonly Dictionary<ulong, int> _reportedLevels = new();
    private readonly Dictionary<ulong, MultiplayerGhostSnapshotReportMessage> _reportedSnapshots = new();
    private bool _disposed;

    public int? AggregateMinLevel { get; private set; }

    /// <summary>Fired whenever <see cref="AggregateMinLevel"/> changes, on every peer (host included) —
    /// M9b's mode-entry code subscribes to push the new value into <c>StartRunLobby.MaxAscension</c>
    /// and re-fire <c>LobbyListener.MaxAscensionChanged()</c>, mirroring M7's
    /// <c>Postfix_MakeAscensionMaxCharacterIndependent</c> "correct the one number after the fact"
    /// pattern.</summary>
    public event Action<int>? AggregateChanged;

    public IReadOnlyDictionary<ulong, MultiplayerGhostSnapshotReportMessage> ReportedSnapshots => _reportedSnapshots;

    public MultiplayerGhostLadderCoordinator(INetGameService net)
    {
        _net = net;
        _net.RegisterMessageHandler<MultiplayerGhostLadderReportMessage>(OnLadderReportReceived);
        _net.RegisterMessageHandler<MultiplayerGhostLadderAggregateMessage>(OnAggregateReceived);
        _net.RegisterMessageHandler<MultiplayerGhostSnapshotReportMessage>(OnSnapshotReceived);
    }

    /// <summary>Call once per human, on entering the multiplayer Ghost-ascension mode-select flow
    /// (M9b). Reports this client's own <see cref="MultiplayerGhostSnapshotStore.GetMaxSelectableLevel"/>
    /// — the multiplayer ladder specifically.</summary>
    public void ReportOwnLadderLevel()
    {
        int ownMax = MultiplayerGhostSnapshotStore.GetMaxSelectableLevel();
        string selfLabel = SelfLabel();
        if (_net.Type == NetGameType.Host)
        {
            RecordLadderReport(_net.NetId, ownMax);
        }
        else
        {
            _net.SendMessage(new MultiplayerGhostLadderReportMessage { maxSelectableLevel = ownMax });
        }
        GhostLog.LadderReport(selfLabel, ownMax, AggregateMinLevel);
    }

    /// <summary>Call once per human, once the party's ladder level is locked in, just before combat
    /// (M9b/c). Reports this client's own previous-level Ghost for that level, or "none" for a fresh
    /// A0 party.</summary>
    public void ReportOwnSnapshot(int lockedInLevel)
    {
        MultiplayerGhostSnapshotReportMessage report = BuildOwnSnapshotReport(lockedInLevel);
        GhostLog.SnapshotSent(SelfLabel(), report.level, report.hasPreviousGhost, report.player?.Deck.Count ?? -1, report.player?.Relics.Count ?? -1);
        // 2026-09-08 (live incident: host entered the Ghost-party fight normally, the joining client
        // went to the Architect scene instead — deterministic, not a timing fluke). Root cause:
        // BuildGhost needs every human's own entry in ITS OWN process's ReportedSnapshots, including
        // that process's own local human — but ShouldBroadcast only auto-relays a message that arrives
        // *from* a peer (NetHostGameService.OnPacketReceived) to *other* peers, never back to the
        // sender, so a non-host caller's own report never looped back to itself. Recording locally here
        // unconditionally (previously host-only) fixes that; the broadcast below is still needed
        // unconditionally too, host included, since a report this process originates locally never
        // reaches anyone else without it.
        RecordSnapshotReport(_net.NetId, report);
        _net.SendMessage(report);
    }

    private static MultiplayerGhostSnapshotReportMessage BuildOwnSnapshotReport(int lockedInLevel)
    {
        if (lockedInLevel <= 0)
        {
            return new MultiplayerGhostSnapshotReportMessage { level = lockedInLevel, hasPreviousGhost = false, player = null };
        }

        GhostSnapshotFileStore.LoadResult load = MultiplayerGhostSnapshotStore.Load(lockedInLevel - 1);
        if (!load.Success || load.Player is null)
        {
            GhostLog.Error($"MultiplayerGhostLadderCoordinator: {load.Error ?? "unknown load failure"}; reporting no previous Ghost for level {lockedInLevel}.");
            return new MultiplayerGhostSnapshotReportMessage { level = lockedInLevel, hasPreviousGhost = false, player = null };
        }

        return new MultiplayerGhostSnapshotReportMessage { level = lockedInLevel, hasPreviousGhost = true, player = load.Player };
    }

    private void OnLadderReportReceived(MultiplayerGhostLadderReportMessage message, ulong senderId) =>
        RecordLadderReport(senderId, message.maxSelectableLevel);

    private void RecordLadderReport(ulong senderId, int maxLevel)
    {
        _reportedLevels[senderId] = maxLevel;
        if (_net.Type != NetGameType.Host)
        {
            return;
        }

        int newMin = _reportedLevels.Values.Min();
        if (AggregateMinLevel == newMin)
        {
            return;
        }
        AggregateMinLevel = newMin;
        MultiplayerGhostLadderAggregateMessage aggregate = new() { aggregateMinLevel = newMin };
        _net.SendMessage(aggregate);
        OnAggregateReceived(aggregate, _net.NetId);
    }

    private void OnAggregateReceived(MultiplayerGhostLadderAggregateMessage message, ulong senderId)
    {
        AggregateMinLevel = message.aggregateMinLevel;
        GhostLog.LadderReport(SelfLabel(), MultiplayerGhostSnapshotStore.GetMaxSelectableLevel(), AggregateMinLevel);
        AggregateChanged?.Invoke(message.aggregateMinLevel);
    }

    private void OnSnapshotReceived(MultiplayerGhostSnapshotReportMessage message, ulong senderId) =>
        RecordSnapshotReport(senderId, message);

    private void RecordSnapshotReport(ulong senderId, MultiplayerGhostSnapshotReportMessage message)
    {
        _reportedSnapshots[senderId] = message;
        GhostLog.SnapshotReceived(senderId, message.level, message.hasPreviousGhost, message.player?.Deck.Count ?? -1, message.player?.Relics.Count ?? -1);
    }

    private string SelfLabel() => $"NetId#{_net.NetId}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _net.UnregisterMessageHandler<MultiplayerGhostLadderReportMessage>(OnLadderReportReceived);
        _net.UnregisterMessageHandler<MultiplayerGhostLadderAggregateMessage>(OnAggregateReceived);
        _net.UnregisterMessageHandler<MultiplayerGhostSnapshotReportMessage>(OnSnapshotReceived);
    }
}
