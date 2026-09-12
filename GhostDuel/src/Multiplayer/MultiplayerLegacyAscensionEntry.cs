using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using GhostDuel.Multiplayer.Messages;
using GhostDuel.Progression;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace GhostDuel.Multiplayer;

/// <summary>
/// PLAN.md M9b: the multiplayer Ghost-ascension mode's entry point — a 4th button, a peer of
/// Standard/Daily/Custom in <c>NMultiplayerHostSubmenu</c>, mirroring
/// <c>GhostDuel.Progression.LegacyAscensionEntry</c>'s established shape exactly, adapted for the
/// multiplayer host flow and the separate multiplayer ladder (<see cref="MultiplayerGhostSnapshotStore"/>,
/// <see cref="MultiplayerGhostLadderCoordinator"/>).
///
/// Only the host ever sees this button (<c>NMultiplayerHostSubmenu</c> is host-only UI — a joining
/// client never reaches it, confirmed via <c>NMultiplayerSubmenu</c>'s own Host/Join button split) so
/// only the host needs to *start* the mode; joining clients simply inherit whatever the host started,
/// exactly like Standard/Daily/Custom already do.
/// </summary>
internal static class MultiplayerLegacyAscensionEntry
{
    private static readonly StringName HueParam = new("h");
    private static readonly StringName ValueParam = new("v");

    /// <summary>Same purple used by the singleplayer ladder (<c>LegacyAscensionEntry.PurpleHue</c>) —
    /// deliberately identical tint so both Ghost-ascension tracks read as visually related, distinct
    /// from a normal run.</summary>
    private const float PurpleHue = 0.78f;

    private static bool _armed;
    private static MultiplayerGhostLadderCoordinator? _coordinator;

    /// <summary>Read by <c>MultiplayerLegacyAscensionGhostFightSplice</c> (M9c) to build the Ghost
    /// party from every human's own reported snapshot, once the run reaches its Act-3-boss victory —
    /// potentially much later than lobby time, so the coordinator is deliberately kept alive (not
    /// disposed) past a successful embark, unlike the back-out case.</summary>
    public static MultiplayerGhostLadderCoordinator? Coordinator => _coordinator;

