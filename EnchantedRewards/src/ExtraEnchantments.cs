using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace EnchantedRewards;

/// <summary>
/// Tracks enchantments beyond a card's single native Enchantment slot, keyed by CardModel instance
/// reference. A card's "first" enchantment always goes through the real slot
/// (CardModel.Enchantment, via CardCmd.Enchant) for maximum compatibility; every additional,
/// *different* enchantment lives here instead (see EnchantmentService.Apply).
///
/// Two-instance model, mirroring how the base game itself handles slot-1: a persistent deck card's
/// extras are the long-term source of truth (read by the reward screen, the deck menu, and anything
/// else working with persistent CardModel instances directly). At combat start,
/// CombatCloneSyncPatch clones each extra onto the corresponding *combat* card instance (the object
/// that's actually drawn, held, and played - see the class doc comment there for why this is
/// necessary, not just a nice-to-have), and at combat end syncs any state changes (Amount, Status)
/// back to the persistent originals. Get() falls back from a combat instance to its DeckVersion (the
/// persistent original) for any card CombatCloneSyncPatch didn't get to for some reason, but the
/// normal path is for a combat card to have its own directly-registered clones by the time anything
/// asks.
///
/// KNOWN LIMITATION: this is in-memory only (a plain ConditionalWeakTable) and does not currently
/// survive a save/quit and reload - extra enchantments persist for the rest of the current play
/// session, but a card's slot-1 enchantment is all that's left after reloading a save. Making this
/// persistent needs BaseLib's SavedSpireField/ExtendedSaveTypes custom-type registration (see
/// EnchantedRewards_STS2_Mod_Spec.md Part 1.4 and Phase 4); deferred until the in-memory behavior is
/// confirmed correct, since getting that JSON type registration wrong would be a much harder bug to
/// diagnose than "extra enchantments are session-only for now".
/// </summary>
internal static class ExtraEnchantments
{
    private static readonly ConditionalWeakTable<CardModel, List<EnchantmentModel>> Table = new();

    /// <summary>
    /// Root cause of extras being invisible everywhere except the deck menu: Player.PopulateCombatState
    /// clones every persistent deck card into a brand new CardModel instance for the draw pile
    /// (`state.CloneCard(item)`), so the card actually sitting in hand/play/discard/exhaust during
    /// combat is never the same object we stored extras against - a ConditionalWeakTable lookup keyed
    /// on that combat instance directly always misses. The clone does keep CardModel.DeckVersion
    /// pointing back at the persistent original (the same link native enchantments like Goopy already
    /// use to write changes back to the deck copy), so fall back to that whenever the direct lookup
    /// misses. The deck menu "just worked" because it shows the persistent instances directly.
    /// </summary>
    public static IReadOnlyList<EnchantmentModel> Get(CardModel card)
    {
        if (Table.TryGetValue(card, out List<EnchantmentModel>? list))
        {
            return list;
        }

        // Walk the whole chain, not just one hop: an in-combat duplication effect could clone an
        // already-cloned combat card, whose own DeckVersion may point at that intermediate combat
        // clone rather than straight at the persistent original.
        for (CardModel? deckVersion = card.DeckVersion; deckVersion != null; deckVersion = deckVersion.DeckVersion)
        {
            if (Table.TryGetValue(deckVersion, out List<EnchantmentModel>? deckList))
            {
                return deckList;
            }
        }

        return Array.Empty<EnchantmentModel>();
    }

    /// <summary>
    /// Same as Get(), but only ever looks at this exact instance - no DeckVersion fallback. Used by
    /// CombatCloneSyncPatch, which needs to tell "this card has its own registered extras already"
    /// apart from "this card would only find something via its DeckVersion".
    /// </summary>
    public static IReadOnlyList<EnchantmentModel> GetDirect(CardModel card)
    {
        return Table.TryGetValue(card, out List<EnchantmentModel>? list) ? list : Array.Empty<EnchantmentModel>();
    }

    public static void Add(CardModel card, EnchantmentModel enchantment)
    {
        Table.GetValue(card, static _ => new List<EnchantmentModel>()).Add(enchantment);
    }

    /// <summary>
    /// Finds the enchantment of the given type already on this card, whether it's the card's slot-1
    /// Enchantment or one of its extras. Null if the card doesn't have this type at all.
    /// </summary>
    public static EnchantmentModel? FindOnCard(CardModel card, Type enchantmentType)
    {
        if (card.Enchantment != null && card.Enchantment.GetType() == enchantmentType)
        {
            return card.Enchantment;
        }

        return Get(card).FirstOrDefault(e => e.GetType() == enchantmentType);
    }

    /// <summary>
    /// Every extra enchantment currently attached to any card any participating player owns in this
    /// combat - passed to ModHelper.SubscribeForCombatStateHooks so the game's own generic
    /// AbstractModel hook dispatch (AfterCardPlayed, ModifyShuffleOrder, AfterCardDrawn, BeforeFlush,
    /// AfterAutoPrePlayPhaseEntered, ...) calls into them exactly as if they were a native
    /// (slot-1) enchantment. Mirrors how CombatState.IterateHookListeners() itself walks
    /// player.PlayerCombatState.AllPiles for the native single-slot case.
    /// </summary>
    public static IEnumerable<AbstractModel> AllListenersIn(CombatState combatState)
    {
        foreach (Player player in combatState.Players)
        {
            if (player.PlayerCombatState == null)
            {
                continue;
            }

            foreach (CardModel card in player.PlayerCombatState.AllCards)
            {
                foreach (EnchantmentModel extra in Get(card))
                {
                    yield return extra;
                }
            }
        }
    }
}
