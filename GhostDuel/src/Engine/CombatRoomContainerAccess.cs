using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace GhostDuel.Engine;

/// <summary>
/// <c>NCombatRoom._enemyContainer</c> is a private field
/// (confirmed: <c>MegaCrit.Sts2.Core.Nodes.Rooms/NCombatRoom.cs:249</c>) with no public accessor.
/// <c>NCombatRoom.AddCreature</c> parents every <c>IsPlayer</c> node into <c>_allyContainer</c>
/// regardless of <c>Side</c> (ENGINE-NOTES.md §7 Q4), so placing the Ghost on the visual enemy side
/// (P6 in ARCHITECTURE.md §4) requires reparenting into this container directly. Isolated in one file
/// with a load-time existence assertion, per CLAUDE.md's reflection rule.
/// </summary>
internal static class CombatRoomContainerAccess
{
    private static readonly FieldInfo EnemyContainerField =
        typeof(NCombatRoom).GetField("_enemyContainer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NCombatRoom).FullName, "_enemyContainer");

    public static Control GetEnemyContainer(NCombatRoom room) =>
        (Control)EnemyContainerField.GetValue(room)!;
}
