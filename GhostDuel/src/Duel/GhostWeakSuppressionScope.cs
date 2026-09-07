using System;
using System.Threading;

namespace GhostDuel.Duel;

/// <summary>
/// Ambient signal read by P15 (<c>EnginePatches.cs</c>): while active, <c>WeakPower
/// .ModifyDamageMultiplicative</c> returns 1 instead of its real value, for exactly the duration of
/// one <c>Hook.ModifyDamage</c> call. Used to compute a <see cref="QueuedDamagePacket.LockedAmount"/>
/// that honestly excludes Weak's contribution — Weak must be re-evaluated live at display/resolution
/// time (COMBAT-RULES.md §5), not divided out of a jointly-computed result after the fact
/// (POSTMORTEM F14). No real state is ever mutated; this only ever changes one pure function's return
/// value for the caller that explicitly opted in.
/// </summary>
internal static class GhostWeakSuppressionScope
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
