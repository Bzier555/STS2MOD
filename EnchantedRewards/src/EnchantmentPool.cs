using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace EnchantedRewards;

/// <summary>
/// Which real enchantments this mod is willing to hand out as a monster-fight reward.
///
/// This is a hand-maintained list rather than a reflection-based scan of every
/// <see cref="EnchantmentModel"/> subtype, so new base-game enchantments won't be picked up
/// automatically - see EnchantedRewards_STS2_Mod_Spec.md Part 2.5 if that becomes worth automating.
///
/// Used to be split into a "safe" tier and an "OnPlay" tier excluded until a (risky, never
/// implemented) transpiler patch on CardModel.OnPlayWrapper existed. That patch turned out to be
/// unnecessary: EnchantedRewardsModifier.AfterCardPlayed (a plain override of an already-generically-
/// dispatched hook, fired once per actual play/replay - confirmed against a fresh decompile of
/// CardModel.OnPlayWrapper's loop) manually invokes each extra's own OnPlay, which is a no-op for
/// every enchantment that doesn't override it. So the whole pool lives in one list now.
/// </summary>
internal static class EnchantmentPool
{
    // IMPORTANT: these stay canonical (no .ToMutable() here). ModelDb.Enchantment<T>() returns the
    // one shared canonical instance; EnchantmentModel.ToMutable() asserts the instance it's called
    // on IS canonical (AssertCanonical) and throws otherwise. NDeckEnchantSelectScreen calls
    // .ToMutable() on whatever we hand it internally for its own preview - if we'd already mutated
    // it ourselves here, that call throws, _Ready() aborts partway through (leaving the screen's
    // placeholder icon/title text in place), and the follow-up preview panel never gets its Confirm
    // button enabled, which is exactly the "default symbol / hang" bug this fixes. Mutable clones
    // are made on demand, right before actually attaching to a card - see EnchantmentService.Apply.
    private static readonly List<Func<EnchantmentModel>> Factories = new()
    {
        () => ModelDb.Enchantment<Sharp>(),
        () => ModelDb.Enchantment<Instinct>(),
        () => ModelDb.Enchantment<Nimble>(),
        () => ModelDb.Enchantment<Vigorous>(),
        () => ModelDb.Enchantment<Goopy>(),
        () => ModelDb.Enchantment<RoyallyApproved>(),
        () => ModelDb.Enchantment<Steady>(),
        () => ModelDb.Enchantment<TezcatarasEmber>(),
        () => ModelDb.Enchantment<PerfectFit>(),
        () => ModelDb.Enchantment<Slither>(),
        () => ModelDb.Enchantment<SoulsPower>(),
        () => ModelDb.Enchantment<Imbued>(),
        () => ModelDb.Enchantment<Spiral>(),
        () => ModelDb.Enchantment<Swift>(),
        () => ModelDb.Enchantment<Sown>(),
        () => ModelDb.Enchantment<Adroit>(),
        () => ModelDb.Enchantment<Corrupted>(),
        () => ModelDb.Enchantment<Momentum>(),
        () => ModelDb.Enchantment<Inky>(),
    };

    // SlumberingEssence excluded from the pool: reported (and reproduced) as not actually doing
    // anything in practice. Its effect (BeforeFlush: reduce energy cost by 1 while the card sits in
    // hand) is dispatched through the same generic AbstractModel hook chain as PerfectFit/Slither's
    // siblings, which otherwise work, so the exact root cause isn't confirmed yet - root-cause and
    // re-add later rather than leave a reward in the pool that does nothing.
    // () => ModelDb.Enchantment<SlumberingEssence>(),

