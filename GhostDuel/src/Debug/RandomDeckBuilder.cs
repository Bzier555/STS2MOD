using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.RelicPools;

namespace GhostDuel.Debug;

/// <summary>
/// M8.5 (PLAN.md): generalizes <see cref="M2Deck.BuildRandomDeck"/>'s sampling shape — a random 20
/// cards / 5 relics, "similar to the previous M2 random" per the user's own words — to <em>any</em>
/// character, not just Ironclad. Unlike <c>M2Deck</c>, which needed a compile-time pool type
/// (<c>ModelDb.CardPool&lt;IroncladCardPool&gt;()</c>), <c>CharacterModel.CardPool</c>/<c>RelicPool</c>
/// (<c>CharacterModel.cs:92-94</c>) are already per-instance properties — no per-character type
/// mapping needed at all.
/// </summary>
internal static class RandomDeckBuilder
{
    /// <summary>
    /// Same unseeded-<see cref="Random"/> reasoning as <see cref="M2Deck.BuildRandomDeck"/>: this
    /// picks which stock fixtures a debug/real-flow run uses, not a gameplay decision during the
    /// duel, so COMBAT-RULES.md §11's determinism rule doesn't apply.
    /// <b>Known gap, stated rather than silently assumed</b>: <see cref="M2Deck"/>'s exclusion
    /// lists (cards/relics confirmed to misread <c>CombatState.Allies</c>/<c>Enemies</c> directly,
    /// ENGINE-NOTES.md §0 M2 section) are Ironclad-specific — audited for Ironclad's pool only. No
    /// equivalent audit exists yet for Silent/Defect/Regent/Necrobinder, so for any character other
    /// than Ironclad this samples the *entire* pool unfiltered; a card sharing that same bug shape
    /// could still turn up until each pool gets its own pass.
    /// </summary>
    public static (IReadOnlyList<Type> Cards, IReadOnlyList<Type> Relics) BuildRandomDeck(CharacterModel character, int cardCount = 20, int relicCount = 5)
    {
        bool isIronclad = character is MegaCrit.Sts2.Core.Models.Characters.Ironclad;

        List<Type> allCards = character.CardPool.AllCards
            .Select(c => c.GetType())
            .Where(t => !isIronclad || !M2Deck.KnownEnemiesBugCards.Contains(t))
            .Distinct()
            .ToList();
        List<Type> allRelics = character.RelicPool.AllRelics
            .Concat(ModelDb.RelicPool<SharedRelicPool>().AllRelics)
            .Select(r => r.GetType())
            .Where(t => !isIronclad || !M2Deck.KnownEnemiesBugRelics.Contains(t))
            .Distinct()
            .ToList();

        Random rng = new();
        List<Type> cards = allCards.OrderBy(_ => rng.Next()).Take(Math.Min(cardCount, allCards.Count)).ToList();
        List<Type> relics = allRelics.OrderBy(_ => rng.Next()).Take(Math.Min(relicCount, allRelics.Count)).ToList();
        return (cards, relics);
    }
}
