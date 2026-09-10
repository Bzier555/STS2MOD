using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;

namespace EnchantedRewards;

/// <summary>
/// Fixes a real, live-confirmed bug: buying the Royal Stamp relic (any native relic/event that grants
/// an enchantment through `CardCmd.Enchant`, not just this one) let the player select an
/// already-enchanted card - since Round 8's CanEnchantIgnoreExistingPatch made `CanEnchant` stop
/// blocking those - but then silently failed to actually enchant it, while gold for the purchase had
/// already been deducted and the relic ended up back in the shop, purchasable again.
///
/// Root cause, confirmed by decompile: `CardCmd.Enchant(EnchantmentModel, CardModel, decimal)` has its
/// *own*, separate "only one enchantment per card" assumption, entirely independent of
/// `EnchantmentModel.CanEnchant`:
///     if (!enchantment.CanEnchant(card)) throw ...;
///     if (card.Enchantment == null) { card.EnchantInternal(enchantment, amount); enchantment.ModifyCard(); }
///     else
///     {
///         if (card.Enchantment.GetType() != enchantment.GetType())
///             throw new InvalidOperationException($"Cannot enchant {card.Id} with {enchantment.Id} because it already has enchantment {card.Enchantment.Id}.");
///         card.Enchantment.Amount += (int)amount;
///     }
/// Before Round 8, the candidate-card list every native enchant-granting source builds (e.g.
/// RoyalStamp.AfterObtained filters the deck through `royalStamp.CanEnchant(c)`) already excluded
/// already-enchanted cards, so this second, harder-coded check inside CardCmd.Enchant itself was never
/// actually reachable - CanEnchant's own blanket "already has an enchantment" gate always caught it
/// first, everywhere. Relaxing that gate (deliberately, at the user's explicit request) let a player
/// select an already-(differently-)enchanted card for the first time ever, and CardCmd.Enchant's own
/// separate guard - never exercised before, never touched by that patch - throws an unhandled
/// `InvalidOperationException` mid-way through the relic's `AfterObtained()` (an async method), which
/// interrupts it before it can finish granting the relic itself: hence gold spent, no enchant applied,
/// relic returned to the shop.
///
/// Fixed with a prefix that only changes behavior for the exact case CardCmd.Enchant itself would
/// otherwise throw on (card already has a *different*-typed enchantment) - the "no enchantment yet" and
/// "same type, merge into Amount" branches are left completely untouched, since native code already
/// handles both correctly. For the previously-throwing case, this mod's own multi-enchant machinery
/// takes over instead: if the enchantment being applied is one of EnchantmentService's own
/// MergeableTypes (Inky, Momentum) and the card already has one (as an extra - the "already has this
/// exact type" native-slot case can't reach here, that's the same-type branch above), it merges into
/// the existing instance's Amount, exactly like this mod's own reward flow does. Otherwise it's applied
/// directly as a brand new extra via EnchantmentService.ApplyAsNewExtra (the enchantment is already a
/// mutable instance by this point - the generic `CardCmd.Enchant&lt;T&gt;()` overload calls
/// `.ToMutable()` before reaching this one - so this calls the mutable-instance overload, not
/// `EnchantmentService.Apply` itself, which expects a *canonical* instance and would assert-fail being
/// handed an already-mutable one).
/// </summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), typeof(EnchantmentModel), typeof(CardModel), typeof(decimal))]
internal static class NativeEnchantMultiSlotPatch
{
    [HarmonyPrefix]
    private static bool AllowDifferentTypeAsExtra(
        EnchantmentModel enchantment, CardModel card, decimal amount, ref EnchantmentModel? __result)
    {
        if (card.Enchantment == null || card.Enchantment.GetType() == enchantment.GetType())
        {
            // Native behavior is already correct here: a fresh application, or a same-type repeat
            // that should merge into the existing instance's Amount - let the original method run.
            return true;
        }

        if (!enchantment.CanEnchant(card))
        {
            // Some other, genuinely functional restriction (wrong card type, Status/Curse/Quest,
            // Unplayable-in-deck, etc.) - preserve native's own throwing behavior, reached the same
            // way native code would reach it.
            return true;
        }

        int intAmount = (int)amount;

        if (EnchantmentService.MergeableTypes.Contains(enchantment.GetType()))
        {
            EnchantmentModel? existing = ExtraEnchantments.FindOnCard(card, enchantment.GetType());
            if (existing != null)
            {
                existing.Amount += intAmount;
                existing.ModifyCard();
                card.FinalizeUpgradeInternal();
                EnchantmentService.LogHistory(card, enchantment);
                __result = existing;
                return false;
            }
        }

        EnchantmentService.ApplyAsNewExtra(card, enchantment, intAmount);
        card.FinalizeUpgradeInternal();
        EnchantmentService.LogHistory(card, enchantment);

        __result = enchantment;
        return false;
    }
}
