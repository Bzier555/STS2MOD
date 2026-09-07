using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GhostDuel.Ai;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace GhostDuel.Debug;

/// <summary>
/// The debug-run bootstrap: build a human and a Ghost, both as whichever character is currently
/// selected on the main menu's character-select screen (<see cref="SelectedCharacterTracker"/>,
/// Ironclad until the player picks something else this session), start a singleplayer run, and enter
/// the zero-monster <see cref="GhostDuelEncounter"/> — the same lower-level primitives
/// <c>NSceneBootstrapper.StartNewRun()</c> uses (ENGINE-NOTES.md §8), called directly instead of
/// through a scene. Invoked by <see cref="DebugMenuButton"/>.
/// </summary>
internal static class DebugBoot
{
    /// <param name="game">The running <see cref="NGame"/> instance.</param>
    /// <param name="ghostCardTypes">
    /// If non-null/non-empty <em>and the selected character is Ironclad</em>, replaces the Ghost's
    /// stock deck with exactly these cards (M2's hand-built deck, <see cref="M2Deck"/>) via
    /// <see cref="GhostPlayerFactory.ConfigureDeckAsync"/>. <c>M2Deck</c>'s card/relic lists are
    /// Ironclad-specific content (CLAUDE.md: no content emulation for other classes), so injecting
    /// them into a different character's deck would be nonsensical — for any other selected character
    /// this is skipped entirely and the run proceeds with that character's own real stock deck, still
    /// a valid M1-style regression check for whichever class M8 is currently exercising.
    /// </param>
    /// <param name="ghostRelicTypes">Same as <paramref name="ghostCardTypes"/>, for relics.</param>
    public static async Task StartDebugRunAsync(NGame game, IReadOnlyList<Type>? ghostCardTypes = null, IReadOnlyList<Type>? ghostRelicTypes = null)
    {
        GhostLog.OpenLogFile();
        GhostLog.Info("Starting Ghost Duel debug run.");

        try
        {
            if (GhostSession.Current is not null)
            {
                // The debug button is a dev/test entry point: a previous attempt that never reached
                // a real combat end (e.g. it failed partway through setup) leaves no native event to
                // clean up after. Treat a fresh click as authoritative intent to restart, rather than
                // making every retry require a full game relaunch.
                GhostLog.Warn("A previous GhostSession was still active (last debug run never reached combat end) — disposing it and starting fresh.");
                GhostSession.Current.Dispose();
            }

            CharacterModel character = SelectedCharacterTracker.Current;
            Player human = Player.CreateForNewRun(character, UnlockState.all, 1uL);
            Player ghostPlayer = GhostPlayerFactory.CreateDebugGhost(character);
            GhostSession.Begin(ghostPlayer, new WeightedRandomGhostChooser());

            string seed = SeedHelper.GetRandomSeed();
            List<ActModel> acts = ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList();
            RunState runState = RunState.CreateForNewRun(
                new[] { human },
                acts,
                Array.Empty<ModifierModel>(),
                GameMode.Standard,
                ascensionLevel: 0,
                seed);
            GhostPlayerFactory.JoinRun(ghostPlayer, runState);
            bool wantsDeckInjection = ghostCardTypes is { Count: > 0 } || ghostRelicTypes is { Count: > 0 };
            if (wantsDeckInjection && character is Ironclad)
            {
                await GhostPlayerFactory.ConfigureDeckAsync(
                    ghostPlayer,
                    ghostCardTypes ?? Array.Empty<Type>(),
                    ghostRelicTypes ?? Array.Empty<Type>());
                GhostLog.Info($"Ghost deck configured: [{string.Join(", ", ghostCardTypes?.Select(t => t.Name) ?? Array.Empty<string>())}] "
                    + $"+ relics [{string.Join(", ", ghostRelicTypes?.Select(t => t.Name) ?? Array.Empty<string>())}]");
            }
            else if (wantsDeckInjection)
            {
                GhostLog.Info($"Skipping M2Deck injection: it is Ironclad-specific content and the selected character is {character.Id}. Running with {character.Id}'s own stock deck instead.");
            }

            RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
            await PreloadManager.LoadRunAssets(new CharacterModel[] { character });
            RunManager.Instance.Launch();
            game.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
            await RunManager.Instance.SetActInternal(0);
            RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(runState.RunLocation);
            RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(runState.MapLocation);

            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, ModelDb.Encounter<GhostDuelEncounter>().ToMutable());
            GhostLog.Info("Debug run entered combat.");

            // Temporary: trace every pile move for both players so a reported card-count
            // discrepancy can be root-caused from the log instead of guessed at.
            PileAudit.AttachAll(human, "Human");
            PileAudit.AttachAll(ghostPlayer, "Ghost");
        }
        catch (Exception ex)
        {
            GhostLog.Error("Debug boot failed", ex);
        }
    }
}
