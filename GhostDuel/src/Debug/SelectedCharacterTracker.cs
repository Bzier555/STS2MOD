using GhostDuel.Diagnostics;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace GhostDuel.Debug;

/// <summary>
/// P19: tracks whichever character the player most recently clicked on the character-select screen,
/// so the debug menu buttons (<see cref="DebugMenuButton"/>) can build the debug run around it
/// instead of hardcoding Ironclad — letting the same buttons debug Silent/Defect/Watcher/Necrobinder
/// once their own M8 sub-milestones exist, just by picking that character first.
///
/// Confirmed by research (no direct source citation needed beyond what's already established): no
/// live "currently selected character" state exists anywhere at main-menu-load time.
/// <c>NCharacterSelectScreen</c>'s own selection lives inside a <c>StartRunLobby</c> object that
/// isn't constructed until the player has already opened that screen (<c>InitializeSingleplayer</c>,
/// called only from the Play button), and there is no persisted "last selected character" save field
/// anywhere in <c>ProgressState</c>/<c>SerializableProgress</c> to fall back on either. So this mirrors
/// exactly what the engine's own default-selection logic does for a fresh lobby player
/// (<c>StartRunLobby.TryAddPlayerInFirstAvailableSlot</c>: defaults to Ironclad,
/// `ModelDb.Character&lt;Ironclad&gt;()`, until <c>SetLocalCharacter</c> overrides it) — a postfix on
/// <c>NCharacterSelectScreen.SelectCharacter</c> (the one method every character click funnels
/// through, confirmed public, called from <c>ICharacterSelectButtonDelegate</c>) stashes the picked
/// <c>CharacterModel</c> into this static field, defaulting to Ironclad until the player picks
/// anything this session — not a Ghost Duel session concern, so no <c>GhostSession.Current</c> guard
/// is needed; this is main-menu/character-select state, entirely outside combat.
/// </summary>
internal static class SelectedCharacterTracker
{
    public static CharacterModel Current { get; private set; } = ModelDb.Character<Ironclad>();

    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
    private static class Postfix_TrackSelection
    {
        [HarmonyPostfix]
        private static void Postfix(CharacterModel characterModel)
        {
            Current = characterModel;
            GhostLog.Info($"SelectedCharacterTracker: debug runs will now use {characterModel.Id}.");
        }
    }
}
