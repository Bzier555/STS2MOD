using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace EnchantedRewards;

/// <summary>
/// The base game only ever renders one enchantment "flag" per card (NCard's %Enchantment tab: an
/// icon + an amount label), because a card only ever has one native Enchantment. Once a card can
/// carry extras (see ExtraEnchantments), those need their own flags too, or they'd be invisible and
/// completely unverifiable in play.
///
/// Rather than build new UI from scratch, this clones the native tab Control (Duplicate()) once per
/// *distinct enchantment type* the card carries beyond its native slot, and re-populates its
/// Icon/Label children - same visuals, same font, no new scene/asset needed. Positioned by stacking
/// downward from wherever the native tab ended up (NCard itself repositions it depending on
/// star-cost display), so this doesn't need to know that logic itself.
///
/// One flag per type, not per instance: a card can carry any number of independent instances of the
/// *same* enchantment type (see EnchantmentService.Apply - repeats are never merged into one
/// instance's Amount), so naively cloning the tab once per instance produced visually-identical
/// duplicate flags stacked on top of each other for a repeated type, rather than one flag showing how
/// many copies are stacked. Fixed by grouping extras by type first: a type with exactly one instance
/// still shows its normal per-enchantment amount (DisplayAmount), same as before; a type with more
/// than one instance shows a single flag with a "xN" stack-count label instead (the individual
/// instances' Amounts already aren't summed anywhere visual - each contributes independently through
/// the normal hook-combination math - so a stack count communicates the state better than any single
/// instance's own Amount would). The same grouping-and-relabeling applies to the *native* slot-1 tab
/// too, when one or more extras happen to share its exact type (native tab left completely alone
/// otherwise, including whenever nothing needs to change its label).
///
/// KNOWN LIMITATIONS (this needed real in-game visual iteration to get exactly right, which wasn't
/// available while writing it):
/// - No hover tooltip on the duplicated flags. The native tab's tooltip is driven by something not
///   investigated here; a duplicated flag may show no tooltip, or the slot-1 enchantment's tooltip,
///   rather than its own.
/// - No "disabled" (used-up-this-turn) shader treatment - see NCard.SetEnchantmentStatus for what
///   the native tab does that this doesn't replicate.
/// - A card with many distinct extra enchantment types will have flags stack off the bottom of the
///   card with no overflow/"+N more" handling.
/// </summary>
[HarmonyPatch(typeof(NCard), "UpdateEnchantmentVisuals")]
internal static class CardEnchantmentVisualsPatch
{
    private const float StackOffset = 50f;

    private static readonly ConditionalWeakTable<NCard, List<Control>> ExtraTabs = new();

    [HarmonyPostfix]
    private static void ShowExtraEnchantmentFlags(NCard __instance)
    {
        List<Control> tabs = ExtraTabs.GetValue(__instance, static _ => new List<Control>());
        foreach (Control tab in tabs)
        {
            tab.GetParent()?.RemoveChild(tab);
            tab.QueueFree();
        }

        tabs.Clear();

        CardModel? model = __instance.Model;
        if (model == null)
        {
            return;
        }

        Control nativeTab = __instance.GetNode<Control>("%Enchantment");

        Type? nativeType = model.Enchantment?.GetType();
        IReadOnlyList<EnchantmentModel> extras = ExtraEnchantments.Get(model);

        if (nativeType != null)
        {
            int matchingExtraCount = extras.Count(e => e.GetType() == nativeType);
            if (matchingExtraCount > 0)
            {
                MegaLabel nativeLabel = nativeTab.GetNode<MegaLabel>("Label");
                nativeLabel.SetTextAutoSize($"x{matchingExtraCount + 1}");
                nativeLabel.Visible = true;
            }
        }

        if (extras.Count == 0)
        {
            return;
        }

        Node? parent = nativeTab.GetParent();
        if (parent == null)
        {
            return;
        }

        Vector2 basePosition = nativeTab.Position;

        var distinctExtraTypes = extras
            .GroupBy(e => e.GetType())
            .Where(group => group.Key != nativeType)
            .ToList();

        for (int i = 0; i < distinctExtraTypes.Count; i++)
        {
            var group = distinctExtraTypes[i];
            EnchantmentModel representative = group.First();
            int count = group.Count();

            if ((Control)nativeTab.Duplicate() is not { } clone)
            {
                continue;
            }

            clone.Position = basePosition + Vector2.Down * (StackOffset * (i + 1));
            clone.Visible = true;
            parent.AddChild(clone);

            clone.GetNode<TextureRect>("Icon").Texture = representative.Icon;
            MegaLabel label = clone.GetNode<MegaLabel>("Label");
            if (count > 1)
            {
                label.SetTextAutoSize($"x{count}");
                label.Visible = true;
            }
            else
            {
                label.SetTextAutoSize(representative.DisplayAmount.ToString());
                label.Visible = representative.ShowAmount;
            }

            tabs.Add(clone);
        }
    }
}
