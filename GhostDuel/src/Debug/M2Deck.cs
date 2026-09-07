using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.RelicPools;

namespace GhostDuel.Debug;

/// <summary>
/// M2's exit gate deck (PLAN.md M2): "~20 distinct cards plus 5 relics." Chosen for breadth across
/// native mechanics rather than familiarity — multi-hit, X-cost, non-AnyEnemy targeting, Exhaust,
/// self-damage, on-card-play/on-damage/on-block-gained triggers, and a deliberate non-Dexterity-scaled
/// Block card as a negative control (<c>StoneArmor</c>/<c>PlatingPower</c>). All from Ironclad's real
/// card/relic pool (<c>IroncladCardPool.cs</c>/<c>IroncladRelicPool.cs</c>/<c>SharedRelicPool.cs</c>)
/// — none of the starter three, since M1 already exercises those permanently.
/// </summary>
internal static class M2Deck
{
    /// <summary>
    /// Every card/relic confirmed (2026-09-02 audit, not a guess) to read
    /// <c>CombatState.Enemies</c>/<c>Allies</c>/<c>HittableEnemies</c> directly to mean "the owner's
    /// opponents/allies" — Side-literal, correct in vanilla play only because such code is always
    /// owned by a Player-side creature there (ENGINE-NOTES.md §0 M2 section). A first fix attempt
    /// (the original P9, actor-relative `Allies`/`Enemies`) was reverted after causing a worse
    /// regression; a second attempt (P10, `HittableEnemies` only) was confirmed live to still miss
    /// relic/power lifecycle hooks (`RedMask` specifically) because of a too-narrow guard, not the
    /// patch target itself; a third attempt (P11, widening the guard via
    /// <c>GhostHookOwnerScope</c>) is now live but not yet confirmed by a real playthrough, which is
    /// why this exclusion list stays in place for <see cref="BuildRandomDeck"/> rather than being
    /// removed the moment P11 was written — remove entries here only after each is specifically
    /// confirmed working, not on the strength of the fix's reasoning alone. `RedMask` is the one
    /// exception: deliberately kept *in* the fixed <see cref="RelicTypes"/> (not the random pool) as
    /// P10/P11's direct, isolated test case. <c>Juggernaut</c> was removed from <see cref="CardTypes"/>
    /// once confirmed broken; <c>Stomp</c>/<c>Conflagration</c>/<c>Dismantle</c> are cosmetic-only
    /// misuses (VFX/UI-hint on the wrong side, not a gameplay effect) and <c>Hellraiser</c> is a
    /// narrow edge case (a Strike auto-play cap heuristic), included here anyway so the random pool
    /// stays entirely clean of the pattern rather than drawing a line between "matters" and "doesn't"
    /// per card.
    /// </summary>
    /// <summary>Internal (not private): reused by <see cref="RandomDeckBuilder"/> for M8.5's
    /// character-agnostic random sampler, which applies this Ironclad-specific list only when the
    /// sampled character actually is Ironclad.</summary>
    internal static readonly HashSet<Type> KnownEnemiesBugCards = new()
    {
        typeof(Thunderclap), typeof(Stomp), typeof(Conflagration), typeof(Dismantle),
        typeof(Juggernaut), typeof(Inferno), typeof(Hellraiser),
    };

    internal static readonly HashSet<Type> KnownEnemiesBugRelics = new()
    {
        typeof(CharonsAshes), typeof(StoneCalendar), typeof(ScreamingFlagon), typeof(RedMask),
        typeof(ParryingShield), typeof(MercuryHourglass), typeof(LetterOpener), typeof(Kusarigama),
        typeof(FestivePopper), typeof(BagOfMarbles),
    };

