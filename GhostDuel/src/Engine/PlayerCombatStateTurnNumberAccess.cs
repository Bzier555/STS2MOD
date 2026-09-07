using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;

namespace GhostDuel.Engine;

/// <summary>
/// Reflected setter for <c>PlayerCombatState.TurnNumber</c> (<c>{ get; private set; }</c>,
/// <c>PlayerCombatState.cs:37</c>) — needed for P33's revised approach. P33's original approach (a
/// Harmony prefix on <c>PlayerCombatState.IncrementTurnNumber</c>) confirmed live not to work: no
/// "P33 IncrementTurnNumber-prefix" log line ever appeared despite <c>TurnNumber</c> visibly changing
/// (1 → 2) across the exact `SwitchSides` call the prefix should have caught. The only explanation
/// consistent with that evidence: the JIT inlined <c>IncrementTurnNumber</c>'s one-line body
/// (<c>TurnNumber++</c>) directly into <c>CombatManager.SwitchSides</c>'s own compiled code, so the
/// method call Harmony patched was never actually reached at runtime — a known failure mode for
/// trivial one-line methods, not something a prefix on the callee can defend against.
/// Undoing the increment after the fact (snapshot before, restore after, in
/// <c>P33_SkipHumanTurnNumberBumpOnGhostOpeningTurn_SwitchSides</c>) is immune to this: it doesn't
/// matter whether the mutation happened via a real call or an inlined one, only that the *value*
/// needs correcting afterward, which requires writing to this private setter from outside the class.
/// </summary>
internal static class PlayerCombatStateTurnNumberAccess
{
    private static readonly MethodInfo Setter = AccessTools.PropertySetter(typeof(PlayerCombatState), nameof(PlayerCombatState.TurnNumber))
        ?? throw new MissingMethodException("PlayerCombatState.TurnNumber has no setter - game update?");

    internal static void Set(PlayerCombatState state, int value) => Setter.Invoke(state, new object[] { value });
}
