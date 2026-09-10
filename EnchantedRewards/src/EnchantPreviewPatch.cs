using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace EnchantedRewards;

/// <summary>
/// Fixes the "after" side of the enchant-select screen's confirmation preview (NEnchantPreview, shown
/// by NDeckEnchantSelectScreen when you click a card) silently dropping every enchantment the card
/// already had, showing only the one about to be applied.
///
/// Root cause is native, not ours - two separate gaps in NEnchantPreview.Init, confirmed by repeated
/// decompile:
///   1. It builds the "after" card via `card.CardScope.CloneCard(card)`. `CardScope` resolves to
///      `((ICardScope)CombatState) ?? ((ICardScope)(_owner?.Creature.CombatState)) ?? RunState` - so
///      depending on whether the card (or its owner) still has a CombatState attached at the moment
///      this reward screen is shown (plausible right after a fight ends, before it's cleared), the
///      clone could come from *either* CombatState.CloneCard or RunState.CloneCard - both have the
///      identical body (`ClonePreservingMutability()` then `AddCard`), but they're two distinct
///      methods, so only patching one leaves the other's callers with an unfixed clone. Either way,
///      the clone's `DeckVersion` ends up null (AbstractModel.MutableClone's own AfterCloned() always
///      resets it - only CombatState.CloneCard's *own separate combat-card-population path*, a
///      different call entirely from this one, sets it back afterward), so
///      ExtraEnchantments.Get's DeckVersion-fallback finds nothing on this specific clone regardless.
///   2. Init then calls `cardModel.EnchantInternal(enchantmentModel, amount)` on that same clone for
///      the *new* enchantment being previewed, which unconditionally does `Enchantment = enchantment`,
///      replacing whatever native slot-1 enchantment DeepCloneFields had just cloned in.
///
/// Earlier attempts at fixing this were each independently ruled out by live testing:
///   - A postfix on Init that fished the preview clone back out of the "%After" container's freshly
///     added child failed because RemoveExistingCards only QueueFrees the old preview card (deferred by
///     Godot to end-of-frame), so a stale not-yet-freed child could still occupy the expected index.
///   - A prefix on CardModel.EnchantInternal (reattaching onto `__instance` right before its own body
///     overwrites the native slot) failed for a stranger reason: extensive logging across several
///     sessions confirmed Init's own explicit EnchantInternal call for the *new* enchantment is simply
///     never intercepted by this mod's Harmony patch on that method, even though the very first
///     EnchantInternal call in the same Init() invocation (DeepCloneFields' own internal restore of the
///     cloned native slot) *is* intercepted correctly, moments earlier. Never resolved why.
///   - A postfix on RunState.CloneCard alone (reattaching onto that method's own return value, several
///     lines before EnchantInternal runs at all, sidestepping the EnchantInternal mystery entirely)
///     *still* didn't fix it live - the missing half of the picture above: if this reward screen's card
///     still had a CombatState attached at that moment, the actual clone came from
///     CombatState.CloneCard instead, which RunState-only patching never touched.
///
/// Fixed by patching *both* CloneCard implementations with the same reattachment logic, keyed off
/// whichever one's `mutableCard` parameter matches the card this mod is expecting (captured by a prefix
/// on Init) - whichever ICardScope this reward screen's card actually resolves to, its clone gets the
/// original's native enchantment and extras reattached, directly onto that method's own return value,
/// before EnchantInternal (whose own body only ever touches the native Enchantment slot, never this
/// mod's separate ExtraEnchantments table) gets anywhere near it - so it no longer matters which
/// CloneCard ran, or whether EnchantInternal is interceptable at all.
///
/// Purely for display/preview-math purposes: never re-running ApplyInternal/ModifyCard for the
/// reattached enchantments (the clone already carries their cumulative effect - DynamicVar mutations,
/// keywords - from being deep-cloned off the original card; re-running OnEnchant for them again here
/// would double it), and safe to share the exact same instances the original card still uses, since
/// this preview clone is read-only and thrown away when the screen closes.
/// </summary>
[HarmonyPatch(typeof(NEnchantPreview), nameof(NEnchantPreview.Init))]
internal static class EnchantPreviewInitPatch
{
    [HarmonyPrefix]
    private static void CaptureOriginalCard(CardModel card)
    {
        EnchantPreviewCloneCaptureShared.OriginalCardBeingPreviewed = card;
        EnchantPreviewCloneCaptureShared.NeedsNativeSlotSwap = true;
    }

