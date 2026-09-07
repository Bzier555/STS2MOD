using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace GhostDuel.Multiplayer;

/// <summary>
/// PLAN.md M9b: the multiplayer sibling of <c>GhostDuel.Progression.LegacyAscensionModifier</c> — tags
/// a <c>RunState</c> as a multiplayer Ghost-ascension run and carries its own ladder-specific state.
/// Deliberately a separate type from the singleplayer modifier (not a shared base with a "which track"
/// flag): the two tracks' splice patches (M7's already-shipped one, M9c's still-to-come one) key off
/// distinct modifier types rather than a shared one with a mode flag, so a future change to one track
/// can never accidentally alter the other's behavior through shared code.
/// </summary>
public sealed class MultiplayerLegacyAscensionModifier : ModifierModel
{
    /// <summary>Which multiplayer ladder level this run is fighting for — the aggregated cross-lobby
    /// minimum at the moment the party embarked (<see cref="MultiplayerGhostLadderCoordinator.AggregateMinLevel"/>),
    /// never the singleplayer ladder's level.</summary>
    [SavedProperty]
    public int LadderLevel { get; set; }

    /// <summary>Set once the Act-3-boss Ghost-party fight for this run has happened (win or loss), so
    /// M9c's splice patch fires exactly once per run.</summary>
    [SavedProperty]
    public bool FoughtActGhostThisAct { get; set; }
}
