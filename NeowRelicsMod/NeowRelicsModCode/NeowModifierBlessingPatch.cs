using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;

namespace NeowRelicsMod.NeowRelicsModCode;

// In a standard run, Neow always finishes her visit by offering the usual relic/curse blessing
// choice. In Custom and Daily runs she instead ends immediately after the last run modifier
// (Draft, Insanity, etc.) is picked, so the blessing choice never happens. When the player has
// opted in via the "Neow's Blessing" custom run modifier (NeowBlessingModifier), this patch runs
// Neow's normal blessing generation right after that last modifier is chosen, so the run gets both
// the modifier reward AND Neow's usual blessing, same as a standard run.
[HarmonyPatch(typeof(Neow), "OnModifierOptionSelected", typeof(Func<Task>), typeof(int))]
public static class NeowModifierBlessingPatch
{
    internal static bool IsActive(Neow neow) =>
        neow.Owner?.RunState.Modifiers.Any(m => m is NeowBlessingModifier) == true;

    private static bool Prefix(Neow __instance, Func<Task> modifierFunc, int index, ref Task __result)
    {
        // Let vanilla handle every modifier choice except the last one, so any other modifier
        // pages still advance normally.
        if (index + 1 < __instance.ModifierOptions.Count)
            return true;

        if (!IsActive(__instance))
            return true;

        __result = RunModifierThenOfferBlessing(__instance, modifierFunc);
        return false;
    }

    private static async Task RunModifierThenOfferBlessing(Neow neow, Func<Task> modifierFunc)
    {
        await modifierFunc();

        IReadOnlyList<EventOption> blessingOptions = GenerateStandardBlessingOptions(neow);
        if (blessingOptions.Count == 0)
        {
            MainFile.Logger.Warn("[NeowRelicsMod] Could not generate Neow's blessing choice; ending the event as usual.");
            neow.SetEventFinished(neow.L10NLookup(neow.Id.Entry + ".pages.DONE.description"));
            return;
        }

        neow.SetEventState(neow.L10NLookup(neow.Id.Entry + ".pages.DONE.description"), blessingOptions);
    }

    // Neow.GenerateInitialOptions() builds the modifier picker when the run has active modifiers,
    // and the normal relic/curse blessing choice when it doesn't. Hide the modifiers for one call
    // so it builds the same blessing choice a standard run would get, then restore them.
    internal static IReadOnlyList<EventOption> GenerateStandardBlessingOptions(Neow neow)
    {
        if (neow.Owner?.RunState is not RunState runState)
            return Array.Empty<EventOption>();

        IReadOnlyList<ModifierModel> savedModifiers = runState.Modifiers;
        try
        {
            runState.Modifiers = Array.Empty<ModifierModel>();
            return neow.GenerateInitialOptions();
        }
        finally
        {
            runState.Modifiers = savedModifiers;
        }
    }
}
