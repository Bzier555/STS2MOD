using System.Linq;
using GhostDuel.Ai;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Debug;

/// <summary>
/// P20: an alternative debug entry point, distinct from <see cref="DebugBoot"/>'s hand-built shortcut
/// (which skips character select, Neow and map generation entirely). This one plays out a real run
/// start — real character select, real Neow event, real map — and substitutes the Ghost Duel only for
/// the first normal-monster fight the player actually reaches, with whatever character/deck/relics
/// Neow left them with. M8's own premise ("one character per sub-milestone") needs this: a hand-built
/// debug run can mirror a selected character's starting kit, but not what a real Neow bonus/curse
/// changes about it.
///
/// <c>Start</c> (called from a menu button, <see cref="DebugMenuButton"/>) reproduces exactly what
/// <c>NMainMenu.SingleplayerButtonPressed</c> does for a player with existing save data
/// (<c>NMainMenu.cs:770-781</c>: <c>SubmenuStack.GetSubmenuType&lt;NCharacterSelectScreen&gt;()
/// .InitializeSingleplayer(); SubmenuStack.Push(screen);</c>) — skipping only the cosmetic
/// controller-focus assignment — then arms <see cref="_armed"/> and lets the player drive the real
/// screen normally (pick a character, Embark). Neow is guaranteed to run first only when
/// <c>UnlockState.IsEpochRevealed&lt;NeowEpoch&gt;()</c> is true (confirmed:
/// <c>RunManager.cs:445-461,502-507,724-756</c> — otherwise the map's starting point is force-set to
/// <c>Monster</c> and Neow is skipped entirely); on any established save this holds.
///
/// The substitution itself is a prefix on <c>ActModel.PullNextEncounter(RoomType)</c> — confirmed by
/// full-tree grep to be the single call site every non-debug room transition funnels an encounter
/// through (<c>RunManager.cs:878</c>, reached from <c>CreateRoom</c>'s <c>Monster</c>/<c>Elite</c>/
/// <c>Boss</c> branch, itself reached from <c>EnterMapPointInternal</c> — the lazy, real path,
/// confirmed distinct from and later converging with <c>EnterRoomDebug</c>'s explicit-model path at
/// the shared <c>EnterRoom</c>). Gated on <see cref="_armed"/> and <c>roomType == RoomType.Monster</c>
/// so it waits, unconsumed, through Neow and any Rest/Shop/Event rooms the player visits first, and
/// fires on the first real Monster room — never Elite/Boss, which use the same method with a
/// different <c>roomType</c> and are left alone.
///
/// The Ghost is built and joined to the real <c>RunState</c> — obtained via <c>RunManager
/// .DebugOnlyGetState()</c>, the one public accessor to it (confirmed no other exists) — using the
/// exact same <see cref="GhostPlayerFactory"/> calls <c>DebugBoot</c> already uses successfully;
/// neither method cares whether the <c>RunState</c> was hand-built or came from the real flow.
///
/// <b>Known limitation, not solved here</b>: `RunManager.EnterMapPointInternal` autosaves on every
/// room entry (`shouldSave: true` for a real run, unlike `DebugBoot`'s `false`), and neither
/// <see cref="GhostSession"/> nor the Ghost `Player` are ever part of `RunState`/`SerializableRun` by
/// design (they are deliberately not real run participants, per `GhostPlayerFactory.JoinRun`'s own
/// doc comment) — a save/reload mid-Ghost-fight would not restore either. Not addressed, since this
/// is a dev-only debug entry point, not a shipping feature; noted so it isn't mistaken for a real bug
/// if a reload during a Ghost Duel started this way behaves oddly.
/// </summary>
internal static class RealFlowGhostDuelEntry
{
    private static bool _armed;

    public static void Start(NMainMenu mainMenu)
    {
        if (GhostSession.Current is not null)
        {
            GhostLog.Warn("RealFlowGhostDuelEntry: a previous GhostSession was still active — disposing it and starting fresh.");
            GhostSession.Current.Dispose();
        }

        GhostLog.OpenLogFile();
        GhostLog.Info("Starting Ghost Duel real-flow debug run: play through character select and Neow normally; the first Monster fight becomes the Ghost Duel.");
        _armed = true;

        var screen = mainMenu.SubmenuStack.GetSubmenuType<NCharacterSelectScreen>();
        screen.InitializeSingleplayer();
        mainMenu.SubmenuStack.Push(screen);
    }

    [HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEncounter))]
    private static class Prefix_SubstituteFirstMonsterFight
    {
        [HarmonyPrefix]
        private static bool Prefix(RoomType roomType, ref EncounterModel __result)
        {
            if (!_armed || roomType != RoomType.Monster)
            {
                return true;
            }
            _armed = false;

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null || runState.Players.Count == 0)
            {
                GhostLog.Warn("RealFlowGhostDuelEntry: no active RunState/human player found at the first Monster room — falling back to a normal encounter.");
                return true;
            }

            CharacterModel character = runState.Players[0].Character;
            Player ghostPlayer = GhostPlayerFactory.CreateDebugGhost(character);
            GhostPlayerFactory.JoinRun(ghostPlayer, runState);
            GhostSession session = GhostSession.Begin(ghostPlayer, new WeightedRandomGhostChooser());

            // M8.5 (PLAN.md): the Ghost's base deck is a random 20 cards / 5 relics from this
            // character's own pool, "similar to the previous M2 random" — replacing what would
            // otherwise be this character's exact stock/Neow-modified starting kit. Deferred to P5
            // (EnginePatches.cs), the earliest point that can actually await the native async
            // commands ConfigureDeckAsync needs; see PendingRandomDeck's own doc comment.
            var (randomCards, randomRelics) = RandomDeckBuilder.BuildRandomDeck(character);
            session.PendingRandomDeck = (randomCards, randomRelics);
            GhostLog.Info($"RealFlowGhostDuelEntry: substituting the first Monster encounter with GhostDuelEncounter for character={character.Id}, "
                + $"random deck=[{string.Join(", ", randomCards.Select(t => t.Name))}] relics=[{string.Join(", ", randomRelics.Select(t => t.Name))}].");

            __result = ModelDb.Encounter<GhostDuelEncounter>();
            return false;
        }
    }
}
