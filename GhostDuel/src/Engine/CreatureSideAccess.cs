using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace GhostDuel.Engine;

/// <summary>
/// <c>Creature.Side</c> is a get-only auto-property (confirmed:
/// <c>MegaCrit.Sts2.Core.Entities.Creatures/Creature.cs:117</c>). Placing a <see cref="MegaCrit.Sts2.Core.Entities.Players.Player"/>
/// on <see cref="CombatSide.Enemy"/> requires writing its backing field directly. This is the one
/// reflection dependency the design accepts (ARCHITECTURE.md §3); it lives in this single file with a
/// load-time existence assertion so a game update fails loudly here rather than mysteriously in combat.
/// </summary>
internal static class CreatureSideAccess
{
    private static readonly FieldInfo SideBackingField =
        typeof(Creature).GetField("<Side>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Creature).FullName, "<Side>k__BackingField");

    public static void SetSide(Creature creature, CombatSide side) =>
        SideBackingField.SetValue(creature, side);
}
