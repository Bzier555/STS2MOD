using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GhostDuel.Ai.Cards;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using GhostDuel.Ghost.Choosers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace GhostDuel.Ai;

/// <summary>
/// M5's <see cref="IGhostCardChooser"/>, replaced 2026-09-04 per the user's (and their friend's)
/// <c>ghostlogic.txt</c> spec — a deliberately non-strategic, weighted-random imitation of a human
/// player, not the scored/planned "best line" approach the deleted <c>SemanticGhostChooser</c>/
/// <c>CombatActionScorer</c>/<c>CombatTurnLinePlanner</c> used. Every decision below is answered fresh
/// from the current hand — <c>GhostTurnController.RunTurnAsync</c> already re-derives
/// <see cref="GhostTurnView"/> and calls <see cref="Choose"/> again after *every* play, so
/// "recalculate everything after every card" (ghostlogic.txt §13) is satisfied by that existing
/// structure; this class holds no lookahead, only the one piece of state that must persist *within* a
/// turn: <see cref="_attackProbability"/>. Always has a deterministic native-legal fallback ready: any
/// exception falls back to <see cref="LeftToRightChooser"/>, which stays in the tree permanently as a
/// regression mode.
///
/// Score weights below (<see cref="ScoreAttack"/>/<see cref="ScoreSkill"/>) are a first-pass judgment
/// call, not user-configurable — deliberately no config file for this system (unlike the deleted
/// per-character <c>.aiconfig</c> schema), matching the "should feel simple" design goal. Retune the
/// constants directly if testing shows a category is picking obviously-wrong cards.
/// </summary>
internal sealed class WeightedRandomGhostChooser : IGhostCardChooser
{
    private const int StartingAttackProbability = 50;
    private const int ProbabilityStep = 15;
    private const int MinAttackProbability = 20;
    private const int MaxAttackProbability = 80;

    /// <summary>Bounded jitter added to a candidate's score before ranking within a category
    /// (ghostlogic.txt §11/§12: "usually choose a good card," not "the mathematically optimal" one).</summary>
    private const int ScoreJitter = 3;

    private readonly LeftToRightChooser _fallback = new();
    private int _attackProbability = StartingAttackProbability;

    /// <summary>ghostlogic.txt §1/§14: "the probabilities reset to 50/50 at the beginning of the next
    /// turn." Called once by <see cref="Ghost.GhostTurnController"/> at the start of each turn.</summary>
    public void OnTurnStart() => _attackProbability = StartingAttackProbability;

    public GhostChoice? Choose(GhostTurnView view)
    {
        try
        {
            return ChooseUnsafe(view);
        }
        catch (Exception ex)
        {
            GhostLog.Error("WeightedRandomGhostChooser: falling back to LeftToRightChooser after an unhandled exception.", ex);
            return _fallback.Choose(view);
        }
    }

