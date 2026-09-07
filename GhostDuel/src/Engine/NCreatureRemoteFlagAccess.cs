using System.Reflection;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace GhostDuel.Engine;

/// <summary>
/// <c>NCreature._isRemotePlayerOrPet</c> (confirmed: <c>NCreature.cs:379</c>) is a private field with
/// no public setter, computed once in <c>_Ready()</c> from <c>LocalContext.IsMe(...)</c> — which is
/// always false for the Ghost (its <c>Player.NetId</c> is never the local human's), so the Ghost's own
/// creature and any pet it owns are permanently misclassified as "a remote co-op teammate's" and have
/// their health/Block display hidden by default, only revealing on hover (and hiding the human's own
/// bar while it does). This is the one reflection dependency P21 (<c>EnginePatches.cs</c>) needs; it
/// lives in this single file with a load-time existence assertion, matching <see cref="CreatureSideAccess"/>'s
/// established pattern, so a game update fails loudly here rather than mysteriously in combat.
/// </summary>
internal static class NCreatureRemoteFlagAccess
{
    private static readonly FieldInfo IsRemotePlayerOrPetField =
        typeof(NCreature).GetField("_isRemotePlayerOrPet", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NCreature).FullName, "_isRemotePlayerOrPet");

    public static void SetIsRemotePlayerOrPet(NCreature node, bool value) =>
        IsRemotePlayerOrPetField.SetValue(node, value);
}
