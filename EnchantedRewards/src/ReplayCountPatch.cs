using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace EnchantedRewards;

/// <summary>
/// Fixes an extra Spiral's replay bonus not showing in a card's "Replay N" hover tip or description
/// text (reported: "when enchanting an already enchanted strike with spiral, replay 1 did not get
/// added to the card text").
///
/// CardModel.GetEnchantedReplayCount() only ever reads the card's native slot-1 Enchantment
/// (`Enchantment?.EnchantPlayCount(BaseReplayCount) ?? BaseReplayCount`), and is used two different
/// ways: (1) real gameplay - `GetEnchantedReplayCount() + 1` is passed into
/// `Hook.ModifyCardPlayCount`, which *does* correctly reach extras (that's the generic hook chain
/// EnchantedRewardsModifier's other overrides participate in); (2) display-only - the card's
/// "Replay N" hover tip and its description text both call GetEnchantedReplayCount() directly, with
/// no hook dispatch at all, so an extra Spiral's contribution was invisible there even though it was
/// mechanically working.
///
/// This postfixes GetEnchantedReplayCount() itself to add extras' contribution, which fixes both the
/// hover tip and the description text - but since case (1) above also flows through this same
/// method, EnchantedRewardsModifier deliberately does *not* have its own ModifyCardPlayCount
/// override anymore (removed - see its doc comment): adding extras' contribution in both places
/// would double it for actual gameplay. This is the only one of the two "text doesn't reflect
/// extras" bugs found this round where the fix could be folded into the single shared method instead
/// of needing a parallel patch like OutOfCombatPreviewPatch's - GetEnchantedReplayCount() has no
/// separate "is this a real resolution or just a preview" branch to conflict with.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetEnchantedReplayCount))]
internal static class ReplayCountPatch
{
    [HarmonyPostfix]
    private static void IncludeExtras(CardModel __instance, ref int __result)
    {
        foreach (EnchantmentModel extra in ExtraEnchantments.Get(__instance))
        {
            __result = extra.EnchantPlayCount(__result);
        }
    }
}
