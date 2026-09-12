using System;
using System.Collections.Generic;
using GhostDuel.Diagnostics;
using GhostDuel.Progression;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
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
/// assignment into a <c>ConnectSignals()</c> override (fired from <c>_Ready()</c>, guaranteed to run
/// only after the whole reparented subtree — <c>_prevButton</c> included — has already had its own
/// <c>_Ready()</c> execute, per Godot's bottom-up child-then-parent order) — that override, and
/// <c>_prevButton</c> itself, no longer exist as of the "ascension selector" revision below.
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
/// still didn't respond to clicks — and the diagnostic <c>Released</c>-signal log (in the now-removed
/// <c>ConnectSignals()</c> override) never fired even once, meaning the click was never reaching the
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
/// this data only needs to exercise the ledger's own display, not be a realistic finished run.
///
/// <b>"Ascension selector" revision, 2026-09-07</b>, per two user reports from the same screenshot: (1)
/// "the compendium screen does not allow for changing which ascension ghost is being viewed," with a
/// request for "an ascension selector similar to what happens before a run starts," and (2) the
/// screenshot itself showed neither the header text nor the prev/next arrows rendering at all — only
/// the deck/relic panels and the back button were visible. Root cause of (2) not independently
/// re-confirmed live (no fresh log from that exact session), but the prime suspect by inspection: the
/// header label and both arrows shared one <c>HBoxContainer</c>, and <c>NRunHistoryArrowButton</c> is
/// exactly the class of native, pre-<c>_Ready()</c>, reparented-from-a-throwaway-source widget that has
/// already cost this file nine separate debugging passes (sizing/positioning not valid until its own
/// layout pass runs — the same failure mode as the <c>IsLeft</c> crash and the back-button positioning
/// saga above). Rather than risk a tenth pass chasing the same bug class, or gamble on reusing the real
/// native <c>NAscensionPanel</c> (its packed-scene resource path is not confirmed from the decompiled
/// source alone, and it is normally only ever instantiated as a character-select-screen child, not
/// standalone), this replaced the prev/next arrow pair entirely with a flat row of plain,
/// code-constructed <c>Button</c>s, one per saved (ladder, level) — the same proven-safe construction
/// technique <c>GhostDeckViewerPresenter</c> already uses successfully elsewhere in this mod.
///
/// <b>Mode-split/background revision, 2026-09-12</b>, per the user's screenshot of that flat selector
/// plus a screenshot of the real ascension-picker's per-level flame/number display, asking for: a
/// dedicated Single Player / Multiplayer mode toggle instead of one mixed "A0 A1 ... MP A0" row, a
/// per-level selector visually closer to that real ascension picker (dropping the flavor-text tooltip,
/// keeping the "flame with a number" read), and a greyed-out/ghostly background of the currently-viewed
/// character. Three concrete findings from a dedicated research pass shaped what got built:
///
/// (1) The real <c>NAscensionPanel</c> flame icon's own texture resource could not be found anywhere —
/// its node path (<c>%AscensionIcon</c> under <c>NAscensionPanel.cs:238</c>) and shader-tint mechanism
/// (<c>h</c>/<c>v</c> params on a duplicated HSV shader, already reflected into by this mod's own
/// <see cref="LegacyAscensionUiAccess"/>) are confirmed, but the actual image is authored in that
/// panel's own <c>.tscn</c> scene file, which the decompiled source tree — C# only, no scenes — simply
/// does not contain. Reparenting the whole native panel standalone remains the same gamble the previous
/// revision already declined (untested outside its normal character-select-child context, and this
/// file's own nine-pass history is a standing warning about exactly that class of bug). So the per-level
/// badges below are a deliberate look-alike, not a reuse of the real icon: a rounded, colored button
/// tinted with the same purple hue (<c>0.78</c>) <c>LegacyAscensionEntry</c>/
/// <c>MultiplayerLegacyAscensionEntry</c> already use for this mod's own ascension theming, showing
/// "A{level}" and a brighter/bordered variant when selected — reads as an ascension-level picker without
/// depending on an asset this mod cannot actually locate.
///
/// (2) The desaturated/"ghostly" look already exists twice in this mod, both confirmed working live:
/// <c>EnginePatches.cs</c>'s <c>ApplyGrayscale</c> (the in-combat Ghost creature itself) and
/// <c>GhostDeckViewerPresenter.CreateDesaturatedMaterial</c> (a plain <c>Control</c>, not a Spine
/// sprite) — both duplicate the engine's own shared <c>res://materials/vfx/hsv.tres</c> material and
/// zero its <c>"s"</c> (saturation) shader parameter. <see cref="CreateDesaturatedMaterial"/> below
/// reproduces that exact, already-proven technique a third time, on a background <c>TextureRect</c>.
///
/// (3) <c>CharacterModel.CharacterSelectIcon</c> (decompiled <c>CharacterModel.cs:150-152</c>) is a
/// public <c>CompressedTexture2D</c> — a full character-art render keyed by character id
/// (<c>char_select_&lt;id&gt;.png</c>), the same kind of already-resolved <c>CharacterModel</c> this
/// screen's own <see cref="Refresh"/> already looks up for the preview deck/relics — directly usable as
/// a <c>TextureRect.Texture</c> with no new resource-loading code needed.
///
/// The background sits behind everything (added as this screen's very first child, before <c>root</c>
/// and the back button — preserving the "ninth pass" lesson that a later sibling wins both rendering
/// order and click hit-testing) with <c>MouseFilter = Ignore</c> so it can never intercept a click, and
/// a low-alpha, slightly cool <c>Modulate</c> tint on top of the desaturation so foreground text/panels
/// stay legible. Not yet confirmed live.
///
/// <b>Live feedback, same day</b>: (1) the background read as "way too zoomed in" — root cause,
/// <c>StretchMode = KeepAspectCovered</c> scales the character art to *cover* (crop to fill) this
/// screen's own full-window rect, and a portrait-oriented character render covering a wide window crops
/// down to a tight, unrecognizable slice. Changed to <c>KeepAspectCentered</c>, which fits the whole
/// image within the available space (letterboxed, never cropped) instead. (2) "the background is a semi
/// transparent overlay from another image" — the low-alpha <c>TextureRect</c> was blending with
/// whatever this screen's own native parent already renders behind it (this page has no opaque backing
/// of its own), reading as two images double-exposed together rather than one clean background. Fixed by
/// adding <see cref="_backgroundBacking"/>, a solid dark <c>ColorRect</c> behind the character texture
/// (and, like it, before <c>root</c>/the back button in child order) so the character art blends only
/// with a flat, known color, never with whatever the native screen underneath happens to be. (3) the
/// per-level button row was replaced with a single flame-badge-and-arrows look-alike selector, per
/// explicit request ("the arrow and flame selector style instead of button") — since replaced entirely,
/// see the next revision.
///
/// <b>Big background image / real ascension panel revision, 2026-09-13</b>: two more requests from the
/// same live screenshot review. (1) <c>CharacterModel.CharacterSelectIcon</c> (used above) turned out to
/// be the small roster-icon thumbnail, not the big scene-filling character-select background art the
/// user actually meant — confirmed by a dedicated research pass that the real big art is
/// <c>CharacterModel.CharacterSelectBg</c>, a scene path, consumed by
/// <c>NCharacterSelectScreen.SelectCharacter</c> (decompiled <c>NCharacterSelectScreen.cs:817-826</c>)
/// via exactly <c>PreloadManager.Cache.GetScene(path).Instantiate&lt;Control&gt;(...)</c> with no
/// further initialization — <see cref="UpdateBackground"/> reproduces that sequence directly. Also made
/// fully opaque (previously a low-alpha tint) per "opaque and greyscale ... ghost like": the desaturation
/// shader alone now carries the whole "ghostly" read, matching how the in-combat Ghost creature itself is
/// grayscale, not translucent.
///
/// (2) The look-alike badge/arrow selector was replaced with the real <see cref="_ascensionPanel"/>
/// (<c>NAscensionPanel</c>) itself, per explicit request ("an exact copy of the ui from the run start
/// menu"). A second dedicated research pass, prompted by this file's own standing caution against
/// reusing native widgets, confirmed: the arrows' click handlers (<c>DecrementAscension</c>/
/// <c>IncrementAscension</c>) mutate only the panel's own local <c>Ascension</c> property and fire a
/// public <c>AscensionLevelChanged</c> C# event — no direct coupling to any live <c>StartRunLobby</c>/
/// <c>RunState</c>, so redirecting them to this screen's own <see cref="_level"/> (via
/// <see cref="OnAscensionPanelLevelChanged"/>) is safe. The panel has no standalone packed scene of its
/// own, though — every one of its 4 real host screens only ever pulls it out of their own pre-baked
/// scene tree by unique name (<c>%AscensionPanel</c>). Sourced the same way this screen already sources
/// <c>BackButton</c>/<c>%DeckHistory</c>/<c>%RelicHistory</c> (a throwaway host screen, reparent the one
/// node wanted, discard the rest) — a third research pass identified <c>NMultiplayerLoadGameScreen</c> as
/// the lightest-weight of the 4 candidates (no character-button generation, no controller-manager
/// signal hookups, no exported-scene-field dependencies unlike <c>NCharacterSelectScreen</c>). See the
/// constructor's own doc comment for why <c>Initialize(...)</c> is deliberately never called.
/// </summary>
internal sealed class GhostLedgerScreen : NSubmenu
{
    private const ulong PreviewNetId = ulong.MaxValue - 2048;