    /// <summary>
    /// Puts the newly-previewed enchantment at the *bottom* of the flag stack instead of the top.
    ///
    /// By the time Init() returns, the clone's native slot holds the *new* enchantment being previewed
    /// (EnchantInternal, called after this mod's own reattachment, unconditionally overwrites it) while
    /// everything the card already had sits in ExtraEnchantments as extras - including whatever used to
    /// be the card's own native enchantment, reattached there as a stand-in (see
    /// EnchantPreviewCloneCaptureShared.ReattachOntoClone). Since CardEnchantmentVisualsPatch always
    /// draws the native slot's flag at its fixed top position and stacks extras downward below it, this
    /// left the *newest* enchantment on top and everything the card already had below it - backwards
    /// from how a freshly-applied enchantment reads everywhere else in this mod (always added last,
    /// always drawn at the bottom of the stack).
    ///
    /// Swaps the two roles for display purposes only: the stand-in for the card's *former* native
    /// enchantment (if there was one) goes back into the clone's native slot - via reflection, since
    /// CardModel.Enchantment's setter is private - reclaiming the top position it already had on the
    /// real card, and the enchantment actually being previewed is moved into the extras list instead,
    /// appended last so it's the bottom-most flag. Purely a display swap: neither instance's own state
    /// (Amount, Card, DynamicVars) is touched, only which of ExtraEnchantments' table and
    /// CardModel.Enchantment each currently lives in.
    /// </summary>
    [HarmonyPostfix]
    private static void PutNewEnchantmentLast(CardModel card)
    {
        CardModel? clone = EnchantPreviewCloneCaptureShared.LastReattachedClone;
        EnchantmentModel? formerNative = EnchantPreviewCloneCaptureShared.LastReattachedFormerNative;
        EnchantPreviewCloneCaptureShared.LastReattachedClone = null;
        EnchantPreviewCloneCaptureShared.LastReattachedFormerNative = null;

        if (clone == null || formerNative == null || clone.Enchantment == null)
        {
            // Nothing to swap: either this Init() call wasn't the one this mod reattached onto (some
            // other, unrelated preview), or the card had no native enchantment to begin with - in which
            // case the newly-previewed one belongs at the top regardless, since there's nothing above it.
            return;
        }

        EnchantmentModel newlyPreviewed = clone.Enchantment;
        ExtraEnchantments.Remove(clone, formerNative);
        EnchantmentProperty.SetValue(clone, formerNative);
        ExtraEnchantments.Add(clone, newlyPreviewed);
    }

    private static readonly PropertyInfo EnchantmentProperty =
        AccessTools.Property(typeof(CardModel), nameof(CardModel.Enchantment));
}

internal static class EnchantPreviewCloneCaptureShared
{
    internal static CardModel? OriginalCardBeingPreviewed;
    internal static CardModel? LastReattachedClone;
    internal static EnchantmentModel? LastReattachedFormerNative;

    /// <summary>
    /// True only for the enchant-select preview (NEnchantPreview.Init), which is about to call
    /// EnchantInternal right after this and overwrite the clone's native slot with the *new*
    /// enchantment being previewed - so the card's *former* native enchantment needs to be stashed
    /// somewhere it won't get clobbered (temporarily, as an extra) until PutNewEnchantmentLast swaps
    /// it back afterward. False for any other preview (e.g. NUpgradePreview), where nothing overwrites
    /// the clone's native slot afterward: CardModel.DeepCloneFields has already correctly cloned it
    /// there, and re-adding it as an extra too would just duplicate it (shown as a bogus "x2").
    /// </summary>
    internal static bool NeedsNativeSlotSwap;

    internal static void ReattachOntoClone(string sourceMethodName, CardModel mutableCard, CardModel __result)
    {
        CardModel? original = OriginalCardBeingPreviewed;
        if (original == null || !ReferenceEquals(mutableCard, original))
        {
            // Not the clone this mod is waiting for.
            return;
        }

        OriginalCardBeingPreviewed = null;
        EnchantedRewardsMod.Logger.Info($"EnchantPreviewCloneCapture: reattaching via {sourceMethodName}.");

        if (NeedsNativeSlotSwap && original.Enchantment != null)
        {
            ExtraEnchantments.Add(__result, original.Enchantment);
        }

        foreach (EnchantmentModel existingExtra in ExtraEnchantments.Get(original))
        {
            ExtraEnchantments.Add(__result, existingExtra);
        }

        if (NeedsNativeSlotSwap)
        {
            LastReattachedClone = __result;
            LastReattachedFormerNative = original.Enchantment;
        }
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.CloneCard))]
internal static class EnchantPreviewRunStateCloneCapturePatch
{
    [HarmonyPostfix]
    private static void ReattachOntoClone(CardModel mutableCard, CardModel __result)
    {
        EnchantPreviewCloneCaptureShared.ReattachOntoClone(nameof(RunState), mutableCard, __result);
    }
}

[HarmonyPatch(typeof(CombatState), nameof(CombatState.CloneCard))]
internal static class EnchantPreviewCombatStateCloneCapturePatch
{
    [HarmonyPostfix]
    private static void ReattachOntoClone(CardModel mutableCard, CardModel __result)
    {
        EnchantPreviewCloneCaptureShared.ReattachOntoClone(nameof(CombatState), mutableCard, __result);
    }
}
