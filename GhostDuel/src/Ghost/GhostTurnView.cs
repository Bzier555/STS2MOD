using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace GhostDuel.Ghost;

/// <summary>
/// Read-only snapshot of the Ghost's turn state handed to a chooser. Re-derived after every card
/// play (ARCHITECTURE.md §6) so a chooser always plans against live state.
/// </summary>
internal sealed record GhostTurnView(Player GhostPlayer, IReadOnlyList<CardModel> Hand)
{
    public static GhostTurnView Capture(Player ghostPlayer) =>
        new(ghostPlayer, ghostPlayer.PlayerCombatState!.Hand.Cards);
}

/// <summary>A chooser's decision: which card to play and at whom. <c>Target</c> is null for untargeted cards.</summary>
internal sealed record GhostChoice(CardModel Card, MegaCrit.Sts2.Core.Entities.Creatures.Creature? Target);