    private const float TopClearance = 100f;

    /// <summary>Same purple used by both Ghost-ascension entry points' own icon tint
    /// (<c>LegacyAscensionEntry.PurpleHue</c>/<c>MultiplayerLegacyAscensionEntry.PurpleHue</c>) —
    /// applied to <see cref="_ascensionPanel"/>'s own icon so it reads as visually related to those,
    /// not an unrelated color scheme.</summary>
    private const float AscensionHue = 0.78f;

    /// <summary>Confirmed live (2026-09-13) by directly measuring the user's own reference screenshot
    /// against this screen's: the reparented panel rendered at roughly 70% of the reference's own size
    /// (icon width ~3.0% of screen width here vs. ~4.2% there). <see cref="_ascensionPanel"/> is the
    /// same native scene either way — the size difference is presumably an ancestor <c>Scale</c> present
    /// in whatever screen the reference was captured from (the real character-select screen or similar)
    /// that this screen's own unscaled hierarchy doesn't reproduce. Corrected empirically from the
    /// measured ratio (1 / 0.7 ≈ 1.4) rather than guessed.</summary>
    private const float AscensionPanelScale = 1.4f;

    /// <summary>Per explicit request ("make the flame 25% bigger") after a close-up screenshot showed
    /// the level number visibly overlapping/exceeding the flame glyph's own edges at the panel's overall
    /// scale — the flame icon specifically (not the whole panel/arrows) needed to grow relative to the
    /// number sitting inside it.</summary>
    private const float AscensionIconExtraScale = 1.25f;

