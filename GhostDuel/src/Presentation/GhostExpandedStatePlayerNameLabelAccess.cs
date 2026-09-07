using System.Reflection;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace GhostDuel.Presentation;

/// <summary>
/// <c>NMultiplayerPlayerExpandedState._playerNameLabel</c> (confirmed:
/// <c>NMultiplayerPlayerExpandedState.cs:185</c>, <c>private MegaRichTextLabel _playerNameLabel;</c>,
/// populated in <c>_Ready()</c> at line 245) is set from
/// <c>PlatformUtil.GetPlayerName(platform, player.NetId)</c> (line 254) — for a Ghost's synthetic
/// <c>NetId</c> (<c>GhostPlayerFactory.GhostNetId</c>, far outside any real platform id range), neither
/// <c>SteamPlatformUtilStrategy</c> nor <c>NullPlatformUtilStrategy</c> can resolve a name, and both fall
/// back to <c>playerId.ToString()</c> — confirmed live (the user saw a long numeric string as the
/// Ghost's name). Patching <c>PlatformUtil.GetPlayerName</c> itself would be the wrong fix (CLAUDE.md:
/// "could a narrower target work?" — that helper is read from many unrelated call sites); this field is
/// touched from exactly one place instead — <see cref="GhostDeckViewerPresenter"/>'s own click handler,
/// right after it opens this exact screen for a Ghost — with no new Harmony patch needed at all. One
/// file, one reflected name, load-time existence assertion, matching this project's established
/// accessor pattern.
/// </summary>
internal static class GhostExpandedStatePlayerNameLabelAccess
{
    private static readonly FieldInfo PlayerNameLabelField =
        typeof(NMultiplayerPlayerExpandedState).GetField("_playerNameLabel", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NMultiplayerPlayerExpandedState).FullName, "_playerNameLabel");

    public static MegaRichTextLabel GetPlayerNameLabel(NMultiplayerPlayerExpandedState screen) =>
        (MegaRichTextLabel)PlayerNameLabelField.GetValue(screen)!;
}
