using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost.Choosers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace GhostDuel.Ghost;

/// <summary>
/// The one turn skeleton (ARCHITECTURE.md §6). <c>CombatManager</c>'s own turn machinery never
/// touches an enemy-side <c>Player</c> (confirmed: <c>SetupPlayerTurn</c> and both
/// <c>EndPlayerTurn*Internal</c> methods are hard-gated on <c>CurrentSide == CombatSide.Player</c>,
/// ENGINE-NOTES.md §9), so there is nothing to route around — only native primitives to call
/// directly, mirroring <c>SetupPlayerTurn</c>/<c>FlushPlayerHand</c>'s own hook sequence exactly
/// (M2: relics and cards beyond the M1 starter set read these hooks). M1 resolves card damage
/// immediately and natively (ENGINE-NOTES.md §10); queueing is M4.
/// </summary>
internal static class GhostTurnController
{
    private const int MaxPlaysPerTurn = 40;
    private static readonly TimeSpan TurnBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Start-of-turn setup — mirrors <c>CombatManager.SetupPlayerTurn</c> (ENGINE-NOTES.md §9) so
    /// energy- and hand-size-modifying relics/powers read correctly instead of being silently
    /// skipped. <b>Must run from P13 (<c>EnginePatches.cs</c>), not from <see cref="RunTurnAsync"/>.</b>
    /// Confirmed live (2026-09-02): native <c>CombatManager.StartTurn</c> calls
    /// <c>Hook.AfterSideTurnStart</c> unconditionally for both sides (<c>CombatManager.cs:522</c>)
    /// *before* branching into the enemy-turn path that eventually reaches <c>Creature.TakeTurn</c>
    /// (P2, which is what used to invoke this method). For a human, the native per-player
    /// `SetupPlayerTurn` loop (`CombatManager.cs:509-519`) runs energy reset *before* line 522, so a
    /// turn-1 energy relic like <c>VeryHotCocoa</c> (`AfterSideTurnStart`, gated on
    /// `TurnNumber &lt;= 1`) grants its bonus on top of an already-reset pool. For the Ghost, that
    /// per-player loop is hard-gated to `CurrentSide == Player` and never runs at all — so nothing
    /// reset the Ghost's energy before line 522 fired, and when this method used to run afterward (via
    /// `Creature.TakeTurn`), its own `ResetEnergy()` call (`Energy = MaxEnergy`, absolute, not
    /// additive) silently discarded whatever `VeryHotCocoa` had just granted. Moving this whole
    /// sequence to run from a prefix on `Hook.AfterSideTurnStart` itself — before the original hook
    /// body executes, via a `HarmonyReversePatch` call to the true original — puts the Ghost's reset
    /// at the same point in the sequence a human's occupies, so `AfterSideTurnStart`-driven relics
    /// land their bonus after the reset like they do for a human.
    /// </summary>
    public static async Task SetupTurn(Player ghostPlayer, ICombatState combatState, GhostSession session)
    {
        PlayerCombatState playerState = ghostPlayer.PlayerCombatState
            ?? throw new InvalidOperationException("Ghost has no PlayerCombatState; turn cannot run.");
        PlayerChoiceContext ctx = new BlockingPlayerChoiceContext();

        // COMBAT-RULES.md §2: "the attack the Ghost queued last turn resolves against the human" is
        // the very first thing that happens on the Ghost's turn — before energy reset or hand draw.
        await session.DamageQueue.ResolvePendingFor(ghostPlayer.Creature, $"Ghost#{ghostPlayer.NetId}");

        if (Hook.ShouldPlayerResetEnergy(combatState, ghostPlayer))
        {
            playerState.ResetEnergy();
        }
        else
        {
            playerState.AddMaxEnergyToCurrent();
        }
        await Hook.AfterEnergyReset(combatState, ghostPlayer);
        await Hook.BeforeHandDraw(combatState, ghostPlayer, ctx);
        decimal handDraw = Hook.ModifyHandDraw(combatState, ghostPlayer, CombatManager.baseHandDrawCount, out _);
        var drawn = await CardPileCmd.Draw(ctx, handDraw, ghostPlayer, fromHandDraw: true);
        GhostLog.Hand($"Ghost#{ghostPlayer.NetId}", drawn.Count());
        await Hook.AfterPlayerTurnStart(combatState, ctx, ghostPlayer);

        // Confirmed live (2026-09-03, Defect): native only ever calls PlayerCombatState.OrbQueue
        // .AfterTurnStart/.BeforeTurnEnd from the CurrentSide == Player branches of CombatManager's
        // turn-start/turn-end methods (CombatManager.cs:526-538, EndPlayerTurnPhaseOneInternal via
        // DoTurnEnd) — the enemy-turn equivalent, EndEnemyTurnInternal, never calls either. Invisible
        // in vanilla (a Monster has no PlayerCombatState/OrbQueue at all); real for the Ghost, whose
        // orbs (Frost's end-of-turn Block, etc.) otherwise never trigger. No native call exists to
        // patch/redirect, so this is wired in directly, mirroring the native ordering as closely as
        // P13's own restructuring allows (native runs OrbQueue.AfterTurnStart after Hook
        // .AfterSideTurnStart finishes; P13 makes SetupTurn itself run before that hook's real body,
        // so this necessarily runs slightly earlier relative to it than a human's does — a known,
        // accepted ordering deviation, not exactly matching native).
        await playerState.OrbQueue.AfterTurnStart(ctx);
    }

