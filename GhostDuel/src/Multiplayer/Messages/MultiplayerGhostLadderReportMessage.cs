using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GhostDuel.Multiplayer.Messages;

/// <summary>
/// PLAN.md M9a: sent by every connected human (including the host, reporting to itself locally rather
/// than over the wire — see <c>MultiplayerGhostLadderCoordinator</c>) carrying that human's own
/// <c>MultiplayerGhostSnapshotStore.GetMaxSelectableLevel()</c> — the multiplayer ladder specifically,
/// never the singleplayer one. The host aggregates via <c>Min()</c> across everyone who has reported,
/// mirroring the native <c>StartRunLobby.UpdateMaxMultiplayerAscension()</c> pattern
/// (<c>StartRunLobby.cs:287-299</c>), then broadcasts <see cref="MultiplayerGhostLadderAggregateMessage"/>.
///
/// Discovered and assigned a wire id automatically: <c>MessageTypes.Initialize()</c> calls
/// <c>ReflectionHelper.GetSubtypesInMods&lt;INetMessage&gt;()</c> in addition to the native
/// <c>INetMessageSubtypes.All</c> (<c>MessageTypes.cs:11-17</c>), and <c>NetTypeCache</c> assigns ids by
/// sorting all discovered types by name (<c>NetTypeCache.cs:20</c>) — deterministic across processes
/// with the same mod set installed, no extra registration patch needed.
/// </summary>
public struct MultiplayerGhostLadderReportMessage : INetMessage
{
    public int maxSelectableLevel;

    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(maxSelectableLevel);
    }

    public void Deserialize(PacketReader reader)
    {
        maxSelectableLevel = reader.ReadInt();
    }
}
