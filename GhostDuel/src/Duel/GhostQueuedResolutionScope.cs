using System;
using System.Threading;

namespace GhostDuel.Duel;

/// <summary>
/// Ambient signal read by P16 and P17 (<c>EnginePatches.cs</c>). Two jobs, both narrow:
/// (1) P16 makes <c>Hook.ModifyDamage</c> return its input <c>amount</c> unchanged while this is
/// active, so resolving a <see cref="QueuedDamagePacket"/> through the real <c>CreatureCmd.Damage</c>
/// does not re-apply modifiers already baked into <c>LockedAmount</c> — the "explicit token carried
/// through the resolution call" COMBAT-RULES.md §5 asks for, replacing the old mod's ambient
/// tuple-matching (POSTMORTEM F14). (2) P17 checks this to avoid re-queuing the very
/// <c>CreatureCmd.Damage</c> call this scope was opened to resolve.
/// </summary>
internal static class GhostQueuedResolutionScope
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
