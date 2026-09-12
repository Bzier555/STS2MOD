using System.Collections.Generic;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Helpers;

namespace NeowRelicsMod.NeowRelicsModCode;

// A selectable Custom Run modifier (BaseLib discovers CustomModifierModel subtypes automatically
// and adds them to the Custom Run modifier list - no manual registration needed). Picking this
// modifier doesn't change anything by itself: NeowModifierBlessingPatch checks for its presence in
// the run's active modifiers and, when found, restores Neow's usual relic/curse blessing choice
// after any other modifiers are resolved, same as a standard run.
public sealed class NeowBlessingModifier : CustomModifierModel, ILocalizationProvider
{
    public override ModifierAlignment Alignment => ModifierAlignment.Good;

    // No art shipped with this mod, so reuse the native "Ancient room" icon the top bar and run
    // history already show for Neow (ui/run_history/<ancient id>.png), instead of falling through
    // to the generic missing-icon placeholder ModifierModel.IconPath would otherwise resolve to.
    public override string IconPath => ImageHelper.GetImagePath("ui/run_history/neow.png");

    public List<(string, string)> Localization => new ModifierLoc(
        "Neow's Blessing",
        "Neow still offers her usual relic/curse choice after any other modifiers are resolved, just like a standard run.");
}
