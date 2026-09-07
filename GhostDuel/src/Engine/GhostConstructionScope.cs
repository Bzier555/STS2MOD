using System;
using System.Threading;

namespace GhostDuel.Engine;

/// <summary>
/// Narrowly scopes <c>Player</c> creature construction that must land on the enemy side. Ported
/// verbatim from the first pass (POSTMORTEM.md salvage list) — a minimal, correct, depth-counted
/// <see cref="AsyncLocal{T}"/> scope, not the AsyncLocal *view rewriting* the postmortem warns against
/// (POSTMORTEM F8). This only ever gates one postfix (P1 in ARCHITECTURE.md §4), never an engine-wide
/// property.
/// </summary>
internal static class GhostConstructionScope
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsActive => Depth.Value > 0;

    public static IDisposable Begin()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Depth.Value = Math.Max(0, Depth.Value - 1);
        }
    }
}
