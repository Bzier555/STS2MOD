using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace GhostDuel.Duel;

/// <summary>
/// One direct-damage hit, queued instead of resolved immediately (COMBAT-RULES.md §2-§3). Confirmed
/// by full reads of <c>CreatureCmd.Damage</c>/<c>Hook.ModifyDamage</c>/<c>WeakPower.cs</c>
/// (ENGINE-NOTES.md M3 section): <see cref="LockedAmount"/> is computed once, at card-play time, via
/// the same <c>Hook.ModifyDamage</c> the engine itself uses for real resolution — while
/// <see cref="GhostWeakSuppressionScope"/> and <see cref="GhostStrengthSuppressionScope"/> are both
/// active, so neither Weak's nor Strength's own contribution is ever folded in at all. Everything
/// else (Vulnerable, relics, other powers) is captured honestly as of that moment, matching
/// "Vulnerable captured in card-effect order" (§5) exactly — Vulnerable is a property of the
/// *target*, correctly frozen at the moment the dealer committed. <see cref="DisplayAmount"/> re-reads
/// the dealer's *current* Weak and Strength directly off their own power instances and folds them in
/// fresh every time — never divides/subtracts a stored value to "recover" a pre-modifier base
/// (POSTMORTEM F14). Generalized 2026-09-04 (user request) from Weak-only to also cover Strength: both
/// are dealer-side (unlike Vulnerable), so a Strength change on the dealer between queueing and
/// resolution — e.g. Mangle's temporary loss — must affect an already-queued packet the same way Weak
/// does, or it can only ever hit cards not yet played, one turn later than intended.
/// </summary>
internal sealed class QueuedDamagePacket
{
    public Creature Dealer { get; }
    public Creature Target { get; }
    public decimal LockedAmount { get; }

    /// <summary>The target's Vulnerable multiplier (e.g. 1.5, or 1 if not Vulnerable), read directly
    /// off <c>VulnerablePower.ModifyDamageMultiplicative</c> at the same moment <see cref="LockedAmount"/>
    /// was computed. Needed because Strength's live contribution (additive, computed *before* the
    /// multiplicative pass in <c>Hook.ModifyDamageInternal</c>) must be scaled by the same
    /// multiplicative factor a fresh computation would apply to it — Vulnerable is correctly frozen at
    /// queue time, so this is captured once rather than re-read live like Weak.</summary>
    public decimal LockedVulnerableMultiplier { get; }

    public ValueProp Props { get; }
    public CardModel? CardSource { get; }

    public QueuedDamagePacket(Creature dealer, Creature target, decimal lockedAmount, decimal lockedVulnerableMultiplier, ValueProp props, CardModel? cardSource)
    {
        Dealer = dealer;
        Target = target;
        LockedAmount = lockedAmount;
        LockedVulnerableMultiplier = lockedVulnerableMultiplier;
        Props = props;
        CardSource = cardSource;
    }

    /// <summary>What this packet would deal right now: <see cref="LockedAmount"/> (which already
    /// excludes both Weak and Strength) plus the dealer's *live* Strength additive contribution scaled
    /// by the target's *locked* Vulnerable multiplier, all then scaled by the dealer's *live* Weak
    /// multiplier — matching <c>Hook.ModifyDamageInternal</c>'s own additive-then-multiplicative order.
    /// Used for both intent display and the actual amount applied at resolution, so the two can never
    /// disagree.</summary>
    public decimal DisplayAmount
    {
        get
        {
            decimal liveStrengthAdditive = Dealer.GetPower<StrengthPower>()?.ModifyDamageAdditive(Target, 0m, Props, Dealer, CardSource) ?? 0m;
            decimal weakFactor = Dealer.GetPower<WeakPower>()?.ModifyDamageMultiplicative(Target, 1m, Props, Dealer, CardSource) ?? 1m;
            return (LockedAmount + liveStrengthAdditive * LockedVulnerableMultiplier) * weakFactor;
        }
    }
}
