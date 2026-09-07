using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace GhostDuel.Progression;

/// <summary>
/// Tags a <c>RunState</c> as a Legacy Ascension run (PLAN.md M7) and carries its ladder-specific
/// state. Attached programmatically at run creation by <c>LegacyAscensionEntry</c> — never shown in
/// the Custom-run modifier checklist, since <c>ModelDb.GoodModifiers</c>/<c>BadModifiers</c>
/// (<c>ModelDb.cs:353,366</c>) are fixed native arrays of specific types, not a reflection-driven scan
/// of every <c>ModifierModel</c> subclass; a type simply absent from those two arrays never appears in
/// <c>NCustomRunModifiersList.GetAllModifiers()</c>.
///
/// <c>[SavedProperty]</c> fields round-trip automatically through <c>RunState.Modifiers</c>'
/// <c>ToSerializable</c>/<c>FromSerializable</c> (<c>ModifierModel.cs:142-157</c>: <c>Props =
/// SavedProperties.From(this)</c> / <c>serializable.Props?.Fill(modifierModel)</c>) — no new
/// serialization code needed, and this survives a save/reload mid-run for free.
/// </summary>
public sealed class LegacyAscensionModifier : ModifierModel
{
    /// <summary>Which ladder level this run is fighting for — the shared counter's value at the
    /// moment this run started, not re-read live (a run in progress must not be affected by a
    /// different Legacy Ascension run completing concurrently, though multiplayer/concurrent runs
    /// are out of scope for v1 per <c>docs/PROGRESSION.md</c> §8).</summary>
    [SavedProperty]
    public int LadderLevel { get; set; }

    /// <summary>Set once the Act-3-boss Ghost fight for this run has happened (win or loss), so the
    /// splice patch on <c>RunManager.EnterNextAct</c> fires exactly once per run.</summary>
    [SavedProperty]
    public bool FoughtActGhostThisAct { get; set; }
}