    private static readonly StringName SaturationShaderParam = new("s");

    private readonly NBackButton _reparentedBackButton;
    private readonly NDeckHistory _deckHistory;
    private readonly NRelicHistory _relicHistory;
    private readonly MegaRichTextLabel _headerLabel;
    private readonly ColorRect _backgroundBacking;
    private readonly Control _backgroundContainer = new()
    {
        AnchorRight = 1f,
        AnchorBottom = 1f,
        MouseFilter = MouseFilterEnum.Ignore,
    };
    private readonly HBoxContainer _modeRow = new();
    private readonly Button _soloModeButton;
    private readonly Button _multiplayerModeButton;
    private readonly CenterContainer _ascensionOverlay = new()
    {
        // Anchors measured directly off the user's own reference screenshot (the real pre-run ascension
        // picker), corrected a second time after a live side-by-side comparison. The vertical read
        // (~73%-75% down) was already right. The horizontal read was wrong: the reference's *icon*
        // itself sits at roughly x=36% of screen width — well left of true center — because the full
        // native row is icon+arrows *plus* a wide description text box extending further right, and the
        // icon sits at the left end of that whole assembly, not its middle. This screen hides that
        // description box entirely (per an earlier explicit request to drop the flavor text), so
        // centering the remaining, much narrower icon+arrows cluster on the *screen's* own midpoint (as
        // the first attempt did) put the icon at ~50% instead of matching where it actually sits in the
        // reference. Recentered this overlay's own midpoint at ~36% instead so the icon lands in the
        // same place, description box or not. Confirmed live (2026-09-13) that a full-width band here
        // silently broke the back button — see this file's own "ninth pass" lesson, reintroduced.
        // MouseFilter.Pass only bubbles an unhandled event up to THIS control's own parent, not sideways
        // to an earlier sibling drawn underneath — it does not make clicks "fall through" to the back
        // button the way that name suggests. Staying clear of that corner horizontally is
        // belt-and-suspenders on top of also reordering this node before the back button in child order
        // (see the constructor).
        AnchorLeft = 0.16f,
        AnchorRight = 0.56f,
        AnchorTop = 0.68f,
        AnchorBottom = 0.8f,
        MouseFilter = MouseFilterEnum.Pass,
    };
    private readonly NAscensionPanel _ascensionPanel;

    private bool _multiplayerMode;
    private int _level;
    private string? _backgroundCharacterId;

    protected override Control? InitialFocusedControl => null;

    public GhostLedgerScreen()
    {
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);

