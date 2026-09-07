using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GhostDuel.Multiplayer.Messages;

/// <summary>
/// Sent client → host, from <c>MultiplayerLegacyAscensionEntry.Postfix_ReportLadderOnClientInit</c>
/// (<c>NCharacterSelectScreen.InitializeMultiplayerAsClient</c>) — the same point the already-working
/// ladder-report messages use <c>screen.Lobby.NetService</c>, so the client's own message-handling
/// pipeline (registered on its own <c>StartRunLobby</c>, constructed earlier) is definitely ready by
/// here. Replaces an original design where the *host* tried to push
/// <see cref="MultiplayerLegacyAscensionArmMessage"/> to a client as soon as it joined — confirmed
/// live not to work: the client's own <c>StartRunLobby</c> didn't exist yet at that point (the actual
/// join handshake goes through a separate <c>JoinFlow</c> class first), so the message arrived to no
/// registered handler and was silently dropped. Asking only once the client itself knows it's ready,
/// rather than the host guessing when that might be, sidesteps that race entirely.
///
/// No target — <c>INetGameService.SendMessage&lt;T&gt;(T)</c> auto-routes to the host for a non-host
/// caller, the same call shape <c>MultiplayerGhostLadderCoordinator.ReportOwnLadderLevel</c> already
/// uses. The host replies with <see cref="MultiplayerLegacyAscensionArmMessage"/> if armed, and simply
/// doesn't reply otherwise (a normal Standard-mode lobby needs no reply at all).
/// </summary>
public struct MultiplayerLegacyAscensionArmQueryMessage : INetMessage
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
