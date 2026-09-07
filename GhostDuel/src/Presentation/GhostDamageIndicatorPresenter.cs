using System;
using System.Collections.Generic;
using System.Linq;
using GhostDuel.Diagnostics;
using GhostDuel.Duel;
using GhostDuel.Ghost;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace GhostDuel.Presentation;

/// <summary>
/// Shows a queued-damage indicator above whichever Ghost currently owns pending
/// <see cref="QueuedDamagePacket"/>s. Combat-rules rework (2026-09-04): a human's own damage now
/// applies immediately (P17, <c>EnginePatches.cs</c>), so only the Ghost's own outgoing attack ever
/// queues anymore — there is no longer a human-side indicator to show (COMBAT-RULES.md, rewritten).
/// A subscriber to <see cref="GhostDamageQueue.Changed"/>, per ARCHITECTURE.md §8 — never a detached
/// task inheriting an ambient scope (POSTMORTEM F8) — so a presentation failure here cannot affect the
/// logical turn; every entry point is wrapped so an exception is logged and swallowed rather than
/// propagated.
///
/// PLAN.md M9d: iterates every Ghost × every human, not one hardcoded pair — and, per the user's
/// explicit design, a Ghost's own indicator is filtered to *this client's own local viewer* (a Ghost
/// that queued damage against a different human shows nothing on this client's screen for that Ghost).
/// In the still-primary 1v1 case this is exactly the original behavior:
/// <see cref="LocalContext.IsMe(MegaCrit.Sts2.Core.Entities.Players.Player)"/> identifies the one
/// human, and there is only ever one possible target.
///
/// <b>Uses the real native <c>NIntent</c> node</b> — not a hand-built substitute — so this looks and
/// animates exactly like a monster's intent icon (confirmed live 2026-09-03: an earlier plain-`Label`
/// version had the wrong size and position; `NIntent`'s own scene layout, positioned the exact way
/// native code positions it, gets both right for free). `NCreature.UpdateIntent` (the usual entry
/// point) throws outright for a <c>Player</c>-backed creature (<c>Creature.Monster == null</c>,
/// confirmed by full read), so this calls the same two lines that method itself calls per intent —
/// <c>NIntent.Create(...)</c> then <c>IntentContainer.AddChildSafely(nIntent)</c> —
/// directly, skipping only the monster-only gate, not the real node or its native positioning.
///
/// One thing native code does NOT solve for a <c>Player</c>-backed creature: confirmed live that a
/// node placed in <c>IntentContainer</c> was invisible. Root cause, confirmed by reading
/// <c>NCreature.cs</c> in full: native <c>ExecuteEnemyTurn</c> (<c>CombatManager.cs:1079</c>,
/// unconditional for every enemy-side creature, Ghost included) calls <c>NCreature.PerformIntent()</c>
/// before that creature's turn starts, which fades <c>IntentContainer.Modulate</c>'s alpha to 0 to
/// hide the previous turn's monster intent icons — and nothing ever restores it for a `Player`-backed
/// creature, since the only restore path (<c>RevealIntents</c>) is reached exclusively through the
/// Monster-only <c>RollMove</c>/<c>PrepareForNextTurn</c> chain. `NIntent` itself only manages its own
/// internal `_intentHolder`'s Modulate (`NIntent.cs:197`), not its parent's, so it does not fix this
/// on its own. Fixed here by explicitly resetting `IntentContainer.Modulate` to opaque whenever this
/// creature has something queued to show — harmless for the human (nothing else native ever touches
/// its `IntentContainer`) and correctly timed for the Ghost (`PerformIntent` always runs *before* the
/// Ghost's own cards can queue anything, never after).
///
/// The displayed number is driven by <see cref="GhostQueuedAttackIntent"/> rather than a native
/// <c>SingleAttackIntent</c> — see that class's own doc comment for why reusing
/// <c>AttackIntent.GetSingleDamage</c> directly would silently double-apply Vulnerable.
/// </summary>
internal sealed class GhostDamageIndicatorPresenter : IDisposable
{
    private readonly GhostSession _session;
    private readonly Dictionary<Creature, NIntent> _indicators = new();
    private bool _disposed;

