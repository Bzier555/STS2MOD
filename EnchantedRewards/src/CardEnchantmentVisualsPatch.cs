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
/// extra enchantment and re-populates its Icon/Label children - same visuals, same font, no new
/// scene/asset needed. Positioned by stacking downward from wherever the native tab ended up
/// (NCard itself repositions it depending on star-cost display), so this doesn't need to know that
/// logic itself.
///
/// KNOWN LIMITATIONS (this needed real in-game visual iteration to get exactly right, which wasn't
/// available while writing it):
/// - No hover tooltip on the duplicated flags. The native tab's tooltip is driven by something not
///   investigated here; a duplicated flag may show no tooltip, or the slot-1 enchantment's tooltip,
///   rather than its own.
/// - No "disabled" (used-up-this-turn) shader treatment - see NCard.SetEnchantmentStatus for what
///   the native tab does that this doesn't replicate.
/// - A card with many extra enchantments will have flags stack off the bottom of the card with no
///   overflow/"+N more" handling.
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

        IReadOnlyList<EnchantmentModel> extras = ExtraEnchantments.Get(model);
        if (extras.Count == 0)
        {
            return;
        }

        Control nativeTab = __instance.GetNode<Control>("%Enchantment");
        Node? parent = nativeTab.GetParent();
        if (parent == null)
        {
            return;
        }

        Vector2 basePosition = nativeTab.Position;

        for (int i = 0; i < extras.Count; i++)
        {
            EnchantmentModel extra = extras[i];
            if ((Control)nativeTab.Duplicate() is not { } clone)
            {
                continue;
            }

            clone.Position = basePosition + Vector2.Down * (StackOffset * (i + 1));
            clone.Visible = true;
            parent.AddChild(clone);

            clone.GetNode<TextureRect>("Icon").Texture = extra.Icon;
            MegaLabel label = clone.GetNode<MegaLabel>("Label");
            label.SetTextAutoSize(extra.DisplayAmount.ToString());
            label.Visible = extra.ShowAmount;

            tabs.Add(clone);
        }
    }
}
