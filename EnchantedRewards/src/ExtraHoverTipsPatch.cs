using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace EnchantedRewards;

/// <summary>
/// A multi-enchanted card's hover-tip panel (the boxes explaining each keyword/enchantment - shown by
/// the inspect close-up, and anywhere else in the game that reads a card's HoverTips) only ever
/// explained the native slot-1 enchantment, never any extras.
///
/// Root cause: CardModel.HoverTips (a property, not a plain method) builds its list from
/// ExtraHoverTips, then `if (Enchantment != null) list.AddRange(Enchantment.HoverTips);` (native slot
/// only) and Affliction's - the same native blind spot as everywhere else in this mod: the base game
/// only ever asks a card's one native slot for something extras also need to contribute.
///
/// Postfixes the property getter to append each extra's own HoverTips too, so every enchantment on the
/// card gets its own explanatory box, not just whichever one happened to land in the native slot.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.HoverTips), MethodType.Getter)]
internal static class ExtraHoverTipsPatch
{
    [HarmonyPostfix]
    private static void AppendExtraHoverTips(CardModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        IReadOnlyList<EnchantmentModel> extras = ExtraEnchantments.Get(__instance);
        if (extras.Count == 0)
        {
            return;
        }

        List<IHoverTip> combined = __result.ToList();
        foreach (EnchantmentModel extra in extras)
        {
            combined.AddRange(extra.HoverTips);
        }

        __result = combined;
    }
}