    public static async Task RunTurnAsync(Player ghostPlayer, GhostSession session)
    {
        GhostPartyMember member = session.FindByPlayer(ghostPlayer)
            ?? throw new InvalidOperationException($"RunTurnAsync: {ghostPlayer.NetId} is not a member of this GhostSession's party.");
        int round = member.NextRound();
        string label = $"Ghost#{ghostPlayer.NetId}";
        GhostLog.TurnStart(label, round);
        member.Chooser.OnTurnStart();

        PlayerChoiceContext ctx = new BlockingPlayerChoiceContext();
        ICombatState combatState = ghostPlayer.Creature.CombatState
            ?? throw new InvalidOperationException("Ghost has no CombatState; turn cannot run.");
        PlayerCombatState playerState = ghostPlayer.PlayerCombatState
            ?? throw new InvalidOperationException("Ghost has no PlayerCombatState; turn cannot run.");

        // Scoped for the whole turn: some card effects (e.g. Armaments' base "pick 1 card to
        // upgrade") prompt for a card selection that has no real UI/network answer for the Ghost.
        // CardSelectCmd.Selector is the engine's own escape hatch for exactly this (used by tests and
        // AutoSlay) — pushed and popped narrowly, never left active outside this turn.
        using IDisposable cardSelectorScope = CardSelectCmd.PushSelector(new GhostCardSelector());

        // Setup (energy reset, hand draw) already ran from P13, before Hook.AfterSideTurnStart fired
        // — see SetupTurn's doc comment. Go straight to the play loop.

        Stopwatch clock = Stopwatch.StartNew();
        int plays = 0;
        while (plays < MaxPlaysPerTurn && clock.Elapsed < TurnBudget)
        {
            GhostTurnView view = GhostTurnView.Capture(ghostPlayer);
            GhostChoice? choice;
            if (session.ActionSync.IsHost)
            {
                // PLAN.md M9e: only the host ever runs a chooser. Every other client executes the
                // exact broadcast event instead (below) — never decides for itself.
                choice = member.Chooser.Choose(view);
                if (choice is null)
                {
                    session.ActionSync.BroadcastEndTurn(ghostPlayer);
                    break;
                }
                int handIndex = view.Hand.ToList().IndexOf(choice.Card);
                uint sequenceNumber = session.ActionSync.BroadcastCardPlay(ghostPlayer, choice, handIndex);
                // The host never receives its own broadcast back (NetHostGameService.SendMessage's
                // broadcast overload only reaches other peers), so its own GHOST-EXECUTE line is
                // logged here, immediately after deciding — not inside GhostActionSync, which only
                // knows about sending/receiving, not "this process is about to execute this now."
                GhostLog.GhostExecute(sequenceNumber, ghostPlayer.NetId, choice.Card.Id.Entry, choice.Target?.LogName);
            }
            else
            {
                // Revised 2026-09-08: pass the turn's own remaining wall-clock budget as an explicit
                // per-await timeout — see GhostActionSync.AwaitNext's own doc comment for why the old
                // no-timeout wait could hang forever on a single missed broadcast, immune to this loop's
                // own TurnBudget check (which only re-runs between iterations, never around one await).
                TimeSpan remaining = TurnBudget - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    GhostLog.Warn($"{label}: turn budget already exhausted before awaiting the next broadcast — ending turn.");
                    break;
                }
                choice = await session.ActionSync.AwaitNext(ghostPlayer, view, remaining);
                if (choice is null)
                {
                    break;
                }
            }

            CardModel card = choice.Card;
            string entry = card.Id.Entry;

            bool canPlay = card.CanPlay(out UnplayableReason reason, out _);
            GhostLog.Legal(label, entry, canPlay, reason.ToString());
            if (!canPlay || !card.IsValidTarget(choice.Target))
            {
                // LeftToRightChooser already filters on CanPlay/IsValidTarget (that's how unaffordable
                // cards get skipped mid-scan, per the M1 gate). This recheck exists only for a card
                // becoming unplayable between Choose() and here. Since nothing in that window changed
                // hand/energy state, re-choosing would very likely hand back the identical choice and
                // spin until the wall-clock guard trips — end the turn cleanly instead and log why.
                GhostLog.Warn($"{label}: {entry} failed recheck after being chosen — ending turn.");
                break;
            }

            (int energySpent, int starsSpent) = await card.SpendResources();
            GhostLog.Spent(label, entry, energySpent, starsSpent);

            ResourceInfo resources = new()
            {
                EnergySpent = energySpent,
                EnergyValue = energySpent,
                StarsSpent = starsSpent,
                StarValue = starsSpent,
            };
            await card.OnPlayWrapper(ctx, choice.Target, isAutoPlay: false, resources);
            GhostLog.Played(label, entry);
            LogState(label, entry, ghostPlayer, playerState);
            plays++;

            // Confirmed live (2026-09-02): without this, a kill mid-turn went unnoticed for the rest
            // of the loop — the Ghost played the same always-legal, zero-cost card ~40 times against
            // an already-dead target until MaxPlaysPerTurn tripped. Root cause, confirmed by reading
            // CombatManager.cs in full: CombatManager.Instance.IsInProgress only flips once
            // CheckWinCondition() actually runs and calls EndCombatInternal — nothing sets it as a
            // side effect of the kill itself. The native paths that call OnPlayWrapper for a human
            // (ActionExecutor.cs:170, after every completed GameAction) and for a real monster's own
            // turn (CombatManager.cs:1084, ExecuteEnemyTurn, once after TakeTurn() returns) both call
            // it; the monster path is coarse enough (a handful of fixed moves) that this is invisible
            // in vanilla, but our own play loop can run up to MaxPlaysPerTurn iterations, so it needs
            // the finer-grained, per-card check ExecuteEnemyTurn's own once-per-turn call can't give.
            if (await CombatManager.Instance.CheckWinCondition() || !CombatManager.Instance.IsInProgress)
            {
                GhostLog.TurnEnd(label, round);
                return;
            }
        }

