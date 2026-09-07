using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace GhostDuel.Ghost.Choosers;

/// <summary>
/// Answers card-grid prompts (e.g. Armaments' base "pick 1 card in hand to upgrade",
/// <c>CardSelectCmd.FromHandForUpgrade</c>, <c>CardSelectCmd.cs:771-790</c>) deterministically from
/// stable option order, the same "leftmost" convention <see cref="LeftToRightChooser"/> already uses
/// for card play — confirmed live need for M2 (ARCHITECTURE.md §6 anticipated this seam;
/// <c>CardSelectCmd.Selector</c>, set via <c>CardSelectCmd.PushSelector</c>, is exactly the escape
/// hatch that skips the real (UI/network) selection path when non-null,
/// <c>CardSelectCmd.cs:787-790</c>). Scoped narrowly to the Ghost's own turn by
/// <see cref="GhostTurnController"/> — never left active outside it.
/// </summary>
internal sealed class GhostCardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
        Task.FromResult(options.Take(maxSelect));

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives) =>
        new() { card = options.FirstOrDefault()?.Card };
}
