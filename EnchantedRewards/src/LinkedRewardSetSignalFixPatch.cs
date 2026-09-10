using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace EnchantedRewards;

/// <summary>
/// Fixes the loot screen's two linked enchant options not greying out (or otherwise resolving) after
/// picking one - clicking one just goes quiet, leaving both options looking untouched, and neither is
/// clickable anymore ("just becomes unselectable").
///
/// Root cause is a genuine native engine bug, confirmed by decompile, in NLinkedRewardSet.Reload -
/// which builds the UI for MegaCrit.Sts2.Core.Rewards.LinkedRewardSet, the native "N alternatives, pick
/// one" mechanism this mod's two-option enchant rewards use. For each option it creates a child
/// NRewardButton and does:
///     nRewardButton.Connect(NRewardButton.SignalName.RewardClaimed, Callable.From(GetReward));
/// NRewardButton.RewardClaimed is a *one-argument* signal (it emits `this`, the button, via
/// `EmitSignal(RewardClaimed, this)` in NRewardButton.GetReward()) - but NLinkedRewardSet.GetReward()
/// is a *zero-argument* private method, and `Callable.From(GetReward)` wraps it with no argument
/// adapter. Godot's Callable trampoline rejects the mismatch the moment the signal is actually emitted
/// ("Invalid argument count for invoking callable. Expected 0 argument(s), received 1."), caught and
/// only logged by the engine rather than crashing - which means NLinkedRewardSet.GetReward()'s actual
/// body never runs at all. The enchant itself was already applied by this point (EnchantReward.OnSelect
/// completes before this signal even fires), so only the *visual* resolution silently breaks - exactly
/// the reported symptom.
///
/// This is a pre-existing bug in native LinkedRewardSet UI, not something this mod introduced -
/// LinkedRewardSet had no other callers anywhere in the decompiled assembly before this mod started
/// using it (see EnchantedRewardsModifier's own history/EnchantReward's SuccessfullySelected fix), so
/// it was never exposed until now.
///
/// Fixed additively, without touching the broken connection at all: postfixes the private Reload()
/// (where the buttons and the broken connection get created) and connects a second, correctly-typed
/// listener per button. A per-button marker (ConditionalWeakTable) prevents wiring the same button
/// twice if Reload() is ever called more than once for it.
///
/// This listener does NOT call the native GetReward() (even via reflection, as an earlier version of
/// this fix did) - GetReward()'s own body is `_rewardsScreen.RewardCollectedFrom(this);
/// LinkedRewardSet.OnSkipped(); EmitSignal(SignalName.RewardClaimed); this.QueueFreeSafely();`, and that
/// third line is *itself* a second, previously-unreachable instance of the exact same class of bug:
/// NLinkedRewardSet.RewardClaimed is declared as a one-argument signal, but this line emits it with
/// zero arguments - invisible before this fix (since GetReward() was never actually reached at all),
/// now newly exposed the moment it is. Confirmed live: this second mismatch
/// ("Expected 1 argument(s), received 0") now also appears in the game's log, immediately after the
/// first. Both are logged-and-swallowed by Godot's engine rather than propagating as a real .NET
/// exception, so GetReward() does still run to completion either way - but there's no reason to accept
/// two confirmed engine bugs when only two of that method's four steps are actually load-bearing for
/// this mod's purposes: `RewardCollectedFrom` (removes/resolves the widget on screen - the actual fix
/// for the reported symptom) and `LinkedRewardSet.OnSkipped()` (marks the sibling option's own Reward
/// as skipped). The `EmitSignal` step is pure redundancy (its only listener, NRewardsScreen, already
/// calls the very same `RewardCollectedFrom` directly - see NRewardsScreen.cs's `_Ready`), and
/// `QueueFreeSafely()` is likewise redundant (`RewardCollectedFrom`'s own `RemoveButton` already queues
/// the widget's removal). So this listener performs the two necessary steps directly instead,
/// sidestepping both bugs entirely rather than relying on the engine swallowing them harmlessly.
/// </summary>
[HarmonyPatch(typeof(NLinkedRewardSet), "Reload")]
internal static class LinkedRewardSetSignalFixPatch
{
    private static readonly FieldInfo RewardContainerField =
        AccessTools.Field(typeof(NLinkedRewardSet), "_rewardContainer");

    private static readonly FieldInfo RewardsScreenField =
        AccessTools.Field(typeof(NLinkedRewardSet), "_rewardsScreen");

    private static readonly ConditionalWeakTable<NRewardButton, object> AlreadyWired = new();

    [HarmonyPostfix]
    private static void FixChildButtonSignals(NLinkedRewardSet __instance)
    {
        if (RewardContainerField.GetValue(__instance) is not Control container)
        {
            return;
        }

        foreach (Node child in container.GetChildren())
        {
            if (child is not NRewardButton button || AlreadyWired.TryGetValue(button, out _))
            {
                continue;
            }

            AlreadyWired.Add(button, button);
            button.Connect(
                NRewardButton.SignalName.RewardClaimed,
                Callable.From<NRewardButton>(_ => ResolveLinkedRewardSet(__instance)));
        }
    }

    private static void ResolveLinkedRewardSet(NLinkedRewardSet linkedRewardSetNode)
    {
        if (RewardsScreenField.GetValue(linkedRewardSetNode) is NRewardsScreen screen)
        {
            screen.RewardCollectedFrom(linkedRewardSetNode);
        }

        linkedRewardSetNode.LinkedRewardSet.OnSkipped();
    }
}
