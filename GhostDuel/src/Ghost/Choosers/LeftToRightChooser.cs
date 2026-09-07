using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace GhostDuel.Ghost.Choosers;

/// <summary>
/// M1's only chooser: the first hand card, left to right, that passes <c>CanPlay</c> and
/// <c>IsValidTarget</c>. Deterministic, no scoring. Permanently retained as a regression mode
/// (ARCHITECTURE.md §6) once M5 adds a real planner.
///
/// PLAN.md M9c: with 2+ valid enemy creatures (a party fight), <c>AnyEnemy</c> now resolves uniformly
/// at random among them rather than always the first — per the user's explicit v1 design ("to start
/// let us make this a random selection"). Unchanged when exactly one valid enemy exists (the original
/// 1v1 case): <c>Random.Shared.Next(int)</c> called with a one-element list always returns index 0.
/// Determinism note (COMBAT-RULES.md §11, "the host is authoritative... no local client
/// randomness"): safe as-is only because a chooser ever runs host-side (M9e gates client execution to
/// replaying a broadcast event, never invoking a chooser locally) — revisit if that ever changes.
/// </summary>
internal sealed class LeftToRightChooser : IGhostCardChooser
{
    public void OnTurnStart()
    {
        // Stateless — nothing to reset.
    }

    public GhostChoice? Choose(GhostTurnView view)
    {
        foreach (CardModel card in view.Hand)
        {
            if (!card.CanPlay(out UnplayableReason _, out _))
            {
                continue;
            }

            Creature? target = ResolveTarget(view, card);
            if (!card.IsValidTarget(target))
            {
                continue;
            }

            return new GhostChoice(card, target);
        }
        return null;
    }

    private static Creature? ResolveTarget(GhostTurnView view, CardModel card)
    {
        CombatSide ghostSide = view.GhostPlayer.Creature.Side;
        return card.TargetType switch
        {
            TargetType.AnyEnemy => PickRandom(view.GhostPlayer.Creature.CombatState?.PlayerCreatures
                .Where(c => c.Side != ghostSide && c.IsAlive)),
            TargetType.AnyAlly => view.GhostPlayer.Creature.CombatState?.PlayerCreatures
                .FirstOrDefault(c => c.Side == ghostSide && c.IsAlive && c != view.GhostPlayer.Creature),
            _ => null,
        };
    }

    private static Creature? PickRandom(IEnumerable<Creature>? candidates)
    {
        List<Creature>? list = candidates?.ToList();
        return list is not { Count: > 0 } ? null : list[Random.Shared.Next(list.Count)];
    }
}
