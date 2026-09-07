using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GhostDuel.Multiplayer.Messages;

/// <summary>
/// PLAN.md M9a: host → every client (including itself, handled locally — see
/// <c>MultiplayerGhostLadderCoordinator</c>), sent whenever the aggregated minimum in
/// <see cref="MultiplayerGhostLadderReportMessage"/>'s collection changes. This is what drives the
/// multiplayer Ghost-ascension picker UI (M9b), the same role
/// <c>LobbyListener.MaxAscensionChanged()</c> plays for the native multiplayer ascension cap.
/// </summary>
public struct MultiplayerGhostLadderAggregateMessage : INetMessage
{
    public int aggregateMinLevel;

    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(aggregateMinLevel);
    }

    public void Deserialize(PacketReader reader)
    {
        aggregateMinLevel = reader.ReadInt();
    }
}
