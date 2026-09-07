using System.Collections.Generic;
using System.Linq;
using GhostDuel.Ai;
using GhostDuel.Debug;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using GhostDuel.Progression;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Multiplayer;

/// <summary>
/// PLAN.md M9's "multiplayer debug fight" — the party-fight equivalent of
/// <c>GhostDuel.Debug.RealFlowGhostDuelEntry</c>, mirroring its exact substitution shape
/// (<c>ActModel.PullNextEncounter</c> prefix, fires on the first real Monster room) rather than
/// building a separate bypass: the point of a debug shortcut here is skipping "play through three acts
/// first," not skipping the actual cross-client machinery being tested. Builds one fresh stock Ghost
/// per human currently in the run (whoever has joined the lobby by the time the first Monster room is
/// reached) — no ladder/snapshot data needed, so this always works regardless of anyone's progress.
///
/// A 5th button on <c>NMultiplayerHostSubmenu</c>, host-only (the same screen only the host ever sees),
/// visible unconditionally rather than gated on lobby population — the party is read from
/// <c>RunState.Players</c> at substitution time, whatever it is by then, matching how a real party's
/// size is only known once players have actually joined.
/// </summary>
internal static class MultiplayerDebugGhostDuelEntry
{
    private static bool _armed;

    public static void Start(NMultiplayerHostSubmenu submenu)
    {
        if (GhostSession.Current is not null)
        {
            GhostLog.Warn("MultiplayerDebugGhostDuelEntry: a previous GhostSession was still active — disposing it and starting fresh.");
            GhostSession.Current.Dispose();
        }

        GhostLog.OpenLogFile();
        GhostLog.Info("MultiplayerDebugGhostDuelEntry: starting a multiplayer debug run — the first Monster fight becomes a Ghost-party fight, one fresh stock Ghost per connected human.");
        _armed = true;

        Control loadingOverlay = MultiplayerUiAccess.GetLoadingOverlay(submenu);
        NSubmenuStack stack = NSubmenuStackAccess.GetStack(submenu);
        TaskHelper.RunSafely(NMultiplayerHostSubmenu.StartHostAsync(GameMode.Standard, loadingOverlay, stack));
    }

    [HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEncounter))]
    private static class Prefix_SubstituteFirstMonsterFightWithGhostParty
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
                GhostLog.Warn("MultiplayerDebugGhostDuelEntry: no active RunState/players found at the first Monster room — falling back to a normal encounter.");
                return true;
            }

            List<GhostPartyMember> party = new(runState.Players.Count);
            for (int i = 0; i < runState.Players.Count; i++)
            {
                Player human = runState.Players[i];
                Player ghost = GhostPlayerFactory.CreateDebugGhost(human.Character, i);
                GhostPlayerFactory.JoinRun(ghost, runState);
                party.Add(new GhostPartyMember(ghost, new WeightedRandomGhostChooser()));
            }
            GhostSession.Begin(party);
            GhostLog.Party("debug-entry", $"[{string.Join(", ", party.Select(m => $"{m.Player.Character.Id}#{m.Player.NetId} (fresh stock, debug)"))}]");

            __result = ModelDb.Encounter<GhostDuelEncounter>();
            return false;
        }
    }

    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), "_Ready")]
    private static class Postfix_AddDebugButton
    {
        [HarmonyPostfix]
        private static void Postfix(NMultiplayerHostSubmenu __instance)
        {
            NSubmenuButton? dailyButton = __instance.GetNodeOrNull<NSubmenuButton>("DailyButton");
            NSubmenuButton? customButton = __instance.GetNodeOrNull<NSubmenuButton>("CustomRunButton");
            NSubmenuButton? modeButton = __instance.GetNodeOrNull<NSubmenuButton>("MultiplayerLegacyAscensionButton");
            NSubmenuButton? template = modeButton ?? customButton;
            if (template is null)
            {
                GhostLog.Warn("MultiplayerDebugGhostDuelEntry: no template button found under NMultiplayerHostSubmenu; skipping debug button.");
                return;
            }

            NSubmenuButton button = (NSubmenuButton)template.Duplicate();
            button.Name = "MultiplayerDebugGhostDuelButton";
            template.GetParent().AddChildSafely(button);
            button.Visible = true;
            button.Enable();

            if (customButton is not null && dailyButton is not null)
            {
                Vector2 gap = template.Position - customButton.Position;
                button.Position = template.Position + (gap == Vector2.Zero ? template.Position - dailyButton.Position : gap);
            }

            button.GetNodeOrNull<MegaLabel>("%Title")?.SetTextAutoSize("DEBUG: GHOST PARTY");
            MegaRichTextLabel? description = button.GetNodeOrNull<MegaRichTextLabel>("%Description");
            if (description is not null)
            {
                description.Text = "Skip to a Ghost-party fight with fresh stock Ghosts, for two-client testing.";
            }

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Start(__instance)));
        }
    }
}
