using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace GhostDuel.Progression;

/// <summary>
/// Two reflected private members needed to make the ascension picker character-independent (per the
/// user's 2026-09-04 request, mirroring how the base game's own multiplayer ascension picker already
/// works) and to recolor its "fire" icon:
///
/// <c>StartRunLobby.MaxAscension</c> (confirmed: <c>StartRunLobby.cs:82</c>,
/// <c>public int MaxAscension { get; private set; }</c>) — get-only from outside the class. Normally
/// only <c>SetSingleplayerAscensionAfterCharacterChanged</c> (<c>StartRunLobby.cs:508-552</c>) writes
/// it, always from the *selected character's own* <c>CharacterStats.MaxAscension</c> — there is no
/// public "use a different, character-independent max" entry point the way multiplayer's own
/// <c>AddLocalHostPlayer</c>-driven path has one (<c>ProgressState.MaxMultiplayerAscension</c>,
/// confirmed account-wide and never re-derived per character). Setting this private-setter property
/// directly is the narrowest way to reuse the *rest* of that method's already-correct behavior
/// (Random Character handling, the ascension-unlocked FTUE popup, etc.) unchanged, then just correct
/// the one number afterward.
///
/// <c>NAscensionPanel._iconHsv</c> (confirmed: <c>NAscensionPanel.cs:205</c>,
/// <c>private ShaderMaterial _iconHsv;</c>) — the same HSV-shader tinting primitive
/// <c>SetFireRed</c>/<c>SetFireBlue</c> already use (<c>NAscensionPanel.cs:286-298</c>,
/// <c>_iconHsv.SetShaderParameter(_h, ...)</c>); reused here with a purple hue instead of adding a new
/// visual asset or shader.
/// </summary>
internal static class LegacyAscensionUiAccess
{
    private static readonly PropertyInfo MaxAscensionProperty =
        typeof(StartRunLobby).GetProperty(nameof(StartRunLobby.MaxAscension), BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMemberException(typeof(StartRunLobby).FullName, nameof(StartRunLobby.MaxAscension));

    private static readonly MethodInfo MaxAscensionSetter =
        MaxAscensionProperty.GetSetMethod(nonPublic: true)
        ?? throw new MissingMethodException(typeof(StartRunLobby).FullName, "set_" + nameof(StartRunLobby.MaxAscension));

    private static readonly FieldInfo IconHsvField =
        typeof(NAscensionPanel).GetField("_iconHsv", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NAscensionPanel).FullName, "_iconHsv");

    public static void SetMaxAscension(StartRunLobby lobby, int value) =>
        MaxAscensionSetter.Invoke(lobby, new object[] { value });

    public static Godot.ShaderMaterial? GetIconHsv(NAscensionPanel panel) =>
        (Godot.ShaderMaterial?)IconHsvField.GetValue(panel);
}
