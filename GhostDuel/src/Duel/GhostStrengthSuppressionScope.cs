using System;
using System.Threading;

namespace GhostDuel.Duel;

/// <summary>
/// Ambient signal read by P28 (<c>EnginePatches.cs</c>): while active, <c>StrengthPower
/// .ModifyDamageAdditive</c> returns 0 instead of its real value, for exactly the duration of one
/// <c>Hook.ModifyDamage</c> call. Same shape and same reason as <see cref="GhostWeakSuppressionScope"/>,
/// generalized from Weak to Strength per the user's 2026-09-04 request: Strength is a property of the
/// *dealer*, not the target, so a Strength change on an attacker (a Mangle-style loss, or a gain)
/// after a packet is queued must still affect that already-queued packet when it resolves — otherwise
/// a debuff like Mangle can only ever affect cards the dealer hasn't played yet, one turn later than
/// intended. Used to compute a <see cref="QueuedDamagePacket.LockedAmount"/> that honestly excludes
/// Strength's contribution — Strength must be re-evaluated live at display/resolution time, not baked
/// in at queue time. No real state is ever mutated; this only ever changes one pure function's return
/// value for the caller that explicitly opted in.
/// </summary>
internal static class GhostStrengthSuppressionScope
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
