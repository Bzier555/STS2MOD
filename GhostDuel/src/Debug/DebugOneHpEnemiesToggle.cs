using GhostDuel.Diagnostics;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace GhostDuel.Debug;

/// <summary>
/// M7 debug aid, requested to speed up testing the Legacy Ascension ladder across many runs/levels: a
/// persistent (main-menu, not combat-scoped — no <c>GhostSession</c> involved) toggle that
/// forces every real monster's HP to 1, so Acts 1-3 can be blitzed through to reach each level's Ghost
/// fight quickly. Deliberately does not touch the Ghost's own HP — a real "normal Legacy Ascension
/// singleplayer run," just with trivial non-Ghost fights along the way, so the duel itself still gets
/// exercised normally each time (per the user's explicit choice, over an "everything at 1 HP including
/// the Ghost" option that would trivialize the duel too).
///
/// Patches <c>Creature.SetUniqueMonsterHpValue</c> (<c>Creature.cs:371-383</c>) — the one method that
/// rolls a real monster's initial HP within its <c>Monster.MinInitialHp</c>/<c>MaxInitialHp</c> range.
/// This method itself throws <c>InvalidOperationException</c> if called for a <c>Player</c>-backed
/// creature ("Can't set unique monster HP value for a player") — confirmed by reading it — so patching
/// it can never touch the Ghost or the human even accidentally; no extra Ghost-exclusion check needed
/// here at all, the native precondition already guarantees it.
///
/// Toggle it on with this file's own debug menu button (<see cref="DebugMenuButton"/>), then use the
/// game's own "LEGACY ASCENSION" button (<see cref="Progression.LegacyAscensionEntry"/>) normally — the
/// two are deliberately independent (this toggle persists across runs until switched off, rather than
/// being tied to any one mode's entry point), so it can also speed up Standard-mode testing if useful.
/// </summary>
internal static class DebugOneHpEnemiesToggle
{
    public static bool Enabled { get; private set; }

    public static void Toggle()
    {
        Enabled = !Enabled;
        GhostLog.Info($"DebugOneHpEnemiesToggle: real monster HP override is now {(Enabled ? "ON (all 1 HP)" : "OFF (normal)")}.");
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.SetUniqueMonsterHpValue))]
    private static class Prefix_ForceOneHp
    {
        [HarmonyPrefix]
        private static bool Prefix(Creature __instance)
        {
            if (!Enabled)
            {
                return true;
            }
            __instance.SetMaxHpInternal(1m);
            __instance.SetCurrentHpInternal(1m);
            return false;
        }
    }
}