    public GhostDamageIndicatorPresenter(GhostSession session)
    {
        _session = session;
        session.DamageQueue.Changed += OnChanged;
    }

    private void OnChanged()
    {
        try
        {
            Creature? viewer = FindLocalViewer();
            HashSet<Creature> stillShown = new();

            foreach (GhostPartyMember member in _session.Party)
            {
                Creature ghost = member.Player.Creature;
                // Per-viewer: only what this Ghost has queued against *this client's own* human,
                // not its aggregate across every human in the party.
                decimal amount = viewer is not null ? _session.DamageQueue.TotalPendingFor(ghost, viewer) : 0m;
                GhostLog.ViewerIndicator(viewer is null ? "unknown" : $"NetId#{viewer.Player?.NetId}", member.Player.NetId, amount);
                if (UpdateFor(ghost, viewer, amount))
                {
                    stillShown.Add(ghost);
                }
            }

            // Combat-rules rework (2026-09-04): a human's own damage now applies immediately (P17,
            // EnginePatches.cs) — nothing ever queues for a human anymore, so the indicator loop that
            // used to run here (unfiltered, above the human's own sprite) was removed rather than kept
            // as a permanent no-op.

            foreach (Creature dealer in _indicators.Keys.Where(d => !stillShown.Contains(d)).ToList())
            {
                RemoveIndicatorFor(dealer);
            }
        }
        catch (Exception ex)
        {
            GhostLog.Warn($"GhostDamageIndicatorPresenter: update failed, ignoring ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>The current local human, the same way the engine itself identifies "whose screen is
    /// this" pervasively (ENGINE-NOTES.md's "M9 research" section). Null only if this combat somehow
    /// has no local human at all (shouldn't happen for a real client).</summary>
    private Creature? FindLocalViewer()
    {
        if (_session.Party.Count == 0)
        {
            return null;
        }
        ICombatState? combatState = _session.Party[0].Player.Creature.CombatState;
        return combatState?.PlayerCreatures.FirstOrDefault(c => c.Player is not null && LocalContext.IsMe(c.Player));
    }

    private bool UpdateFor(Creature dealer, Creature? target, decimal total)
    {
        if (total <= 0m || target is null)
        {
            RemoveIndicatorFor(dealer);
            return false;
        }

        NCreature? node = NCombatRoom.Instance?.GetCreatureNode(dealer);
        if (node is null)
        {
            return false;
        }

        // See class doc comment: PerformIntent() (native) fades this to 0 for the Ghost every turn,
        // and nothing else ever restores it for a Player-backed creature.
        node.IntentContainer.Modulate = Colors.White;

        if (!_indicators.TryGetValue(dealer, out NIntent? nIntent) || !GodotObject.IsInstanceValid(nIntent))
        {
            nIntent = NIntent.Create(0f);
            node.IntentContainer.AddChildSafely(nIntent);
            _indicators[dealer] = nIntent;
        }

        Creature[] targets = { target };
        GhostQueuedAttackIntent intent = new(() => (int)Math.Round(total));
        nIntent.UpdateIntent(intent, targets, dealer);
        return true;
    }

    private void RemoveIndicatorFor(Creature dealer)
    {
        if (!_indicators.Remove(dealer, out NIntent? nIntent) || !GodotObject.IsInstanceValid(nIntent))
        {
            return;
        }
        nIntent.GetParent()?.RemoveChildSafely(nIntent);
        nIntent.QueueFreeSafely();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _session.DamageQueue.Changed -= OnChanged;
        foreach (Creature dealer in _indicators.Keys.ToList())
        {
            RemoveIndicatorFor(dealer);
        }
    }
}
