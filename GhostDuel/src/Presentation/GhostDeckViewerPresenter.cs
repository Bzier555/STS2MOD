using System;
using System.Collections.Generic;
using GhostDuel.Diagnostics;
using GhostDuel.Ghost;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;

namespace GhostDuel.Presentation;

/// <summary>
/// PLAN.md M10b: one small clickable button per Ghost, attached to that Ghost's own <c>NCreature</c>
/// node, opening the native "view a player's deck/relics/potions" screen
/// (<c>NMultiplayerPlayerExpandedState</c>) against that specific Ghost's own live <c>Player</c>.
/// Confirmed by direct read: that native screen has no <c>CombatSide</c>/<c>IsPlayer</c> check anywhere
/// and reads live <c>Relics</c>/<c>Potions</c>/<c>Deck.Cards</c> directly off the <c>Player</c> handed
/// to it, so it works unmodified on a Ghost — the two calls here
/// (<c>NMultiplayerPlayerExpandedState.Create</c> then <c>NCapstoneContainer.Instance.Open</c>) are the
/// exact ones the native click handler itself makes (<c>NMultiplayerPlayerState.OnRelease</c>). The
/// native *trigger* for this screen (the multiplayer party bar,
/// <c>NMultiplayerPlayerStateContainer</c>) only enumerates <c>RunState.Players</c> — a Ghost is
/// deliberately never in that list (this whole mod's premise), so it never gets an entry there; this
/// presenter is the replacement trigger, not a repurposing of anything native.
///
/// No new Godot scene or texture ships with this mod (<c>"has_pck": false</c>, <c>GhostDuel.json</c>),
/// so rather than invent a new visual, the button reuses the character's own portrait —
/// <c>CharacterModel.IconTexture</c> — confirmed to be the exact same texture the native multiplayer
/// party bar renders for this purpose (<c>NMultiplayerPlayerState.cs:413</c>:
/// <c>_characterIcon.Texture = Player.Character.IconTexture;</c>). A <c>TextureButton</c>, not a plain
/// text <c>Button</c> with a caption: no button chrome, just the recognizable portrait, matching that
/// native look. Per the user's 2026-09-05 request, desaturated to gray — reusing the exact same
/// <c>res://materials/vfx/hsv.tres</c> shader-material technique (duplicated instance, "s" shader param
/// zeroed) that <c>EnginePatches.cs</c>'s own <c>ApplyGrayscale</c> already applies to the Ghost's own
/// sprite, so the button visually reads as part of the same desaturated Ghost rather than a normal
/// full-color character icon.
///
/// <b>Position</b>: per the user's 2026-09-05 request, pinned to the right edge of the screen rather
/// than floating near the Ghost's own sprite (which can sit anywhere on the enemy side, especially with
/// a multi-Ghost party). Reparented under <c>NRun.Instance.GlobalUi</c> — the same always-on-top,
/// viewport-spanning container the native TopBar/RelicInventory/CapstoneContainer already live under
/// (confirmed: <c>NMultiplayerPlayerExpandedState.AfterCapstoneOpened</c> reaches all three off of it) —
/// anchored to ITS right edge (<c>AnchorLeft = AnchorRight = 1</c>, a real Godot anchor fraction, not a
/// literal screen coordinate) with a small local inset offset.
///
/// <b>Revision 2026-09-05, second pass</b>: per the user's explicit request, sized up 25%
/// (<c>44 -&gt; 55</c>) and re-derived for a multi-Ghost party. The first pass took its vertical position
/// from the Ghost's own <c>IntentContainer.GlobalPosition</c> — safe for exactly one Ghost, but every
/// enemy-side creature in this game (Ghosts included) stands on the same ground line, so a party of 2-4
/// Ghosts would all resolve to nearly the same Y and stack their icons on top of each other at the same
/// right-edge X. Replaced with a fixed vertical list: each Ghost gets a slot ordinal (this attach order —
/// <see cref="AttachButtonFor"/> is called once per Ghost, in the same order every session, so it is
/// deterministic per party) stacked downward from <c>NRun.Instance.GlobalUi</c>'s own top edge, each slot
/// spaced by one full <c>ButtonSize.Y</c> plus a fixed margin.
///
/// <b>Revision 2026-09-05, third pass</b> — the icon stopped showing at all after the second pass.
/// Root cause: the second pass anchored Y off <c>GlobalUi.TopBar</c> specifically, not <c>GlobalUi</c>
/// itself — but <c>TopBar</c> is animated (<c>NMultiplayerPlayerExpandedState.AfterCapstoneOpened</c>
/// calls <c>globalUi.TopBar.AnimHide()</c>/<c>AnimShow()</c> elsewhere in this exact codebase, confirming
/// it moves), so its position at the exact moment P6 fires during combat setup is not guaranteed to be
/// its resting, on-screen one — a hidden/mid-animation TopBar could put every button off-screen with no
/// error logged (this is a positioning bug, not a crash — nothing throws). Replaced the anchor with
/// <c>GlobalUi</c> itself, which is never hidden or animated (the persistent root of all run UI) —
/// still a real node's own position, per <c>CLAUDE.md</c>, just a more stable one, with a fixed
/// (hand-tuned, not derived) clearance margin from its top edge in place of <c>TopBar.Size.Y</c>.
///
/// Mirrors the click guard the native handler itself uses
/// (<c>!NTargetManager.Instance.IsInSelection</c>) so this can never fire while the human is mid-
/// target-selection for a card.
///
/// <b>Ghost display name</b>: the opened screen's own <c>_Ready()</c> computes its title via
/// <c>PlatformUtil.GetPlayerName(platform, player.NetId)</c> — for a Ghost's synthetic NetId this
/// resolves to nothing and falls back to the raw numeric id (confirmed live: the user saw a long digit
/// string instead of a name). Fixed here, not with a Harmony patch on that widely-shared platform
/// helper (CLAUDE.md's "narrower target" question) — right after opening, this click handler reaches
/// into the screen it just created (<see cref="GhostExpandedStatePlayerNameLabelAccess"/>) and
/// overwrites the label with "{Character} Ghost" instead.
///
/// One instance per combat, disposed with the session — same lifetime shape as
/// <see cref="GhostDamageIndicatorPresenter"/> (CLAUDE.md's code-shape rule: no new static mutable
/// state). Attaches one button per <see cref="GhostSession.Party"/> member, correct for any party
/// size — no assumption of exactly one Ghost.
///
/// <b>Construction timing</b>: unlike <see cref="GhostDamageIndicatorPresenter"/> (purely event-driven,
/// so it never needs a node to exist until damage actually queues), this presenter needs each Ghost's
/// own <c>NCreature</c> node to exist *at construction/attach time*. <c>GhostSession</c>'s own
/// constructor runs before any Ghost has been added to the combat room (confirmed live: "Session
/// begin" always logs before "Ghost added to CombatState" in every session log this milestone), so
/// attaching eagerly there would silently find every node missing. Instead, <see cref="AttachButtonFor"/>
/// is called once per Ghost from P6 (<c>EnginePatches.cs</c>'s <c>P6_PlaceGhostOnEnemySide</c>), the
/// one place already guaranteed to hold a valid node reference for that exact creature at that exact
/// moment.
/// </summary>
internal sealed class GhostDeckViewerPresenter : IDisposable
{
    private static readonly Vector2 ButtonSize = new(55f, 55f); // 44 * 1.25, per the user's 2026-09-05 request
    private const float RightEdgeInset = 16f;
    private const float TopMargin = 100f; // hand-tuned clearance below GlobalUi's own top edge, past the native TopBar
    private const float VerticalSpacing = 12f;
    private static readonly StringName SaturationShaderParam = new("s");

