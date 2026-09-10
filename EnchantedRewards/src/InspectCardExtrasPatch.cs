using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace EnchantedRewards;

/// <summary>
/// Fixes a card's extra enchantments showing wrong in the full-size inspect popup (distinct from the
/// enchant-select confirmation preview EnchantPreviewPatch fixes - a different screen, similar cause).
///
/// NInspectCardScreen.UpdateCardDisplay() builds its displayed card via `_cards[_index].MutableClone()`
/// - a third, independent card-cloning call site (distinct from combat cloning and from
/// NEnchantPreview's run-scope cloning). Like every mutable clone, AbstractModel.MutableClone()'s
/// AfterCloned() step unconditionally resets the new clone's DeckVersion to null, so
/// ExtraEnchantments.Get's DeckVersion-fallback lookup finds nothing on this clone.
///
/// First attempt at fixing this reattached the original's extras onto the clone correctly (confirmed
/// live via logging: ExtraEnchantments.Get(original) does find every extra), then only re-triggered
/// NCard's private UpdateEnchantmentVisuals (the enchantment-tab flag drawing) - deliberately narrow, to
/// avoid disturbing whichever of UpdateVisuals/ShowUpgradePreview native code had already chosen for
/// the current upgrade-preview toggle state. That undersold the actual problem: the card's *damage/
/// block number and description text* (e.g. "Deal 7 damage" instead of the correct combined total) are
/// computed by that native UpdateVisuals/ShowUpgradePreview call - which native code already ran
/// *before* this postfix attaches the extras at all. Only re-invoking the flag-drawing method afterward
/// left the flags looking right while the number/text stayed stale, reflecting a card with no extras -
/// exactly the reported "close-up looks like it only has the first enchant" symptom, while the deck's
/// own card-list view (which redraws after this postfix already ran, not before) showed correctly.
///
/// Fixed by re-invoking the *same* full visual-refresh call native code used for the current toggle
/// state (ShowUpgradePreview() if IsShowingUpgradedCard, else UpdateVisuals()) after the extras are
/// attached, mirroring how the in-combat card display already gets this right: recomputing everything -
/// flags, damage/block preview, description text - from a state where the extras are already visible,
/// rather than only patching up the one piece (flags) that was rendered wrong.
/// </summary>
[HarmonyPatch(typeof(NInspectCardScreen), "UpdateCardDisplay")]
internal static class InspectCardExtrasPatch
{
    private static readonly FieldInfo CardsField = AccessTools.Field(typeof(NInspectCardScreen), "_cards");
    private static readonly FieldInfo IndexField = AccessTools.Field(typeof(NInspectCardScreen), "_index");
    private static readonly FieldInfo CardNodeField = AccessTools.Field(typeof(NInspectCardScreen), "_card");
    private static readonly PropertyInfo IsShowingUpgradedCardProperty =
        AccessTools.Property(typeof(NInspectCardScreen), "IsShowingUpgradedCard");

    [HarmonyPostfix]
    private static void ShowExtrasOnInspectClone(NInspectCardScreen __instance)
    {
        if (CardsField.GetValue(__instance) is not List<CardModel> cards
            || IndexField.GetValue(__instance) is not int index
            || index < 0 || index >= cards.Count)
        {
            return;
        }

        CardModel original = cards[index];
        IReadOnlyList<EnchantmentModel> extras = ExtraEnchantments.Get(original);
        if (extras.Count == 0)
        {
            return;
        }

        if (CardNodeField.GetValue(__instance) is not NCard cardNode || cardNode.Model is not { } clone)
        {
            return;
        }

        foreach (EnchantmentModel extra in extras)
        {
            ExtraEnchantments.Add(clone, extra);
        }

        if (IsShowingUpgradedCardProperty.GetValue(__instance) is true)
        {
            cardNode.ShowUpgradePreview();
        }
        else
        {
            cardNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        }
    }
}
