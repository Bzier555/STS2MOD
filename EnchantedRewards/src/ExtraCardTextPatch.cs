using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace EnchantedRewards;

/// <summary>
/// Extras with their own extra card text (e.g. Goopy's Exhaust callout, TezcatarasEmber's Eternal
/// callout) never showed that text anywhere - not on the card face in hand/deck, and not in the
/// enchant-select preview - only the native slot-1 enchantment's ExtraCardText was ever surfaced.
///
/// Root cause: CardModel.GetDescriptionForPile (the private, 3-parameter overload that actually
/// assembles a card's full display text - base description, enchantment/affliction extra text,
/// keyword lines, replay count) only ever reads `Enchantment?.DynamicExtraCardText` (native slot-1)
/// directly; it has no notion of extras at all, same class of gap as everywhere else in this mod - the
/// native game only ever asks a card's one native slot for things extras also need to contribute.
///
/// IMPORTANT: CardModel declares *two* methods named GetDescriptionForPile - a public 2-parameter one
/// that just forwards into the private 3-parameter one this patch actually wants. A plain
/// `[HarmonyPatch(typeof(CardModel), "GetDescriptionForPile")]` attribute (no argument types) can't
/// tell them apart and throws `AmbiguousMatchException` inside `Harmony.PatchAll()` - which doesn't
/// just fail this one patch, it aborts PatchAll entirely, silently skipping every patch class Harmony
/// hadn't gotten to yet (confirmed the hard way: this exact mistake, shipped for one round, is what
/// broke several *other*, unrelated fixes that round - see EnchantedRewards_STS2_Mod_Spec.md's Round 9
/// addenda). Disambiguating by parameter types isn't possible via the declarative attribute here
/// either, because the third parameter (`DescriptionPreviewType`) is itself a *private nested enum* on
/// CardModel - inaccessible as a compile-time `typeof()` reference. So this patch resolves and applies
/// itself manually (via `Apply`, called explicitly from EnchantedRewardsMod.Initialize after PatchAll)
/// instead of relying on attribute-based auto-discovery: reflection finds the nested enum type, then
/// `AccessTools.Method` with the full, correct 3-parameter signature to get the one exact MethodInfo,
/// with nothing left for Harmony to disambiguate on its own.
///
/// Appends each extra's own DynamicExtraCardText (only for the ones that actually have any - most
/// enchantments don't override HasExtraCardText/ExtraCardText at all) after everything else, matching
/// the "[purple]...[/purple]" styling the native slot-1/Affliction lines already use.
///
/// Stacking two copies of the same enchantment (e.g. two Inky's, each independently applying its own
/// fixed +1 Weak - the enchantment's own text template is a hardcoded literal, not scaled by Amount, so
/// two instances just repeat the same line rather than combining into "2") used to print the identical
/// line twice verbatim ("Apply 1 Weak." / "Apply 1 Weak."). There's no generic way to turn two
/// independent "1"s into a "2" without parsing an arbitrary, localized, tag-laden string per
/// enchantment type - so instead, CollapseDuplicateLines runs over the *entire* assembled description
/// (native lines included, not just this patch's own extras) and collapses any exactly-repeated line
/// into one copy with an "xN" suffix, the same convention CardEnchantmentVisualsPatch already uses for
/// stacked flags - enchantment-agnostic, and not limited to lines this patch itself added.
/// </summary>
internal static class ExtraCardTextPatch
{
    public static void Apply(Harmony harmony)
    {
        Type previewType = AccessTools.Inner(typeof(CardModel), "DescriptionPreviewType");
        MethodInfo original = AccessTools.Method(
            typeof(CardModel),
            "GetDescriptionForPile",
            new[] { typeof(PileType), previewType, typeof(Creature) });

        harmony.Patch(original, postfix: new HarmonyMethod(typeof(ExtraCardTextPatch), nameof(AppendExtraCardText)));
    }

    private static void AppendExtraCardText(CardModel __instance, ref string __result)
    {
        List<string> extraLines = new();
        foreach (EnchantmentModel extra in ExtraEnchantments.Get(__instance))
        {
            if (extra.HasExtraCardText)
            {
                string? text = extra.DynamicExtraCardText?.GetFormattedText();
                if (!string.IsNullOrEmpty(text))
                {
                    extraLines.Add("[purple]" + text + "[/purple]");
                }
            }
        }

        if (extraLines.Count == 0)
        {
            return;
        }

        string combined = string.IsNullOrEmpty(__result)
            ? string.Join('\n', extraLines)
            : __result + "\n" + string.Join('\n', extraLines);

        __result = CollapseDuplicateLines(combined);
    }

    private static string CollapseDuplicateLines(string text)
    {
        string[] lines = text.Split('\n');
        Dictionary<string, int> counts = new();
        List<string> order = new();
        foreach (string line in lines)
        {
            if (counts.TryGetValue(line, out int existing))
            {
                counts[line] = existing + 1;
            }
            else
            {
                counts[line] = 1;
                order.Add(line);
            }
        }

        if (counts.Count == lines.Length)
        {
            // No duplicates at all - nothing to collapse, return the original untouched.
            return text;
        }

        List<string> result = new();
        foreach (string line in order)
        {
            int count = counts[line];
            result.Add(count > 1 ? InsertStackSuffix(line, count) : line);
        }

        return string.Join('\n', result);
    }

    private static string InsertStackSuffix(string line, int count)
    {
        const string closeTag = "[/purple]";
        int index = line.IndexOf(closeTag, StringComparison.Ordinal);
        if (index < 0)
        {
            return $"{line} x{count}";
        }

        return line[..index] + $" x{count}" + line[index..];
    }
}
