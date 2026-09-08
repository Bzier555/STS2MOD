using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace RotatingDecks;

/// <summary>
/// SetUpCombat is the earliest committed-combat boundary on STS2 0.107.1.
/// This prefix runs before the original method resets PlayerCombatState and
/// clones persistent Deck cards into each combat DrawPile.
/// </summary>
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
internal static class CombatSetupPatch
{
    private sealed class ProcessedMarker;

    private static readonly ConditionalWeakTable<CombatState, ProcessedMarker> ProcessedCombats = new();

    [HarmonyPrefix]
    private static void BeforeCombatSetup(CombatState state)
    {
        RotatingDecksModifier? modifier = state.Modifiers.OfType<RotatingDecksModifier>().SingleOrDefault();
        if (modifier is null)
        {
            return;
        }

        if (ProcessedCombats.TryGetValue(state, out _))
        {
            RotatingDecksMod.Logger.Info(
                $"Rotation already processed for encounter {GetEncounterLabel(state)}; skipping.");
            return;
        }

        IReadOnlyList<Player> orderedPlayers = GetStableParticipatingPlayers(state);
        IReadOnlyList<DeckRotationService.Assignment> assignments;

        try
        {
            assignments = DeckRotationService.Rotate(orderedPlayers);
        }
        catch (Exception error)
        {
            RotatingDecksMod.Logger.Error(
                $"Failed to rotate decks for encounter {GetEncounterLabel(state)}: {error}");
            throw;
        }

        ProcessedCombats.Add(state, new ProcessedMarker());
        modifier.CombatRotationCount++;

        RotatingDecksMod.Logger.Info(
            $"Combat rotation #{modifier.CombatRotationCount} for encounter {GetEncounterLabel(state)}.");
        foreach (DeckRotationService.Assignment assignment in assignments)
        {
            RotatingDecksMod.Logger.Info(
                $"Player {assignment.Receiver.NetId} receives player {assignment.Source.NetId}'s previous deck " +
                $"({assignment.CardCount} cards)." );
        }
    }

    private static IReadOnlyList<Player> GetStableParticipatingPlayers(CombatState state)
    {
        HashSet<ulong> participatingNetIds = state.Players
            .Select(player => player.NetId)
            .ToHashSet();

        // RunState.Players is the game's stable multiplayer/session order. The
        // filter retains only players the actual CombatState has attached.
        List<Player> orderedPlayers = state.RunState.Players
            .Where(player => participatingNetIds.Contains(player.NetId))
            .ToList();

        if (orderedPlayers.Count != state.Players.Count)
        {
            throw new InvalidOperationException(
                "The combat player list could not be mapped exactly onto the run's stable player order.");
        }

        return orderedPlayers;
    }

    private static string GetEncounterLabel(CombatState state)
    {
        return state.Encounter?.Id.ToString() ?? "<unknown>";
    }
}
