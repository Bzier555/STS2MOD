using System;
using GhostDuel.Diagnostics;
using GhostDuel.Progression;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Unlocks;

namespace GhostDuel.Ui;

/// <summary>
/// PLAN.md M10a: a Compendium page for browsing currently-saved Ghosts.
///
/// <b>Revision 2026-09-05</b>: the first pass (<c>ICapstoneScreen</c> + <c>NCapstoneContainer</c>) never
/// actually opened — confirmed live (<c>godot.log</c>: three swallowed
/// <c>NullReferenceException</c>s logged the moment Compendium was opened, with no on-screen change).
/// Root cause, confirmed by reading <c>NCapstoneContainer.cs:130</c>:
/// <c>NCapstoneContainer.Instance =&gt; NRun.Instance?.GlobalUi.CapstoneContainer</c> — that container
/// only exists <i>during an active run</i>. The Compendium is a main-menu screen; <c>NRun.Instance</c>
/// is null there, so <c>NCapstoneContainer.Instance?.Open(screen)</c> silently no-opped every time
/// (M10b's own use of the same container is unaffected — it opens from inside a live Ghost fight, where
/// <c>NRun.Instance</c> is real; confirmed working by the user's own testing).
///
/// This screen is now an <c>NSubmenu</c> instead, pushed via the SAME mechanism
/// <see cref="GhostDuel.Progression.NSubmenuStackAccess"/> already uses for the Legacy Ascension button
/// on <c>NSingleplayerSubmenu</c> — <c>NSubmenuStack.Push(NSubmenu)</c> (<c>NSubmenuStack.cs:118</c>) is
/// public and accepts an already-constructed instance directly, bypassing
/// <c>GetSubmenuType(Type)</c>'s hardcoded factory entirely (confirmed by reading
/// <c>NMainMenuSubmenuStack.GetSubmenuType(Type)</c>: every native submenu is lazily constructed then
/// <c>this.AddChildSafely(...)</c>'d onto the stack BEFORE <c>Push</c> is ever called on it — the same
/// two steps <c>GhostLedgerEntry.Open</c> now performs). This also fixes the underlying readiness bug
/// the first pass had regardless of container choice: <c>NRunHistory.Create()</c> instantiates a
/// <b>packed scene</b> (<c>NRunHistory.cs:313</c>) that is not yet part of any live <c>SceneTree</c>, so
/// none of its children's own <c>_Ready()</c> have fired yet — calling <c>LoadDeck</c>/<c>LoadRelics</c>
/// immediately (as the constructor used to) hit private fields those methods only populate in
/// <c>_Ready()</c>/<c>ConnectSignals()</c>, throwing. Now the data refresh happens from
/// <see cref="OnSubmenuOpened"/> — fired only after <c>NSubmenuStackAccess.GetStack(...).
/// AddChildSafely(this)</c> has already added the whole reparented subtree (BackButton, arrows,
/// DeckHistory, RelicHistory) to the live tree and their own <c>_Ready()</c> has run — the exact
/// convention <c>NRunHistory.OnSubmenuOpened</c> itself uses for the identical reason.
///
/// <b>Revision 2026-09-05, second pass</b> — two more issues found by the user's own testing after the
/// page finally opened: (1) no visible way to back out. Root cause: this screen defaulted to
/// <c>Visible = true</c> (a plain <c>Control</c>'s default) — <c>NSubmenuStack.Push</c>
/// (<c>NSubmenuStack.cs:129</c>) sets <c>screen.Visible = true</c> unconditionally, and Godot only
/// raises <c>VisibilityChanged</c> when a property write actually changes the value, so that line was a
/// no-op and <c>NSubmenu.OnScreenVisibilityChange</c> (which calls <c>_backButton.Enable()</c>) never
/// ran — the reparented back button stayed in the <c>Disable()</c>d state
/// <c>NSubmenu.ConnectSignals()</c> leaves every submenu's back button in until shown. Fixed by starting
/// <c>Visible = false</c>, matching every native submenu's own construction
/// (<c>NMainMenuSubmenuStack.GetSubmenuType(Type)</c>: <c>_singleplayerSubmenu.Visible = false;</c>
/// right after construction, same for every other case in that method). (2) the interactable controls
/// (ladder toggle, prev/next) were plain default-themed Godot <c>Button</c>s, not styled like the rest
/// of the game's UI. Fixed by reparenting the native <c>LeftArrow</c>/<c>RightArrow</c>
/// (<c>NRunHistoryArrowButton</c>) from the same throwaway <c>NRunHistory.Create()</c> instance already
/// providing <c>BackButton</c>/<c>DeckHistory</c>/<c>RelicHistory</c> — the exact themed pagination
/// control the page this screen is modeled on already uses — and removing the separate toggle button
/// entirely in favor of one flat, single-arrow-pair sequence across both ladders (solo levels, then
/// multiplayer levels), so there is exactly one themed prev/next control instead of two different kinds
/// of navigation next to each other.
///
/// Reuses the native deck/relic display widgets without reimplementing anything about how a card or
/// relic actually renders: constructs one throwaway <c>NRunHistory.Create()</c> instance purely to
/// source its already-scene-wired <c>BackButton</c>/<c>LeftArrow</c>/<c>RightArrow</c>/
/// <c>%DeckHistory</c>/<c>%RelicHistory</c> children (confirmed public methods
/// <c>NDeckHistory.LoadDeck(Player, IEnumerable&lt;SerializableCard&gt;)</c>/
/// <c>NRelicHistory.LoadRelics(Player, IEnumerable&lt;SerializableRelic&gt;)</c> take exactly the shape
/// <c>GhostSnapshotFileStore.Load(level).Player</c> already returns — no conversion code needed),
/// reparents those nodes into this screen, and discards the rest of that throwaway instance. Never
/// touches the game's own shared/cached <c>NRunHistory</c> instance — this one belongs to nobody else.
///
/// <b>Revision 2026-09-05, third pass</b> — the button stopped doing anything at all after the second
/// pass. Root cause: <c>_prevButton.IsLeft = true;</c> was set in this class's own constructor — before
/// <c>_prevButton</c> (reparented from the still-never-<c>_Ready()</c>'d throwaway instance, same as
/// every other reparented node here) had ever been added to a live tree. <c>NRunHistoryArrowButton
/// .IsLeft</c>'s setter (<c>NRunHistoryArrowButton.cs:64-74</c>) unconditionally touches
/// <c>_icon.FlipH</c> — a field only populated in that button's own <c>_Ready()</c> — so the write threw
/// a <c>NullReferenceException</c> straight out of this screen's constructor, which
/// <c>GhostLedgerEntry.Open</c> calls directly (<c>new GhostLedgerScreen()</c>, uncaught), aborting
/// before <c>stack.Push(screen)</c> ever ran — the button looked entirely dead. Fixed by moving that
/// assignment into <see cref="ConnectSignals"/> (fired from <c>_Ready()</c>, guaranteed to run only
/// after the whole reparented subtree — <c>_prevButton</c> included — has already had its own
/// <c>_Ready()</c> execute, per Godot's bottom-up child-then-parent order).
///
/// <b>Revision 2026-09-05, fourth pass</b> — three more issues found live: the header text never
/// showed (which ascension/character was being viewed was invisible), and the back button did nothing.
/// Root cause of the header: <c>MegaLabel</c> asserts it has an explicit per-instance "theme font
/// override" set before its own <c>_Ready()</c> completes (<c>MegaLabelHelper
/// .AssertThemeFontOverride</c>, a deliberate guard against a documented Godot engine bug around
/// quit-time auto-sizing) — a scene-authored <c>MegaLabel</c> gets this for free from the editor, but a
/// bare <c>new MegaLabel()</c> never does, so both of this screen's own hand-built labels threw
/// <c>InvalidOperationException</c> the moment they entered the tree (confirmed live: two such
/// exceptions logged, harmlessly swallowed by Godot's own C#-exception-to-error boundary, which is why
/// the rest of the screen — deck/relic widgets, unaffected — still rendered while the header text
/// silently never did). Confirmed by grep that the native game never constructs a bare <c>MegaLabel</c>
/// in code anywhere — every one is scene-authored. Fixed the same way as every other widget on this
/// screen: reparent an existing, already-theme-configured header label instead
/// (<c>%GameModeLabel</c>, a <c>MegaRichTextLabel</c> — that type has no such per-instance assertion,
/// and reparenting keeps whatever font override the original scene already gave it) — replacing both
/// bare labels with this one reused one (normal/empty/error text are mutually exclusive here, so one
/// label suffices).
///
/// Root cause of the dead back button: this screen's own header row (<c>root</c>, anchored
/// full-rect starting at the very top-left) was added as a sibling positioned to overlap the
/// reparented <c>BackButton</c>'s own native top-left corner position — being added to the tree later,
/// it drew on top and won the hit-test, so clicks aimed at the back button actually landed on this
/// screen's own controls instead. Originally fixed by offsetting <c>root</c> down by
/// <c>_reparentedBackButton.Position.Y + _reparentedBackButton.Size.Y</c>.
///
/// <b>Revision 2026-09-05, fifth pass</b> — the user reported the back button still not working and,
/// newly, the deck/relic cards no longer showing at all. Two things complicate diagnosing this:
/// (1) godot.log evidence for the exact "fourth pass" bug signature (the bare-<c>MegaLabel</c>
/// exceptions) turned up in an archived log whose session most likely predates the fourth-pass fix even
/// being deployed (a running game process keeps whatever assembly it already loaded — a mod DLL
/// overwritten on disk mid-session has no effect until the game itself is fully restarted, not just the
/// menu/run) — so it is not certain the fourth pass was ever actually exercised live yet, as opposed to
/// this being a stale report. (2) Independent of that, the fourth pass's back-button-overlap fix itself
/// had a real, separate latent bug worth fixing regardless: it read <c>_reparentedBackButton.Size</c>
/// (and <c>.Position</c>) to compute <c>root</c>'s offset — but, like every other node this screen
/// reparents from the still-never-<c>_Ready()</c>'d throwaway source instance, there is no hard
/// guarantee a Control's <c>Size</c> reflects its real laid-out value before it has gone through its own
/// layout pass (this exact class of bug already caused two earlier passes here — the <c>IsLeft</c>
/// crash, the <c>MegaLabel</c> assertion). A degenerate offset here would push <c>root</c> (and the
/// deck/relic widgets inside it) far down the screen — invisible, not crashed, matching "cards no longer
/// show up" exactly. Replaced with a fixed, hand-tuned clearance constant, removing the dependency on
/// that node's own <c>Size</c> entirely.
///
/// <b>Revision 2026-09-05, seventh pass</b> — the sixth-pass fix (fixed clearance instead of a
/// Size-derived offset) brought the deck/relic cards back, but the user reported the back button still
/// doesn't work. Per the user's explicit direction, this pass is research-only, not another guess: read
/// <c>NBestiary.cs</c>/<c>NStatsScreen.cs</c>/<c>NRunHistory.cs</c> in full (every other native
/// <c>NSubmenu</c> this project has reason to compare against) and <c>NBackButton.cs</c> itself.
/// Confirmed: none of those three screens override <c>ConnectSignals()</c> — all rely purely on the
/// inherited <c>NSubmenu.ConnectSignals()</c>, exactly like this class. But <c>NBackButton</c> itself
/// turned out to be far more involved than a simple <c>Visible</c>-gated button: it computes its own
/// show/hidden *positions* in its own <c>_Ready()</c> from <c>GetWindow().ContentScaleSize</c> and its
/// own anchor offsets (<c>_posOffset</c>), starts forced to its hidden position
/// (<c>_Ready()</c> ends by calling <c>OnDisable()</c> directly), and only ever animates to its shown
/// position via a 0.35s tween inside <c>OnEnable()</c> — entirely independent of the screen's own
/// <c>Visible</c> property beyond triggering that one <c>Enable()</c> call. Nothing found in this reading
/// definitively explains the reported failure (the mechanism appears self-consistent whether the button
/// is reparented or not, since it positions itself from the window, not its immediate parent) — so
/// rather than risk another blind, possibly-regressing guess, this pass adds two diagnostic-only log
/// lines (a click-detection log on the BackButton's own <c>Released</c> signal, and a settled-state dump
/// ~0.6s after <c>OnSubmenuOpened()</c>, past the show-tween's own 0.35s) to get real data on the next
/// test: is the click even reaching the button (a hit-test/position problem) or reaching it and then
/// failing to navigate (a stack/Pop problem)? No behavior change in this pass.
///
/// <b>Revision 2026-09-05, eighth pass</b> — the seventh pass's diagnostic answered its own question:
/// <c>GhostLedgerScreen: BackButton settled state — IsEnabled=True, Visible=True, MouseFilter=Stop,
/// GlobalPosition=(-40, 726), ...</c>. The button is fully enabled, visible, and accepting clicks — it
/// is simply sitting off the left edge of the screen (negative X), so there is nothing there for the
/// user to click. Not a hit-test or stack/Pop problem at all. Root cause, following on from the seventh
/// pass's reading of <c>NBackButton.OnWindowChange()</c>: that method assigns <c>base.Position</c> (not
/// <c>GlobalPosition</c>) from a formula computed purely from the window size and the button's own
/// <c>OffsetLeft</c>/<c>OffsetBottom</c> — but its own show/hide *tweens* animate
/// <c>"global_position"</c> toward that same value, which only produces the intended on-screen result
/// if the button's parent screen sits at <c>GlobalPosition == (0, 0)</c> exactly, matching whatever the
/// original NRunHistory scene's own root offset actually is (not independently verifiable without the
/// .tscn itself). This screen's own root, a plain code-constructed <c>Control</c>, evidently does not
/// match that assumption closely enough. Rather than guess at reproducing an unknown, potentially
/// fragile scene-file offset, this fixes the one observable symptom directly and robustly: after the
/// show tween settles, if the button's real <c>GlobalPosition.X</c> is negative (off-screen), mirror it
/// back on-screen by negating it — using the actual observed magnitude as the correction (real
/// button-margin data, not an arbitrary constant), rather than a fixed guessed value.
///
/// <b>Revision 2026-09-05, ninth pass</b> — the eighth pass's position fix made the button visible
/// (screenshot confirmed: sitting correctly bottom-left, looking like a normal back button), but it
/// still didn't respond to clicks — and the diagnostic <c>Released</c>-signal log
/// (<see cref="ConnectSignals"/>) never fired even once, meaning the click was never reaching the
/// button at all, not failing after reaching it. Root cause: the eighth pass revealed the back button
/// actually lives at the *bottom*-left of the screen (<c>GlobalPosition.Y = 726</c>), not top-left as
/// every earlier pass assumed — meaning the "fourth/fifth pass" top-clearance fix was solving a
/// collision that was never the real one. This screen's own <c>root</c> container
/// (<c>AnchorBottom = 1</c>, spanning from just below the header all the way to the bottom of the
/// screen) fully overlaps the button's real, bottom-left position — and <c>root</c> was added to the
/// tree *after* the back button, so as a later sibling it draws on top and wins the click hit-test
/// anywhere the two overlap, exactly like the original top-left overlap bug two passes ago, just at the
/// opposite corner. Fixed by reordering: the back button is now added *after* <c>root</c>, so it is
/// always the topmost sibling regardless of where either one's rect actually lands.
///
/// <b>Confirmed working live 2026-09-05</b> — the user confirmed the back button responds to clicks.
/// Remaining ask, same session: its left edge sat with a small gap from the window's true left edge
/// (mirrored to <c>+</c>the same margin the off-screen bug was found at, per the eighth pass) rather
/// than flush against it. Changed the post-tween correction to pin <c>GlobalPosition.X</c> to exactly
/// <c>0</c> instead of mirroring the observed negative value. The diagnostic-only click-detection log
/// added in the seventh pass is removed now that it answered its question.
///
/// <b>Test data</b>: per the user's request, <c>ghost_a1.json</c>-<c>ghost_a4.json</c> were added to the
/// solo ladder's own save directory (<c>ghostduel_legacy_ascension/</c>, alongside the one real,
/// already-existing <c>ghost_a0.json</c> from an actual completed run — left untouched, never
/// overwritten) with Silent/Defect/Necrobinder/Regent respectively, and <c>ghost_a0.json</c>-
/// <c>ghost_a4.json</c> to the multiplayer ladder's own directory
/// (<c>ghostduel_legacy_ascension_multiplayer/</c>, otherwise empty) covering all five characters
/// including Ironclad. Each is a minimal but genuinely valid <c>SerializablePlayer</c> JSON (schema
/// confirmed by structuring it exactly like the real <c>ghost_a0.json</c>): that character's own
/// starting relic (<c>BurningBlood</c>/<c>RingOfTheSnake</c>/<c>CrackedCore</c>/<c>BoundPhylactery</c>/
/// <c>DivineRight</c>, confirmed via each character's own <c>StartingRelics</c> override) plus 5 of
/// their own Strike + 5 Defend cards (confirmed valid IDs: every character has its own dedicated
/// <c>Strike&lt;Character&gt;</c>/<c>Defend&lt;Character&gt;</c> class, e.g. <c>StrikeRegent</c> ->
/// <c>CARD.STRIKE_REGENT</c>, the same class-name-to-id convention already confirmed live for
/// <c>StrikeIronclad</c>/<c>CARD.STRIKE_IRONCLAD</c>) — not attempting a full random 20-card deck, since
/// this data only needs to exercise the ledger's own display, not be a realistic finished run. This
/// existing prev/next navigation (spanning both ladders as one flat sequence) is the "ascension
/// selector"-like feature requested — it already loads whichever level/ladder the index points to; what
/// was missing was simply something to browse, now provided by this data.
///
/// <b>Not yet confirmed live</b> — needs a real Compendium-opened test, after a full game restart (not
/// just returning to the main menu), before being considered done.
/// </summary>
internal sealed class GhostLedgerScreen : NSubmenu
{
    private const ulong PreviewNetId = ulong.MaxValue - 2048;

