using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace EnchantedRewards;

/// <summary>
/// Fixes extra enchantments whose own logic compares the *specific card instance* it's attached to
/// against the card an event is happening to - Goopy's AfterCardPlayed
/// (`if (cardPlay.Card != base.Card) return;`), Vigorous's AfterCardPlayed (same shape, used to
/// disable itself after one use), PerfectFit's ModifyShuffleOrder (`cards.Contains(base.Card)`), and
/// Slither's AfterCardDrawn (same shape) are all confirmed examples. Before this patch, all four
/// were silently inert whenever used as an *extra*, because EnchantmentModel.Card is set once, at
/// EnchantmentService.Apply time, to the *persistent* deck card - but the object actually drawn,
/// held, and played during combat is a completely different CardModel instance (see
/// ExtraEnchantments' class doc comment for why). The comparison inside each of those methods was
/// therefore always false for an extra, no matter how the card was actually being played.
///
/// The native game avoids this for slot-1 enchantments because cloning a card for combat
/// (Player.PopulateCombatState -> CombatState.CloneCard -> CardModel.MutableClone ->
/// CardModel.DeepCloneFields) *also* clones its Enchantment via EnchantmentModel.ClonePreservingMutability
/// and re-points the clone's Card at the new combat card. Extras never got the same treatment because
/// they live in ExtraEnchantments, a side table the native cloning code has no idea exists. This patch
/// gives them it: clone each extra onto its card's combat instance at combat start (properly
/// Card-linked, exactly like the native slot-1 clone), and sync state (Amount, Status) back to the
/// persistent original at combat end, mirroring how e.g. Goopy's own AfterCardPlayed writes back to
/// `base.Card.DeckVersion.Enchantment.Amount` for slot-1.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.PopulateCombatState))]
internal static class CloneExtrasIntoCombatPatch
{
    [HarmonyPostfix]
    private static void CloneExtrasOntoCombatCards(Player __instance)
    {
        if (__instance.PlayerCombatState == null)
        {
            return;
        }

        foreach (CardModel combatCard in __instance.PlayerCombatState.DrawPile.Cards)
        {
            CardModel? persistent = combatCard.DeckVersion;
            if (persistent == null)
            {
                continue;
            }

            foreach (EnchantmentModel extra in ExtraEnchantments.GetDirect(persistent))
            {
                EnchantmentModel clone = (EnchantmentModel)extra.ClonePreservingMutability();
                clone.ApplyInternal(combatCard, extra.Amount);
                ExtraEnchantments.Add(combatCard, clone);
            }
        }
    }
}

/// <summary>
/// The other half of CloneExtrasIntoCombatPatch: writes each combat-instance extra's final state
/// back to its persistent original before combat state is torn down, so progress (e.g. Goopy's
/// growing block bonus) carries over to the next fight instead of resetting every combat. A prefix,
/// not a postfix: Player.AfterCombatEnd() itself calls `PlayerCombatState?.AfterCombatEnd()` as part
/// of its own body, which may clear/tear down piles - this needs to read them beforehand.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.AfterCombatEnd))]
internal static class SyncExtrasBackToDeckPatch
{
    [HarmonyPrefix]
    private static void SyncCombatExtrasToPersistent(Player __instance)
    {
        if (__instance.PlayerCombatState == null)
        {
            return;
        }

        foreach (CardModel combatCard in __instance.PlayerCombatState.AllCards)
        {
            IReadOnlyList<EnchantmentModel> combatExtras = ExtraEnchantments.GetDirect(combatCard);
            if (combatExtras.Count == 0)
            {
                continue;
            }

            CardModel? persistent = combatCard.DeckVersion;
            if (persistent == null)
            {
                continue;
            }

            IReadOnlyList<EnchantmentModel> persistentExtras = ExtraEnchantments.GetDirect(persistent);
            foreach (EnchantmentModel combatExtra in combatExtras)
            {
                EnchantmentModel? match = persistentExtras.FirstOrDefault(e => e.GetType() == combatExtra.GetType());
                if (match != null)
                {
                    match.Amount = combatExtra.Amount;
                    match.Status = combatExtra.Status;
                }
            }
        }
    }
}
