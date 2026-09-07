using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GhostDuel.Diagnostics;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace GhostDuel.Duel;

/// <summary>
/// Owned by <see cref="Ghost.GhostSession"/> (one per combat, disposed for free with the session — no
/// new static mutable state, per CLAUDE.md's code-shape rule). Holds direct-damage packets queued by
/// P17 instead of resolved immediately (COMBAT-RULES.md §2-§3). Since the 2026-09-04 combat-rules
/// rework only the Ghost's own direct damage ever queues here — the human's applies immediately.
/// </summary>
internal sealed class GhostDamageQueue
{
    private readonly List<QueuedDamagePacket> _pending = new();

    /// <summary>Raised after the pending set changes (enqueue or resolve) — the presentation layer's
    /// one seam (ARCHITECTURE.md §8: "a subscriber to a plain event the controller raises," not a
    /// detached task inheriting an AsyncLocal scope, POSTMORTEM F8). Never raised from inside a
    /// Harmony patch's own stack in a way that could let a subscriber's exception affect game logic —
    /// subscribers are responsible for catching their own presentation failures.</summary>
    public event Action? Changed;

    /// <summary>What <paramref name="dealer"/> currently has queued against its opponent, live —
    /// re-reads each packet's <see cref="QueuedDamagePacket.DisplayAmount"/> (which itself re-reads
    /// Weak fresh) rather than caching a total, so a display always agrees with what resolution would
    /// produce at that same instant. Includes packets dealt by a pet <paramref name="dealer"/> owns
    /// (see <see cref="BelongsTo"/>) — e.g. Necrobinder's Osty, attacking via Unleash.</summary>
    public decimal TotalPendingFor(Creature dealer) => _pending.Where(p => BelongsTo(p, dealer)).Sum(p => p.DisplayAmount);

    /// <summary>PLAN.md M9d: what <paramref name="dealer"/> currently has queued *specifically against
    /// <paramref name="target"/>* — the per-viewer figure a party fight's presenter needs (a Ghost may
    /// have queued different amounts against different humans). Equivalent to
    /// <see cref="TotalPendingFor(Creature)"/> in the 1v1 case, where there's only ever one possible
    /// target.</summary>
    public decimal TotalPendingFor(Creature dealer, Creature target) =>
        _pending.Where(p => BelongsTo(p, dealer) && ReferenceEquals(p.Target, target)).Sum(p => p.DisplayAmount);

    public void Enqueue(QueuedDamagePacket packet)
    {
        _pending.Add(packet);
        Changed?.Invoke();
    }

    /// <summary>
    /// Confirmed live (2026-09-04, reported): the Ghost's own damage indicator didn't update when Weak
    /// was applied to the Ghost on the human's turn. Root cause: <see cref="QueuedDamagePacket.DisplayAmount"/>
    /// already re-reads the dealer's *current* Weak and Strength fresh every time it's read (its own
    /// doc comment) — the data was never stale, but nothing ever told the presenter to *re-read* it,
    /// since applying/removing Weak or Strength on a creature doesn't touch this queue's own pending
    /// list at all (no <see cref="Enqueue"/>/resolve call, hence no existing <see cref="Changed"/>).
    /// Called by a patch on the one native chokepoint every power amount-change funnels through
    /// (<c>PowerModel.SetAmount</c>) so the presenter re-renders with the live number instead of a
    /// stale one.
    /// </summary>
    public void NotifyDisplayChanged() => Changed?.Invoke();

    /// <summary>
    /// Resolves and removes every packet belonging to <paramref name="dealer"/>, individually — not
    /// aggregated — through the real <c>CreatureCmd.Damage</c>, so on-hit reactive effects (Thorns,
    /// relic hit-counters) fire once per hit exactly as they would natively (COMBAT-RULES.md §4).
    /// Wrapped in <see cref="GhostQueuedResolutionScope"/> so P16 stops <c>Hook.ModifyDamage</c> from
    /// re-applying modifiers already baked into each packet's <see cref="QueuedDamagePacket.LockedAmount"/>.
    /// </summary>
    public Task ResolvePendingFor(Creature dealer, string ownerLabel) =>
        ResolveMatching(p => BelongsTo(p, dealer), ownerLabel);

    private async Task ResolveMatching(Func<QueuedDamagePacket, bool> predicate, string logLabel)
    {
        List<QueuedDamagePacket> toResolve = _pending.Where(predicate).ToList();
        if (toResolve.Count == 0)
        {
            return;
        }
        foreach (QueuedDamagePacket packet in toResolve)
        {
            _pending.Remove(packet);
            Changed?.Invoke();
            if (packet.Target.IsDead)
            {
                continue;
            }
            decimal amount = packet.DisplayAmount;
            using (GhostQueuedResolutionScope.Enter())
            {
                await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), packet.Target, amount, packet.Props, packet.Dealer, packet.CardSource);
            }
            GhostLog.Resolved(logLabel, packet.Target.LogName, packet.LockedAmount, amount);
        }
    }

    /// <summary>
    /// Confirmed live (2026-09-03, Necrobinder): Unleash makes Osty — a pet, not the Necrobinder's own
    /// creature — deal the damage (<c>AttackCommand.FromOsty</c>, <c>dealer: Attacker</c> set to the
    /// Osty <c>Creature</c>). Without this, such a packet's <c>Dealer</c> reference-equals neither the
    /// Ghost's own creature nor the human's, so it never matched either <c>ResolvePendingFor</c> call
    /// site (turn-start for the Ghost's own packets, turn-end for the human's) and stayed queued
    /// forever, silently. A packet belongs to <paramref name="dealer"/> either directly, or when its
    /// own dealer is a pet <paramref name="dealer"/> owns.
    /// </summary>
    private static bool BelongsTo(QueuedDamagePacket packet, Creature dealer) =>
        ReferenceEquals(packet.Dealer, dealer) || (packet.Dealer.PetOwner is not null && ReferenceEquals(packet.Dealer.PetOwner, dealer.Player));
}
