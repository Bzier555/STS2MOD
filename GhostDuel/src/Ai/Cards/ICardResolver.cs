using MegaCrit.Sts2.Core.Models;

namespace GhostDuel.Ai.Cards;

internal interface ICardResolver
{
    ResolvedCardView Resolve(CardModel liveCard, string cardInstanceId);
}
