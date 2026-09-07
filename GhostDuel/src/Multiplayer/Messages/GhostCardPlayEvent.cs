using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GhostDuel.Multiplayer.Messages;

/// <summary>
/// PLAN.md M9e: host → all, one per Ghost decision. Only the host ever runs a chooser
/// (<c>GhostActionSync.IsHost</c>); every other client executes exactly the named card/target instead
/// of deciding for itself. <see cref="handIndex"/> + <see cref="cardIdForValidation"/> (the card's own
/// canonical <c>Id.Entry</c>, e.g. "STRIKE") identify the card: a bare index alone would silently
/// misplay if a client's own hand ever drifted out of sync with the host's, so the id is cross-checked
/// before executing, not trusted blindly — a mismatch is logged loudly rather than played anyway. The
/// target, if any, is identified by its owning <see cref="MegaCrit.Sts2.Core.Entities.Players.Player.NetId"/>
/// — stable and already synced by the native lobby/join flow for every human and, via
/// <c>GhostPlayerFactory</c>'s own deterministic per-party-slot pool, for every Ghost too. A pet
/// target (no <c>NetId</c> of its own) is a known gap, not yet handled.
/// </summary>
public struct GhostCardPlayEvent : INetMessage
{
    public uint sequenceNumber;

    public ulong ghostNetId;

    public bool isEndTurn;

    public int handIndex;

    public string? cardIdForValidation;

    public bool hasTarget;

    public ulong targetNetId;

    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(sequenceNumber);
        writer.WriteULong(ghostNetId);
        writer.WriteBool(isEndTurn);
        writer.WriteInt(handIndex);
        writer.WriteString(cardIdForValidation ?? string.Empty);
        writer.WriteBool(hasTarget);
        writer.WriteULong(targetNetId);
    }

    public void Deserialize(PacketReader reader)
    {
        sequenceNumber = reader.ReadUInt();
        ghostNetId = reader.ReadULong();
        isEndTurn = reader.ReadBool();
        handIndex = reader.ReadInt();
        cardIdForValidation = reader.ReadString();
        hasTarget = reader.ReadBool();
        targetNetId = reader.ReadULong();
    }
}
