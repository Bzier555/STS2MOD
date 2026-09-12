using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models.Events;

namespace NeowRelicsMod.NeowRelicsModCode;

// NeowBlessingModifier never contributes a Neow page of its own (GenerateNeowOption defaults to
// null), so if the player picks it without any other modifier that does, Neow.GenerateInitialOptions
// returns an empty list and there's no "last modifier" page for NeowModifierBlessingPatch to hook.
// This covers that case: show the blessing choice immediately as Neow's initial (and only) page.
[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class NeowNoModifierPageFallbackPatch
{
    private static void Postfix(Neow __instance, ref IReadOnlyList<EventOption> __result)
    {
        if (__result.Count > 0)
            return;

        if (!NeowModifierBlessingPatch.IsActive(__instance))
            return;

        IReadOnlyList<EventOption> blessingOptions = NeowModifierBlessingPatch.GenerateStandardBlessingOptions(__instance);
        if (blessingOptions.Count > 0)
            __result = blessingOptions;
    }
}
