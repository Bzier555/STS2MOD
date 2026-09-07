using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GhostDuel.Multiplayer.Messages;

/// <summary>
/// Fixes a real multiplayer desync (confirmed live, 2026-09-04): <c>MultiplayerLegacyAscensionEntry
/// ._armed</c> is a plain static field, local to each process — set to <c>true</c> only on the host's
/// own process, when the host clicks the host-only "GHOST ASCENSION" button. A joining client's own
/// process never learns this, so every client-side effect gated on <c>_armed</c> (ladder-coordinator
/// attachment, the purple-icon recolor, and critically tagging that client's own local
/// <c>RunState.CreateForNewRun</c> with <c>MultiplayerLegacyAscensionModifier</c>) silently never
/// fired for the joiner — the joining client simulated a completely normal Standard run while the
/// host's simulation had the modifier, diverging as soon as real gameplay started and killing the
/// connection.
///
/// This empty marker message is sent host → one specific client (targeted, not broadcast), in direct
/// reply to that client's own <see cref="MultiplayerLegacyAscensionArmQueryMessage"/> — see that
/// class's own doc comment for why a client-initiated query replaced the original host-initiated
/// push (confirmed live not to work: the client's own message-handling pipeline wasn't ready yet at
/// the point the host originally tried to push this). Its only job is to flip that client's own
/// <c>_armed</c> to <c>true</c>; see <c>MultiplayerLegacyAscensionEntry.OnArmMessageReceived</c> for
/// what that retroactively unlocks.
///
/// Discovered and assigned a wire id automatically, same as every other mod-defined <c>INetMessage</c>
/// (see <c>MultiplayerGhostLadderReportMessage</c>'s own doc comment for the mechanism).
/// </summary>
public struct MultiplayerLegacyAscensionArmMessage : INetMessage
{
    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
    }

    public void Deserialize(PacketReader reader)
    {
    }
}