    public static readonly IReadOnlyList<Type> CardTypes = new[]
    {
        typeof(TwinStrike),      // multi-hit attack
        typeof(Whirlwind),       // X-cost, AllEnemies, multi-hit
        typeof(SwordBoomerang),  // multi-hit, RandomEnemy targeting
        typeof(Bludgeon),        // plain Strength-scaled attack
        typeof(IronWave),        // Block + attack in one card
        typeof(FiendFire),       // Exhaust-hand, count-driven multi-hit
        typeof(Uppercut),        // Strength-scaled attack + applies Weak/Vulnerable
        typeof(BloodWall),       // self-damage + Dexterity-scaled Block
        typeof(Armaments),       // Dexterity-scaled Block + meaningful upgrade
        typeof(TrueGrit),        // Exhaust + Dexterity-scaled Block + meaningful upgrade
        typeof(ShrugItOff),      // Dexterity-scaled Block + draw
        typeof(Bloodletting),    // self-damage -> Energy
        typeof(Offering),        // self-damage + Exhaust + Energy + draw
        typeof(Rage),            // on-card-played trigger (RagePower)
        typeof(Barricade),       // Power: suppresses Block clearing
        typeof(DemonForm),       // Power: recurring Strength grant
        typeof(Inflame),         // Power: immediate Strength grant
        typeof(StoneArmor),      // Power: non-Dexterity-scaled Block (negative control); also P12's
                                 // direct test case (PlatingPower.ShouldScaleInMultiplayer over-grants
                                 // in a Ghost Duel, ENGINE-NOTES.md §0 — not yet confirmed live)
        typeof(Rupture),         // Power: on-damage-taken trigger
        typeof(Anger),           // attack that copies itself to Discard
    };

    public static readonly IReadOnlyList<Type> RelicTypes = new[]
    {
        typeof(BurningBlood),  // AfterCombatVictory (M1's default, kept as a known-good baseline)
        typeof(Brimstone),     // AfterSideTurnStart -> grants Strength
        typeof(DemonTongue),   // AfterDamageReceived -> heals
        typeof(Nunchaku),      // AfterCardPlayed -> grants Energy every 10th Attack
        // RedMask deliberately included (swapped in for Shuriken, which overlapped with Nunchaku's
        // every-Nth-Attack shape): turn-1 Weak via CombatState.HittableEnemies — RedMask.cs:28 — is
        // one of the 17 confirmed HittableEnemies-misuse cases (see KnownEnemiesBugRelics). P10 alone
        // did not fix it (its guard never covers a relic's BeforeSideTurnStart hook); P11
        // (EnginePatches.cs) widens the guard specifically to cover this shape. Included here as a
        // direct, isolated test of that fix, not excluded the way it is from BuildRandomDeck's blind
        // sampling.
        typeof(RedMask),
    };

    /// <summary>
    /// A fresh random sample from Ironclad's *entire* card/relic pool (87 cards; Ironclad + shared
    /// relics), for broader coverage across repeated test runs than the fixed 20/5 list above can
    /// give alone — the request that motivated this was finding the P9 Allies/Enemies bug via a card
    /// (Juggernaut) that happened to be in the fixed list; sampling the full pool is how more of the
    /// ~19 similarly-shaped cards/powers get exercised over time. Uses a plain, unseeded
    /// <see cref="Random"/> deliberately — this picks *which stock test fixtures* a debug run uses,
    /// not a gameplay decision during the duel, so COMBAT-RULES.md §11's "no local client randomness"
    /// rule (which governs the shipping Ghost's own decisions) does not apply here. Every choice is
    /// logged by entry name (<see cref="GhostDuel.Debug.DebugBoot"/>), so a run is reconstructable
    /// after the fact even though it isn't reproducible in advance.
    /// </summary>
    public static (IReadOnlyList<Type> Cards, IReadOnlyList<Type> Relics) BuildRandomDeck(int cardCount = 20, int relicCount = 5)
    {
        List<Type> allCards = ModelDb.CardPool<IroncladCardPool>().AllCards
            .Select(c => c.GetType())
            .Where(t => !KnownEnemiesBugCards.Contains(t))
            .Distinct()
            .ToList();
        List<Type> allRelics = ModelDb.RelicPool<IroncladRelicPool>().AllRelics
            .Concat(ModelDb.RelicPool<SharedRelicPool>().AllRelics)
            .Select(r => r.GetType())
            .Where(t => !KnownEnemiesBugRelics.Contains(t))
            .Distinct()
            .ToList();

        Random rng = new();
        List<Type> cards = allCards.OrderBy(_ => rng.Next()).Take(Math.Min(cardCount, allCards.Count)).ToList();
        List<Type> relics = allRelics.OrderBy(_ => rng.Next()).Take(Math.Min(relicCount, allRelics.Count)).ToList();
        return (cards, relics);
    }
}
