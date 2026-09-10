using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace EnchantedRewards;

/// <summary>
/// Makes Inky's own effect actually scale with `Amount`, so a repeated Inky (merged into the existing
/// instance by EnchantmentService.Apply - see its own doc comment) applies proportionally more Weak and
/// bonus damage, instead of leaving the amount hardcoded at native Inky's fixed literal.
///
/// Native Inky (decompiled): its `CanonicalVars` are `new DamageVar(1m, ...)` and
/// `new PowerVar&lt;WeakPower&gt;(1m)` - fixed literals, never read from `Amount` at all. Both
/// `EnchantDamageAdditive` (the damage-bonus hook) and `OnPlay` (which calls
/// `PowerCmd.Apply&lt;WeakPower&gt;(..., base.DynamicVars.Weak.BaseValue, ...)`) read those same
/// `DynamicVars`, so making the vars themselves track `Amount` fixes both consumers - and the display
/// text (`DynamicExtraCardText`, which substitutes `DynamicVars` into Inky's localized template) - all
/// at once, with nowhere left needing its own separate fix.
///
/// `EnchantmentModel.RecalculateValues()` is `public virtual` with a no-op base body, and Inky doesn't
/// override it - so a postfix on the *base* method still runs for an Inky instance (virtual dispatch
/// with no override just calls the base implementation) and is exactly where every other consumer
/// already expects derived values to be (re)computed: EnchantmentService.Apply's ModifyCard() call
/// path, and NDeckEnchantSelectScreen's own reward-preview code, both already call RecalculateValues()
/// at the right moments without this mod needing to add any new call sites of its own.
/// </summary>
[HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.RecalculateValues))]
internal static class InkyStackingPatch
{
    [HarmonyPostfix]
    private static void ScaleWithAmount(EnchantmentModel __instance)
    {
        if (__instance is not Inky)
        {
            return;
        }

        __instance.DynamicVars.Damage.BaseValue = __instance.Amount;
        __instance.DynamicVars.Weak.BaseValue = __instance.Amount;
    }
}
