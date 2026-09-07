using System;
using System.Linq;
using System.Threading.Tasks;
using GhostDuel.Diagnostics;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace GhostDuel.Debug;

/// <summary>
/// Adds two buttons to the main menu's existing vertical button list (<c>%MainMenuTextButtons</c>)
/// by duplicating a real button node, instead of hand-laying-out new UI — CLAUDE.md: "No hardcoded
/// screen coordinates. Anchor to nodes." POSTMORTEM F16 is the cautionary tale for doing more than
/// this (five duplicated buttons, hardcoded pixel gaps, a `_locKeyPrefix` reflection hack); these are
/// plain buttons, positioned by the native container's own layout, with labels set directly via
/// <c>MegaLabel.SetTextAutoSize</c> instead of touching localization internals. One runs
/// <see cref="M2Deck"/>'s fixed 20-card/5-relic deck via <see cref="DebugBoot"/>'s hand-built shortcut
/// (instant, no real character-select/Neow/map); one starts <see cref="RealFlowGhostDuelEntry"/>'s
/// real run flow instead, replacing the old "M3 random" button — random-Ironclad-deck coverage is
/// better served by that real flow now (a real Neow bonus/curse on top of a real starting deck) than
/// by a second hand-built shortcut. The M1 stock-deck button (kept as a regression check through M2)
/// was removed once M2's own gate passed live — every M1 check still runs, just exercised via these.
/// </summary>
internal static class DebugMenuButton
{
    [HarmonyPatch(typeof(NMainMenu), "_Ready")]
    private static class Postfix_AddButton
    {
        [HarmonyPostfix]
        private static void Postfix(NMainMenu __instance)
        {
            Control container = __instance.GetNode<Control>("%MainMenuTextButtons");
            NMainMenuTextButton? template = container.GetChildren().OfType<NMainMenuTextButton>().LastOrDefault();
            if (template is null)
            {
                GhostLog.Warn("DebugMenuButton: no template button found under %MainMenuTextButtons; skipping.");
                return;
            }

            AddButton(container, template, "GhostDuelDebugM3Button", "GHOST DUEL DEBUG (M3 fixed)",
                () => DebugBoot.StartDebugRunAsync(NGame.Instance!, M2Deck.CardTypes, M2Deck.RelicTypes));
            AddButton(container, template, "GhostDuelDebugM8Button", "GHOST DUEL DEBUG (M8 real run)",
                () =>
                {
                    RealFlowGhostDuelEntry.Start(__instance);
                    return Task.CompletedTask;
                });

            // M7 debug aid: a persistent toggle, not a "start" action — click it, then use the game's
            // own "LEGACY ASCENSION" button normally. Relabels itself on each click so its current
            // on/off state is visible without needing to check the log.
            NMainMenuTextButton oneHpButton = AddButton(container, template, "GhostDuelDebugOneHpButton", OneHpButtonLabel(),
                () =>
                {
                    DebugOneHpEnemiesToggle.Toggle();
                    return Task.CompletedTask;
                });
            oneHpButton.Released += _ => oneHpButton.label?.SetTextAutoSize(OneHpButtonLabel());
        }

        private static string OneHpButtonLabel() => $"GHOST DUEL DEBUG (M7 1 HP enemies: {(DebugOneHpEnemiesToggle.Enabled ? "ON" : "OFF")})";

        private static NMainMenuTextButton AddButton(Control container, NMainMenuTextButton template, string name, string text, Func<Task> onPressed)
        {
            NMainMenuTextButton button = (NMainMenuTextButton)template.Duplicate();
            button.Name = name;
            container.AddChildSafely(button);
            button.Visible = true;
            button.Enable();
            button.label?.SetTextAutoSize(text);
            button.Released += _ => TaskHelper.RunSafely(onPressed());
            return button;
        }
    }
}
