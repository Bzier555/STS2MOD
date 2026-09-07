using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GhostDuel.Multiplayer.Messages;

/// <summary>
/// PLAN.md M9f: host → a specific rejoining client, one entry per Ghost in the party. Deliberately
/// scoped narrower than a full state rebuild: this project has no confirmed trace of whatever native
/// method a client calls to reconstruct its own combat *scene* after a genuine process-restart-style
/// rejoin (the seam P5/P6 patch, <c>CombatRoom.StartCombat</c>/<c>NCombatRoom.AddCreature</c>, fires on
/// original combat entry — whether it fires again on a native rejoin is unverified), so building a
/// full "recreate the Ghosts from nothing" path here would be guessing at an unobserved mechanism.
/// Scoped instead to the case this project *can* build honestly: a brief connection drop where the
/// rejoining client's own process and <c>GhostSession</c> survive locally and only the network link
/// needs restoring — here, this message's job is to report the host's authoritative current values so
/// drift can be logged (<c>RECONNECT-GHOST-RESYNC</c>), not to reconstruct anything from scratch.
/// </summary>
public struct GhostPartyResyncMessage : INetMessage
{
    public ulong[]? ghostNetIds;

    public int[]? currentHp;

    public int[]? block;

    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        ulong[] ids = ghostNetIds ?? [];
        writer.WriteInt(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            writer.WriteULong(ids[i]);
            writer.WriteInt(currentHp?[i] ?? 0);
            writer.WriteInt(block?[i] ?? 0);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        int count = reader.ReadInt();
        ghostNetIds = new ulong[count];
        currentHp = new int[count];
        block = new int[count];
        for (int i = 0; i < count; i++)
        {
            ghostNetIds[i] = reader.ReadULong();
            currentHp[i] = reader.ReadInt();
            block[i] = reader.ReadInt();
        }
    }
}
