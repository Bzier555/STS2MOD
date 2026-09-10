using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace EnchantedRewards;

/// <summary>
/// One-time correction for saves made while Sown/Swift were briefly (and wrongly) in
/// EnchantmentService.MergeableTypes: a second application of either would merge its Amount into the
/// existing instance instead of creating an independent copy, exactly like Inky/Momentum still do
/// today - reverted once decompile confirmed (see EnchantmentService's own doc comment) that both
/// permanently disable after their first play with nothing ever resetting Status back to Normal, so a
/// merged instance that had already fired absorbed a repeat application's entire benefit and then went
/// permanently inert (and its card text went blank too - EnchantmentModel.DynamicExtraCardText returns
/// null once Status is Disabled). Live-confirmed via a diagnostic log: an existing save had a Sown
/// extra sitting at Amount=2 from exactly this - a single serialized instance, restored as-is on every
/// load, forever - not a live bug in current code, but stale data baked in during that window.
///
/// Both Sown and Swift default to a plain Amount of 1 per application
/// (EnchantmentPool.DefaultAmountFor's fallback - neither has its own entry in DefaultAmounts), so an
/// Amount greater than 1 on either unambiguously represents that many independent 1-Amount
/// applications having been wrongly merged - there's no other way either could have gotten there.
/// Splitting back into that many Amount=1 instances exactly undoes the merge, matching what would have
/// happened all along had the bug never shipped. (Vigorous shared the same Status bug and was pulled
/// from MergeableTypes at the same time, but its default Amount is 8, not 1 - an observed Amount could
/// only be unambiguously split if it happens to be an exact multiple of 8, and no live report has
/// actually shown a merged Vigorous yet, so it's deliberately left alone here rather than guessing.)
///
/// Covers both places a card's enchantments come back from a save: extras, via
/// ExtraEnchantmentSave.Set (this mod's own restore path), and the native slot-1 enchantment, via this
/// file's own postfix on the native CardModel.FromSerializable (the game's own restore path, since a
/// merge could just as easily have landed in the native slot if Sown/Swift happened to be the first
/// enchantment ever applied to that card). Both call into the same SplitIfNeeded, so the two paths
/// can't drift out of sync on which types/thresholds count as a legacy merge.
///
/// Self-limiting: once a save has been loaded and corrected once, every instance is back to Amount=1,
/// so SplitIfNeeded becomes a no-op for it on every subsequent load - safe to leave in permanently
/// rather than needing to be removed after some migration window.
/// </summary>
internal static class LegacyMergedStackFix
{
    private static readonly HashSet<Type> LegacyNeverMergeTypes = new()
    {
        typeof(Sown),
        typeof(Swift),
    };

    /// <summary>
    /// How many independent Amount=1 instances a freshly-restored (mutable, not yet Card-attached)
    /// enchantment instance should actually become: 1 (itself, unchanged) if nothing needs splitting,
    /// or its current Amount if it's a legacy-merged Sown/Swift.
    /// </summary>
    internal static int SplitCountFor(EnchantmentModel restored)
    {
        if (!LegacyNeverMergeTypes.Contains(restored.GetType()) || restored.Amount <= 1)
        {
            return 1;
        }

        EnchantedRewardsMod.Logger.Info(
            $"LegacyMergedStackFix: splitting a legacy-merged {restored.GetType().Name} " +
            $"(Amount={restored.Amount}) into {restored.Amount} independent Amount=1 instances.");
        return restored.Amount;
    }

    /// <summary>
    /// Builds `count - 1` additional fresh Amount=1 instances of the same type as `template` (which
    /// itself becomes/stays the first of the `count` - callers keep using their own already-existing
    /// instance for that one rather than getting a redundant copy back here).
    /// </summary>
    internal static IReadOnlyList<EnchantmentModel> BuildAdditionalCopies(EnchantmentModel template, int count)
    {
        SerializableEnchantment serialized = template.ToSerializable();
        serialized.Amount = 1;

        List<EnchantmentModel> result = new(count - 1);
        for (int i = 1; i < count; i++)
        {
            result.Add(EnchantmentModel.FromSerializable(serialized));
        }

        return result;
    }
}

/// <summary>
/// Native-slot half of LegacyMergedStackFix - see that class's own doc comment. Runs after the game's
/// own CardModel.FromSerializable has already attached whatever ended up in the native slot; if it's a
/// legacy-merged Sown/Swift, demotes the native slot back to a single Amount=1 instance and adds the
/// rest as independent extras (matching how a fresh repeat application would land today - the first
/// copy stays wherever it already was, every additional one is an extra).
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
internal static class LegacyMergedNativeStackSplitPatch
{
    [HarmonyPostfix]
    private static void SplitIfLegacyMerged(CardModel __result)
    {
        EnchantmentModel? native = __result.Enchantment;
        if (native == null)
        {
            return;
        }

        int count = LegacyMergedStackFix.SplitCountFor(native);
        if (count <= 1)
        {
            return;
        }

        IReadOnlyList<EnchantmentModel> additionalCopies = LegacyMergedStackFix.BuildAdditionalCopies(native, count);
        native.Amount = 1;
        foreach (EnchantmentModel extra in additionalCopies)
        {
            extra.ApplyInternal(__result, extra.Amount);
            extra.ModifyCard();
            ExtraEnchantments.Add(__result, extra);
        }
    }
}
