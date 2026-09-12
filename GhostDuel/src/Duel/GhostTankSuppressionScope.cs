using System;
using System.Threading;

namespace GhostDuel.Duel;

/// <summary>
/// Ambient signal read by P36 (<c>EnginePatches.cs</c>): while active, <c>TankPower
/// .ModifyDamageMultiplicative</c> returns 1 instead of its real value, for exactly the duration of one
/// <c>Hook.ModifyDamage</c> call. Same reasoning as <see cref="GhostGuardedSuppressionScope"/>, kept as
/// its own scope rather than shared — <c>Tank</c>'s own effect (doubling "powered attack" damage its
/// own holder takes, confirmed by reading <c>TankPower.cs</c>) is a self-inflicted cost the *player who
/// played Tank* accepts, a materially different case from Guarded's ally-protecting one, even though
/// both are target-side multiplicative hooks needing the same live-at-resolution treatment (see that
/// class's doc comment for the full "why not locked like Vulnerable" reasoning). No real state is ever
/// mutated; this only ever changes one pure function's return value for the caller that explicitly
/// opted in.
/// </summary>
internal static class GhostTankSuppressionScope
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