        // Source a working BackButton/%GameModeLabel/%DeckHistory/%RelicHistory set from a throwaway
        // NRunHistory instance — see class doc comment for why this is safe (an independent instance,
        // not the game's shared one) and necessary (NSubmenu.ConnectSignals() requires a "BackButton"
        // child; the header label is this page's own themed control, reused rather than hand-built, per
        // the "fourth pass" doc comment above). The prev/next NRunHistoryArrowButton pair this used to
        // also source is long gone — see the "ascension selector"/"mode-split" revisions above for why.
        NRunHistory? source = NRunHistory.Create();
        if (source is null)
        {
            throw new InvalidOperationException("GhostLedgerScreen: NRunHistory.Create() returned null (TestMode?) — cannot source deck/relic widgets.");
        }
        _reparentedBackButton = source.GetNode<NBackButton>("BackButton");
        _headerLabel = source.GetNode<MegaRichTextLabel>("%GameModeLabel");
        _deckHistory = source.GetNode<NDeckHistory>("%DeckHistory");
        _relicHistory = source.GetNode<NRelicHistory>("%RelicHistory");
        foreach (Node reparented in new Node[] { _reparentedBackButton, _headerLabel, _deckHistory, _relicHistory })
        {
            reparented.GetParent()?.RemoveChild(reparented);
        }
        source.QueueFree();

        _reparentedBackButton.Name = "BackButton";

        // See class doc comment ("mode-split/background" revision, and the "live feedback" entry right
        // after it) — plain Control nodes, not reparented native ones, so none of this file's usual "not
        // _Ready() yet" hazards apply. Added as the very first children so every later sibling (root,
        // the back button) draws on top of them and wins any click hit-test, per the "ninth pass"
        // lesson. The solid backing goes first so the character art above it only ever blends with this
        // known flat color, never with whatever this screen's native parent renders underneath.
        _backgroundBacking = new ColorRect
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            Color = new Color(0.05f, 0.05f, 0.07f, 1f),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        AddChild(_backgroundBacking);
        AddChild(_backgroundContainer);

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
        // The real ascension panel lives in its own independently-anchored overlay, not in root's
        // document flow — see class doc comment ("panel repositioning" revision) for why: squeezed into
        // a thin row alongside the header/mode buttons, the panel (sized for a full character-select
        // screen) rendered cramped and partly clipped. Anchored to a fixed band of the screen rather
        // than reordered within root, so its position never depends on how much deck/relic content
        // happens to be above it. Added here — BEFORE the back button — deliberately: this file's
        // "ninth pass" lesson is that a later sibling wins hit-testing on any rect overlap, and this
        // overlay was confirmed live to break the back button when added after it (see this field's own
        // doc comment on MouseFilter.Pass not being the fix that name suggests).
        AddChild(_ascensionOverlay);
        // Added after root (and after the ascension overlay): see class doc comment ("ninth pass") —
        // root's own AnchorBottom = 1 rect extends to the bottom of the screen, overlapping wherever the
        // back button's real position turns out to be, and a later sibling wins both rendering order and
        // click hit-testing when two Controls' rects overlap. Must stay the LAST child added.
        AddChild(_reparentedBackButton);

        // The header now gets its own full-width row rather than sharing an HBoxContainer with the old
        // prev/next arrows — see class doc comment ("ascension selector" revision) for why that sharing
        // is suspected to have clipped the header text.
        root.AddChild(_headerLabel);
        root.AddChild(_modeRow);

        root.AddChild(_deckHistory);
        root.AddChild(_relicHistory);

        // Styled to match this mod's own established purple ascension theme (same hue the level icon
        // is tinted with) rather than left as plain, unthemed default Godot buttons — per explicit
        // request ("better formatted"). A small gap between the two and real padding inside each reads
        // as an intentional toggle pair instead of two default widgets glued together.
        _modeRow.AddThemeConstantOverride("separation", 10);
        _soloModeButton = new Button { Text = "SINGLE PLAYER", ToggleMode = true, FocusMode = FocusModeEnum.None };
        _multiplayerModeButton = new Button { Text = "MULTIPLAYER", ToggleMode = true, FocusMode = FocusModeEnum.None };
        foreach (Button modeButton in new[] { _soloModeButton, _multiplayerModeButton })
        {
            modeButton.AddThemeStyleboxOverride("normal", CreateModeButtonStyleBox(selected: false));
            modeButton.AddThemeStyleboxOverride("hover", CreateModeButtonStyleBox(selected: false));
            modeButton.AddThemeStyleboxOverride("pressed", CreateModeButtonStyleBox(selected: true));
        }
        _soloModeButton.Connect(BaseButton.SignalName.Pressed, Callable.From(() => SetMode(multiplayer: false)));
        _multiplayerModeButton.Connect(BaseButton.SignalName.Pressed, Callable.From(() => SetMode(multiplayer: true)));
        _modeRow.AddChild(_soloModeButton);
        _modeRow.AddChild(_multiplayerModeButton);

