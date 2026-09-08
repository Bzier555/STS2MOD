using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace RotatingDecks;

[ModInitializer(nameof(Initialize))]
public static class RotatingDecksMod
{
    public const string ModId = "RotatingDecks";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());
        Logger.Info("Initialized.");
    }
}
