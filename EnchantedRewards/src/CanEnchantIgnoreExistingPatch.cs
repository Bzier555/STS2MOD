using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace EnchantedRewards;

/// <summary>
/// Makes native enchant-granting sources (relics like FresnelLens/WingCharm/GnarledHammer, and events)
/// stop excluding already-enchanted cards from their own candidate lists - previously a documented,
/// deliberate non-fix ("expected, safe behavior" per earlier rounds), now explicitly requested: a card
/// should be able to receive an enchantment from *any* source, native or this mod's own rewards, no
/// matter how many it already carries.
///
/// EnchantmentModel.CanEnchant (confirmed by decompile) is: Status/Curse/Quest card types always
/// refused; CanEnchantCardType(card.Type) must pass; an Unplayable card sitting in the deck pile is
/// refused; and then - the part actually being removed here -
///     if (card.Enchantment != null &amp;&amp; (!IsStackable || card.Enchantment.GetType() != GetType()))
///         return false;
/// blocks any card that already has a *different* enchantment outright, and even a *same*-type repeat
/// unless IsStackable is overridden true (nothing in this game version does). This mod's own reward
/// flow never calls CanEnchant at all (EnchantmentPool.MeetsRestrictions replaces it entirely - see
/// that class's own comment for why), so this patch only ever affects *other* callers: relics, events,
/// and anything else that calls the real CanEnchant directly.
///
/// Implemented as a full prefix replacement (skip=false) rather than a transpiler removing just that
/// one check, matching this project's existing style (EnchantmentPool.MeetsRestrictions already fully
/// reimplements CanEnchant's restrictions rather than surgically patching around them) - the tradeoff
/// being that if a future game patch changes CanEnchant's other checks, this needs re-verifying against
/// a fresh decompile rather than picking up the change automatically.
///
/// The four enchantments that override CanEnchant themselves (Goopy, Nimble, SoulsPower, Slither) all
/// call `base.CanEnchant(card)` first (confirmed by decompile - none reimplement it from scratch), so
/// this patch on the base method transparently reaches their own restrictions too, unaffected -
/// Goopy still requires the Defend tag, Nimble still requires GainsBlock, etc., exactly as before; only
/// the "already has an enchantment" blanket gate common to all of them is gone.
/// </summary>
[HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.CanEnchant))]
internal static class CanEnchantIgnoreExistingPatch
{
    [HarmonyPrefix]
    private static bool AllowAlreadyEnchantedCards(EnchantmentModel __instance, CardModel card, ref bool __result)
    {
        if (card.Type is CardType.Status or CardType.Curse or CardType.Quest)
        {
            __result = false;
            return false;
        }

        if (!__instance.CanEnchantCardType(card.Type))
        {
            __result = false;
            return false;
        }

        if (card.Pile is { Type: PileType.Deck } && card.Keywords.Contains(CardKeyword.Unplayable))
        {
            __result = false;
            return false;
        }

        __result = true;
        return false;
    }
}
