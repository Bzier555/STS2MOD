using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace GhostDuel.Multiplayer.Messages;

/// <summary>
/// PLAN.md M9a: sent by every connected human (including the host, handled locally — see
/// <c>MultiplayerGhostLadderCoordinator</c>) once the party's multiplayer ladder level is locked in,
/// just before combat. Carries that human's own <c>MultiplayerGhostSnapshotStore.Load(level - 1)</c>
/// result — their personal previous-level Ghost — or <see cref="hasPreviousGhost"/> = false for a
/// fresh A0 party (no prior multiplayer-ladder save yet). <see cref="SerializablePlayer"/> is
/// <c>IPacketSerializable</c> (confirmed via <c>GhostSnapshotFileStore</c>'s own doc comment), so it
/// round-trips through <see cref="PacketWriter.Write{T}"/>/<see cref="PacketReader.Read{T}"/> directly
/// — the same native serialization the engine already uses for player data, not a re-encoded copy.
///
/// <see cref="ShouldBroadcast"/> is <c>true</c> (unlike the ladder-level report): every human needs to
/// end up with *every* other human's snapshot, not just the host, so the whole party can be built
/// identically on every client (confirmed via <c>NetHostGameService.OnPacketReceived</c>/
/// <c>BroadcastMessage</c>: setting this flag makes the host automatically relay a client's message to
/// every other connected client while preserving the *original* sender's id — exactly what's needed
/// here, since the receiving dictionary is keyed by sender NetId).
/// </summary>
public struct MultiplayerGhostSnapshotReportMessage : INetMessage
{
    public int level;

    public bool hasPreviousGhost;

    public SerializablePlayer? player;

    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(level);
        writer.WriteBool(hasPreviousGhost);
        if (hasPreviousGhost && player is { } value)
        {
            writer.Write(value);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        level = reader.ReadInt();
        hasPreviousGhost = reader.ReadBool();
        player = hasPreviousGhost ? reader.Read<SerializablePlayer>() : null;
    }
}