    private GhostChoice? ChooseUnsafe(GhostTurnView view)
    {
        Creature ghostCreature = view.GhostPlayer.Creature;
        CombatSide ghostSide = ghostCreature.Side;
        ICombatState? combatState = ghostCreature.CombatState;
        if (combatState is null)
        {
            return _fallback.Choose(view);
        }

        CardResolver cardResolver = new(CardCatalogRepository.Shared, new CardDefinitionRepository(), new RunCardStateStore(), new CombatCardStateStore());
        List<PlayableCard> playable = new();
        foreach (CardModel card in view.Hand)
        {
            if (!card.CanPlay(out UnplayableReason _, out _))
            {
                continue;
            }
            Creature? target = ResolveTarget(card, combatState, ghostSide);
            if (!card.IsValidTarget(target))
            {
                continue;
            }
            string instanceId = RuntimeHelpers.GetHashCode(card).ToString();
            playable.Add(new PlayableCard(card, cardResolver.Resolve(card, instanceId), target));
        }

        if (playable.Count == 0)
        {
            return null;
        }

        // 1. Powers first (§3) — priority over Attack/Skill, and does not touch the drift.
        List<PlayableCard> powers = playable.Where(p => p.Resolved.Type == CardType.Power).ToList();
        if (powers.Count > 0)
        {
            return ToChoice(powers[Random.Shared.Next(powers.Count)]);
        }

        // 2. Spend Energy-costing cards before 0-cost ones (§4/§5) — "no currently playable
        // Energy-costing cards," not "Energy == 0," so leftover unspendable Energy still falls
        // through to the 0-cost pool correctly.
        List<PlayableCard> costly = playable.Where(p => p.Resolved.EffectiveCost > 0).ToList();
        List<PlayableCard> pool = costly.Count > 0 ? costly : playable;

        List<PlayableCard> attacks = pool.Where(p => p.Resolved.Type == CardType.Attack).ToList();
        List<PlayableCard> skills = pool.Where(p => p.Resolved.Type == CardType.Skill).ToList();

        if (attacks.Count == 0 && skills.Count == 0)
        {
            // Neither category playable in this pool (e.g. only a Curse/Status is somehow playable) —
            // no known deck instance of this; pick uniformly rather than force a category, and leave
            // the drift untouched since neither category was actually exercised.
            return ToChoice(pool[Random.Shared.Next(pool.Count)]);
        }

        // 3. Attack/Skill weighted choice (§9/§10) — a forced single-category pick still updates the
        // drift afterward, per §2's explicit "still updates as if it intentionally chose" rule.
        bool playAttack = attacks.Count > 0 && skills.Count > 0
            ? Random.Shared.Next(100) < _attackProbability
            : attacks.Count > 0;

        PlayableCard chosen = playAttack ? ChooseBest(attacks, ScoreAttack) : ChooseBest(skills, ScoreSkill);

        _attackProbability = playAttack
            ? Math.Max(MinAttackProbability, _attackProbability - ProbabilityStep)
            : Math.Min(MaxAttackProbability, _attackProbability + ProbabilityStep);

        return ToChoice(chosen);
    }

    /// <summary>§11/§12: rank by score plus a bounded jitter, so the pick is usually — not always —
    /// the top-scoring candidate.</summary>
    private static PlayableCard ChooseBest(List<PlayableCard> candidates, Func<ResolvedCardView, int> score) =>
        candidates
            .Select(candidate => (Candidate: candidate, Jittered: score(candidate.Resolved) + Random.Shared.Next(-ScoreJitter, ScoreJitter + 1)))
            .OrderByDescending(x => x.Jittered)
            .First()
            .Candidate;

    /// <summary>§11: higher damage first, bonus for Vulnerable/Weak/Poison/draw.</summary>
    private static int ScoreAttack(ResolvedCardView card) =>
        card.GetEstimatedDamage() * 2
        + card.GetEnemyVulnerableAmount() * 3
        + card.GetEnemyWeakAmount() * 3
        + card.GetAppliedPowerAmount("Poison") * 3
        + card.GetCardsDrawn() * 2;

    /// <summary>§12: more Block first, bonus for Strength/Dexterity/draw/Energy/debuffs.</summary>
    private static int ScoreSkill(ResolvedCardView card) =>
        card.GetEstimatedBlock() * 2
        + (card.GetSelfStrengthAmount() + card.GetSelfTemporaryStrengthAmount()
            + card.GetSelfDexterityAmount() + card.GetSelfTemporaryDexterityAmount()) * 3
        + card.GetEnemyWeakAmount() * 2
        + card.GetEnemyVulnerableAmount() * 2
        + card.GetCardsDrawn() * 2
        + card.GetEnergyGain() * 3;

    private static GhostChoice ToChoice(PlayableCard card) => new(card.Card, card.Target);

    /// <summary>Mirrors <see cref="LeftToRightChooser"/>'s own side-relative target resolution
    /// (random among valid candidates). Determinism note (COMBAT-RULES.md §11): safe only because a
    /// chooser ever runs host-side — see that class's matching note.</summary>
    private static Creature? ResolveTarget(CardModel card, ICombatState combatState, CombatSide ghostSide) =>
        card.TargetType switch
        {
            TargetType.AnyEnemy => PickRandom(combatState.PlayerCreatures?.Where(c => c.Side != ghostSide && c.IsAlive)),
            TargetType.AnyAlly => combatState.PlayerCreatures?.FirstOrDefault(c => c.Side == ghostSide && c.IsAlive),
            _ => null,
        };

    private static Creature? PickRandom(IEnumerable<Creature>? candidates)
    {
        List<Creature>? list = candidates?.ToList();
        return list is not { Count: > 0 } ? null : list[Random.Shared.Next(list.Count)];
    }

    private readonly record struct PlayableCard(CardModel Card, ResolvedCardView Resolved, Creature? Target);
}
