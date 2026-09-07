using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GhostDuel.Engine;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace GhostDuel.Ghost;

/// <summary>
/// Constructs the enemy-side <c>Player</c>. M1: a stock character via
/// <c>Player.CreateForNewRun</c>, built inside <see cref="GhostConstructionScope"/> so P1
/// flips its <c>Creature.Side</c> to <c>Enemy</c> at construction (ARCHITECTURE.md §3). HP, energy,
/// starting deck and starting relic all come from the character model
/// (confirmed: <c>Player.cs:301-346</c>, ENGINE-NOTES.md §8) — nothing is hand-built mod-side.
/// </summary>
internal static class GhostPlayerFactory
{
    /// <summary>
    /// A net id far outside the range a real run would ever assign, matching the first pass's
    /// collision-free convention (ARCHITECTURE.md §3). PLAN.md M9c: a small deterministic pool, one
    /// per party slot, rather than a single constant — a party of Ghosts each needs its own distinct
    /// id (<c>Player.NetId</c> is read pervasively, e.g. as a dictionary key, so two Ghosts sharing one
    /// would silently collapse into each other everywhere that happens). <paramref name="partyIndex"/>
    /// 0 (the default used by every M1-M8 singleplayer call site) yields the exact original constant.
    /// </summary>
    private static ulong GhostNetId(int partyIndex = 0) => ulong.MaxValue - 1024 - (ulong)partyIndex;

    /// <summary>
    /// Takes a runtime <see cref="CharacterModel"/> rather than a generic type parameter (M8 debug
    /// harness: mirrors <c>GhostDuel.Debug.SelectedCharacterTracker.Current</c>, so the character
    /// isn't known at compile time — the non-generic <c>Player.CreateForNewRun(CharacterModel, ...)</c>
    /// overload, <c>Player.cs:301</c>, is what the generic one wraps internally anyway).
    /// </summary>
    public static Player CreateDebugGhost(CharacterModel character, int partyIndex = 0)
    {
        using (GhostConstructionScope.Begin())
        {
            return Player.CreateForNewRun(character, UnlockState.all, GhostNetId(partyIndex));
        }
    }

    /// <summary>
    /// M7 (PLAN.md): restores a historical Ghost from a previously-won Legacy Ascension level's
    /// snapshot. <c>Player.FromSerializable</c> (<c>Player.cs:314-322</c>) constructs a <c>Creature</c>
    /// internally the same way <c>Player.CreateForNewRun</c> does, through the same constructor P1
    /// patches — so this needs the identical <see cref="GhostConstructionScope"/> wrapping to land the
    /// Ghost's side flip, even though it isn't "new run" construction. <c>Player.NetId</c> is
    /// get-only (<c>Player.cs:48</c>, baked in at construction from <c>SerializablePlayer.NetId</c>),
    /// so the collision-free <see cref="GhostNetId"/> must be written onto the snapshot's own settable
    /// <c>NetId</c> field before <c>FromSerializable</c> runs, not onto the constructed <c>Player</c>
    /// afterward — otherwise a restored Ghost would carry whatever NetId the human who originally
    /// earned it had (almost always the same id the *current* run's human already uses).
    /// </summary>
    public static Player CreateGhostFromSnapshot(SerializablePlayer snapshot, int partyIndex = 0)
    {
        snapshot.NetId = GhostNetId(partyIndex);
        using (GhostConstructionScope.Begin())
        {
            return Player.FromSerializable(snapshot);
        }
    }

    /// <summary>
    /// Wires a Ghost into a real <see cref="RunState"/> after the fact. Confirmed live: a freshly
    /// constructed <c>Player</c>'s own doc comment says its "models will not work properly until the
    /// player is added to a RunState" (<c>Player.cs:278-280</c>) — and unlike relics/potions (whose
    /// <c>Owner</c> is set directly inside <c>Player.PopulateStartingRelics</c>/potion population,
    /// <c>Player.cs:451,648</c>), a deck card's <c>Owner</c> is only ever set by
    /// <c>RunState.AddCard</c>, called from <c>RunState.CreateShared</c>'s per-player loop
    /// (<c>RunState.cs:319-334</c>) — which the Ghost never goes through, since it is deliberately not
    /// part of <c>runState.Players</c>. Without this, every one of the Ghost's deck cards has a null
    /// <c>Owner</c>, and the first native code to read it (<c>CombatState.Contains</c>, reached via
    /// <c>Player.PopulateCombatState</c>'s shuffle hook during <c>CombatManager.SetUpCombat</c>)
    /// NullReferenceExceptions. Mirrors <c>RunState.CreateShared</c>'s own loop exactly, just for one
    /// player joining after <c>CreateForNewRun</c> already ran instead of as part of it.
    /// </summary>
    public static void JoinRun(Player ghostPlayer, RunState runState)
    {
        ghostPlayer.RunState = runState;
        foreach (var card in ghostPlayer.Deck.Cards)
        {
            runState.AddCard(card, ghostPlayer);
        }
    }

    /// <summary>
    /// Replaces the Ghost's deck and relics with an explicit set, for M2's exit gate ("a Ghost with a
    /// hand-built Ironclad deck of ~20 distinct cards plus 5 relics", PLAN.md M2). Must run after
    /// <see cref="JoinRun"/> — <c>CardPileCmd.RemoveFromDeck</c> dereferences <c>card.Owner.RunState</c>
    /// directly (<c>CardPileCmd.cs:60</c>), which is null until the Ghost has joined a real run.
    /// Uses only native commands: <c>CardPileCmd.RemoveFromDeck</c>/<c>Add</c> and
    /// <c>RelicCmd.Remove</c>/<c>Obtain</c> — the same ones a card-removal service or relic pickup use
    /// — never manual pile/list manipulation. Choosing which stock cards/relics populate the pile is
    /// not content emulation (CLAUDE.md's rule); the cards and relics themselves are untouched.
    /// </summary>
    public static async Task ConfigureDeckAsync(Player ghostPlayer, IReadOnlyList<Type> cardTypes, IReadOnlyList<Type> relicTypes)
    {
        foreach (CardModel card in ghostPlayer.Deck.Cards.ToList())
        {
            await CardPileCmd.RemoveFromDeck(card, showPreview: false);
        }
        foreach (RelicModel relic in ghostPlayer.Relics.ToList())
        {
            await RelicCmd.Remove(relic);
        }

        RunState runState = (RunState)ghostPlayer.RunState;
        foreach (Type cardType in cardTypes)
        {
            CardModel canonical = ModelDb.GetById<CardModel>(ModelDb.GetId(cardType));
            CardModel owned = runState.CreateCard(canonical, ghostPlayer);
            await CardPileCmd.Add(owned, PileType.Deck);
        }
        foreach (Type relicType in relicTypes)
        {
            RelicModel canonical = ModelDb.GetById<RelicModel>(ModelDb.GetId(relicType));
            await RelicCmd.Obtain(canonical.ToMutable(), ghostPlayer);
        }
    }
}