    private const float TopClearance = 100f;

    private readonly NBackButton _reparentedBackButton;
    private readonly NRunHistoryArrowButton _prevButton;
    private readonly NRunHistoryArrowButton _nextButton;
    private readonly NDeckHistory _deckHistory;
    private readonly NRelicHistory _relicHistory;
    private readonly MegaRichTextLabel _headerLabel;

    private int _index;

    protected override Control? InitialFocusedControl => null;

    public GhostLedgerScreen()
    {
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);

        // Source a working BackButton/LeftArrow/RightArrow/%GameModeLabel/%DeckHistory/%RelicHistory
        // set from a throwaway NRunHistory instance — see class doc comment for why this is safe (an
        // independent instance, not the game's shared one) and necessary (NSubmenu.ConnectSignals()
        // requires a "BackButton" child; the arrows and header label are this page's own themed
        // controls, reused rather than hand-built, per the "fourth pass" doc comment above).
        NRunHistory? source = NRunHistory.Create();
        if (source is null)
        {
            throw new InvalidOperationException("GhostLedgerScreen: NRunHistory.Create() returned null (TestMode?) — cannot source deck/relic widgets.");
        }
        _reparentedBackButton = source.GetNode<NBackButton>("BackButton");
        _prevButton = source.GetNode<NRunHistoryArrowButton>("LeftArrow");
        _nextButton = source.GetNode<NRunHistoryArrowButton>("RightArrow");
        _headerLabel = source.GetNode<MegaRichTextLabel>("%GameModeLabel");
        _deckHistory = source.GetNode<NDeckHistory>("%DeckHistory");
        _relicHistory = source.GetNode<NRelicHistory>("%RelicHistory");
        foreach (Node reparented in new Node[] { _reparentedBackButton, _prevButton, _nextButton, _headerLabel, _deckHistory, _relicHistory })
        {
            reparented.GetParent()?.RemoveChild(reparented);
        }
        source.QueueFree();

