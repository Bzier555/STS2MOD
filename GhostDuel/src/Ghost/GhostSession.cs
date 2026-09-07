using System;
using System.Collections.Generic;
using System.Linq;
using GhostDuel.Diagnostics;
using GhostDuel.Duel;
using GhostDuel.Ghost.Choosers;
using GhostDuel.Multiplayer.Messages;
using GhostDuel.Presentation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Ghost;

/// <summary>
/// PLAN.md M9c: one member of the Ghost party — a real enemy-side <see cref="Player"/>, the chooser
/// that plays its turns, its own detached run-history row (P8, <c>EnginePatches.cs</c> — each Ghost
/// needs its own, since they have distinct <see cref="Player.NetId"/>s), and its own per-turn round
/// counter (logging only, matching "this Ghost's Nth turn" rather than a party-wide count that would
/// inflate faster than true combat rounds once more than one Ghost is acting).
/// </summary>
internal sealed class GhostPartyMember
{
    public Player Player { get; }
    public IGhostCardChooser Chooser { get; }
    public PlayerMapPointHistoryEntry HistoryEntry { get; }
    public int RoundNumber { get; private set; }

    public GhostPartyMember(Player player, IGhostCardChooser chooser)
    {
        Player = player;
        Chooser = chooser;
        HistoryEntry = new PlayerMapPointHistoryEntry { PlayerId = player.NetId };
    }

    public int NextRound() => ++RoundNumber;
}

/// <summary>
/// The mod's one piece of static mutable state besides <see cref="GhostLog"/> (CLAUDE.md's "Code
/// shape" rule). Created when the Ghost encounter is entered, disposed on every exit path. Every
/// engine patch's mode guard is <c>GhostSession.Current is not null</c> — one field, one meaning
/// (ARCHITECTURE.md §10).
///
/// PLAN.md M9c: generalized from a single Ghost to a <see cref="Party"/> of 1-4, per the user's M9
/// design — one Ghost per human, all in one combined combat. <see cref="GhostPlayer"/>/<see cref="Chooser"/>
/// remain as aliases for <c>Party[0]</c>, so every one of the M1-M8 singleplayer call sites (exactly
/// one Ghost, still the only live-tested shape) keeps working completely unchanged — this is an
/// additive generalization, not a rewrite of the tested 1v1 path. <see cref="Begin(Player,IGhostCardChooser)"/>
/// is likewise kept as the exact original single-Ghost overload.
/// </summary>
internal sealed class GhostSession : IDisposable
{
    public static GhostSession? Current { get; private set; }

    public IReadOnlyList<GhostPartyMember> Party { get; }

    /// <summary>Alias for <c>Party[0]</c> — every existing singleplayer (exactly-one-Ghost) call site
    /// keeps reading this exactly as before.</summary>
    public Player GhostPlayer => Party[0].Player;

    /// <summary>Alias for <c>Party[0]</c>'s chooser — see <see cref="GhostPlayer"/>.</summary>
    public IGhostCardChooser Chooser => Party[0].Chooser;

    /// <summary>Alias for <c>Party[0]</c>'s round counter — see <see cref="GhostPlayer"/>.</summary>
    public int RoundNumber => Party[0].RoundNumber;

    /// <summary>
    /// M8.5 (PLAN.md): a random-deck request that couldn't be applied synchronously at the point
    /// the Ghost was constructed — <see cref="Debug.RealFlowGhostDuelEntry"/>'s substitution runs
    /// inside a synchronous prefix on <c>ActModel.PullNextEncounter</c>, but
    /// <c>GhostPlayerFactory.ConfigureDeckAsync</c> needs native async commands
    /// (<c>CardPileCmd</c>/<c>RelicCmd</c>). Set here, consumed and cleared by P5's extended prefix
    /// (<c>EnginePatches.cs</c>) — the same reverse-patch technique P13 already uses to run async
    /// setup before an async native method (<c>CombatRoom.StartCombat</c>) actually begins, i.e.
    /// before <c>CombatManager.SetUpCombat</c> shuffles the Ghost's (stock, at this point) deck into
    /// its draw pile. Not consumed by <see cref="Debug.DebugBoot"/>'s hand-built shortcut, which
    /// already awaits <c>ConfigureDeckAsync</c> directly in its own linear async flow and never sets
    /// this. Left session-wide, singular (not per-party-member): this is a singleplayer debug-only
    /// feature (M8.5), never exercised by M9's real-party flow, which builds every party member from a
    /// real snapshot or a fresh stock character instead.
    /// </summary>
    public (IReadOnlyList<Type> Cards, IReadOnlyList<Type> Relics)? PendingRandomDeck { get; set; }