        if (plays >= MaxPlaysPerTurn || clock.Elapsed >= TurnBudget)
        {
            GhostLog.Warn($"{label}: turn loop guard tripped (plays={plays}, elapsed={clock.Elapsed}) — ending turn.");
        }

        // Mirrors native CombatManager.DoTurnEnd (CombatManager.cs:1216-1246) at the Ghost's own turn
        // end. P22 (EnginePatches.cs) now skips DoTurnEnd entirely for the Ghost when it would
        // otherwise run — spuriously — during the human's own turn end (same playersEndingTurn bug
        // family as P9/P14, confirmed live as Defect's orb passives firing at the end of every turn
        // instead of just their owner's), so this replicates its full body here instead, not just the
        // OrbQueue call: Ethereal-exhaust and OnTurnEndInHandEffect cards need to see the hand before
        // FlushHand (below) discards it, matching native's own ordering (DoTurnEnd runs before the
        // flush phase in CombatManager.EndPlayerTurnPhaseOneInternal/Two).
        await DoTurnEnd(ghostPlayer, combatState, ctx);

        // Combat-rules rework (2026-09-04): the human's own damage now applies immediately (P17,
        // EnginePatches.cs), so there is nothing left to resolve here — removed the "human's queued
        // attack resolves against the Ghost's now-current Block" step (COMBAT-RULES.md, rewritten).
        if (await CombatManager.Instance.CheckWinCondition() || !CombatManager.Instance.IsInProgress)
        {
            GhostLog.TurnEnd(label, round);
            return;
        }

