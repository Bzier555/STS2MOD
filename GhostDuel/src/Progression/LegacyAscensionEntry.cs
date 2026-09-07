using System;
using System.Collections.Generic;
using System.Linq;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Progression;

/// <summary>
/// M7b (PLAN.md): the Legacy Ascension mode's entry point — a 4th button, a peer of Standard/Daily/
/// Custom (added, not swapped in for Custom Run) in <c>NSingleplayerSubmenu</c>. Real character
/// select, real Neow, real map, same as Standard — the ladder's ascension level is not forced to a
/// single value; per the user's 2026-09-04 request, the player picks it from the same native
/// <c>NAscensionPanel</c> stepper Standard/Custom use, just bounded to
/// <see cref="GhostSnapshotStore.GetMaxSelectableLevel"/> instead of the selected character's own
/// <c>CharacterStats.MaxAscension</c> — mirroring how the base game's own multiplayer-host ascension
/// picker is already character-independent (bound to account-wide
/// <c>ProgressState.MaxMultiplayerAscension</c> instead, confirmed by reading
/// <c>StartRunLobby.AddLocalHostPlayer</c>/<c>UpdateMaxMultiplayerAscension</c>), not a new UI pattern
/// invented for this mode.
///
/// The one native method that ever writes <c>StartRunLobby.MaxAscension</c> for a singleplayer-shaped
/// run, <c>SetSingleplayerAscensionAfterCharacterChanged</c> (<c>StartRunLobby.cs:508-552</c>), always
/// derives it from the *selected character's own* progress — there's no built-in "ignore the
/// character" switch. Rather than reimplement that method's several branches (Random Character
/// handling, the ascension-unlocked FTUE popup, the no-progress-yet case), a postfix lets it run in
/// full and then corrects the one number afterward via <see cref="LegacyAscensionUiAccess"/>,
/// re-triggering the exact same native propagation (<c>LobbyListener.MaxAscensionChanged()</c> →
/// <c>NCharacterSelectScreen.MaxAscensionChanged()</c> → <c>NAscensionPanel.SetMaxAscension(...)</c>,
/// confirmed by reading <c>NCharacterSelectScreen.cs:939-942</c>) that the native call already used —
/// so the on-screen picker, its clamping, and everything downstream stay consistent with no new UI
/// code. This re-fires every time the player switches characters (matching the method's own name), so
/// the bound stays correct throughout browsing, not just once.
/// </summary>
internal static class LegacyAscensionEntry
{
    private static readonly StringName HueParam = new("h");
    private static readonly StringName ValueParam = new("v");

    /// <summary>A cool violet, between <c>SetFireBlue</c>'s 0.52 and <c>SetFireRed</c>'s 1.0/0.0 wrap
    /// point (<c>NAscensionPanel.cs:286-298</c>) — same shader, same primitive, just a different hue
    /// so a Legacy Ascension run reads as visually distinct from a normal one at a glance.</summary>
    private const float PurpleHue = 0.78f;

    private static bool _armed;

    public static void Start(NSingleplayerSubmenu submenu)
    {
        if (GhostSession.Current is not null)
        {
            GhostLog.Warn("LegacyAscensionEntry: a previous GhostSession was still active — disposing it and starting fresh.");
            GhostSession.Current.Dispose();
        }

        GhostLog.OpenLogFile();
        _armed = true;
        GhostLog.Info($"LegacyAscensionEntry: starting Legacy Ascension character select (max selectable A{GhostSnapshotStore.GetMaxSelectableLevel()}).");

        NSubmenuStack stack = NSubmenuStackAccess.GetStack(submenu);
        NCharacterSelectScreen screen = stack.GetSubmenuType<NCharacterSelectScreen>();
        screen.InitializeSingleplayer();
        stack.Push(screen);
    }