    /// <summary>M3's queued-damage core (COMBAT-RULES.md §2-§5) — see <see cref="GhostDamageQueue"/>.
    /// Session-wide (shared by the whole party): every packet already carries its own specific
    /// dealer/target creature references, so nothing here needs to be per-Ghost.</summary>
    public GhostDamageQueue DamageQueue { get; } = new();

    /// <summary>Subscribes to <see cref="DamageQueue"/> to show the queued-damage indicator above
    /// whichever creature currently has a pending attack against the local viewer (COMBAT-RULES.md §8,
    /// PLAN.md M9d). Owned here, not static, so its lifetime matches the session's exactly — created
    /// with the session, disposed with it.</summary>
    private readonly GhostDamageIndicatorPresenter _damagePresenter;

    /// <summary>PLAN.md M10b: attaches a small "view deck" button per Ghost, once that Ghost's own
    /// <c>NCreature</c> node is known to exist — see <see cref="GhostDeckViewerPresenter"/>'s own doc
    /// comment for why this can't be wired up eagerly the way <see cref="_damagePresenter"/> is.
    /// Notified via <see cref="NotifyGhostCreatureNodeReady"/>, called from P6
    /// (<c>EnginePatches.cs</c>).</summary>
    private readonly GhostDeckViewerPresenter _deckViewerPresenter = new();

    /// <summary>PLAN.md M9e: the host-authoritative Ghost-action broadcast — see
    /// <see cref="GhostActionSync"/>'s own doc comment. Resolved from
    /// <c>RunManager.Instance.NetService</c> — safe to read here (unlike during the M9b lobby phase)
    /// because a <see cref="GhostSession"/> only ever exists once a real run/combat is already active.</summary>
    public GhostActionSync ActionSync { get; }

    private readonly INetGameService _net;
    private bool _disposed;

    private GhostSession(IReadOnlyList<GhostPartyMember> party)
    {
        if (party.Count == 0)
        {
            throw new ArgumentException("A GhostSession needs at least one party member.", nameof(party));
        }
        Party = party;
        _damagePresenter = new GhostDamageIndicatorPresenter(this);
        _net = RunManager.Instance.NetService;
        ActionSync = new GhostActionSync(_net);
        _net.RegisterMessageHandler<GhostPartyResyncMessage>(OnPartyResyncReceived);
    }

    /// <summary>PLAN.md M9f: client-side half of the reconnect resync. Deliberately diagnostic-only —
    /// see <see cref="GhostPartyResyncMessage"/>'s own doc comment for why this logs drift rather than
    /// attempting to force-correct live HP/Block outside a native command.</summary>
    private void OnPartyResyncReceived(GhostPartyResyncMessage message, ulong senderId)
    {
        ulong[] ids = message.ghostNetIds ?? [];
        for (int i = 0; i < ids.Length; i++)
        {
            GhostPartyMember? member = Party.FirstOrDefault(m => m.Player.NetId == ids[i]);
            if (member is null)
            {
                GhostLog.ReconnectGhostResync("received", $"unknown ghost NetId={ids[i]} in resync from host — not a member of this local party.");
                continue;
            }
            int reportedHp = message.currentHp?[i] ?? 0;
            int reportedBlock = message.block?[i] ?? 0;
            int localHp = (int)member.Player.Creature.CurrentHp;
            int localBlock = (int)member.Player.Creature.Block;
            string match = localHp == reportedHp && localBlock == reportedBlock ? "match" : "DRIFT";
            GhostLog.ReconnectGhostResync("received", $"ghost={ids[i]} host_hp={reportedHp} local_hp={localHp} host_block={reportedBlock} local_block={localBlock} -> {match}");
        }
    }