        await FlushHand(combatState, ghostPlayer, ctx, playerState);
        GhostLog.TurnEnd(label, round);
    }

    /// <summary>PLAN.md M2's exit gate: "per-card logs showing native values" — the opponent creature
    /// is found the same side-relative way <see cref="Choosers.LeftToRightChooser"/> resolves a
    /// target, not a stored reference, so this stays correct even if sides were ever re-derived.
    /// PLAN.md M9c: in a party fight this logs only the first opposing creature found, a known
    /// simplification for a diagnostic-only log line — not the queued-damage resolution path, which
    /// is exact (<c>GhostDamageQueue.ResolvePendingAgainst</c>).</summary>
    private static void LogState(string label, string cardEntry, Player ghostPlayer, PlayerCombatState playerState)
    {
        Creature ghost = ghostPlayer.Creature;
        Creature? opponent = ghost.CombatState?.PlayerCreatures.FirstOrDefault(c => c.Side != ghost.Side);
        GhostLog.State(label, cardEntry, ghost.CurrentHp, ghost.MaxHp, ghost.Block, playerState.Energy,
            opponent?.CurrentHp ?? -1, opponent?.Block ?? -1);
    }

    /// <summary>Mirrors native <c>CombatManager.DoTurnEnd</c> (<c>CombatManager.cs:1216-1246</c>)
    /// exactly, at the Ghost's own turn end — P22 (<c>EnginePatches.cs</c>) skips the native call
    /// entirely for the Ghost, since native only ever reaches it via a list that spuriously includes
    /// the Ghost during the *human's* own turn end (the same <c>playersEndingTurn</c> bug family as
    /// P9/P14). Orb passives first (confirmed live necessary — Defect's Frost Block), then
    /// Ethereal-exhaust and <c>HasTurnEndInHandEffect</c> cards still in hand, in that order, before
    /// <see cref="FlushHand"/> discards whatever remains.</summary>
    private static async Task DoTurnEnd(Player ghostPlayer, ICombatState combatState, PlayerChoiceContext ctx)
    {
        PlayerCombatState playerState = ghostPlayer.PlayerCombatState
            ?? throw new InvalidOperationException("Ghost has no PlayerCombatState; turn cannot end.");
        await playerState.OrbQueue.BeforeTurnEnd(ctx);
        if (CombatManager.Instance.IsOverOrEnding)
        {
            return;
        }
        CardPile hand = PileType.Hand.GetPile(ghostPlayer);
        List<CardModel> turnEndCards = new();
        List<CardModel> etherealCards = new();
        foreach (CardModel card in hand.Cards)
        {
            if (card.HasTurnEndInHandEffect)
            {
                turnEndCards.Add(card);
            }
            else if (card.Keywords.Contains(CardKeyword.Ethereal) && Hook.ShouldEtherealTrigger(combatState, card))
            {
                etherealCards.Add(card);
            }
        }
        foreach (CardModel card in etherealCards)
        {
            await CardCmd.Exhaust(ctx, card, causedByEthereal: true);
        }
        foreach (CardModel card in turnEndCards)
        {
            await card.OnTurnEndInHandWrapper(ctx);
        }
    }

    /// <summary>Mirrors <c>CombatManager.FlushPlayerHand</c> exactly (ENGINE-NOTES.md §9), including
    /// Retain — M2's card pool may include a Retain-granting card or power.</summary>
    private static async Task FlushHand(ICombatState combatState, Player ghostPlayer, PlayerChoiceContext ctx, PlayerCombatState playerState)
    {
        bool shouldFlush = Hook.ShouldFlush(combatState, ghostPlayer);
        List<CardModel> cardsToFlush = new();
        List<CardModel> cardsToRetain = new();
        foreach (CardModel card in playerState.Hand.Cards)
        {
            if (!shouldFlush || card.ShouldRetainThisTurn)
            {
                cardsToRetain.Add(card);
            }
            else
            {
                cardsToFlush.Add(card);
            }
        }
        if (cardsToFlush.Count > 0)
        {
            await CardPileCmd.Add(cardsToFlush, PileType.Discard);
        }
        await Hook.AfterFlush(combatState, ghostPlayer, ctx, cardsToFlush, cardsToRetain);
        playerState.EndOfTurnCleanup();
    }
}