    /// <summary>Defensive: if the player backs out of character select without embarking, don't leave
    /// a later, unrelated Standard/Custom run's ascension picker character-independent too.</summary>
    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
    private static class Postfix_DisarmOnBackOut
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (_armed)
            {
                _armed = false;
                GhostLog.Info("LegacyAscensionEntry: character select closed without embarking — disarmed.");
            }
        }
    }

    [HarmonyPatch(typeof(StartRunLobby), "SetSingleplayerAscensionAfterCharacterChanged")]
    private static class Postfix_MakeAscensionMaxCharacterIndependent
    {
        [HarmonyPostfix]
        private static void Postfix(StartRunLobby __instance)
        {
            if (!_armed)
            {
                return;
            }
            int max = GhostSnapshotStore.GetMaxSelectableLevel();
            LegacyAscensionUiAccess.SetMaxAscension(__instance, max);
            __instance.LobbyListener.MaxAscensionChanged();
            __instance.SyncAscensionChange(Math.Min(__instance.Ascension, max));
        }
    }

    /// <summary>
    /// Confirmed live (2026-09-04): with nothing beaten yet, <c>GetMaxSelectableLevel()</c> correctly
    /// returns 0 (only A0 is selectable), but <c>NAscensionPanel.SetMaxAscension</c>
    /// (<c>NAscensionPanel.cs:343-353</c>) does <c>base.Visible = maxAscension &gt; 0</c> — vanilla
    /// ascension treats 0 as "off, nothing to pick," so it hides the whole control, icon included
    /// (explaining why the purple tint was invisible too — same hidden node, not a second bug).
    /// Legacy Ascension's level 0 is a real, always-selectable rung, not "off," so this native
    /// equivalence doesn't hold for this mode. Postfixing <c>SetMaxAscension</c> itself (every call
    /// site, including <c>Initialize</c>'s own early <c>SetMaxAscension(0)</c>) rather than only the
    /// one call site above keeps this correct regardless of ordering.
    /// </summary>
    [HarmonyPatch(typeof(NAscensionPanel), nameof(NAscensionPanel.SetMaxAscension))]
    private static class Postfix_KeepPanelVisibleAtZero
    {
        [HarmonyPostfix]
        private static void Postfix(NAscensionPanel __instance)
        {
            if (_armed)
            {
                __instance.Visible = true;
            }
        }
    }

    [HarmonyPatch(typeof(NAscensionPanel), nameof(NAscensionPanel.Initialize))]
    private static class Postfix_PurpleFireForLegacyAscension
    {
        [HarmonyPostfix]
        private static void Postfix(NAscensionPanel __instance, MultiplayerUiMode mode)
        {
            if (!_armed || mode != MultiplayerUiMode.Singleplayer)
            {
                return;
            }
            ShaderMaterial? iconHsv = LegacyAscensionUiAccess.GetIconHsv(__instance);
            iconHsv?.SetShaderParameter(HueParam, PurpleHue);
            iconHsv?.SetShaderParameter(ValueParam, 1.1f);
        }
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
    private static class Prefix_TagLegacyAscensionRun
    {
        [HarmonyPrefix]
        private static void Prefix(ref IReadOnlyList<ModifierModel> modifiers, int ascensionLevel)
        {
            if (!_armed)
            {
                return;
            }
            _armed = false;

            LegacyAscensionModifier modifier = (LegacyAscensionModifier)ModelDb.Modifier<LegacyAscensionModifier>().ToMutable();
            modifier.LadderLevel = ascensionLevel;
            List<ModifierModel> combined = (modifiers ?? Array.Empty<ModifierModel>()).ToList();
            combined.Add(modifier);
            modifiers = combined;

            GhostLog.Info($"LegacyAscensionEntry: tagged run at A{ascensionLevel} with LegacyAscensionModifier.");
        }
    }

    [HarmonyPatch(typeof(NSingleplayerSubmenu), "_Ready")]
    private static class Postfix_AddButton
    {
        [HarmonyPostfix]
        private static void Postfix(NSingleplayerSubmenu __instance)
        {
            NSubmenuButton? dailyButton = __instance.GetNodeOrNull<NSubmenuButton>("DailyButton");
            NSubmenuButton? template = __instance.GetNodeOrNull<NSubmenuButton>("CustomRunButton");
            if (template is null)
            {
                GhostLog.Warn("LegacyAscensionEntry: CustomRunButton template not found under NSingleplayerSubmenu; skipping menu button.");
                return;
            }

            NSubmenuButton button = (NSubmenuButton)template.Duplicate();
            button.Name = "LegacyAscensionButton";
            template.GetParent().AddChildSafely(button);
            button.Visible = true;
            button.Enable();

            // No shared layout container between these buttons (confirmed: neither NSingleplayerSubmenu
            // nor NSubmenuButton positions them in code, so it's scene-authored) — a plain Duplicate()
            // would land exactly on top of CustomRunButton. Anchor the new button's position off the
            // real, already-laid-out sibling gap instead of a literal offset (CLAUDE.md: no hardcoded
            // screen coordinates) — extrapolate the Daily-to-Custom spacing one step further.
            if (dailyButton is not null)
            {
                button.Position = template.Position + (template.Position - dailyButton.Position);
            }
            else
            {
                GhostLog.Warn("LegacyAscensionEntry: DailyButton not found; could not compute the new button's position from sibling spacing, leaving it at CustomRunButton's own position.");
            }

            // Bypasses SetIconAndLocalization (which needs a registered "main_menu_ui" loc-table
            // entry we don't have) the same way DebugMenuButton.cs bypasses NMainMenuTextButton's own
            // localization for the exact same reason — set the duplicated button's own label text
            // nodes directly instead.
            button.GetNodeOrNull<MegaLabel>("%Title")?.SetTextAutoSize("LEGACY ASCENSION");
            MegaRichTextLabel? description = button.GetNodeOrNull<MegaRichTextLabel>("%Description");
            if (description is not null)
            {
                description.Text = "Fight the ghost of your own last winning run.";
            }

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Start(__instance)));
        }
    }
}
