using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs.History;

namespace EnchantedRewards;

/// <summary>
/// Applies a rolled enchantment to a chosen card. Two cases:
///
/// 1. The card has no enchantment at all yet: goes into the card's native slot-1 Enchantment, for
///    maximum compatibility with everything that reads it. Does *not* go through CardCmd.Enchant -
///    that would re-validate with the real EnchantmentModel.CanEnchant, which enforces restrictions
///    (e.g. Goopy's native "must have the Defend tag", RoyallyApproved's native "must be Attack or
///    Skill") this mod deliberately relaxes (see EnchantmentPool.MeetsRestrictions) - so it's
///    inlined here instead, minus that gate.
/// 2. The card already has *any* enchantment (slot-1 or an extra, whether the same type being
///    applied now or a different one): always added as a brand new, independent extra via
///    ExtraEnchantments - never merged into an existing instance's Amount, even for a repeat of the
///    same type. This is deliberate: most enchantments don't even read Amount for their effect
///    (Instinct's multiplier and Spiral's replay bonus are both fixed constants), so merging into
///    Amount would silently do nothing for them. Treating every application as its own independent
///    instance means each one contributes its own share through the normal per-extra hook dispatch -
///    two Instinct copies each independently contribute their own 2x multiplicative factor (compounds
///    to 4x total), two Spiral copies each independently contribute their own +1 replay (adds to
///    +2 total) - with no per-enchantment-type special-casing needed anywhere in this method.
///    EnchantmentPool.BenefitsFromRepeatApplication is what decides whether repeating a type is even
///    offered as a reward in the first place.
///
/// Combat-time hooks for extras are wired up by EnchantedRewardsModifier (the math ones: damage/
/// block/play-count/OnPlay) and by the ModHelper.SubscribeForCombatStateHooks registration in
/// EnchantedRewardsMod (the generic AbstractModel ones: ModifyShuffleOrder, AfterCardDrawn, etc.) -
/// see ExtraEnchantments and EnchantedRewards_STS2_Mod_Spec.md Part 1.3/2.4.
/// </summary>
internal static class EnchantmentService
{
    /// <param name="card">Card to enchant.</param>
    /// <param name="enchantment">
    /// Always the shared *canonical* instance from EnchantmentPool (never already-mutated - see the
    /// comment there for why that matters). Both cases need a mutable instance, so each clones one
    /// here, right before it's actually attached to a card.
    /// </param>
    /// <param name="amount">Amount to apply (see EnchantmentPool.DefaultAmountFor).</param>
    public static void Apply(CardModel card, EnchantmentModel enchantment, int amount)
    {
        if (card.Enchantment == null)
        {
            EnchantmentModel mutable = enchantment.ToMutable();
            card.EnchantInternal(mutable, amount);
            mutable.ModifyCard();
            LogHistory(card, enchantment);
            return;
        }

        EnchantmentModel extra = enchantment.ToMutable();
        extra.ApplyInternal(card, amount);
        extra.ModifyCard();
        ExtraEnchantments.Add(card, extra);
        LogHistory(card, enchantment);
    }

    private static void LogHistory(CardModel card, EnchantmentModel enchantment)
    {
        if (card.Owner != null)
        {
            card.Owner.RunState.CurrentMapPointHistoryEntry?.GetEntry(card.Owner.NetId).CardsEnchanted
                .Add(new CardEnchantmentHistoryEntry(card, enchantment.Id));
        }
    }
}