        // The real native ascension picker, per explicit request ("an exact copy of the ui from the run
        // start menu") — see class doc comment ("real ascension panel" revision) for the research this
        // is grounded in. NAscensionPanel has no standalone scene of its own; sourced the same way
        // BackButton/DeckHistory/RelicHistory already are above — a throwaway host screen
        // (NMultiplayerLoadGameScreen, confirmed the lightest-weight of the four screens that carry
        // this panel: no character-button generation, no controller-manager hookups, no exported-field
        // dependencies), reparent %AscensionPanel out, discard the rest.
        NMultiplayerLoadGameScreen? ascensionSource = NMultiplayerLoadGameScreen.Create();
        if (ascensionSource is null)
        {
            throw new InvalidOperationException("GhostLedgerScreen: NMultiplayerLoadGameScreen.Create() returned null (TestMode?) — cannot source the ascension panel.");
        }
        _ascensionPanel = ascensionSource.GetNode<NAscensionPanel>("%AscensionPanel");
        _ascensionPanel.GetParent()?.RemoveChild(_ascensionPanel);
        ascensionSource.QueueFree();

        // Never calling Initialize(...) — confirmed via decompiled source that _leftArrow/_rightArrow's
        // click wiring (-> SetAscensionLevel -> AscensionLevelChanged) happens in the panel's own
        // _Ready(), independently of Initialize; Initialize itself only sets an initial level/max,
        // tints the icon, and (for Host/Singleplayer only) pushes two global hotkey bindings this
        // read-only browsing screen has no use for. SetMaxAscension/SetAscensionLevel are instead kept
        // in sync with this screen's own _level every Refresh (below) — safe to call there specifically
        // because Refresh only ever runs after OnSubmenuOpened, by which point this whole reparented
        // subtree has already entered the live tree and had its own _Ready() run (same guarantee this
        // file's other reparented widgets already rely on).
        _ascensionPanel.AscensionLevelChanged += OnAscensionPanelLevelChanged;

