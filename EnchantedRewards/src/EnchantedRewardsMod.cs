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
        // Harmony patching is wrapped in its own try/catch, deliberately isolated from everything
        // below: a single bad patch target throwing here (as ExtraCardTextPatch's ambiguous-match bug
        // once did, for one round) must never again be allowed to silently skip the registrations
        // below too, on top of whatever patches PatchAll hadn't reached yet - that combination is what
        // made this mistake so hard to track down the first time (see ExtraCardTextPatch's own doc
        // comment for the full story). A failure here is now at least loud, in our own log, right
        // where it happened.
        try
        {
            Harmony harmony = new(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Not attribute-discoverable via PatchAll: its target is disambiguated by a private
            // nested enum type, which needs a runtime-reflection lookup PatchAll's declarative
            // attribute can't express - see ExtraCardTextPatch's own doc comment.
            ExtraCardTextPatch.Apply(harmony);
        }
        catch (Exception e)
        {
            Logger.Error($"Harmony patching failed: {e}");
        }

        // Makes extra (non-slot-1) enchantments participate in combat's generic hook dispatch
        // (AfterCardPlayed, ModifyShuffleOrder, AfterCardDrawn, BeforeFlush,
        // AfterAutoPrePlayPhaseEntered, ...) exactly like a native enchantment would. See
        // ExtraEnchantments.AllListenersIn and EnchantedRewardsModifier for the combat-math hooks
        // that aren't covered by generic dispatch and need an explicit override instead.
        ModHelper.SubscribeForCombatStateHooks(ModId, ExtraEnchantments.AllListenersIn);

        // Makes extras (see ExtraEnchantments) survive save/quit/reload - see ExtraEnchantmentSave's
        // own doc comment for how.
        ExtraEnchantmentSave.Register();

        Logger.Info("Initialized.");
    }
}
