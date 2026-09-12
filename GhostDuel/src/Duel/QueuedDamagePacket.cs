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
/// <see cref="GhostWeakSuppressionScope"/>, <see cref="GhostStrengthSuppressionScope"/>,
/// <see cref="GhostGuardedSuppressionScope"/> and <see cref="GhostTankSuppressionScope"/> are all
/// active, so none of Weak's, Strength's, Guarded's or Tank's own contribution is ever folded in at
/// all. Everything else (Vulnerable, relics, other powers) is captured honestly as of that moment,
/// matching "Vulnerable captured in card-effect order" (§5) exactly — Vulnerable is a property of the
/// *target*, correctly frozen at the moment the dealer committed. <see cref="DisplayAmount"/> re-reads
/// the dealer's *current* Weak and Strength, and the target's *current* Guarded and Tank, directly off
/// their own power instances and folds them in fresh every time — never divides/subtracts a stored
/// value to "recover" a pre-modifier base (POSTMORTEM F14). Generalized 2026-09-04 (user request) from
/// Weak-only to also cover Strength: both are dealer-side (unlike Vulnerable), so a Strength change on
/// the dealer between queueing and resolution — e.g. Mangle's temporary loss — must affect an
/// already-queued packet the same way Weak does, or it can only ever hit cards not yet played, one
/// turn later than intended. Generalized again 2026-09-07 (user report: "playing tank does not update
/// incoming damage indicators properly") to also cover Guarded/Tank: both are target-side, like
/// Vulnerable, but unlike Vulnerable they exist specifically to be played *reactively* against an
/// already-visible queued threat (<c>Tank</c>/<c>GuardedPower</c> are the one <c>MultiplayerOnly</c>
/// card pair in the pool built around exactly that use case) — locking them at queue time like
/// Vulnerable would mean they can never affect the attack they were played in response to, defeating
/// the card's entire purpose in this delayed-queue duel.
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
    /// excludes Weak, Strength, Guarded and Tank) plus the dealer's *live* Strength additive
    /// contribution scaled by the target's *locked* Vulnerable multiplier, all then scaled by the
    /// dealer's *live* Weak multiplier and the target's *live* Guarded/Tank multiplier — matching
    /// <c>Hook.ModifyDamageInternal</c>'s own additive-then-multiplicative order (confirmed by reading
    /// it directly: the multiplicative pass is a plain running product across every hook listener,
    /// <c>num *= num3</c>, so combining a third live multiplicative factor here the same way the second
    /// already was is exactly what a fresh computation would produce). Used for both intent display and
    /// the actual amount applied at resolution, so the two can never disagree.</summary>
    public decimal DisplayAmount
    {
        get
        {
            decimal liveStrengthAdditive = Dealer.GetPower<StrengthPower>()?.ModifyDamageAdditive(Target, 0m, Props, Dealer, CardSource) ?? 0m;
            decimal weakFactor = Dealer.GetPower<WeakPower>()?.ModifyDamageMultiplicative(Target, 1m, Props, Dealer, CardSource) ?? 1m;
            decimal guardedFactor = Target.GetPower<GuardedPower>()?.ModifyDamageMultiplicative(Target, 1m, Props, Dealer, CardSource) ?? 1m;
            decimal tankFactor = Target.GetPower<TankPower>()?.ModifyDamageMultiplicative(Target, 1m, Props, Dealer, CardSource) ?? 1m;
            return (LockedAmount + liveStrengthAdditive * LockedVulnerableMultiplier) * weakFactor * guardedFactor * tankFactor;
        }
    }
}
