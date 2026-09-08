using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace EnchantedRewards;

[ModInitializer(nameof(Initialize))]
public static class EnchantedRewardsMod
{
    public const string ModId = "EnchantedRewards";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());

        // Makes extra (non-slot-1) enchantments participate in combat's generic hook dispatch
        // (AfterCardPlayed, ModifyShuffleOrder, AfterCardDrawn, BeforeFlush,
        // AfterAutoPrePlayPhaseEntered, ...) exactly like a native enchantment would. See
        // ExtraEnchantments.AllListenersIn and EnchantedRewardsModifier for the combat-math hooks
        // that aren't covered by generic dispatch and need an explicit override instead.
        ModHelper.SubscribeForCombatStateHooks(ModId, ExtraEnchantments.AllListenersIn);

        Logger.Info("Initialized.");
    }
}
