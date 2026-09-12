using System;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace GhostDuel.Diagnostics;

/// <summary>
/// Structured, always-on logging with a stable prefix and a taxonomy of named log points, so that a
/// missing entry is itself diagnostic. Writes to both the native Godot log and a dedicated file,
/// following the shape of the game's own <c>AutoSlayLog</c>
/// (<c>MegaCrit.Sts2.Core.AutoSlay/AutoSlayLog.cs</c>).
/// </summary>
public static class GhostLog
{
    private const string Prefix = "[GhostDuel]";

    private static StreamWriter? _fileWriter;
    private static readonly object Lock = new();

    /// <summary>Opens the dedicated log file under the Godot user data directory. Call once at mod init.</summary>
    public static string OpenLogFile()
    {
        string logDir = Path.Combine(OS.GetUserDataDir(), "logs");
        Directory.CreateDirectory(logDir);
        string path = Path.Combine(logDir, "ghostduel.log");
        lock (Lock)
        {
            _fileWriter?.Dispose();
            _fileWriter = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        return path;
    }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(LogLevel level, string message)
    {
        string line = $"{Prefix} {message}";
        switch (level)
        {
            case LogLevel.Warn:
                Log.Warn(line);
                break;
            case LogLevel.Error:
                Log.Error(line);
                break;
            default:
                Log.Info(line);
                break;
        }
        lock (Lock)
        {
            _fileWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {line}");
        }
    }

    // Named log points from PLAN.md M0 task 2. Each name states what it proves, so its absence in a
    // run's log is itself the diagnosis:
    //   no HAND entry        -> draw / pile population problem
    //   no LEGAL entry       -> CanPlay or IsValidTarget problem
    //   no SPENT entry       -> resource-spending problem
    //   SPENT but no PLAYED  -> OnPlayWrapper / card-effect problem
    //   no TURN-END entry    -> hang inside the turn

    public static void Hand(string ownerLabel, int count) =>
        Info($"HAND {ownerLabel}: drew {count} card(s)");

    public static void Legal(string ownerLabel, string cardEntry, bool canPlay, string reason) =>
        Info($"LEGAL {ownerLabel} {cardEntry}: canPlay={canPlay} reason={reason}");

    public static void Spent(string ownerLabel, string cardEntry, int energy, int stars) =>
        Info($"SPENT {ownerLabel} {cardEntry}: energy={energy} stars={stars}");

    public static void Played(string ownerLabel, string cardEntry) =>
        Info($"PLAYED {ownerLabel} {cardEntry}");

    /// <summary>
    /// State snapshot after a card resolves — PLAN.md M2's exit gate asks for "per-card logs showing
    /// native values": the actual post-resolution HP/Block/Energy read straight off the Creature/
    /// PlayerCombatState, not a value computed mod-side, so a wrong number here is the engine's own
    /// number, not a reimplementation bug.
    /// </summary>
    public static void State(string ownerLabel, string cardEntry, int ghostHp, int ghostMaxHp, int ghostBlock, int energyRemaining, int targetHp, int targetBlock) =>
        Info($"STATE {ownerLabel} {cardEntry}: ghostHp={ghostHp}/{ghostMaxHp} ghostBlock={ghostBlock} energy={energyRemaining} targetHp={targetHp} targetBlock={targetBlock}");

    public static void TurnStart(string ownerLabel, int round) =>
        Info($"TURN-START {ownerLabel} round={round}");

    public static void TurnEnd(string ownerLabel, int round) =>
        Info($"TURN-END {ownerLabel} round={round}");

    /// <summary>M3's queued-damage core (COMBAT-RULES.md §2-§5): a direct-damage hit was locked
    /// instead of resolved immediately. <paramref name="lockedAmount"/> is the Weak-free baseline.</summary>
    public static void Queued(string ownerLabel, string cardEntry, string targetLabel, decimal lockedAmount) =>
        Info($"QUEUED {ownerLabel} {cardEntry}: target={targetLabel} lockedAmount={lockedAmount}");

    /// <summary>A previously queued packet resolved for real — <paramref name="displayAmount"/> is
    /// what actually applied (LockedAmount adjusted by the dealer's live Weak at this moment).</summary>
    public static void Resolved(string ownerLabel, string targetLabel, decimal lockedAmount, decimal displayAmount) =>
        Info($"RESOLVED {ownerLabel}: target={targetLabel} lockedAmount={lockedAmount} appliedAmount={displayAmount}");

    // PLAN.md M9 named log points. This is the first milestone where one person reading one log isn't
    // enough — these are written on every client that observes the event, specifically so a host log
    // and a joining-client log can be diffed line-for-line:
    //   PARTY absent on a client              -> it never received the party data at all
    //   LADDER-REPORT sent but no aggregate    -> the host never received or never recomputed
    //   SNAPSHOT-SENT with no matching -RECV   -> a dropped message, not a logic bug
    //   GHOST-BROADCAST vs GHOST-EXECUTE       -> the direct evidence for "all clients agree" (M9e gate)
    //   VIEWER-INDICATOR mismatch              -> the per-viewer filter is wrong, not the queue itself

    public static void Party(string selfLabel, string partyDescription) =>
        Info($"PARTY {selfLabel}: {partyDescription}");

    public static void LadderReport(string selfLabel, int ownMaxLevel, int? aggregateMin) =>
        Info($"LADDER-REPORT {selfLabel}: ownMax={ownMaxLevel} aggregateMin={(aggregateMin?.ToString() ?? "pending")}");

    /// <summary>2026-09-07: deckCount/relicCount added (user report: "in Ascension 1 fight the ghosts
    /// appear to have base decks") — without these, a "hasPreviousGhost=true" log line can't
    /// distinguish "loaded the real winning build" from "loaded/reported something that happens to look
    /// like a fresh 10-card starter deck," which is exactly the open question here. Read this alongside
    /// <see cref="SnapshotReceived"/>'s own count on the *other* end of the same report to see whether a
    /// mismatch happens in transit, or the sent count was already wrong (the file itself, or the human
    /// identified as "me" when it was saved).</summary>
    public static void SnapshotSent(string selfLabel, int level, bool hasPreviousGhost, int deckCount = -1, int relicCount = -1) =>
        Info($"SNAPSHOT-SENT {selfLabel}: level={level} hasPreviousGhost={hasPreviousGhost} deckCount={deckCount} relicCount={relicCount}");

    public static void SnapshotReceived(ulong senderNetId, int level, bool hasPreviousGhost, int deckCount = -1, int relicCount = -1) =>
        Info($"SNAPSHOT-RECV from={senderNetId}: level={level} hasPreviousGhost={hasPreviousGhost} deckCount={deckCount} relicCount={relicCount}");

    public static void GhostBroadcast(uint sequenceNumber, ulong ghostNetId, string cardEntry, string? targetLabel) =>
        Info($"GHOST-BROADCAST seq={sequenceNumber} ghost={ghostNetId} card={cardEntry} target={targetLabel ?? "none"}");

    public static void GhostExecute(uint sequenceNumber, ulong ghostNetId, string cardEntry, string? targetLabel) =>
        Info($"GHOST-EXECUTE seq={sequenceNumber} ghost={ghostNetId} card={cardEntry} target={targetLabel ?? "none"}");

    public static void ViewerIndicator(string viewerLabel, ulong ghostNetId, decimal amount) =>
        Info($"VIEWER-INDICATOR viewer={viewerLabel} ghost={ghostNetId} amount={amount}");

    public static void ReconnectGhostResync(string direction, string summary) =>
        Info($"RECONNECT-GHOST-RESYNC {direction}: {summary}");
}