    /// <summary>The original, still-primary single-Ghost entry point (M1-M8) — unchanged signature,
    /// so every existing singleplayer call site (<c>DebugBoot</c>, <c>RealFlowGhostDuelEntry</c>,
    /// <c>LegacyAscensionGhostFightSplice</c>) needs no changes at all.</summary>
    public static GhostSession Begin(Player ghostPlayer, IGhostCardChooser chooser) =>
        Begin(new[] { new GhostPartyMember(ghostPlayer, chooser) });

    /// <summary>PLAN.md M9c: the real party entry point — one <see cref="GhostPartyMember"/> per
    /// human, in the human party's own stable order.</summary>
    public static GhostSession Begin(IReadOnlyList<GhostPartyMember> party)
    {
        if (Current is not null)
        {
            throw new InvalidOperationException("A GhostSession is already active; the previous one was not disposed.");
        }
        GhostSession session = new(party);
        Current = session;
        CombatManager.Instance.CombatEnded += session.OnCombatEnded;
        GhostLog.Info($"Session begin: party=[{string.Join(", ", party.Select(m => $"{m.Player.Character.Id}#{m.Player.NetId}"))}]");
        return session;
    }

    public int NextRound() => Party[0].NextRound();

    /// <summary>Party-aware version of <see cref="NextRound"/>: increments the specific party
    /// member's own counter, not always <c>Party[0]</c>'s — <see cref="GhostTurnController"/> uses
    /// this once there is more than one Ghost.</summary>
    public int NextRoundFor(Player ghostPlayer) =>
        (FindByPlayer(ghostPlayer) ?? throw new InvalidOperationException($"NextRoundFor: {ghostPlayer.NetId} is not a member of this GhostSession's party.")).NextRound();

    public bool IsGhostCreature(Creature creature) => Party.Any(m => ReferenceEquals(m.Player.Creature, creature));

    public bool IsGhostPlayer(Player player) => Party.Any(m => ReferenceEquals(m.Player, player));

    /// <summary>Combat-rules rework (2026-09-04): true for the Ghost's own creature, or a pet a Ghost
    /// party member owns (e.g. Necrobinder's Osty) — the same "effectively dealt by the Ghost" shape
    /// <see cref="Duel.GhostDamageQueue"/>'s own <c>BelongsTo</c> already uses to attribute a pet's
    /// damage back to its owner, reused here so P17 (<c>EnginePatches.cs</c>) only ever queues damage
    /// that's actually the Ghost's, in one place rather than two copies of the same pet check.</summary>
    public bool IsGhostOwnedDealer(Creature dealer) =>
        IsGhostCreature(dealer) || (dealer.PetOwner is not null && IsGhostPlayer(dealer.PetOwner));

    public GhostPartyMember? FindByCreature(Creature creature) => Party.FirstOrDefault(m => ReferenceEquals(m.Player.Creature, creature));

    public GhostPartyMember? FindByPlayer(Player player) => Party.FirstOrDefault(m => ReferenceEquals(m.Player, player));

    /// <summary>PLAN.md M10b: called from P6 (<c>EnginePatches.cs</c>) the moment a specific Ghost's own
    /// <c>NCreature</c> node is confirmed to exist and be correctly positioned — the one place that's
    /// already guaranteed true, well before any other point this session's own constructor could rely
    /// on.</summary>
    public void NotifyGhostCreatureNodeReady(Player ghostPlayer, NCreature node) =>
        _deckViewerPresenter.AttachButtonFor(ghostPlayer, node);

    private void OnCombatEnded(CombatRoom room) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _damagePresenter.Dispose();
        _deckViewerPresenter.Dispose();
        ActionSync.Dispose();
        _net.UnregisterMessageHandler<GhostPartyResyncMessage>(OnPartyResyncReceived);
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
        GhostLog.Info("Session end.");
    }
}
