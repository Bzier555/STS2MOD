using MegaCrit.Sts2.Core.Saves.Runs;

namespace GhostDuel.Progression;

/// <summary>
/// PLAN.md M9a: the *multiplayer* Ghost ladder — a second, fully independent progression from
/// <see cref="GhostSnapshotStore"/>'s singleplayer one. Same per-human, per-level file shape
/// (<c>ghost_a{level}.json</c>, one <c>SerializablePlayer</c>), same refuse-on-mismatch/never-overwrite
/// discipline, via the same shared <see cref="GhostSnapshotFileStore"/> implementation — just pointed
/// at its own directory, so a human's multiplayer-ladder progress can never read from, overwrite, or
/// cap their singleplayer ladder, or vice versa. Progress here is tracked per human (this store lives
/// on that human's own machine, exactly like the singleplayer one), not per party — whichever subset
/// of players group up next each bring their own independent progress.
/// </summary>
internal static class MultiplayerGhostSnapshotStore
{
    private static readonly GhostSnapshotFileStore Store = new("ghostduel_legacy_ascension_multiplayer");

    public static bool Exists(int level) => Store.Exists(level);

    public static int GetMaxSelectableLevel() => Store.GetMaxSelectableLevel();

    public static GhostSnapshotFileStore.LoadResult Load(int level) => Store.Load(level);

    public static bool Save(int level, SerializablePlayer player) => Store.Save(level, player);
}