    /// <summary>
    /// Enchantment types where a card carrying more than one copy is actually meaningfully stronger
    /// than carrying just one - i.e. where offering the *same* type again to a card that already has
    /// it is a real reward, not a wasted one. This is a different (and stricter) question than "does
    /// this read Amount": Sharp/Nimble/Goopy/TezcatarasEmber/Corrupted/Momentum/Adroit/Inky do, and
    /// each additional copy simply adds its own contribution on top via the normal per-extra hook
    /// dispatch (EnchantmentService.Apply always creates an independent new instance rather than
    /// merging into an existing one's Amount - see its doc comment for why that's what makes
    /// "Instinct doubles again per stack" and "Spiral adds another +1 replay per stack" fall out for
    /// free, with no per-type special-casing). Swift/Sown re-trigger their "disable after one use"
    /// gate independently per instance, so multiple copies mean multiple independent first-uses.
    ///
    /// Excluded, because repeating them provides no additional benefit at all: RoyallyApproved/Steady
    /// (keyword adds are idempotent - a card either has Innate/Retain or it doesn't), PerfectFit
    /// (multiple copies would all just redundantly reorder the same card to the same position),
    /// Slither (multiple copies would redundantly re-randomize the same card's cost on the same
    /// draw, last one applied "wins" - no compounding benefit). Excluded out of caution rather than
    /// zero benefit: Imbued (multiple copies would each independently try to auto-play the same
    /// already-played card, which nothing confirms is safe). SoulsPower isn't listed here at all
    /// because it doesn't need to be - reapplying it removes a keyword its own restriction requires
    /// being present, so MeetsRestrictions naturally stops offering it again after the first use.
    /// </summary>
    private static readonly HashSet<Type> BenefitsFromRepeatApplication = new()
    {
        typeof(Sharp),
        typeof(Instinct),
        typeof(Nimble),
        typeof(Vigorous),
        typeof(Goopy),
        typeof(TezcatarasEmber),
        typeof(Spiral),
        typeof(Swift),
        typeof(Sown),
        typeof(Adroit),
        typeof(Corrupted),
        typeof(Momentum),
        typeof(Inky),
    };

    /// <summary>
    /// How much of each enchantment a reward applies, matching the amounts real game content grants
    /// where a clear precedent exists (found by searching the decompiled assembly for every
    /// CardCmd.Enchant&lt;T&gt; call site and its DynamicVar default): Sharp 3 (GnarledHammer),
    /// Nimble 2 (FresnelLens), Vigorous 8 (StoneOfAllTime), Adroit 3 (Kifuda), Momentum 5
    /// (PunchDagger). Everything else either has no DynamicVar-tuned precedent (every source found
    /// for it applies a flat 1) or (per BenefitsFromRepeatApplication) doesn't mechanically depend on
    /// the amount anyway, so defaults to 1.
    /// </summary>
    private static readonly Dictionary<Type, int> DefaultAmounts = new()
    {
        [typeof(Sharp)] = 3,
        [typeof(Nimble)] = 2,
        [typeof(Vigorous)] = 8,
        [typeof(Adroit)] = 3,
        [typeof(Momentum)] = 5,
    };

    private const int FallbackAmount = 1;

    public static int DefaultAmountFor(EnchantmentModel enchantment)
    {
        return DefaultAmounts.GetValueOrDefault(enchantment.GetType(), FallbackAmount);
    }

    private static readonly Random Rng = new();

    /// <summary>
    /// Rolls up to two *distinct* enchantment types, each paired with the cards in the given deck it
    /// could legally be applied to. Only enchantments with at least one valid target are returned,
    /// e.g. Nimble is never offered if nothing in the deck currently gains block.
    ///
    /// A card can carry any number of enchantments (see ExtraEnchantments): a fresh enchantment
    /// (whether the card already has other, *different* enchantments or none at all) is always
    /// offered as a candidate; a card that already carries this *same* type is only offered again if
    /// it's actually worth stacking (see BenefitsFromRepeatApplication).
    ///
    /// Scans the whole pool once (shuffled) rather than rerolling a fixed number of random picks, so
    /// it reliably finds up to two valid options if they exist instead of possibly missing one by
    /// bad luck. An empty result means nothing in the pool has any valid target at all (see
    /// EnchantedRewardsModifier for the gold-reward fallback in that case); a result with one entry
    /// means only one distinct enchantment type currently has a valid target.
    /// </summary>
    public static IReadOnlyList<(EnchantmentModel Enchantment, IReadOnlyList<CardModel> Candidates)> RollUpToTwoWithCandidates(
        IReadOnlyList<CardModel> deck)
    {
        List<(EnchantmentModel, IReadOnlyList<CardModel>)> results = new();

        foreach (Func<EnchantmentModel> factory in Factories.OrderBy(_ => Rng.Next()))
        {
            if (results.Count == 2)
            {
                break;
            }

            EnchantmentModel enchantment = factory();
            List<CardModel> candidates = deck.Where(card => CanTarget(card, enchantment)).ToList();
            if (candidates.Count > 0)
            {
                results.Add((enchantment, candidates));
            }
        }

        return results;
    }