    /// <summary>
    /// Confirmed live 2026-09-08: a run resumed via the "load"/reconnect join path
    /// (<c>ClientLoadJoinRequestMessage</c>, seen replacing the fresh-lobby
    /// <c>ClientLoadJoinRequestMessage</c> in <c>godot.log</c> after the player restarted their lobby to
    /// work around the stranded-overlay bug) never touches <c>NCharacterSelectScreen</c> at all, so
    /// neither <see cref="Start"/> nor <see cref="Postfix_ReportLadderOnHostInit"/> /
    /// <see cref="Postfix_ReportLadderOnClientInit"/> ever ran — <see cref="_coordinator"/> stayed
    /// <c>null</c> for the entire process. <c>MultiplayerLegacyAscensionGhostFightSplice.BuildGhost</c>
    /// found no coordinator at all and logged "no multiplayer-ladder snapshot report received," skipping
    /// the whole party fight for every human — confirmed from a live log showing none of this class's own
    /// log lines (no "starting multiplayer Ghost-ascension host flow," no "tagged multiplayer run") ever
    /// printed that session, only the splice's own boss-defeated/error lines.
    ///
    /// <c>RunState.Modifiers</c> is loaded straight from the save regardless of how the run was reached,
    /// so a live <see cref="MultiplayerLegacyAscensionModifier"/> is licence enough to (re)attach a
    /// coordinator here and redo the same report handshake <see cref="AttachCoordinatorAndReport"/> does
    /// at fresh-embark time — just later, and without "Acts 1-3 give plenty of time" to rely on, hence
    /// the bounded wait for every human's report rather than firing and hoping.
    /// </summary>
    public static async Task<MultiplayerGhostLadderCoordinator> EnsureSnapshotReportsAsync(
        INetGameService net, IReadOnlyList<Player> humans, int lockedInLevel, TimeSpan timeout)
    {
        bool freshlyAttached = _coordinator is null;
        MultiplayerGhostLadderCoordinator coordinator = _coordinator ??= new MultiplayerGhostLadderCoordinator(net);
        if (freshlyAttached)
        {
            GhostLog.Warn("MultiplayerLegacyAscensionEntry: no ladder coordinator was attached this session (this run was likely resumed/reconnected rather than freshly hosted or joined through character select) — attaching one now and re-reporting this human's own snapshot.");
            coordinator.ReportOwnSnapshot(lockedInLevel);
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (humans.Any(h => !coordinator.ReportedSnapshots.ContainsKey(h.NetId)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        IEnumerable<ulong> stillMissing = humans.Select(h => h.NetId).Where(id => !coordinator.ReportedSnapshots.ContainsKey(id));
        if (stillMissing.Any())
        {
            GhostLog.Warn($"MultiplayerLegacyAscensionEntry: gave up waiting for snapshot reports from [{string.Join(", ", stillMissing)}] after {timeout.TotalSeconds}s.");
        }
        return coordinator;
    }

    public static void Start(NMultiplayerHostSubmenu submenu)
    {
        if (GhostSession.Current is not null)
        {
            GhostLog.Warn("MultiplayerLegacyAscensionEntry: a previous GhostSession was still active — disposing it and starting fresh.");
            GhostSession.Current.Dispose();
        }

        GhostLog.OpenLogFile();
        _armed = true;
        GhostLog.Info("MultiplayerLegacyAscensionEntry: starting multiplayer Ghost-ascension host flow.");

        Control loadingOverlay = MultiplayerUiAccess.GetLoadingOverlay(submenu);
        NSubmenuStack stack = NSubmenuStackAccess.GetStack(submenu);
        TaskHelper.RunSafely(NMultiplayerHostSubmenu.StartHostAsync(GameMode.Standard, loadingOverlay, stack));
    }

    /// <summary>Defensive: if the player backs out of character select without embarking, don't leave
    /// a later, unrelated multiplayer run's ascension picker or run-tagging armed.</summary>
    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
    private static class Postfix_DisarmOnBackOut
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!_armed)
            {
                return;
            }
            _armed = false;
            _coordinator?.Dispose();
            _coordinator = null;
            GhostLog.Info("MultiplayerLegacyAscensionEntry: character select closed without embarking — disarmed.");
        }
    }

