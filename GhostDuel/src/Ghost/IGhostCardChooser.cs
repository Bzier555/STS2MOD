namespace GhostDuel.Ghost;

/// <summary>
/// Chooses the Ghost's next play. Null means end turn. The controller re-derives the
/// <see cref="GhostTurnView"/> and calls this again after every play (ARCHITECTURE.md §6) — a
/// chooser never sees a stale hand.
/// </summary>
internal interface IGhostCardChooser
{
    /// <summary>Called once by <c>GhostTurnController.RunTurnAsync</c> at the start of each of this
    /// chooser's turns, before the first <see cref="Choose"/> call — a chooser that keeps per-turn
    /// state (e.g. a weighted-random chooser's Attack/Skill drift) resets it here. Stateless choosers
    /// (<see cref="Choosers.LeftToRightChooser"/>) implement this as a no-op.</summary>
    void OnTurnStart();

    GhostChoice? Choose(GhostTurnView view);
}