    private readonly Dictionary<Creature, TextureButton> _buttons = new();
    private bool _disposed;

    /// <summary>Called once per Ghost, from P6, as soon as that Ghost's own <c>NCreature</c> node is
    /// known to exist. <paramref name="node"/> itself is no longer used for positioning (see class doc
    /// comment, "second pass") — kept as the parameter P6 already reliably provides at this exact call
    /// site, not because this method still reads it.</summary>
    public void AttachButtonFor(Player ghostPlayer, NCreature node)
    {
        try
        {
            Creature ghostCreature = ghostPlayer.Creature;
            if (_buttons.ContainsKey(ghostCreature))
            {
                return;
            }

            TextureButton button = new()
            {
                TextureNormal = ghostPlayer.Character.IconTexture,
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                Size = ButtonSize,
                AnchorLeft = 1f,
                AnchorRight = 1f,
                Position = new Vector2(-(ButtonSize.X + RightEdgeInset), 0f),
                Material = CreateDesaturatedMaterial(),
            };
            button.Pressed += () => OnPressed(ghostPlayer);

            var globalUi = NRun.Instance!.GlobalUi;
            int slot = _buttons.Count;
            globalUi.AddChildSafely(button);
            // AnchorLeft/Right already placed the X coordinate against GlobalUi's own right edge; Y is
            // a fixed per-slot stack below GlobalUi's own (never-hidden, never-animated) top edge — see
            // class doc comment ("third pass") for why this isn't anchored to TopBar specifically.
            button.GlobalPosition = new Vector2(
                button.GlobalPosition.X,
                globalUi.GlobalPosition.Y + TopMargin + slot * (ButtonSize.Y + VerticalSpacing));

            _buttons[ghostCreature] = button;
        }
        catch (Exception ex)
        {
            GhostLog.Warn($"GhostDeckViewerPresenter: failed to attach deck-view button for Ghost#{ghostPlayer.NetId}, ignoring ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Reproduces <c>EnginePatches.cs</c>'s own <c>ApplyGrayscale</c> pattern (duplicate the
    /// engine's shared HSV shader material, zero its "s" parameter) — see class doc comment.</summary>
    private static ShaderMaterial CreateDesaturatedMaterial()
    {
        ShaderMaterial hsvMaterial = (ShaderMaterial)PreloadManager.Cache.GetMaterial("res://materials/vfx/hsv.tres");
        ShaderMaterial shaderMaterial = (ShaderMaterial)hsvMaterial.Duplicate();
        shaderMaterial.SetShaderParameter(SaturationShaderParam, 0f);
        return shaderMaterial;
    }

    private static void OnPressed(Player ghostPlayer)
    {
        try
        {
            if (NTargetManager.Instance.IsInSelection)
            {
                return;
            }
            NMultiplayerPlayerExpandedState screen = NMultiplayerPlayerExpandedState.Create(ghostPlayer);
            NCapstoneContainer.Instance?.Open(screen);
            // See class doc comment "Ghost display name" — overwrite after Open(), since that call
            // synchronously runs the screen's own _Ready() (which sets the wrong, netId-based text)
            // before returning.
            GhostExpandedStatePlayerNameLabelAccess.GetPlayerNameLabel(screen).Text = $"{ghostPlayer.Character.Title} Ghost";
        }
        catch (Exception ex)
        {
            GhostLog.Warn($"GhostDeckViewerPresenter: failed to open deck viewer for Ghost#{ghostPlayer.NetId}, ignoring ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (TextureButton button in _buttons.Values)
        {
            if (!GodotObject.IsInstanceValid(button))
            {
                continue;
            }
            button.GetParent()?.RemoveChildSafely(button);
            button.QueueFreeSafely();
        }
        _buttons.Clear();
    }
}
