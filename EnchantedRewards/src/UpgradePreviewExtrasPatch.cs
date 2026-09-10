using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace EnchantedRewards;

/// <summary>
/// Fixes the "after" (upgraded) side of the upgrade-preview screen (NUpgradePreview - used at Rest
/// Site, Armaments, and anywhere else a card gets upgraded) silently dropping every extra enchantment
/// a card carries - same root cause as the enchant-select preview bug EnchantPreviewPatch fixes, a
/// different native screen hitting the identical gap.
///
/// NUpgradePreview.Reload builds its "after" card via `Card.CardScope.CloneCard(Card)` (confirmed by
/// decompile - either RunState.CloneCard or CombatState.CloneCard, same ambiguity EnchantPreviewPatch's
/// own comment explains), and this mod's ExtraEnchantments side table has no idea that clone exists
/// unless something reattaches them onto it. Reuses the exact same CloneCard postfixes
/// EnchantPreviewPatch already installs (EnchantPreviewRunStateCloneCapturePatch /
/// EnchantPreviewCombatStateCloneCapturePatch) rather than duplicating that "which CloneCard ran"
/// uncertainty a second time - this patch only needs to capture a *different* source card
/// (NUpgradePreview's own, not NEnchantPreview's) right before Reload's CloneCard call runs.
///
/// Unlike the enchant-select case, nothing here calls EnchantInternal afterward (Reload only ever
/// calls UpgradeInternal) - so the clone's native slot-1 enchantment, already correctly cloned by
/// CardModel.DeepCloneFields, must be left alone; only the extras need copying over.
/// EnchantPreviewCloneCaptureShared.NeedsNativeSlotSwap=false is what tells the shared
/// ReattachOntoClone helper to skip the enchant-preview-only "stash former native as an extra" step,
/// which would otherwise duplicate the native enchantment onto this clone as a bogus extra (shown as a
/// false "x2" stack by CardEnchantmentVisualsPatch).
/// </summary>
[HarmonyPatch(typeof(NUpgradePreview), "Reload")]
internal static class UpgradePreviewExtrasPatch
{
    [HarmonyPrefix]
    private static void CaptureOriginalCard(NUpgradePreview __instance)
    {
        EnchantPreviewCloneCaptureShared.OriginalCardBeingPreviewed = __instance.Card;
        EnchantPreviewCloneCaptureShared.NeedsNativeSlotSwap = false;
    }
}