    private static bool CanTarget(CardModel card, EnchantmentModel enchantment)
    {
        Type type = enchantment.GetType();

        if (ExtraEnchantments.FindOnCard(card, type) != null && !BenefitsFromRepeatApplication.Contains(type))
        {
            // Already has this exact type somewhere (slot 1 or an extra), and repeating it wouldn't
            // do anything more - don't offer a reward with no effect.
            return false;
        }

        // Never calls the real EnchantmentModel.CanEnchant here, for *any* card state (not just
        // already-enchanted ones): that method's base implementation refuses outright whenever
        // card.Enchantment != null regardless of type - the whole thing "unlimited enchants" needs
        // to get around - so it can't be the single source of truth for a system meant to allow a
        // card to carry several different (or repeated) enchantments at once. See MeetsRestrictions
        // for what replaces it, applied uniformly regardless of what the card already carries.
        return MeetsRestrictions(card, enchantment);
    }

    /// <summary>
    /// The restrictions this mod actually enforces for each pool enchantment, deliberately *not* the
    /// same set the real EnchantmentModel.CanEnchant/CanEnchantCardType overrides would enforce.
    ///
    /// Policy (per explicit request after playtesting): keep a restriction only where the
    /// enchantment's effect genuinely cannot manifest without it - Sharp/Instinct/Vigorous/Corrupted/
    /// Momentum need a damage-dealing Attack, Nimble/Goopy need a card that grants block, Slither's
    /// cost randomization needs a non-X cost, SoulsPower needs a card that actually has Exhaust to
    /// remove. Drop restrictions that are really just a category/flavor gate unrelated to whether the
    /// effect does anything - Spiral's native "must be Basic-rarity Strike/Defend" and
    /// RoyallyApproved's native "must be Attack or Skill" (Innate+Retain are meaningful on any card
    /// type) are both examples of this and have been removed. Swift/Sown/Adroit/Inky have no native
    /// CanEnchantCardType override at all in this game version, so there was nothing to relax for
    /// them. Imbued keeps its native Skill-only restriction as the one deliberate exception: unlike
    /// the others, letting it auto-play an Attack automatically raises a targeting question (which
    /// enemy?) that nothing in its own implementation resolves, so this was kept out of caution
    /// rather than because it's clearly functional - revisit if that turns out to be overly
    /// conservative.
    ///
    /// Every branch here was checked against the corresponding enchantment's decompiled CanEnchant/
    /// CanEnchantCardType override, so the *functional* restrictions kept below are confirmed
    /// accurate, not guessed. Keep this in sync if the pool in Factories ever changes.
    /// </summary>
    private static bool MeetsRestrictions(CardModel card, EnchantmentModel enchantment)
    {
        if (card.Type is CardType.Status or CardType.Curse or CardType.Quest)
        {
            return false;
        }

        if (card.Pile is { Type: PileType.Deck } && card.Keywords.Contains(CardKeyword.Unplayable))
        {
            return false;
        }

        Type type = enchantment.GetType();

        if (type == typeof(Sharp) || type == typeof(Instinct) || type == typeof(Vigorous)
            || type == typeof(Corrupted) || type == typeof(Momentum))
        {
            // Their damage bonus only ever applies to a "powered attack" (EnchantDamageAdditive/
            // Multiplicative check props.IsPoweredAttack()), so restricting to Attack cards is a
            // functional necessity, not an arbitrary category gate.
            return enchantment.CanEnchantCardType(card.Type);
        }

        if (type == typeof(Nimble) || type == typeof(Goopy))
        {
            // Functional, not Goopy's native "must have the Defend tag": a card can gain block
            // without being tagged Defend, and this bonus does nothing on a card that never grants
            // block regardless of its tags.
            return card.GainsBlock;
        }

        if (type == typeof(Slither))
        {
            return !card.EnergyCost.CostsX;
        }

        if (type == typeof(SoulsPower))
        {
            return card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Exhaust);
        }

        if (type == typeof(Imbued))
        {
            return enchantment.CanEnchantCardType(card.Type);
        }

        // RoyallyApproved, Spiral, Swift, Sown, Adroit, Inky: no restriction beyond the universal
        // checks above. Steady, TezcatarasEmber, and PerfectFit already had no restriction beyond
        // CanEnchantCardType/the checks above natively.
        return true;
    }
}