    /// <summary>Host side: the lobby now exists (<c>StartRunLobby.NetService.Type == Host</c>) —
    /// attach the coordinator and report the host's own multiplayer-ladder progress. See
    /// <see cref="MultiplayerGhostLadderCoordinator"/>'s doc comment for why the lobby's own
    /// <c>NetService</c>, not <c>RunManager.Instance.NetService</c>, is the correct instance here.</summary>
    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsHost))]
    private static class Postfix_ReportLadderOnHostInit
    {
        [HarmonyPostfix]
        private static void Postfix(NCharacterSelectScreen __instance)
        {
            if (_armed)
            {
                AttachCoordinatorAndReport(__instance);
            }
        }
    }

    /// <summary>Client side: a joining human reaches the same screen once they've joined the host's
    /// lobby. Symmetric with the host postfix above — every connected human reports exactly once,
    /// regardless of which side of the connection they are. <b>Revised 2026-09-04</b>: <c>_armed</c>
    /// is never yet <c>true</c> on the client at this exact synchronous moment — see this class's
    /// "arm query" doc comment for why. Ask the host directly instead of assuming, and remember this
    /// screen so <see cref="OnArmMessageReceived"/> can finish the job once the reply arrives.
    /// <b>Revised 2026-09-07</b> (user question: "this should have debug logs for host and joiners?"):
    /// confirmed by reading every <c>GhostLog.OpenLogFile()</c> call site — all of them (this class's own
    /// <see cref="Start"/>, <c>MultiplayerDebugGhostDuelEntry.Start</c>, the singleplayer entries) sit
    /// behind a host-only or singleplayer-only button; a joining client never called any of them, so it
    /// never got its own dedicated <c>ghostduel.log</c> — its <c>[GhostDuel]</c> lines only ever reached
    /// the native, much noisier <c>godot.log</c>. This postfix already fires for every joining client on
    /// every multiplayer character-select entry regardless of which host mode was picked (Legacy
    /// Ascension, the plain debug fight, or neither), so opening the log file here — unconditionally,
    /// before the arm check below — covers a joining client for both multiplayer Ghost modes for free,
    /// symmetric with how the host-side entries also open it unconditionally.</summary>
    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsClient))]
    private static class Postfix_ReportLadderOnClientInit
    {
        [HarmonyPostfix]
        private static void Postfix(NCharacterSelectScreen __instance, ClientLobbyJoinResponseMessage message)
        {
            GhostLog.OpenLogFile();
            GhostLog.Info("MultiplayerLegacyAscensionEntry: joining client reached character select — opened this client's own ghostduel.log.");

            if (_armed)
            {
                AttachCoordinatorAndReport(__instance);
                return;
            }
            _pendingClientScreen = __instance;
            __instance.Lobby.NetService.SendMessage(new MultiplayerLegacyAscensionArmQueryMessage());
        }
    }

    private static void AttachCoordinatorAndReport(NCharacterSelectScreen screen)
    {
        StartRunLobby lobby = screen.Lobby;
        _coordinator?.Dispose();
        MultiplayerGhostLadderCoordinator coordinator = new(lobby.NetService);
        _coordinator = coordinator;
        coordinator.AggregateChanged += newMax =>
        {
            LegacyAscensionUiAccess.SetMaxAscension(lobby, newMax);
            lobby.LobbyListener.MaxAscensionChanged();
            lobby.SyncAscensionChange(Math.Min(lobby.Ascension, newMax));
        };
        coordinator.ReportOwnLadderLevel();
    }

    /// <summary>Same fix as <c>LegacyAscensionEntry.Postfix_KeepPanelVisibleAtZero</c>: level 0 is a
    /// real, always-selectable rung for this ladder too, not "off."</summary>
    [HarmonyPatch(typeof(NAscensionPanel), nameof(NAscensionPanel.SetMaxAscension))]
    private static class Postfix_KeepPanelVisibleAtZero
    {
        [HarmonyPostfix]
        private static void Postfix(NAscensionPanel __instance)
        {
            if (_armed)
            {
                __instance.Visible = true;
            }
        }
    }

    /// <summary><b>Revised 2026-09-04</b>: same "not yet armed at this synchronous moment" issue as
    /// <see cref="Postfix_ReportLadderOnClientInit"/> — for a joining client, stash this panel instead
    /// of giving up, so <see cref="OnArmMessageReceived"/> can recolor it retroactively once the host's
    /// reply arrives.</summary>
    [HarmonyPatch(typeof(NAscensionPanel), nameof(NAscensionPanel.Initialize))]
    private static class Postfix_PurpleFireForMultiplayerLegacyAscension
    {
        [HarmonyPostfix]
        private static void Postfix(NAscensionPanel __instance, MultiplayerUiMode mode)
        {
            if (mode is not (MultiplayerUiMode.Host or MultiplayerUiMode.Client))
            {
                return;
            }
            if (_armed)
            {
                ApplyPurpleFire(__instance);
                return;
            }
            if (mode == MultiplayerUiMode.Client)
            {
                _pendingClientAscensionPanel = __instance;
            }
        }
    }

    private static void ApplyPurpleFire(NAscensionPanel panel)
    {
        ShaderMaterial? iconHsv = LegacyAscensionUiAccess.GetIconHsv(panel);
        iconHsv?.SetShaderParameter(HueParam, PurpleHue);
        iconHsv?.SetShaderParameter(ValueParam, 1.1f);
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
    private static class Prefix_TagMultiplayerLegacyAscensionRun
    {
        [HarmonyPrefix]
        private static void Prefix(ref IReadOnlyList<ModifierModel> modifiers, int ascensionLevel)
        {
            if (!_armed)
            {
                return;
            }
            _armed = false;

            MultiplayerLegacyAscensionModifier modifier = (MultiplayerLegacyAscensionModifier)ModelDb.Modifier<MultiplayerLegacyAscensionModifier>().ToMutable();
            modifier.LadderLevel = ascensionLevel;
            List<ModifierModel> combined = (modifiers ?? Array.Empty<ModifierModel>()).ToList();
            combined.Add(modifier);
            modifiers = combined;

            GhostLog.Info($"MultiplayerLegacyAscensionEntry: tagged multiplayer run at A{ascensionLevel} with MultiplayerLegacyAscensionModifier.");

            // Report now (run creation), not at the splice — Acts 1-3 give plenty of time for every
            // human's snapshot report to arrive well before it's actually needed, and this is the one
            // point that reliably fires exactly once, after the level is genuinely locked in.
            _coordinator?.ReportOwnSnapshot(ascensionLevel);
        }
    }

    [HarmonyPatch(typeof(NMultiplayerHostSubmenu), "_Ready")]
    private static class Postfix_AddButton
    {
        [HarmonyPostfix]
        private static void Postfix(NMultiplayerHostSubmenu __instance)
        {
            NSubmenuButton? dailyButton = __instance.GetNodeOrNull<NSubmenuButton>("DailyButton");
            NSubmenuButton? template = __instance.GetNodeOrNull<NSubmenuButton>("CustomRunButton");
            if (template is null)
            {
                GhostLog.Warn("MultiplayerLegacyAscensionEntry: CustomRunButton template not found under NMultiplayerHostSubmenu; skipping menu button.");
                return;
            }

            NSubmenuButton button = (NSubmenuButton)template.Duplicate();
            button.Name = "MultiplayerLegacyAscensionButton";
            template.GetParent().AddChildSafely(button);
            button.Visible = true;
            button.Enable();

            // Same "no shared layout container" situation as LegacyAscensionEntry.Postfix_AddButton —
            // anchor off the real Daily-to-Custom spacing rather than a hardcoded offset.
            if (dailyButton is not null)
            {
                button.Position = template.Position + (template.Position - dailyButton.Position);
            }
            else
            {
                GhostLog.Warn("MultiplayerLegacyAscensionEntry: DailyButton not found; could not compute the new button's position from sibling spacing, leaving it at CustomRunButton's own position.");
            }

            button.GetNodeOrNull<MegaLabel>("%Title")?.SetTextAutoSize("GHOST ASCENSION");
            MegaRichTextLabel? description = button.GetNodeOrNull<MegaRichTextLabel>("%Description");
            if (description is not null)
            {
                description.Text = "Fight your party's own ghosts of your last winning multiplayer run.";
            }

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Start(__instance)));
        }
    }

    /// <summary>
    /// Confirmed live (2026-09-04, two-client test): the host correctly saw the purple flame icon and
    /// Ascension 0; the joining client saw the normal orange/red Standard-mode icon instead. Both
    /// proceeded through character select, Neow and into the first combat, then lost connection and
    /// crashed. Root cause: <c>_armed</c> is a plain static field, local to each process — it becomes
    /// <c>true</c> only on the host's own process, when the host clicks the host-only button above; a
    /// joining client's own process never learns this, so every effect gated on <c>_armed</c>
    /// (<see cref="Postfix_ReportLadderOnClientInit"/>, the purple-icon recolor, and critically
    /// <see cref="Prefix_TagMultiplayerLegacyAscensionRun"/>) silently never fires for the joiner — the
    /// joining client's own <c>RunState</c> is never tagged with <c>MultiplayerLegacyAscensionModifier</c>
    /// at all, so it independently simulates a completely normal Standard run while the host's
    /// simulation has the modifier, diverging as soon as real gameplay starts and killing the connection.
    /// <see cref="Postfix_ReportLadderOnClientInit"/>'s own doc comment says "symmetric with the host
    /// postfix above" — that was always the intent, the <c>_armed</c> guard just silently broke it.
    ///
    /// <b>First fix attempt (confirmed live NOT to work, same day)</b>: a targeted message sent from a
    /// prefix on <c>StartRunLobby.HandleClientLobbyJoinRequestMessage</c>, betting that a Harmony
    /// prefix there would fire before the native response and arrive first on the same connection.
    /// Confirmed live from `godot.log`: it did arrive first — but the client's own `StartRunLobby`
    /// (where the handler for it was registered) didn't exist yet. The actual join handshake goes
    /// through a separate `JoinFlow` class first (`[JoinFlow] Sending ClientLobbyJoinRequestMessage and
    /// waiting for response message`); the client's own `StartRunLobby` isn't constructed until
    /// *after* that response comes back. The message arrived to a `NetMessageBus` with no handler
    /// registered for its type at all and was silently dropped (`Received message of type
    /// ...MultiplayerLegacyAscensionArmMessage, but no message handlers are registered for that
    /// type!`) — so `_armed` never actually flipped, despite the send-order bet being correct.
    ///
    /// <b>Fixed instead by having the client ask, once it actually knows it's ready</b> — inverting the
    /// flow rather than racing it. `Postfix_ReportLadderOnClientInit` (`NCharacterSelectScreen
    /// .InitializeMultiplayerAsClient`) is exactly the point the *working* ladder-report messages
    /// already use `screen.Lobby.NetService` successfully — by definition, the client's own
    /// `StartRunLobby` (and any handler registered in its constructor) must already exist there. When
    /// not yet armed, that postfix now sends `MultiplayerLegacyAscensionArmQueryMessage` (client →
    /// host, no target — <c>INetGameService.SendMessage&lt;T&gt;(T)</c> auto-routes to the host for a
    /// non-host caller, the same call shape <c>MultiplayerGhostLadderCoordinator.ReportOwnLadderLevel</c>
    /// already uses) and remembers the screen. The host's reply handler (registered alongside the arm
    /// handler in <see cref="Postfix_RegisterArmMessageHandlers"/>, a closure capturing that
    /// `StartRunLobby`'s own `NetService` so it can reply to the specific sender) answers immediately
    /// with `MultiplayerLegacyAscensionArmMessage` if `_armed` — the host is guaranteed already armed
    /// by the time any lobby exists at all, since `_armed = true` runs synchronously in `Start()`
    /// before `StartHostAsync` even creates one. <see cref="OnArmMessageReceived"/> then finishes the
    /// job retroactively: attaches the coordinator/reports (on the remembered screen) and recolors the
    /// ascension panel purple (on the remembered panel, stashed the same way by
    /// <see cref="Postfix_PurpleFireForMultiplayerLegacyAscension"/> when it also found itself not yet
    /// armed).
    ///
    /// <see cref="Prefix_TagMultiplayerLegacyAscensionRun"/> fires at actual embark time, well after
    /// character select — by far the most time of any of these to have completed one network
    /// round-trip, so it's the most confidently fixed by this change. The icon/panel-visibility fixes
    /// depend on this round-trip actually completing before the human finishes clicking through
    /// character select, which is a much smaller window — worth specifically watching on the next live
    /// test rather than assumed fixed.
    ///
    /// Not fixed here: <c>MultiplayerDebugGhostDuelEntry.cs</c> has its own, separate <c>_armed</c>
    /// field with a similar shape, not checked for the same bug class — the live report was
    /// specifically about the Ghost Ascension mode-select flow, not the debug-fight button.
    /// </summary>
    private static NCharacterSelectScreen? _pendingClientScreen;
    private static NAscensionPanel? _pendingClientAscensionPanel;

    private static void OnArmMessageReceived(MultiplayerLegacyAscensionArmMessage message, ulong senderId)
    {
        _armed = true;
        GhostLog.Info("MultiplayerLegacyAscensionEntry: armed by the host's reply to this client's arm query.");
        if (_pendingClientScreen is { } screen)
        {
            AttachCoordinatorAndReport(screen);
            _pendingClientScreen = null;
        }
        if (_pendingClientAscensionPanel is { } panel)
        {
            ApplyPurpleFire(panel);
            panel.Visible = true;
            _pendingClientAscensionPanel = null;
        }
    }

    [HarmonyPatch(typeof(StartRunLobby), MethodType.Constructor,
        typeof(GameMode), typeof(INetGameService), typeof(IStartRunLobbyListener), typeof(int))]
    private static class Postfix_RegisterArmMessageHandlers
    {
        [HarmonyPostfix]
        private static void Postfix(StartRunLobby __instance)
        {
            INetGameService net = __instance.NetService;
            net.RegisterMessageHandler<MultiplayerLegacyAscensionArmMessage>(OnArmMessageReceived);
            net.RegisterMessageHandler<MultiplayerLegacyAscensionArmQueryMessage>((message, senderId) =>
            {
                if (!_armed)
                {
                    return;
                }
                net.SendMessage(new MultiplayerLegacyAscensionArmMessage(), senderId);
                GhostLog.Info($"MultiplayerLegacyAscensionEntry: replied armed to NetId#{senderId}'s arm query.");
            });
        }
    }
}
