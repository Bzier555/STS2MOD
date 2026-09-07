using MegaCrit.Sts2.Core.Saves.Runs;

namespace GhostDuel.Progression;

/// <summary>
/// One file per completed *singleplayer* Legacy Ascension level (PLAN.md M7,
/// <c>docs/PROGRESSION.md</c> §3), never overwritten once written. Thin static façade over
/// <see cref="GhostSnapshotFileStore"/> (PLAN.md M9a split) so every existing call site keeps working
/// unchanged; all behavior lives in that shared class now. Deliberately a separate directory/instance
/// from <see cref="MultiplayerGhostSnapshotStore"/> — the two ladders must never read, overwrite or cap
/// each other.
/// </summary>
internal static class GhostSnapshotStore
{
    private static readonly GhostSnapshotFileStore Store = new("ghostduel_legacy_ascension");

    public static bool Exists(int level) => Store.Exists(level);

    public static int GetMaxSelectableLevel() => Store.GetMaxSelectableLevel();

    public static GhostSnapshotFileStore.LoadResult Load(int level) => Store.Load(level);

    public static bool Save(int level, SerializablePlayer player) => Store.Save(level, player);
}
