using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
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
/// Deliberate, per-type exceptions to rule 2 - repeated applications of these merge into the *existing*
/// instance's Amount instead of creating a new independent one. Audited every pool member's own
/// decompiled implementation (not just assumed) to decide each one:
///
/// Already scale off Amount natively, no patch needed - merging is a pure display improvement (one
/// line/flag showing the true combined total instead of the same smaller number repeated "xN" times),
/// with no change to actual combat math either way (independent instances already summed correctly):
///   - Sharp (`EnchantDamageAdditive` returns `Amount` directly), Nimble (`EnchantBlockAdditive` same),
///     and Adroit (its own `RecalculateValues` override already sets `DynamicVars.Block.BaseValue =
///     Amount` - the exact pattern InkyStackingPatch had to add for Inky, already present natively
///     here). None of these three ever touch Status, so there's no repeat-fire concern at all.
///   - Momentum: ratcheting damage counter (`ExtraDamage += base.Amount` per play) - N independent
///     copies ratcheting by their own Amount each is mathematically identical to one copy ratcheting by
///     the summed Amount (Round 17).
///
/// Needed a dedicated Harmony patch to scale at all, the same way Inky did (Round 16):
///   - Inky: `CanonicalVars` are fixed `1m` literals for both its damage bonus and Weak application,
///     completely ignoring Amount - InkyStackingPatch makes its DynamicVars track Amount instead.
///
/// Deliberately NOT merged, each for a specific, confirmed reason - still handled correctly as
/// independent instances (each contributing its own share via the normal per-extra hook dispatch), and
/// still covered by ExtraCardTextPatch's generic duplicate-line collapsing for anything with its own
/// repeated card text:
///   - Instinct (`EnchantDamageMultiplicative` returns a fixed `2m`) and Corrupted (fixed `1.5m`
///     multiplier, plus a fixed `2m` self-damage in `OnPlay`) are both multiplicative/compounding - two
///     Instinct copies deliberately compound to 4x total, not merge into a flat "2x, scaled by Amount"
///     that would need its own made-up growth formula and could silently change combat balance. Left
///     alone rather than guess at a formula nothing asked for.
///   - Goopy: its own growth is per-instance and *time-based*, not Amount-based (`AfterCardPlayed` does
///     `base.Amount++` every play, contributing `Amount - 1`) - merging two copies (each independently
///     accruing since whenever they were each applied) into one shared counter would change how fast
///     the total grows, not just how it's displayed.
///   - Vigorous/Swift/Sown (Round 19 correction - Round 18 wrongly merged these three): each gates its
///     one-shot effect behind `base.Status == EnchantmentStatus.Normal`, then sets `Status =
///     EnchantmentStatus.Disabled` after firing once - and a full-decompile grep of every class in the
///     game (`grep -r "EnchantmentStatus.Normal"` across a complete `ilspycmd -p` project dump) turned
///     up exactly two hits, both *reads* (the `if` checks inside Sown.OnPlay and Swift.OnPlay
///     themselves) - `Normal` is never assigned anywhere, only read. Disabled is therefore permanent for
///     the rest of the run once it fires, not "resets every combat" as Round 18 assumed (no clone path -
///     DeepCloneFields, ClonePreservingMutability, PopulateCombatState - ever touches Status either).
///     Independent per-instance stacking isn't just safe here, it's the ONLY thing that keeps a repeat
///     application useful: if the existing instance already fired (Status already Disabled) and a
///     second application's Amount got merged into it, that entire second application's worth of effect
///     would be silently absorbed into an instance that will never fire again - permanently lost, not
///     merely a display quirk. A brand new independent instance, by contrast, is still Status.Normal
///     and gets its own real chance to fire. (If NEITHER instance has fired yet, both still correctly
///     grant their own share on the same first play, same as before Round 18's change - nothing
///     regresses for the not-yet-fired case, only the already-fired one is fixed.) Confirmed live: a
///     second Sown merged onto a card whose first Sown had already been played went completely inert
///     and stopped showing any text at all (DynamicExtraCardText returns null once Status is Disabled) -
///     exactly this failure mode.
///   - Spiral: its replay bonus already reads from a separate, fixed `"Times"` DynamicVar rather than
///     Amount, *and* its display text comes entirely through a different, already-correctly-summing
///     mechanism (`ReplayCountPatch` patches `CardModel.GetEnchantedReplayCount()` directly, which the
///     card's dedicated "Replay N" description line reads - not `HasExtraCardText`/`DynamicExtraCardText`
///     at all, since Spiral doesn't override either). Nothing to fix; already shows one correct combined
///     "Replay N" line for any number of stacks.
///   - TezcatarasEmber: `CanonicalVars` is a fixed `DamageVar(3m, ...)`, same class of problem as Inky's
///     originally - not merged this round for lack of a confirmed live report needing it (unlike Inky
///     and Momentum, both fixed at the user's own explicit request); a reasonable next candidate if it
///     turns out to need the same treatment.
///
/// Enchantments with no repeat-stacking scenario at all (excluded from
/// EnchantmentPool.BenefitsFromRepeatApplication, so never re-offered as a reward on a card that already
/// has one - see that set's own doc comment for why each): Steady, PerfectFit, Slither, RoyallyApproved,
/// Imbued, SoulsPower. Nothing to merge because nothing ever stacks for them in the first place.
///
/// Combat-time hooks for extras are wired up by EnchantedRewardsModifier (the math ones: damage/
/// block/play-count/OnPlay) and by the ModHelper.SubscribeForCombatStateHooks registration in
/// EnchantedRewardsMod (the generic AbstractModel ones: ModifyShuffleOrder, AfterCardDrawn, etc.) -
/// see ExtraEnchantments and EnchantedRewards_STS2_Mod_Spec.md Part 1.3/2.4.
/// </summary>
internal static class EnchantmentService
{
    /// <summary>
    /// Shared with NativeEnchantMultiSlotPatch, so a native relic/event granting a mergeable type onto
    /// a card that already has one (as an extra - the native slot case is handled by native
    /// CardCmd.Enchant itself already, untouched) merges the same way this mod's own reward flow does,
    /// rather than only merging when the enchantment came from a monster-fight reward.
    /// </summary>
    internal static readonly HashSet<Type> MergeableTypes = new()
    {
        typeof(Inky),
        typeof(Momentum),
        typeof(Sharp),
        typeof(Nimble),
        typeof(Adroit),
    };

