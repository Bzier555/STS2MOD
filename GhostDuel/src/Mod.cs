using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace GhostDuel;

[ModInitializer(nameof(Initialize))]
public static class Mod
{
    public const string ModId = "GhostDuel";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());
        Logger.Info("Ghost Duel initialized (M1 baseline).");
    }
}
