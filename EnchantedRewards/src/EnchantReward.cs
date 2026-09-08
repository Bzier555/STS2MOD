using System.Reflection;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace EnchantedRewards;

/// <summary>
/// Reward offered instead of CardReward after a normal (non-Elite, non-Boss) fight: pick a card
/// already in your deck and apply a specific, pre-rolled enchantment to it.
///
/// One EnchantReward represents a single enchantment choice. EnchantedRewardsModifier normally rolls
/// two of these (each a different enchantment type) and wraps them in a
/// MegaCrit.Sts2.Core.Rewards.LinkedRewardSet, the base game's own "N alternative rewards, taking one
/// discards the rest" mechanism (used natively via MegaCrit.Sts2.Core.Nodes.Rewards.NLinkedRewardSet)
/// - so no custom "pick an enchantment" UI needed to be built.
///
/// Modeled on MegaCrit.Sts2.Core.Rewards.SpecialCardReward: the "what" (which enchantment, which
/// candidate cards) is resolved up front by EnchantedRewardsModifier.TryModifyRewards, not rolled
/// lazily in Populate(), so there's always something valid to offer by the time this reward is shown.
///
/// The actual application (fresh slot-1 / same-type stack / different-type extra) is handled by
/// EnchantmentService.Apply - see EnchantedRewards_STS2_Mod_Spec.md Part 2.2/2.4.
/// </summary>
internal sealed class EnchantReward : Reward
{
    // Reward.SuccessfullySelected is `{ get; private set; }` - only Reward itself can assign it, so
    // a sibling subclass needs reflection to fix up the parent's flag (see the comment in OnSelect).
    private static readonly PropertyInfo SuccessfullySelectedProperty =
        typeof(Reward).GetProperty(nameof(SuccessfullySelected))!;

    private readonly int _amount;
    private readonly EnchantmentModel _enchantment;
    private readonly IReadOnlyList<CardModel> _candidates;
    private readonly PlayerChoiceSynchronizer _synchronizer;

    private bool _wasTaken;

    protected override RewardType RewardType => RewardType.None;

    public override int RewardsSetIndex => 5;

    // Short label under the icon - reuses the enchantment's own (already-localized) name instead of
    // adding a new loc table entry.
    public override LocString Description => _enchantment.Title;

    // The actual per-enchantment explanation (what the user asked for: "hovering over enchantment
    // options in the card rewards, the display text should read for the specific enchantment").
    // EnchantmentModel.HoverTips is the same hover tip content the card face itself shows once
    // enchanted (main HoverTip - title, DynamicDescription, icon - plus anything the enchantment
    // adds via its own ExtraHoverTips, e.g. Goopy's/RoyallyApproved's keyword explanations), so this
    // reward's tooltip matches exactly what the card will actually say once you've picked one.
    //
    // Can't use _enchantment.HoverTips directly: _enchantment is the shared *canonical* pool
    // instance (see EnchantmentPool's comment on why it must stay that way), whose Amount is never
    // set - reading its DynamicDescription would show "0" instead of the real amount this reward is
    // about to apply. Building a throwaway mutable preview clone with Amount actually set (then
    // RecalculateValues(), for any enchantment whose DynamicVars derive from Amount) mirrors exactly
    // what NDeckEnchantSelectScreen._Ready() already does for its own single-card preview.
    protected override IEnumerable<IHoverTip> ExtraHoverTips
    {
        get
        {
            EnchantmentModel preview = _enchantment.ToMutable();
            preview.Amount = _amount;
            preview.RecalculateValues();
            return preview.HoverTips;
        }
    }

    // So the two linked options actually look different on the reward screen.
    protected override string? IconPath => _enchantment.IconPath;

    public override bool IsPopulated => _candidates.Count > 0;

    public EnchantReward(
        EnchantmentModel enchantment,
        IReadOnlyList<CardModel> candidates,
        Player player,
        PlayerChoiceSynchronizer? synchronizer = null)
        : base(player)
    {
        _enchantment = enchantment;
        _candidates = candidates;
        _amount = EnchantmentPool.DefaultAmountFor(enchantment);
        _synchronizer = synchronizer ?? RunManager.Instance.PlayerChoiceSynchronizer;
    }

    public override void Populate()
    {
        // Fully resolved by the constructor; nothing to do.
    }

    protected override async Task<bool> OnSelect()
    {
        uint choiceId = _synchronizer.ReserveChoiceId(base.Player);
        CardModel? chosen;

        if (LocalContext.IsMe(base.Player))
        {
            CardSelectorPrefs prefs = new(CardSelectorPrefs.EnchantSelectionPrompt, 1)
            {
                Cancelable = false,
            };
            NDeckEnchantSelectScreen screen = NDeckEnchantSelectScreen.ShowScreen(_candidates, _enchantment, _amount, prefs);
            IEnumerable<CardModel> selected = await screen.CardsSelected();
            chosen = selected.FirstOrDefault();

            PlayerChoiceResult result = chosen != null
                ? PlayerChoiceResult.FromMutableDeckCard(chosen)
                : PlayerChoiceResult.FromMutableDeckCard(null);
            _synchronizer.SyncLocalChoice(base.Player, choiceId, result);
        }
        else
        {
            PlayerChoiceResult result = await _synchronizer.WaitForRemoteChoice(base.Player, choiceId);
            chosen = result.AsDeckCards().FirstOrDefault();
        }

        if (chosen == null)
        {
            return false;
        }

        EnchantmentService.Apply(chosen, _enchantment, _amount);
        _wasTaken = true;
        EnchantedRewardsMod.Logger.Info(
            $"Player {base.Player.NetId} enchanted {chosen.Id} with {_enchantment.Id}");

        if (base.ParentRewardSet != null)
        {
            // Bugfix: MegaCrit.Sts2.Core.Rewards.Reward.SelectUnsynchronized() calls
            // ParentRewardSet.OnSelect() directly (not through SelectUnsynchronized), so a
            // LinkedRewardSet is never itself marked SuccessfullySelected when one of its children
            // succeeds. RewardsSet.AllRewardsSuccessfullySelected - which both the reward screen's
            // "Skip" -> "Proceed" transition and the multiplayer backend's completion tracking
            // depend on - checks the *top-level* Rewards list, where the LinkedRewardSet (not this
            // child) is the entry. Without this fix the reward screen can be left thinking the
            // enchant choice is still pending even after one option was fully resolved.
            SuccessfullySelectedProperty.SetValue(base.ParentRewardSet, true);
        }

        return true;
    }

    public override void OnSkipped()
    {
        if (!_wasTaken)
        {
            EnchantedRewardsMod.Logger.Info($"Player {base.Player.NetId} skipped an enchant reward");
        }
    }

    public override void MarkContentAsSeen()
    {
    }
}
