using GhostDuel.Diagnostics;
using GhostDuel.Progression;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace GhostDuel.Ui;

/// <summary>
/// PLAN.md M10a: adds a new button to the native Compendium menu, duplicating <c>RunHistoryButton</c>
/// the same way every other menu button this mod has added does (<c>LegacyAscensionEntry
/// .Postfix_AddButton</c>, <c>MultiplayerLegacyAscensionEntry.Postfix_AddButton</c>) — reparent a
/// cloned template node as a sibling, retitle it, wire its own click handler.
/// </summary>
internal static class GhostLedgerEntry
{
    [HarmonyPatch(typeof(NCompendiumSubmenu), "_Ready")]
    private static class Postfix_AddButton
    {
        [HarmonyPostfix]
        private static void Postfix(NCompendiumSubmenu __instance)
        {
            NCompendiumBottomButton? template = __instance.GetNodeOrNull<NCompendiumBottomButton>("%RunHistoryButton");
            if (template is null)
            {
                GhostLog.Warn("GhostLedgerEntry: RunHistoryButton template not found under NCompendiumSubmenu; skipping menu button.");
                return;
            }

            NCompendiumBottomButton button = (NCompendiumBottomButton)template.Duplicate();
            button.Name = "GhostLedgerButton";
            template.GetParent().AddChildSafely(button);
            button.Visible = true;
            button.Enable();
            // Anchored off the existing template's own position rather than a hardcoded offset — a
            // small, deliberately simple vertical nudge since this is a 4th button in a row of 3.
            button.Position = template.Position + new Vector2(0f, template.Size.Y + 8f);

            // NCompendiumBottomButton's own SetLocalization(string) resolves a loc-table key we don't
            // have an entry for — calling the same public MegaLabel.SetTextAutoSize it uses
            // internally, directly, on its child "Label" node (confirmed via NCompendiumBottomButton
            // .cs: `_label = GetNode<MegaLabel>("Label")`), matches the established pattern this mod
            // already uses elsewhere (LegacyAscensionEntry/MultiplayerLegacyAscensionEntry's own
            // button retitling) without needing a fake loc key or reflection into the private field.
            button.GetNodeOrNull<MegaLabel>("Label")?.SetTextAutoSize("GHOST LEDGER");

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Open(__instance)));
        }
    }

    /// <summary>
    /// Pushed via <c>NSubmenuStack.Push(NSubmenu)</c> — not <c>NCapstoneContainer</c> (see
    /// <c>GhostLedgerScreen</c>'s own doc comment: that container only exists during an active run,
    /// and the Compendium is a main-menu screen). Mirrors exactly how every native submenu lazily
    /// spawns itself (<c>NMainMenuSubmenuStack.GetSubmenuType(Type)</c>: construct, <c>AddChildSafely</c>
    /// onto the stack, then <c>Push</c>) — the same two steps <see cref="NSubmenuStackAccess"/> already
    /// lets <c>LegacyAscensionEntry</c> use for the Standard/Daily/Custom-sibling button.
    /// </summary>
    private static void Open(NCompendiumSubmenu compendium)
    {
        NSubmenuStack stack = NSubmenuStackAccess.GetStack(compendium);
        GhostLedgerScreen screen = new();
        stack.AddChildSafely(screen);
        stack.Push(screen);
    }
}
