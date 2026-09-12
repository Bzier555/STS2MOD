using System;
using System.Threading;

namespace GhostDuel.Duel;

/// <summary>
/// Ambient signal read by P35 (<c>EnginePatches.cs</c>): while active, <c>GuardedPower
/// .ModifyDamageMultiplicative</c> returns 1 instead of its real value, for exactly the duration of one
/// <c>Hook.ModifyDamage</c> call. Same shape as <see cref="GhostWeakSuppressionScope"/>/
/// <see cref="GhostStrengthSuppressionScope"/>, but for a target-side hook (like Vulnerable) rather
/// than a dealer-side one — added 2026-09-07 (user report: "playing tank does not update incoming
/// damage indicators properly"). Confirmed by reading <c>GuardedPower.cs</c>: Guarded halves damage
/// from a "powered attack" landing on its own holder — unlike Vulnerable (deliberately locked at queue
/// time, COMBAT-RULES.md §5, since it represents the dealer's own committed setup), Guarded is a
/// defensive commitment a *teammate* plays reactively, in response to an already-visible queued
/// intent (<c>Tank</c>'s own card text grants it to allies specifically for this purpose) — for that to
/// have any effect in this mod's delayed-queue duel, it must be re-evaluated live at display/resolution
/// time, the same reasoning already applied to Weak/Strength, not baked into
/// <see cref="QueuedDamagePacket.LockedAmount"/> at queue time where a later application can never
/// reach it. No real state is ever mutated; this only ever changes one pure function's return value for
/// the caller that explicitly opted in.
/// </summary>
internal static class GhostGuardedSuppressionScope
{
    private static readonly AsyncLocal<bool> _active = new();

    internal static bool IsActive => _active.Value;

    internal static IDisposable Enter() => new Scope();

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous;
        private bool _disposed;

        public Scope()
        {
            _previous = _active.Value;
            _active.Value = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _active.Value = _previous;
        }
    }
}
