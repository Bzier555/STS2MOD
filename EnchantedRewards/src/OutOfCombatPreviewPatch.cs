using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace EnchantedRewards;

/// <summary>
/// Fixes extra enchantments being invisible in a card's printed "Deal X damage"/"Gain X block" text
/// whenever the card isn't currently in your hand/play during an active combat (e.g. the deck menu,
/// or any other out-of-combat card view).
///
/// Root cause: CardModel.UpdateDynamicVarPreview only sets `runGlobalHooks = true` (which is what
/// makes DamageVar/BlockVar.UpdateCardPreview call Hook.ModifyDamage/ModifyBlock - the generic hook
/// chain our extra enchantments participate in) when CombatState != null AND the card is in the
/// Hand or Play pile. Everywhere else, DamageVar/BlockVar.UpdateCardPreview only ever apply
/// `card.Enchantment`'s (i.e. slot-1's) contribution directly, never consulting the hook chain at
/// all. This isn't a bug our mod introduced: vanilla cards only ever have one enchantment, so there
/// was never a reason for the out-of-combat preview path to look any further than the native slot.
/// It only becomes visible now that a card can carry enchantments the native slot doesn't know
/// about.
///
/// This does NOT affect actual combat resolution (in-hand/in-play damage and block are computed via
/// the exact same Hook.ModifyDamage/ModifyBlock our EnchantedRewardsModifier overrides already
/// handle correctly) - only what the card's *text* says when you're not actively fighting with it.
/// </summary>
[HarmonyPatch(typeof(DamageVar), nameof(DamageVar.UpdateCardPreview))]
internal static class OutOfCombatDamagePreviewPatch
{
    [HarmonyPostfix]
    private static void IncludeExtraEnchantments(DamageVar __instance, CardModel card, bool runGlobalHooks)
    {
        if (runGlobalHooks)
        {
            // The native path already ran Hook.ModifyDamage, which already covers extras via
            // EnchantedRewardsModifier's ModifyDamageAdditive/Multiplicative overrides.
            return;
        }

        OutOfCombatPreviewHelper.ApplyExtraDamageOrBlock(
            __instance,
            card,
            (extra, running) => extra.EnchantDamageAdditive(running, __instance.Props),
            (extra, running) => extra.EnchantDamageMultiplicative(running, __instance.Props));
    }
}

[HarmonyPatch(typeof(BlockVar), nameof(BlockVar.UpdateCardPreview))]
internal static class OutOfCombatBlockPreviewPatch
{
    [HarmonyPostfix]
    private static void IncludeExtraEnchantments(BlockVar __instance, CardModel card, bool runGlobalHooks)
    {
        if (runGlobalHooks)
        {
            return;
        }

        OutOfCombatPreviewHelper.ApplyExtraDamageOrBlock(
            __instance,
            card,
            (extra, running) => extra.EnchantBlockAdditive(running),
            (extra, running) => extra.EnchantBlockMultiplicative(running));
    }
}

internal static class OutOfCombatPreviewHelper
{
    public static void ApplyExtraDamageOrBlock(
        DynamicVar var,
        CardModel card,
        Func<EnchantmentModel, decimal, decimal> additive,
        Func<EnchantmentModel, decimal, decimal> multiplicative)
    {
        IReadOnlyList<EnchantmentModel> extras = ExtraEnchantments.Get(card);
        if (extras.Count == 0)
        {
            return;
        }

        // Mirrors Hook.ModifyDamage/ModifyBlock's own combination order: everyone's additive
        // contribution first, then everyone's multiplicative contribution, matching what the
        // in-combat path already does for these same extras via EnchantedRewardsModifier.
        decimal running = var.PreviewValue;
        foreach (EnchantmentModel extra in extras)
        {
            running += additive(extra, running);
        }

        foreach (EnchantmentModel extra in extras)
        {
            running *= multiplicative(extra, running);
        }

        running = Math.Max(0m, running);
        var.PreviewValue = running;
        if (!card.IsEnchantmentPreview)
        {
            var.EnchantedValue = running;
        }
    }
}
