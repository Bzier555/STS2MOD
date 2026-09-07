using System;
using System.Collections.Generic;
using GhostDuel.Duel;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace GhostDuel.Presentation;

/// <summary>
/// Drives the real native <c>NIntent</c> node with a live queued-damage total, without going through
/// <c>AttackIntent.GetSingleDamage</c> (confirmed non-virtual, always re-runs <c>Hook.ModifyDamage</c>
/// against the local player with <c>ValueProp.Move</c> and no card source). That recompute is correct
/// for a real monster — whose base move value has no modifiers baked in yet — but wrong here: this
/// mod's queued packets already carry Vulnerable locked in from card-play time
/// (<see cref="QueuedDamagePacket"/>/COMBAT-RULES.md §5) and re-running the full modifier fold would
/// silently re-apply it live, double-counting it. <see cref="AbstractIntent.GetIntentLabel"/> and
/// <see cref="AttackIntent.GetTotalDamage"/> are both genuinely virtual, so this overrides them
/// directly with the packet total already correctly computed — bypassing the recompute entirely
/// rather than fighting it.
/// </summary>
internal sealed class GhostQueuedAttackIntent : AttackIntent
{
    private readonly Func<int> _totalDamage;

    public GhostQueuedAttackIntent(Func<int> totalDamage)
    {
        _totalDamage = totalDamage;
    }

    public override int Repeats => 1;

    public override int GetTotalDamage(IEnumerable<Creature> targets, Creature owner) => _totalDamage();

    /// <summary>Same loc-table entry and "Damage" placeholder <c>SingleAttackIntent.GetIntentLabel</c>
    /// uses (<c>SingleAttackIntent.cs:29-35</c>) — same displayed format, our own number.</summary>
    public override LocString GetIntentLabel(IEnumerable<Creature> targets, Creature owner)
    {
        LocString label = new("intents", "FORMAT_DAMAGE_SINGLE");
        label.Add("Damage", (decimal)GetTotalDamage(targets, owner));
        return label;
    }

    // Known gap: AttackIntent.GetIntentDescription (base, not overridden here) still calls
    // GetSingleDamage for the hover-tooltip body text, so hovering this icon shows a recomputed
    // (and therefore wrong, for the same double-modifier reason as the class doc comment) number in
    // the tooltip even though the main displayed label above is correct. Not fixed yet — no live
    // report has confirmed it matters in practice.
}
