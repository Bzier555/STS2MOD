using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace RotatingDecks;

internal static class DeckRotationService
{
    internal sealed record Assignment(Player Receiver, Player Source, int CardCount);

    /// <summary>
    /// Rotates persistent deck contents without cloning or serializing cards.
    /// Player deck objects are get-only in STS2, so the cards themselves and
    /// their owner references are transferred between those permanent piles.
    /// </summary>
    public static IReadOnlyList<Assignment> Rotate(IReadOnlyList<Player> players)
    {
        ArgumentNullException.ThrowIfNull(players);

        if (players.Count == 0)
        {
            throw new InvalidOperationException("Cannot rotate an empty player list.");
        }

        ValidatePlayers(players);

        CardModel[][] oldDecks = players
            .Select(player => player.Deck.Cards.ToArray())
            .ToArray();

        ValidateDeckSnapshot(players, oldDecks);

        if (players.Count == 1)
        {
            return [new Assignment(players[0], players[0], oldDecks[0].Length)];
        }

        try
        {
            RemoveAllCards(players, oldDecks);
            ClearOwners(oldDecks);

            List<Assignment> assignments = new(players.Count);
            for (int receiverIndex = 0; receiverIndex < players.Count; receiverIndex++)
            {
                int sourceIndex = (receiverIndex - 1 + players.Count) % players.Count;
                Player receiver = players[receiverIndex];

                foreach (CardModel card in oldDecks[sourceIndex])
                {
                    card.Owner = receiver;
                    receiver.Deck.AddInternal(card, silent: true);
                }

                assignments.Add(new Assignment(receiver, players[sourceIndex], oldDecks[sourceIndex].Length));
            }

            ValidateAppliedRotation(players, oldDecks);
            NotifyDecksChanged(players);
            return assignments;
        }
        catch (Exception rotationError)
        {
            try
            {
                RestoreOriginalDecks(players, oldDecks);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Deck rotation failed and the original deck assignment could not be restored.",
                    rotationError,
                    rollbackError);
            }

            throw new InvalidOperationException(
                "Deck rotation failed; the original deck assignment was restored.",
                rotationError);
        }
    }

    private static void ValidatePlayers(IReadOnlyList<Player> players)
    {
        HashSet<ulong> netIds = [];
        foreach (Player player in players)
        {
            if (player is null)
            {
                throw new InvalidOperationException("The ordered player list contains a null player.");
            }

            if (!netIds.Add(player.NetId))
            {
                throw new InvalidOperationException($"Player net ID {player.NetId} appears more than once.");
            }
        }
    }

    private static void ValidateDeckSnapshot(IReadOnlyList<Player> players, IReadOnlyList<CardModel[]> oldDecks)
    {
        HashSet<CardModel> cards = new(ReferenceEqualityComparer.Instance);

        for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
        {
            Player expectedOwner = players[playerIndex];
            foreach (CardModel card in oldDecks[playerIndex])
            {
                if (!cards.Add(card))
                {
                    throw new InvalidOperationException(
                        $"The same persistent card instance ({card.Id}) occurs in more than one deck.");
                }

                if (!ReferenceEquals(card.Owner, expectedOwner))
                {
                    throw new InvalidOperationException(
                        $"Persistent card {card.Id} is in player {expectedOwner.NetId}'s deck but has a different owner.");
                }
            }
        }
    }

    private static void RemoveAllCards(IReadOnlyList<Player> players, IReadOnlyList<CardModel[]> oldDecks)
    {
        for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
        {
            foreach (CardModel card in oldDecks[playerIndex])
            {
                players[playerIndex].Deck.RemoveInternal(card, silent: true);
            }
        }
    }

    private static void ClearOwners(IEnumerable<CardModel[]> decks)
    {
        foreach (CardModel card in decks.SelectMany(deck => deck))
        {
            // CardModel deliberately requires the old owner to be cleared before
            // a different non-null owner can be assigned.
            card.Owner = null!;
        }
    }

    private static void ValidateAppliedRotation(IReadOnlyList<Player> players, IReadOnlyList<CardModel[]> oldDecks)
    {
        int expectedCardCount = oldDecks.Sum(deck => deck.Length);
        int actualCardCount = players.Sum(player => player.Deck.Cards.Count);
        if (expectedCardCount != actualCardCount)
        {
            throw new InvalidOperationException(
                $"Card count changed during rotation ({expectedCardCount} before, {actualCardCount} after)." );
        }

        HashSet<CardModel> actualCards = new(
            players.SelectMany(player => player.Deck.Cards),
            ReferenceEqualityComparer.Instance);
        if (actualCards.Count != expectedCardCount)
        {
            throw new InvalidOperationException("A persistent card was duplicated during deck rotation.");
        }

        foreach (CardModel expectedCard in oldDecks.SelectMany(deck => deck))
        {
            if (!actualCards.Contains(expectedCard))
            {
                throw new InvalidOperationException($"Persistent card {expectedCard.Id} was lost during deck rotation.");
            }
        }

        for (int receiverIndex = 0; receiverIndex < players.Count; receiverIndex++)
        {
            int sourceIndex = (receiverIndex - 1 + players.Count) % players.Count;
            Player receiver = players[receiverIndex];
            CardModel[] expectedDeck = oldDecks[sourceIndex];

            if (!receiver.Deck.Cards.SequenceEqual(expectedDeck, ReferenceEqualityComparer.Instance))
            {
                throw new InvalidOperationException(
                    $"Player {receiver.NetId} did not receive the exact expected deck contents.");
            }

            if (expectedDeck.Any(card => !ReferenceEquals(card.Owner, receiver)))
            {
                throw new InvalidOperationException(
                    $"At least one card transferred to player {receiver.NetId} has the wrong owner.");
            }
        }
    }

    private static void RestoreOriginalDecks(IReadOnlyList<Player> players, IReadOnlyList<CardModel[]> oldDecks)
    {
        foreach (Player player in players)
        {
            player.Deck.Clear(silent: true);
        }

        ClearOwners(oldDecks);

        for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
        {
            Player originalOwner = players[playerIndex];
            foreach (CardModel card in oldDecks[playerIndex])
            {
                card.Owner = originalOwner;
                originalOwner.Deck.AddInternal(card, silent: true);
            }
        }

        NotifyDecksChanged(players);
    }

    private static void NotifyDecksChanged(IEnumerable<Player> players)
    {
        foreach (Player player in players)
        {
            player.Deck.InvokeContentsChanged();
        }
    }
}