        _ascensionOverlay.AddChild(_ascensionPanel);
    }

    /// <summary>A rounded, purple-tinted toggle-button style for the Single Player/Multiplayer row —
    /// per explicit request ("better formatted"), replacing the plain unstyled default Godot
    /// <c>Button</c> look with something visually consistent with this mod's own established ascension
    /// theming (<see cref="AscensionHue"/>, the same hue used elsewhere for both Ghost-ascension entry
    /// points' own icon tint).</summary>
    private static StyleBoxFlat CreateModeButtonStyleBox(bool selected)
    {
        return new StyleBoxFlat
        {
            BgColor = Color.FromHsv(AscensionHue, selected ? 0.7f : 0.35f, selected ? 0.85f : 0.45f),
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            BorderWidthTop = selected ? 2 : 0,
            BorderWidthBottom = selected ? 2 : 0,
            BorderWidthLeft = selected ? 2 : 0,
            BorderWidthRight = selected ? 2 : 0,
            BorderColor = new Color(1f, 0.9f, 0.4f),
        };
    }

    /// <summary>Hides the real panel's own flavor-text description, resizes its separate card backdrop
    /// to hug just the icon/arrows, applies this mod's established purple ascension tint (<see
    /// cref="LegacyAscensionUiAccess"/>, already used identically by <c>LegacyAscensionEntry</c>/
    /// <c>MultiplayerLegacyAscensionEntry</c>), and scales the whole panel up to match the reference
    /// screenshot's own measured size — all deferred out of the constructor into
    /// <see cref="OnSubmenuOpened"/> since every one of these touches nodes/materials the panel's own
    /// <c>_Ready()</c> populates, which (per this file's own repeated "not _Ready() yet" lesson) has not
    /// run at construction time.
    ///
    /// <b>"Background" NinePatchRect, 2026-09-13</b>: a live diagnostic dump of the panel's actual node
    /// tree (this screen previously couldn't identify it — no <c>.tscn</c>, and it has no C# field
    /// reference in <c>NAscensionPanel.cs</c> to search decompiled source for) confirmed the leftover
    /// semi-transparent rounded-rectangle the user kept seeing is a sibling of <c>HBoxContainer</c>
    /// named plainly "Background" (<c>Position=(14,5) Size=(601,118)</c> in the panel's own local,
    /// unscaled coordinates) — sized and positioned to back the *original* icon+description card as a
    /// whole, not tied to the icon/arrows at all. <c>HBoxContainer</c> itself (the actual icon+arrows,
    /// confirmed <c>Position=(-120,-32) Size=(240,64)</c>) turns out to already be centered exactly on
    /// the panel's own local origin — resizing/repositioning "Background" to match that same span,
    /// rather than trying to hide or reason about it further, is what "centered on the widget" needs.
    /// This also explains why the earlier <c>Scale</c> fix needed a deferred <c>Size</c> read at all:
    /// <c>_ascensionPanel</c>'s own root <c>Size</c> reports <c>(0,0)</c> unconditionally (confirmed in
    /// the same dump, taken well after layout) — it is simply never computed by any container, so that
    /// division was always a no-op. It happened to look fine for the icon purely because
    /// <c>HBoxContainer</c> was already centered at local (0,0); an explicit <c>Vector2.Zero</c> pivot is
    /// the same value, just no longer accidental, and — critically — no longer depends on reading a
    /// <c>Size</c> that was never going away from zero no matter how long this waited. Everything below
    /// now runs synchronously in this method, not deferred to a later frame at all.</summary>
    private void SetUpAscensionPanelAppearance()
    {
        // Per the user's earlier explicit request ("without the extra text"): the description shows the
        // real game's own canonical Ascension-N flavor text (e.g. "Tight Belt"), which has nothing to do
        // with this screen's own saved-ladder levels — actively misleading here, not just unwanted.
        Control? description = _ascensionPanel.GetNodeOrNull<Control>("HBoxContainer/AscensionDescription");
        if (description is not null)
        {
            description.Visible = false;
        }

        // Resize the card backdrop to hug just the icon+arrows' own confirmed-centered span (-120..120,
        // -32..32), instead of the much wider original card width. A little more margin than a bare
        // hug — per explicit request ("fix the flame formatting") the first attempt's tight -10/-8
        // margin read as cramped, the icon and arrows crowding the backing card's own edges.
        Control? background = _ascensionPanel.GetNodeOrNull<Control>("Background");
        if (background is not null)
        {
            background.Position = new Vector2(-150f, -50f);
            background.Size = new Vector2(300f, 100f);
        }

        ShaderMaterial? iconHsv = LegacyAscensionUiAccess.GetIconHsv(_ascensionPanel);
        iconHsv?.SetShaderParameter(new StringName("h"), AscensionHue);
        iconHsv?.SetShaderParameter(new StringName("v"), 1.1f);

        // The flame icon itself, scaled up further still (on top of the whole panel's own
        // AscensionPanelScale) — see AscensionIconExtraScale's own doc comment. AscensionLevel (the
        // number) is a child of AscensionIcon (confirmed by the earlier diagnostic dump: both report the
        // same 64x64 Size), so scaling the icon carries the label along with it at the same factor,
        // preserving their relative proportion.
        //
        // Two data points from live feedback, both wrong in opposite directions: Position=(0,0) (this
        // method's first attempt) read as "too high" — the flame glyph is visually bottom-heavy (a wide
        // base tapering to a point at top), so box-centering the label puts the number above the flame's
        // actual visual mass. But the native Position=(-2,13) (the second attempt, on the theory that
        // the original devs had already compensated for exactly this) then read as "too low" instead —
        // comparing the user's own fresh reference screenshot (an unmodified vanilla "No Ascension"
        // panel) against this one side by side, the reference's number sits close to centered within the
        // flame's rounded body, nowhere near as low as the native offset placed it here. Since the two
        // tried values bracket the correct one from opposite sides, this interpolates roughly halfway
        // between them (13/2 ≈ 7) rather than guessing a third arbitrary value blind.
        TextureRect? icon = _ascensionPanel.GetNodeOrNull<TextureRect>("HBoxContainer/AscensionIconContainer/AscensionIcon");
        if (icon is not null)
        {
            icon.PivotOffset = icon.Size / 2f;
            icon.Scale = new Vector2(AscensionIconExtraScale, AscensionIconExtraScale);
            Control? level = icon.GetNodeOrNull<Control>("AscensionLevel");
            if (level is not null)
            {
                level.Position = new Vector2(-2f, 7f);
            }
        }

        _ascensionPanel.PivotOffset = Vector2.Zero;
        _ascensionPanel.Scale = new Vector2(AscensionPanelScale, AscensionPanelScale);
    }

    private void OnAscensionPanelLevelChanged() => SetLevel(_ascensionPanel.Ascension);

    /// <summary>Reproduces <c>EnginePatches.cs</c>'s own <c>ApplyGrayscale</c>/
    /// <c>GhostDeckViewerPresenter.CreateDesaturatedMaterial</c> pattern (duplicate the engine's shared
    /// HSV shader material, zero its "s" parameter) — see class doc comment ("mode-split/background"
    /// revision) for why this is the third, not first, use of this exact technique in this mod.</summary>
    private static ShaderMaterial CreateDesaturatedMaterial()
    {
        ShaderMaterial hsvMaterial = (ShaderMaterial)PreloadManager.Cache.GetMaterial("res://materials/vfx/hsv.tres");
        ShaderMaterial shaderMaterial = (ShaderMaterial)hsvMaterial.Duplicate();
        shaderMaterial.SetShaderParameter(SaturationShaderParam, 0f);
        return shaderMaterial;
    }

    /// <summary>Swaps in the real character-select background scene — see class doc comment ("big
    /// background image" revision) for why this replaced a plain <c>CharacterSelectIcon</c> texture
    /// (that property is the small roster icon, not this big art). Mirrors
    /// <c>NCharacterSelectScreen.SelectCharacter</c>'s own exact pattern (confirmed via decompiled
    /// source, <c>NCharacterSelectScreen.cs:817-826</c>): free whatever was in the container before,
    /// then <c>PreloadManager.Cache.GetScene(...).Instantiate&lt;Control&gt;(...)</c> and add the result
    /// as a child — nothing else. Confirmed the two known bg-root scripts in the decompiled source
    /// (<c>NCharacterSelectScreenBg</c>, <c>NRegentCharacterSelectBg</c>) depend only on their own
    /// children/<c>GetTree().Root</c>, never on being parented by <c>NCharacterSelectScreen</c>
    /// specifically — unlike <c>NAscensionPanel</c>, which needs an external <c>Initialize(...)</c> call
    /// this mod has already declined to gamble on reproducing. Not otherwise verified per-character
    /// (some other character's bg scene could still assume something not seen in those two scripts) —
    /// wrapped in try/catch so a bad one degrades to no background rather than breaking this screen.
    ///
    /// <b>Desaturation, second attempt (2026-09-13)</b>: the first attempt walked every descendant and
    /// overwrote its <c>Material</c> directly (mirroring <c>EnginePatches.ApplyGrayscale</c>'s own
    /// pattern for the in-combat Ghost creature). Confirmed live this was actively harmful here, not
    /// just ineffective: at least one background (Necrobinder/a similarly-composited scene) rendered
    /// broken, hard-edged overlapping shapes — some descendant nodes evidently already carry their own
    /// materials for legitimate masking/compositing/hover-skin effects (<c>NRegentCharacterSelectBg</c>'s
    /// own doc comment already flagged a hover-skin-swap mechanism), and blindly overwriting every one
    /// of them broke that composition rather than just tinting it, while other characters' backgrounds
    /// still weren't desaturated at all (plausibly art using <c>MegaSprite</c>/Spine, where the correct
    /// target is <c>GetNormalMaterial</c>/<c>SetNormalMaterial</c>, not the plain <c>CanvasItem
    /// .Material</c> this walk assumed everywhere). Rather than chase per-node type detection further,
    /// this renders the whole untouched scene into an offscreen <c>SubViewport</c> — every internal
    /// material, mask, and hover effect keeps working exactly as authored, since nothing inside the
    /// scene is touched — then displays that viewport's own composited output as a single flat texture
    /// through one plain <c>TextureRect</c>, with the desaturation material applied to only that one
    /// node. A single ordinary texture is exactly the case this mod's own established technique
    /// (<c>GhostDeckViewerPresenter.CreateDesaturatedMaterial</c>) already handles correctly.</summary>
    private void UpdateBackground(CharacterModel? characterModel)
    {
        if (characterModel?.Id.Entry == _backgroundCharacterId)
        {
            return;
        }
        _backgroundCharacterId = characterModel?.Id.Entry;

        foreach (Node child in _backgroundContainer.GetChildren())
        {
            _backgroundContainer.RemoveChildSafely(child);
            child.QueueFreeSafely();
        }

        if (characterModel is null)
        {
            _backgroundBacking.Visible = false;
            return;
        }

        try
        {
            Control bg = PreloadManager.Cache.GetScene(characterModel.CharacterSelectBg).Instantiate<Control>(PackedScene.GenEditState.Disabled);
            bg.Name = characterModel.Id.Entry + "_bg";
            bg.MouseFilter = MouseFilterEnum.Ignore;

            SubViewport viewport = new()
            {
                Size = (Vector2I)GetViewport().GetVisibleRect().Size,
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                GuiDisableInput = true,
            };
            viewport.AddChild(bg);

            TextureRect display = new()
            {
                AnchorRight = 1f,
                AnchorBottom = 1f,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = MouseFilterEnum.Ignore,
                Texture = viewport.GetTexture(),
                // Fully opaque, no color tint — per explicit request ("opaque and greyscale ... ghost
                // like"), the desaturation shader alone (zeroed saturation) does the "ghostly" work, the
                // same way the in-combat Ghost creature itself reads as grayscale rather than
                // translucent.
                Modulate = new Color(1f, 1f, 1f, 1f),
                Material = CreateDesaturatedMaterial(),
            };

            _backgroundContainer.AddChildSafely(viewport);
            _backgroundContainer.AddChildSafely(display);
            _backgroundBacking.Visible = true;
        }
        catch (Exception ex)
        {
            GhostLog.Warn($"GhostLedgerScreen: failed to load character-select background for {characterModel.Id.Entry}, hiding it ({ex.GetType().Name}: {ex.Message}).");
            _backgroundBacking.Visible = false;
        }
    }

    public override void _Ready()
    {
        ConnectSignals();
    }

    /// <summary>Defensive, not strictly required by this screen's own usage — since <c>Initialize(...)</c>
    /// is never called (see the constructor's own doc comment), no global hotkey binding was ever pushed
    /// for <see cref="_ascensionPanel"/> to remove. Confirmed via decompiled source that
    /// <c>NAscensionPanel.Cleanup()</c> is safe to call unconditionally regardless (a no-op removal of a
    /// binding that was never added), so this costs nothing and guards against that assumption ever
    /// changing.</summary>
    public override void _ExitTree()
    {
        _ascensionPanel.AscensionLevelChanged -= OnAscensionPanelLevelChanged;
        _ascensionPanel.Cleanup();
        base._ExitTree();
    }

    public override void OnSubmenuOpened()
    {
        base.OnSubmenuOpened();
        SetUpAscensionPanelAppearance();
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

    private void SetMode(bool multiplayer)
    {
        if (_multiplayerMode == multiplayer)
        {
            return;
        }
        _multiplayerMode = multiplayer;
        _level = 0;
        Refresh();
    }

    private void SetLevel(int level)
    {
        int max = _multiplayerMode ? MultiplayerGhostSnapshotStore.GetMaxSelectableLevel() : GhostSnapshotStore.GetMaxSelectableLevel();
        _level = Math.Clamp(level, 0, Math.Max(0, max - 1));
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            _soloModeButton.ButtonPressed = !_multiplayerMode;
            _multiplayerModeButton.ButtonPressed = _multiplayerMode;

            int modeMax = _multiplayerMode ? MultiplayerGhostSnapshotStore.GetMaxSelectableLevel() : GhostSnapshotStore.GetMaxSelectableLevel();
            if (modeMax <= 0)
            {
                _headerLabel.Text = _multiplayerMode ? "No multiplayer Ghosts saved yet." : "No solo Ghosts saved yet.";
                _deckHistory.Visible = false;
                _relicHistory.Visible = false;
                _ascensionPanel.SetMaxAscension(0);
                _ascensionPanel.SetAscensionLevel(0);
                // Confirmed via decompiled source: NAscensionPanel.SetMaxAscension does
                // `base.Visible = maxAscension > 0` natively — calling it with 0 (exactly this branch,
                // e.g. a mode with no saved Ghosts yet) hides the whole panel, not just its arrows. This
                // mod already has a Harmony postfix for exactly this native behavior
                // (LegacyAscensionEntry.Postfix_KeepPanelVisibleAtZero), but it's gated behind that
                // class's own `_armed` static field, which is never true for this screen's browsing
                // flow — so it never corrected our panel. Forcing it back on here directly, the same
                // one-line fix that patch already applies elsewhere.
                _ascensionPanel.Visible = true;
                UpdateBackground(null);
                return;
            }

            _deckHistory.Visible = true;
            _relicHistory.Visible = true;
            _level = Math.Clamp(_level, 0, modeMax - 1);
            _ascensionPanel.SetMaxAscension(modeMax - 1);
            _ascensionPanel.SetAscensionLevel(_level);
            _ascensionPanel.Visible = true;

            GhostSnapshotFileStore.LoadResult result = _multiplayerMode
                ? MultiplayerGhostSnapshotStore.Load(_level)
                : GhostSnapshotStore.Load(_level);
            string ladderName = _multiplayerMode ? "MULTIPLAYER" : "SOLO";
            if (!result.Success || result.Player is not { } snapshot)
            {
                _headerLabel.Text = $"{ladderName} A{_level}: unavailable ({result.Error})";
                UpdateBackground(null);
                GhostLog.Warn($"GhostLedgerScreen: failed to load A{_level} ({ladderName}): {result.Error}");
                return;
            }

            CharacterModel? characterModel = snapshot.CharacterId is { } characterId
                ? ModelDb.GetByIdOrNull<CharacterModel>(characterId)
                : null;
            string characterName = characterModel?.Id.Entry ?? snapshot.CharacterId?.ToString() ?? "?";
            _headerLabel.Text = $"{ladderName} A{_level} — {characterName}";

            UpdateBackground(characterModel);

            Player previewPlayer = Player.CreateForNewRun(
                characterModel ?? throw new InvalidOperationException("GhostLedgerScreen: snapshot's CharacterId did not resolve to a CharacterModel."),
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