        _reparentedBackButton.Name = "BackButton";

        VBoxContainer root = new()
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            // See class doc comment ("ninth pass"): TopClearance no longer matters for overlap — see
            // that doc entry for why the real back-button position turned out to be bottom-left, not
            // top-left — but is kept as a reasonable header margin regardless.
            // Not derived from _reparentedBackButton's own Size — that field is not reliably valid
            // before the node's own layout pass runs (see "fifth pass" for why that bit us already).
            Position = new Vector2(0f, TopClearance),
        };
        AddChild(root);
        // Added after (not before) root: see class doc comment ("ninth pass") — root's own
        // AnchorBottom = 1 rect extends to the bottom of the screen, overlapping wherever the back
        // button's real position turns out to be, and a later sibling wins both rendering order and
        // click hit-testing when two Controls' rects overlap.
        AddChild(_reparentedBackButton);

        HBoxContainer header = new();
        root.AddChild(header);
        _prevButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Navigate(-1)));
        header.AddChild(_prevButton);
        header.AddChild(_headerLabel);
        _nextButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Navigate(1)));
        header.AddChild(_nextButton);

        root.AddChild(_deckHistory);
        root.AddChild(_relicHistory);
    }

    public override void _Ready()
    {
        ConnectSignals();
    }

    protected override void ConnectSignals()
    {
        base.ConnectSignals();
        // Safe only here — see class doc comment ("third pass"): _prevButton's own _Ready() (and thus
        // its _icon field) is guaranteed to have already run by the time our own _Ready() (which calls
        // this) fires, since Godot readies the whole reparented subtree bottom-up before this node.
        _prevButton.IsLeft = true;
    }

    public override void OnSubmenuOpened()
    {
        base.OnSubmenuOpened();
        Refresh();
        // See class doc comment ("eighth"/"tenth" passes): the reparented BackButton settles at an
        // off-screen GlobalPosition.X after its own 0.35s show tween (confirmed live) — corrected once
        // that tween would have finished. Per the user's explicit request, its left edge is pinned
        // flush to the window's own left edge (X = 0) rather than mirrored to the button's own margin.
        GetTree().CreateTimer(0.6).Timeout += () =>
        {
            Vector2 pos = _reparentedBackButton.GlobalPosition;
            if (pos.X != 0f)
            {
                _reparentedBackButton.GlobalPosition = new Vector2(0f, pos.Y);
            }
        };
    }

    private void Navigate(int delta) => SetIndex(_index + delta);

    private void SetIndex(int index)
    {
        int max = GhostSnapshotStore.GetMaxSelectableLevel() + MultiplayerGhostSnapshotStore.GetMaxSelectableLevel();
        _index = Math.Clamp(index, 0, Math.Max(0, max - 1));
        Refresh();
    }

    /// <summary>One flat sequence across both ladders — see class doc comment ("second pass") for why
    /// this replaced a separate toggle button next to the arrows.</summary>
    private (bool multiplayer, int level) ResolveIndex(int soloMax) =>
        _index < soloMax ? (false, _index) : (true, _index - soloMax);

    private void Refresh()
    {
        try
        {
            int soloMax = GhostSnapshotStore.GetMaxSelectableLevel();
            int multiMax = MultiplayerGhostSnapshotStore.GetMaxSelectableLevel();
            int total = soloMax + multiMax;
            if (total <= 0)
            {
                _headerLabel.Text = "No Ghosts saved on this ladder yet.";
                _deckHistory.Visible = false;
                _relicHistory.Visible = false;
                _prevButton.Disable();
                _nextButton.Disable();
                return;
            }

            _deckHistory.Visible = true;
            _relicHistory.Visible = true;
            _index = Math.Clamp(_index, 0, total - 1);
            if (_index <= 0) _prevButton.Disable(); else _prevButton.Enable();
            if (_index >= total - 1) _nextButton.Disable(); else _nextButton.Enable();

            (bool multiplayerLadder, int level) = ResolveIndex(soloMax);
            GhostSnapshotFileStore.LoadResult result = multiplayerLadder
                ? MultiplayerGhostSnapshotStore.Load(level)
                : GhostSnapshotStore.Load(level);
            string ladderName = multiplayerLadder ? "MULTIPLAYER" : "SOLO";
            if (!result.Success || result.Player is not { } snapshot)
            {
                _headerLabel.Text = $"{ladderName} A{level}: unavailable ({result.Error})";
                GhostLog.Warn($"GhostLedgerScreen: failed to load A{level} ({ladderName}): {result.Error}");
                return;
            }

            string characterName = snapshot.CharacterId is { } characterId
                ? ModelDb.GetByIdOrNull<CharacterModel>(characterId)?.Id.Entry ?? characterId.ToString()
                : "?";
            _headerLabel.Text = $"{ladderName} A{level} — {characterName}";

            Player previewPlayer = Player.CreateForNewRun(
                ModelDb.GetByIdOrNull<CharacterModel>(snapshot.CharacterId!) ?? throw new InvalidOperationException("GhostLedgerScreen: snapshot's CharacterId did not resolve to a CharacterModel."),
                UnlockState.all,
                PreviewNetId);
            _deckHistory.LoadDeck(previewPlayer, snapshot.Deck);
            _relicHistory.LoadRelics(previewPlayer, snapshot.Relics);
        }
        catch (Exception ex)
        {
            GhostLog.Warn($"GhostLedgerScreen: refresh failed, ignoring ({ex.GetType().Name}: {ex.Message}).");
        }
    }
}
