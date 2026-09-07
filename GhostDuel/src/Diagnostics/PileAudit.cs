using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace GhostDuel.Diagnostics;

/// <summary>
/// Temporary diagnostic: logs every card that enters or leaves any pile (Deck + all combat piles) for
/// a given player, so a reported card-count discrepancy can be traced to the exact move that caused
/// it instead of guessed at. Pure native event subscription — no patch, no gameplay effect. Attach for
/// both the human and the Ghost; a real bug should show up as an asymmetry between the two logs.
/// </summary>
internal static class PileAudit
{
    public static void AttachAll(Player player, string label)
    {
        Attach(player.Deck, label, "Deck");
        var combatState = player.PlayerCombatState;
        if (combatState is null)
        {
            GhostLog.Warn($"PileAudit: {label} has no PlayerCombatState yet; combat piles not attached.");
            return;
        }
        foreach (CardPile pile in combatState.AllPiles)
        {
            Attach(pile, label, pile.Type.ToString());
        }
    }

    private static void Attach(CardPile pile, string label, string pileName)
    {
        pile.CardAdded += card => GhostLog.Info($"PILE {label}: +{card.Id.Entry} -> {pileName} (count={pile.Cards.Count})");
        pile.CardRemoved += card => GhostLog.Info($"PILE {label}: -{card.Id.Entry} <- {pileName} (count={pile.Cards.Count})");
    }
}