    /// <param name="card">Card to enchant.</param>
    /// <param name="enchantment">
    /// Always the shared *canonical* instance from EnchantmentPool (never already-mutated - see the
    /// comment there for why that matters). Both cases need a mutable instance, so each clones one
    /// here, right before it's actually attached to a card.
    /// </param>
    /// <param name="amount">Amount to apply (see EnchantmentPool.DefaultAmountFor).</param>
    public static void Apply(CardModel card, EnchantmentModel enchantment, int amount)
    {
        if (MergeableTypes.Contains(enchantment.GetType()))
        {
            EnchantmentModel? existing = ExtraEnchantments.FindOnCard(card, enchantment.GetType());
            if (existing != null)
            {
                existing.Amount += amount;
                existing.ModifyCard();
                LogHistory(card, enchantment);
                return;
            }
        }

        if (card.Enchantment == null)
        {
            EnchantmentModel mutable = enchantment.ToMutable();
            card.EnchantInternal(mutable, amount);
            mutable.ModifyCard();
            LogHistory(card, enchantment);
            return;
        }

        ApplyAsNewExtra(card, enchantment.ToMutable(), amount);
        LogHistory(card, enchantment);
    }

    /// <summary>
    /// The "add as a brand new, independent extra" step in isolation - shared with
    /// NativeEnchantMultiSlotPatch, which already has a *mutable* enchantment instance in hand (native
    /// CardCmd.Enchant's caller already called ToMutable() before reaching it) and would assert-fail
    /// calling ToMutable() on it again, the way Apply()'s own canonical-instance case does.
    /// </summary>
    internal static void ApplyAsNewExtra(CardModel card, EnchantmentModel mutableEnchantment, int amount)
    {
        mutableEnchantment.ApplyInternal(card, amount);
        mutableEnchantment.ModifyCard();
        ExtraEnchantments.Add(card, mutableEnchantment);
    }

    internal static void LogHistory(CardModel card, EnchantmentModel enchantment)
    {
        if (card.Owner != null)
        {
            card.Owner.RunState.CurrentMapPointHistoryEntry?.GetEntry(card.Owner.NetId).CardsEnchanted
                .Add(new CardEnchantmentHistoryEntry(card, enchantment.Id));
        }
    }
}
