using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace GhostDuel.Multiplayer;

/// <summary>
/// PLAN.md M9b: <c>NMultiplayerHostSubmenu._loadingOverlay</c> (confirmed private,
/// <c>NMultiplayerHostSubmenu.cs:126</c>, <c>private Control _loadingOverlay;</c>) — needed so
/// <c>MultiplayerLegacyAscensionEntry.Start</c> can call the class's own public static
/// <c>StartHostAsync(GameMode, Control, NSubmenuStack)</c> directly (reusing 100% of the native
/// host-starting flow — Steam vs. ENet transport choice, error popups — rather than reimplementing
/// any of it) instead of the instance-only <c>StartHost(GameMode)</c> convenience wrapper, which has no
/// public way to read the two parameters it closes over.
/// </summary>
internal static class MultiplayerUiAccess
{
    private static readonly FieldInfo LoadingOverlayField =
        typeof(NMultiplayerHostSubmenu).GetField("_loadingOverlay", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NMultiplayerHostSubmenu).FullName, "_loadingOverlay");

    public static Control GetLoadingOverlay(NMultiplayerHostSubmenu submenu) =>
        (Control)LoadingOverlayField.GetValue(submenu)!;
}
