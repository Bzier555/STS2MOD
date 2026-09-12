using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GhostDuel.Diagnostics;
using GhostDuel.Duel;
using GhostDuel.Ghost;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.ValueProps;

namespace GhostDuel.Engine;

/// <summary>
/// P1-P18, the M1-M6 gameplay patch set (ARCHITECTURE.md §4). Every patch's mode guard is the first
/// statement, per CLAUDE.md's Harmony rules. P1 guards on <see cref="GhostConstructionScope"/>
/// (construction-time only); P2-P18 guard on <see cref="GhostSession.Current"/> — one field, one
/// meaning, no other static mode state. P7-P18 were not part of the original five-patch plan — each
/// is earned by a confirmed live failing observation (ENGINE-NOTES.md), not added in advance, per
/// ARCHITECTURE.md §4's rule. There is no numeric ceiling on this list (2026-09-02 decision) — an
/// earlier, different patch also numbered P9 (actor-relative `CombatState.Allies`/`Enemies`) was
/// tried and reverted; see the historical note at the bottom of this file. The P9 above is unrelated
/// and replaces that freed slot. P10 and P18 are not yet confirmed live (P11-P17 are, as of
/// 2026-09-03): P10 is a narrower second attempt at the same underlying bug the reverted P9 tried to
/// fix, and P11
/// (confirmed live) widens P10's guard to cover relic/power lifecycle hooks (P10 alone did not fix
/// RedMask — see P11's doc comment; RedMask itself is now confirmed fixed, but P10's own narrower
/// mechanism was never isolated from P11's, so it stays listed as not separately confirmed); P12
/// (confirmed live) is an unrelated bug found the same test round (StoneArmor/PlatingPower granting 3x
/// its intended Block); P13 (confirmed live) is a third, separate bug from the same round (a turn-1
/// energy relic's bonus silently discarded by the Ghost's own energy reset running after it instead of
/// before, per <see cref="GhostTurnController.SetupTurn"/>'s doc comment); P14 (confirmed live) is a
/// fourth bug, found after P11 (PlatingPower's Block grant firing twice per round — once at the
/// human's turn end too); P15-P17 (confirmed live 2026-09-03) are M3/M4's queued-damage core (see each
/// patch's own doc comment); P18 (not yet confirmed live) is M6's status-expiry fix (a native
/// grace-tick asymmetry that let a one-turn debuff persist for two).
/// </summary>
internal static class EnginePatches
{
    /// <summary>P1: side flip inside <see cref="GhostConstructionScope"/>.</summary>
    [HarmonyPatch(typeof(Creature), MethodType.Constructor, typeof(MegaCrit.Sts2.Core.Entities.Players.Player), typeof(int), typeof(int))]
    private static class P1_AssignEnemySide
    {
        [HarmonyPostfix]
        private static void Postfix(Creature __instance)
        {
            if (!GhostConstructionScope.IsActive)
            {
                return;
            }
            CreatureSideAccess.SetSide(__instance, CombatSide.Enemy);
        }
    }

    /// <summary>
    /// P2: native <c>Creature.TakeTurn</c> throws for a non-monster (ENGINE-NOTES.md §2). Run the
    /// Ghost turn controller instead.
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.TakeTurn))]
    private static class P2_RunGhostTurn
    {
        [HarmonyPrefix]
        private static bool Prefix(Creature __instance, ref Task __result)
        {
            if (GhostSession.Current is not { } session || session.FindByCreature(__instance) is not { } member)
            {
                return true;
            }
            __result = GhostTurnController.RunTurnAsync(member.Player, session);
            return false;
        }
    }

    /// <summary>
    /// P3: native <c>Creature.AfterAddedToRoom</c> dereferences a null <c>Monster</c> for a
    /// Player-backed creature (ENGINE-NOTES.md §2). No-op for the Ghost.
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.AfterAddedToRoom))]
    private static class P3_SkipMonsterRoomLifecycle
    {
        [HarmonyPrefix]
        private static bool Prefix(Creature __instance, ref Task __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostCreature(__instance))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
    }

    /// <summary>
    /// P4: native <c>CombatManager.AfterCreatureAdded</c> calls <c>creature.Monster.RollMove(...)</c>
    /// unconditionally for any enemy-side creature — confirmed NPE at
    /// <c>CombatManager.cs:865</c> (ENGINE-NOTES.md §7 Q2, §8). Skip for the Ghost.
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.AfterCreatureAdded))]
    private static class P4_SkipMonsterMoveRoll
    {
        [HarmonyPrefix]
        private static bool Prefix(Creature creature, ref Task __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostCreature(creature))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
    }

    /// <summary>
    /// P5: add the Ghost to <c>CombatState</c> as <c>CombatRoom.StartCombat</c>'s first statement.
    /// <b>Revised 2026-09-02</b>: originally targeted <c>CombatManager.SetUpCombat</c>, which is
    /// early enough for combat-state population but too late for node creation — confirmed live: the
    /// Ghost never got a visual node, because <c>CombatRoom.StartCombat</c> calls
    /// <c>NCombatRoom.Create(...)</c> (which builds enemy nodes from whatever is in
    /// <c>CombatState.Enemies</c> at that moment) *before* calling <c>SetUpCombat</c>
    /// (<c>CombatRoom.cs:197-231</c>: monster generation → `NCombatRoom.Create` at `:219` →
    /// `CombatManager.Instance.SetUpCombat(CombatState)` at `:225`). Patching the earlier method
    /// means every native pass `SetUpCombat` runs (<c>ResetCombatState</c>, <c>PopulateCombatState</c>,
    /// <c>NetCombatCardDb.StartCombat</c>, per-creature <c>AddCreature</c>) still includes the Ghost,
    /// *and* so does node creation (ENGINE-NOTES.md §8, §9 Q3).
    /// </summary>
    /// <summary>
    /// P5 <b>revised 2026-09-03 (M8.5)</b>: <see cref="GhostDuel.Debug.RealFlowGhostDuelEntry"/>'s random-deck
    /// substitution runs inside a synchronous prefix on <c>ActModel.PullNextEncounter</c> and cannot
    /// itself await <c>GhostPlayerFactory.ConfigureDeckAsync</c>'s native async commands — it leaves
    /// the request on <see cref="GhostSession.PendingRandomDeck"/> instead. <c>StartCombat</c> is the
    /// earliest async native method downstream of that point (confirmed:
    /// <c>private async Task StartCombat(IRunState? runState)</c>, <c>CombatRoom.cs:197</c>) and runs
    /// strictly before <c>CombatManager.SetUpCombat</c> shuffles the Ghost's deck into its draw pile
    /// — the same reverse-patch technique P13 already uses (call the real body through a
    /// <c>HarmonyReversePatch</c> stub after awaiting our own setup first) rather than recursing back
    /// into this same patch by calling the method by name.
    /// </summary>
    [HarmonyPatch(typeof(CombatRoom), "StartCombat")]
    private static class P5_AddGhostToCombatState
    {
        [HarmonyPrefix]
        private static bool Prefix(CombatRoom __instance, IRunState runState, ref Task __result)
        {
            if (GhostSession.Current is not { } session)
            {
                return true;
            }
            CombatState state = __instance.CombatState;
            // PLAN.md M9c: every party member, not just one — still exactly one iteration for the
            // still-primary 1v1 case.
            foreach (GhostPartyMember member in session.Party)
            {
                if (!state.Players.Contains(member.Player))
                {
                    state.AddPlayer(member.Player);
                    GhostLog.Info($"Ghost added to CombatState: netId={member.Player.NetId}");
                }
            }

            if (session.PendingRandomDeck is not { } pending)
            {
                return true;
            }
            session.PendingRandomDeck = null;
            __result = ConfigureDeckThenOriginal(__instance, runState, session, pending);
            return false;
        }

        private static async Task ConfigureDeckThenOriginal(
            CombatRoom instance, IRunState runState, GhostSession session,
            (IReadOnlyList<Type> Cards, IReadOnlyList<Type> Relics) pending)
        {
            await GhostPlayerFactory.ConfigureDeckAsync(session.GhostPlayer, pending.Cards, pending.Relics);
            GhostLog.Info($"P5: Ghost's M8.5 random deck configured before combat setup — "
                + $"[{string.Join(", ", pending.Cards.Select(t => t.Name))}] + relics [{string.Join(", ", pending.Relics.Select(t => t.Name))}].");
            await Original(instance, runState);
        }

        [HarmonyReversePatch]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task Original(CombatRoom instance, IRunState runState)
        {
            throw new NotImplementedException("P5: replaced by HarmonyReversePatch at patch time - should never actually run.");
        }
    }

    /// <summary>
    /// P7: native <c>Creature.PrepareForNextTurn</c> defaults <c>rollNewMove: true</c> and
    /// unconditionally calls <c>Monster.RollMove(...)</c> — confirmed NPE for a Player-backed
    /// creature. Called every human turn, for every enemy-side creature, from
    /// <c>CombatManager.cs:480-483</c> (<c>foreach (Creature enemy in _state.Enemies)
    /// enemy.PrepareForNextTurn(_state.PlayerCreatures);</c>) — this fires for the Ghost on every
    /// single human turn, not just the rare summon path <c>CreatureCmd.Add</c> already guards with
    /// <c>rollNewMove: false</c>. No-op for the Ghost; M1 has no canned "next move" to roll and no
    /// intent nodes yet (M3).
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.PrepareForNextTurn))]
    private static class P7_SkipMonsterMoveRollForNextTurn
    {
        [HarmonyPrefix]
        private static bool Prefix(Creature __instance)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostCreature(__instance))
            {
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// P6: <c>NCombatRoom.AddCreature</c> parents every <c>IsPlayer</c> node into
    /// <c>_allyContainer</c> regardless of <c>Side</c> (ENGINE-NOTES.md §7 Q4). Reparent the Ghost's
    /// node into the enemy container so it renders and is targetable on the correct side
    /// (POSTMORTEM F9).
    /// <b>Revised 2026-09-03</b>: confirmed live (Necrobinder) this same method has a sibling bug for
    /// pets — <c>NCombatRoom.cs:722-733</c>'s check is <c>creature.IsPlayer || creature.PetOwner !=
    /// null</c>, so any pet (a <c>Monster</c>-backed creature with no <c>Side</c> of its own that would
    /// trigger this branch) is unconditionally parented into <c>_allyContainer</c> regardless of its
    /// owner's side — this original guard (<c>ReferenceEquals(creature,
    /// session.GhostPlayer.Creature)</c>) never matches a pet (a different <c>Creature</c> object), so
    /// Osty, summoned by the Ghost's Bodyguard, was never reparented. The mid-combat pet-positioning
    /// code in the same native method (<c>NCombatRoom.cs:747-763</c>) sets the pet's <c>Position</c>
    /// relative to its owner's own node <c>Position</c> — which, for the Ghost, is already expressed
    /// in the enemy container's coordinate frame by the time any pet is summoned (this patch runs
    /// synchronously the moment the Ghost itself was added, long before). Reparenting the pet the same
    /// way, with the same <c>keepGlobalTransform: false</c>, is confirmed sufficient on its own — no
    /// position recompute needed — since that already-correct numeric value simply gets reinterpreted
    /// in the matching frame it was actually computed for.
    /// </summary>
    [HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
    private static class P6_PlaceGhostOnEnemySide
    {
        [HarmonyPostfix]
        private static void Postfix(NCombatRoom __instance, Creature creature)
        {
            if (GhostSession.Current is not { } session)
            {
                return;
            }
            if (session.IsGhostCreature(creature))
            {
                NCreature? node = __instance.GetCreatureNode(creature);
                if (node is null)
                {
                    GhostLog.Warn("P6: Ghost creature node not found after AddCreature; cannot reposition.");
                    return;
                }
                node.Reparent(CombatRoomContainerAccess.GetEnemyContainer(__instance), keepGlobalTransform: false);

                // The Ghost's sprite is a Player character sprite, authored facing right (toward the
                // enemy side) for its normal ally position. Sitting in the enemy container it needs to
                // face the opposite way, back toward the human, like any other enemy.
                // Flip only the visuals sub-node (the skeleton/sprite), not NCreature itself — the health
                // bar, hitbox, intent container and selection reticle are siblings under NCreature, not
                // children of Visuals, but confirmed live (2026-09-02) that flipping the whole node's
                // Scale mirrors the health bar and damage numbers along with the sprite, which is wrong.
                node.Visuals.Scale = new Godot.Vector2(-Godot.Mathf.Abs(node.Visuals.Scale.X), node.Visuals.Scale.Y);

                // Per explicit user request (2026-09-03): desaturate the Ghost's own sprite to
                // black and white, so it reads as visually distinct from the character it mirrors.
                // Reuses the engine's own res://materials/vfx/hsv.tres shader material — the same
                // one NCreatureVisuals.SetScaleAndHue (NCreatureVisuals.cs:277-298) and several UI
                // classes (e.g. NCharacterSelectButton's locked-character dimming) already apply to
                // a SpineSprite's NormalMaterial via a duplicated instance with h/s/v shader
                // parameters — just setting "s" (saturation) to 0 instead of shifting "h" (hue).
                ApplyGrayscale(node);

                // Confirmed live (2026-09-03) — the four-corner targeting reticle shows correctly
                // on the first hover but never fades out again after the first card target
                // selection completes, permanently visible from then on. Root cause, confirmed by
                // reading NSelectionReticle in full: OnDeselect() (NSelectionReticle.cs:96-109) is
                // a no-op once its private, readonly _cancelToken has been cancelled, and
                // _ExitTree() (NSelectionReticle.cs:75-79) cancels it — Reparent(...) above fires
                // _ExitTree() on every descendant, including this creature's %SelectionReticle
                // child, as an unavoidable side effect of reparenting a still-alive node (an
                // ordinary creature is never reparented mid-life, so this never happens natively).
                // See NSelectionReticleCancelTokenAccess for why reflection is the narrowest fix.
                ResetSelectionReticle(node);

                // PLAN.md M10b: this is the first point guaranteed to hold a valid, correctly
                // positioned node reference for this specific Ghost — see
                // GhostDeckViewerPresenter's own doc comment for why it can't attach eagerly instead.
                if (creature.Player is { } ghostPlayer)
                {
                    session.NotifyGhostCreatureNodeReady(ghostPlayer, node);
                }
                return;
            }

            if (creature.PetOwner is not null && session.IsGhostPlayer(creature.PetOwner))
            {
                NCreature? petNode = __instance.GetCreatureNode(creature);
                if (petNode is null)
                {
                    GhostLog.Warn("P6: Ghost-owned pet creature node not found after AddCreature; cannot reposition.");
                    return;
                }
                NCreature? ownerNode = __instance.GetCreatureNode(creature.PetOwner.Creature);

                petNode.Reparent(CombatRoomContainerAccess.GetEnemyContainer(__instance), keepGlobalTransform: false);

                // A pet's Side matches its owner's (confirmed: PlayerCmd.AddPet<T>, CreateCreature(...,
                // player.Creature.Side, ...)) — for a normal human, that means Osty is genuinely
                // ally-side and its sprite is authored facing right (toward the enemy) accordingly, the
                // same convention as a Player character sprite. Sitting in the enemy container now, for
                // the same reason as the Ghost's own sprite, it needs the same horizontal flip.
                petNode.Visuals.Scale = new Godot.Vector2(-Godot.Mathf.Abs(petNode.Visuals.Scale.X), petNode.Visuals.Scale.Y);
                ApplyGrayscale(petNode);
                ResetSelectionReticle(petNode);

                // Confirmed live (2026-09-03) — Osty sat on the wrong side of the Ghost. Root
                // cause: this same native method's own mid-body pet-layout loop
                // (NCombatRoom.cs:749-763) sets `nCreature2.Position = new Vector2(creatureNode
                // .Position.X - 20f + num2 * num + nCreature2.Visuals.Bounds.Size.X * 0.5f, ...)` —
                // a fixed offset baked for the ally-side convention (owner faces right, pet sits
                // slightly toward the enemy, i.e. to the owner's right). The Ghost's own sprite is
                // mirrored to face the opposite way (P6, above); its pet's offset needs the same
                // mirroring, which this native formula has no side-awareness to do. Reflecting the
                // already-computed X offset around the owner's own (already correctly repositioned)
                // Position — negating the delta rather than recomputing the native formula's
                // internals (num/num2/list are local to that method, not available here) — puts
                // Osty on the correct side with the exact same native-computed magnitude.
                if (ownerNode is not null)
                {
                    float delta = petNode.Position.X - ownerNode.Position.X;
                    petNode.Position = new Godot.Vector2(ownerNode.Position.X - delta, petNode.Position.Y);
                }
                else
                {
                    GhostLog.Warn("P6: Ghost owner creature node not found; cannot mirror pet offset.");
                }

                // Confirmed live (2026-09-03, Necrobinder): this same native method's own
                // mid-body pet-layout loop (NCombatRoom.cs:749-763) builds its affected-pet list
                // with `!(c.Entity.Monster is Osty) || !LocalContext.IsMe(player)` — for the
                // Ghost, LocalContext.IsMe(player) is always false, so every Ghost-owned pet
                // (Osty included) is unconditionally swept into that loop and gets
                // ToggleIsInteractable(on: false) called on it (NCombatRoom.cs:762), which sets
                // NCreature._stateDisplay.Visible = false directly. That loop runs earlier in
                // this same original method body, before this postfix — so it silently re-hides
                // whatever P21's _Ready() postfix had just shown, every single time any pet of
                // the Ghost's is (re-)added to the tree. Undo it here, the one point after the
                // full native method (including that loop) has finished running.
                petNode.ToggleIsInteractable(on: true);
            }
        }

        private static void ResetSelectionReticle(NCreature node)
        {
            NSelectionReticle? reticle = node.GetNodeOrNull<NSelectionReticle>("%SelectionReticle");
            if (reticle is null)
            {
                GhostLog.Warn("P6: SelectionReticle node not found; targeting-box stuck-visible bug not fixed for this creature.");
                return;
            }
            NSelectionReticleCancelTokenAccess.ResetCancelToken(reticle);
        }

        private static readonly Godot.StringName SaturationShaderParam = new("s");

        /// <summary>
        /// Zeroes the "s" (saturation) shader parameter on every native Spine sprite under
        /// <paramref name="node"/> — not just <c>node.Visuals.SpineBody</c> — and mirrors any of them
        /// that live outside <c>node.Visuals</c>' own subtree (which already got its own flip, above,
        /// via <c>Visuals.Scale.X</c>).
        ///
        /// <b>Revised 2026-09-05</b>: confirmed live (Regent) — a held weapon stayed full-color and
        /// facing its original direction while the rest of the Ghost correctly desaturated and flipped.
        /// Root cause, confirmed by reading <c>NRegentVfx.cs</c> (a monster-specific VFX helper script
        /// this mod did not write): some characters render hand-held elements as their own, separate
        /// native <c>"SpineSprite"</c> nodes (<c>NRegentVfx._weapon</c>/<c>_weapon2</c>, wrapping
        /// <c>"Weapons/WeaponAnim1"</c>/<c>"WeaponAnim2"</c>) — entirely independent of
        /// <c>node.Visuals.SpineBody</c>'s own material and transform, so the original single-sprite
        /// version of this method never touched them. Rather than special-case Regent by id
        /// (CLAUDE.md's core rule), this instead walks every descendant of <paramref name="node"/> for
        /// any node whose native class is <c>"SpineSprite"</c> (the same identity check
        /// <c>NCreatureVisuals.cs:185</c> itself already uses) and desaturates each one found — a
        /// character-agnostic fix that happens to catch Regent's extra weapon sprites without ever
        /// naming them, and costs nothing extra for any character that (like most) has only the one.
        /// Orientation: a sprite already inside <c>node.Visuals</c>' own subtree already inherited that
        /// node's mirrored <c>Scale.X</c> — flipping it again here would cancel that back out, so a
        /// sprite only gets its own flip when it is found <i>outside</i> that subtree (checked via
        /// <c>Node.IsAncestorOf</c>, not by assuming a particular scene shape).
        ///
        /// <b>Not yet confirmed live</b> — this is a first pass based on reading <c>NRegentVfx.cs</c>,
        /// not a live-observed fix. In particular, if Regent's weapon sprites turn out to sit *inside*
        /// <c>Visuals</c>' own subtree after all, this fixes the desaturation but not the orientation —
        /// that would instead point to Spine's IK/constraint system not respecting a naive parent-level
        /// mirror for a bone-attached held item, a deeper native limitation this method cannot reach.
        /// </summary>
        private static void ApplyGrayscale(NCreature node)
        {
            bool foundAny = false;
            foreach (Godot.Node descendant in node.FindChildren("*", recursive: true, owned: false))
            {
                if (descendant.GetClass() != MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSprite.spineClassName)
                {
                    continue;
                }
                foundAny = true;
                DesaturateSpineSprite(new MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSprite(descendant));
                if (!node.Visuals.IsAncestorOf(descendant) && descendant is Godot.Node2D spineNode2D)
                {
                    spineNode2D.Scale = new Godot.Vector2(-Godot.Mathf.Abs(spineNode2D.Scale.X), spineNode2D.Scale.Y);
                }
            }
            if (!foundAny)
            {
                GhostLog.Warn("P6: Ghost creature has no SpineSprite descendants; cannot desaturate.");
            }
        }

        private static void DesaturateSpineSprite(MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSprite sprite)
        {
            Godot.Material? normalMaterial = sprite.GetNormalMaterial();
            Godot.ShaderMaterial shaderMaterial;
            if (normalMaterial is null)
            {
                Godot.Material hsvMaterial = (Godot.ShaderMaterial)MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetMaterial("res://materials/vfx/hsv.tres");
                shaderMaterial = (Godot.ShaderMaterial)hsvMaterial.Duplicate();
                sprite.SetNormalMaterial(shaderMaterial);
            }
            else
            {
                shaderMaterial = (Godot.ShaderMaterial)normalMaterial;
            }
            shaderMaterial.SetShaderParameter(SaturationShaderParam, 0f);
        }
    }

    /// <summary>
    /// P24: confirmed live (2026-09-03) — the Ghost's Osty starts combat correctly flipped (P6),
    /// but visibly un-flips the moment any scale transition plays (e.g. right after being
    /// summoned). Root cause, confirmed by reading <c>NCreature.OstyScaleToSize</c> in full
    /// (<c>NCreature.cs:1096-1110</c>): its tween's target is <c>Vector2.One * num *
    /// Visuals.DefaultScale</c> — <c>Vector2.One</c> is always <c>(1, 1)</c> and
    /// <c>DefaultScale</c> is a single, always-positive <c>float</c> (not a per-axis value, so its
    /// sign can't be flipped without also flipping the Y axis) — so this tween's target scale is
    /// always uniformly positive, regardless of the pet's current facing, and <c>TweenProperty</c>
    /// linearly interpolates from whatever the current (correctly negative) X is toward that
    /// positive target, visibly crossing zero. Called from <c>OstyCmd.Summon</c>
    /// (<c>OstyCmd.cs:88</c>, <c>duration: 0.75</c>) on every summon/revive/max-HP-grow, and once
    /// more at Osty's own death (<c>NCreature.cs:1055</c>, <c>OstyScaleToSize(0f, 0.75)</c>).
    /// Reimplementing this tween's own math to keep it negative-X throughout was considered and
    /// rejected: <c>OstyScaleToSize</c> also conditionally tweens <c>Position</c> (only when
    /// <c>LocalContext.IsMe(Entity.PetOwner)</c>, never true for the Ghost, so irrelevant here) and
    /// finishes with a <c>TweenCallback</c> into <c>UpdateBounds</c>, a private method — reproducing
    /// that exactly would mean either guessing at private internals or leaving them out and risking
    /// a silent visual mismatch. Instead: let the native tween run entirely unchanged (right
    /// magnitude, right easing, right bounds update), then run a second, independent tween on the
    /// same node — Godot tweens created via separate <c>CreateTween()</c> calls run independently,
    /// so this cannot fight or be fought by the native one — that waits the same <c>duration</c>
    /// and then snaps the X sign back negative. This does not eliminate the
    /// brief mid-transition flash to the un-flipped orientation (a genuine limitation — the
    /// resize/regrow animation the native tween plays is itself expected and wanted, only its
    /// sign is wrong), but the pet is confirmed correctly oriented again the instant the native
    /// tween completes, rather than staying wrong for the rest of combat.
    /// </summary>
    [HarmonyPatch(typeof(NCreature), nameof(NCreature.OstyScaleToSize))]
    private static class P24_PreserveGhostOstyFacingThroughScaleTween
    {
        [HarmonyPostfix]
        private static void Postfix(NCreature __instance, double duration)
        {
            if (GhostSession.Current is not { } session
                || __instance.Entity.PetOwner is null
                || !session.IsGhostPlayer(__instance.Entity.PetOwner))
            {
                return;
            }
            if (duration <= 0.0)
            {
                Snap(__instance);
                return;
            }
            Godot.Tween fixupTween = __instance.CreateTween();
            fixupTween.TweenInterval(duration);
            fixupTween.TweenCallback(Godot.Callable.From(() => Snap(__instance)));
        }

        private static void Snap(NCreature node)
        {
            node.Visuals.Scale = new Godot.Vector2(-Godot.Mathf.Abs(node.Visuals.Scale.X), node.Visuals.Scale.Y);
        }
    }

    /// <summary>
    /// P25: confirmed live (2026-09-03) — when the human wins, the defeated Ghost is healed 1 HP
    /// and plays its stand-up/revive animation right as combat ends. Root cause, confirmed by
    /// reading <c>CombatManager.EndCombatInternal</c> in full (<c>CombatManager.cs:970-1014</c>):
    /// <c>foreach (Player player in combatState.Players) { await player.ReviveBeforeCombatEnd(); }</c>
    /// (<c>:984-987</c>) — the fifth confirmed consumer this project has found of the same
    /// <c>IsPlayer</c>-derived, not Side-filtered, <c>CombatState.Players</c> shape (ENGINE-NOTES.md
    /// §7 Q2; P9, P12, P14, P22 are the other four). <c>Player.ReviveBeforeCombatEnd</c>
    /// (<c>Player.cs:821-827</c>: <c>if (Creature.IsDead) { await CreatureCmd.Heal(Creature, 1m); }</c>)
    /// exists for genuine co-op — if a teammate died mid-fight but the party still won, don't leave
    /// them dead for the next room — and fires for every entry in that list, including the Ghost,
    /// which <c>IsPlayer</c> but is very much supposed to stay dead: its death is how this fight
    /// was won. Skip entirely for the Ghost, the same shape as P9/P22; the second loop in this same
    /// method (<c>player2.AfterCombatEnd()</c>, <c>:995-998</c> — power/Block teardown, not a
    /// revive) is untouched since running it for the Ghost too is harmless (its whole
    /// <c>GhostSession</c> is about to be disposed regardless) and no live failure was observed
    /// there.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.ReviveBeforeCombatEnd))]
    private static class P25_SkipGhostReviveOnCombatVictory
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, ref Task __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostPlayer(__instance))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
    }

    /// <summary>
    /// P8: <c>MapPointHistoryEntry.GetEntry(ulong)</c> throws <c>InvalidOperationException</c> for
    /// any player id not already in the run's history stats — confirmed live (2026-09-02): the Ghost
    /// hits this the moment it takes any unblocked damage, because
    /// <c>CreatureCmd.Damage</c> (<c>CreatureCmd.cs:331-335</c>) calls
    /// <c>receiver.Player.RunState.CurrentMapPointHistoryEntry.GetEntry(receiver.Player.NetId).
    /// DamageTaken += ...</c> whenever <c>damage &gt; 0</c>, and the Ghost was never added to
    /// <c>RunState.CreateForNewRun</c>'s players (by design). That exception faults the whole
    /// <c>PlayCardAction</c>, which is why the triggering attack card is left stuck in the Play pile
    /// forever — <c>OnPlayWrapper</c> never reaches its own result-pile step. Supply a detached
    /// <see cref="GhostPartyMember.HistoryEntry"/> instead of the real lookup — PLAN.md M9c: one per
    /// party member, looked up by the requested <c>playerId</c>, since each Ghost has its own NetId.
    /// </summary>
    [HarmonyPatch(typeof(MapPointHistoryEntry), nameof(MapPointHistoryEntry.GetEntry))]
    private static class P8_SupplyGhostHistoryEntry
    {
        [HarmonyPrefix]
        private static bool Prefix(ulong playerId, ref PlayerMapPointHistoryEntry __result)
        {
            if (GhostSession.Current is not { } session
                || session.Party.FirstOrDefault(m => m.Player.NetId == playerId) is not { } member)
            {
                return true;
            }
            __result = member.HistoryEntry;
            return false;
        }
    }

    /// <summary>
    /// P9: native <c>CombatManager.SetupPlayerTurn</c> (<c>CombatManager.cs:629-676</c>, private) is
    /// only *intended* to run for the human side, but its caller's <c>playersStartingTurn</c> list
    /// (<c>CombatManager.cs:446</c>: <c>_state.Players.ToList()</c> whenever <c>CurrentSide ==
    /// CombatSide.Player</c>) is built from <c>CombatState.Players</c>, which is <c>IsPlayer</c>-
    /// derived, not Side-filtered (ENGINE-NOTES.md §7 Q2) — so it already includes the Ghost
    /// regardless of whose turn it actually is. Confirmed live: this gave the Ghost a full, spurious
    /// extra turn-setup (energy reset, hand draw, every associated hook including
    /// <c>Hook.AfterPlayerTurnStart</c>) during the *human's own turn*, on top of the real one
    /// <c>GhostTurnController</c> runs during the Ghost's own turn — observed as
    /// <c>CrimsonMantlePower</c>'s self-damage/Block firing twice per round, once at the wrong time.
    /// Skip entirely for the Ghost; <c>GhostTurnController</c> already replicates this exact sequence
    /// (ENGINE-NOTES.md §9) at the correct time, so nothing is lost.
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), "SetupPlayerTurn")]
    private static class P9_SkipGhostNativeTurnSetup
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player, ref Task __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostPlayer(player))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
    }

    /// <summary>
    /// P10: second attempt at the bug P9 (original numbering) tried and reverted — narrower this
    /// time. Every one of the 17 cards/relics audited in ENGINE-NOTES.md §0 M2 section reads
    /// <c>CombatState.HittableEnemies</c> specifically (never the raw <c>Allies</c>/<c>Enemies</c>
    /// directly), and — confirmed by direct read — <c>HittableEnemies</c> is not on the real
    /// damage-resolution path at all: the one place core engine code reads it,
    /// <c>Hook.cs:1531-1548</c> (inside `ModifyDamage`'s multi-target aggregation), only runs when
    /// <c>target == null &amp;&amp; previewMode == CardPreviewMode.MultiCreatureTargeting</c> — a
    /// UI hover-preview path, never hit during actual resolution
    /// (`CreatureCmd.Damage` calls `Hook.ModifyDamage` with `CardPreviewMode.None`, confirmed by the
    /// full read that found the P8 bug). `CreatureCmd.cs`/`DamageCmd.cs`/`AttackCommand.cs` do not
    /// read `HittableEnemies` at all (confirmed by grep across all three). The previous attempt
    /// patched `Allies` and `Enemies` directly — both read in many more places across the engine than
    /// just these 17 classes (`WhisperingEarring`, `GalvanicPower`, `VitalSparkPower`, a `Creature.Kill`
    /// cleanup check, ENGINE-NOTES.md §7 Q2) — and the regression it caused was never root-caused
    /// before reverting. Patching only `HittableEnemies`, and computing the flipped result directly
    /// from the already-public <c>CombatState.Creatures</c> (no reflection needed, unlike the reverted
    /// attempt), touches nothing else `Allies`/`Enemies` readers depend on.
    /// <b>Revised 2026-09-02</b>: confirmed live that P10 alone did NOT fix RedMask — the original
    /// guard (<c>CombatManager.IsExecutingCardOrPotionEffect</c> only) never becomes true for
    /// <c>RedMask.BeforeSideTurnStart</c>, a relic turn-hook, not a card/potion effect (see P11). The
    /// guard below adds <see cref="GhostHookOwnerScope"/> as a second, independent way to recognize
    /// "the Ghost is the implicit actor right now."
    /// </summary>
    [HarmonyPatch(typeof(CombatState), nameof(CombatState.HittableEnemies), MethodType.Getter)]
    private static class P10_ActorRelativeHittableEnemies
    {
        [HarmonyPostfix]
        private static void Postfix(CombatState __instance, ref IReadOnlyList<Creature> __result)
        {
            if (GhostSession.Current is not { } session
                || !ReferenceEquals(session.GhostPlayer.Creature.CombatState, __instance)
                || !(CombatManager.Instance.IsExecutingCardOrPotionEffect(session.GhostPlayer)
                    || ReferenceEquals(GhostHookOwnerScope.Current, session.GhostPlayer.Creature)))
            {
                return;
            }
            CombatSide ghostSide = session.GhostPlayer.Creature.Side;
            __result = __instance.Creatures.Where(c => c.Side != ghostSide && c.IsHittable).ToList();
        }
    }

    /// <summary>
    /// P11: feeds <see cref="GhostHookOwnerScope"/> from the two generic, content-agnostic
    /// chokepoints <c>Hook.cs</c> uses for every relic/power lifecycle hook that is not part of a
    /// card/potion play — <c>BeforeSideTurnStart</c> (<c>Hook.cs:1144-1158</c>), <c>AfterDeath</c>
    /// and <c>AfterDiedToDoom</c> all construct a
    /// <c>HookPlayerChoiceContext(AbstractModel, ulong, ICombatState, GameActionType)</c> per
    /// listener, then call <c>model.InvokeExecutionFinished()</c> once that listener's hook
    /// <c>Task</c> completes (directly in the loop, and again — harmlessly, <see cref="GhostHookOwnerScope.Pop"/>
    /// is a no-op the second time — via <c>HookPlayerChoiceContext.ExecuteTaskThenInvokeExecutionFinished</c>).
    /// The constructor (<c>HookPlayerChoiceContext.cs:70-89</c>) already resolves "owner Player" for
    /// every model kind (<c>CardModel</c>/<c>RelicModel</c>/<c>PotionModel</c>/<c>AfflictionModel</c>/
    /// <c>EnchantmentModel</c>/<c>PowerModel</c>) into its own public <c>Owner</c> property — reused
    /// here via that property rather than recomputed, so this patch stays a thin push/pop and does not
    /// special-case any relic/power by name (RedMask is one instance of the shape, not the target).
    /// Root cause this fills the gap for: <c>RedMask.BeforeSideTurnStart</c>
    /// (<c>RedMask.cs:23-30</c>) evaluates <c>combatState.HittableEnemies</c> as a plain argument
    /// expression, synchronously, before its own first <c>await</c> — <c>CombatManager
    /// .IsExecutingCardOrPotionEffect</c> is false at that moment (no card/potion is being played),
    /// so P10's original guard never activated for it, confirmed live after P10 alone shipped.
    /// </summary>
    [HarmonyPatch(typeof(HookPlayerChoiceContext), MethodType.Constructor, typeof(AbstractModel), typeof(ulong), typeof(ICombatState), typeof(GameActionType))]
    private static class P11_TrackHookOwner_Push
    {
        [HarmonyPostfix]
        private static void Postfix(HookPlayerChoiceContext __instance, AbstractModel source)
        {
            if (GhostSession.Current is null)
            {
                return;
            }
            GhostHookOwnerScope.Push(source, __instance.Owner?.Creature);
        }
    }

    /// <summary>P11 (continued): pop side of the push in <see cref="P11_TrackHookOwner_Push"/>.</summary>
    [HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.InvokeExecutionFinished))]
    private static class P11_TrackHookOwner_Pop
    {
        [HarmonyPrefix]
        private static void Prefix(AbstractModel __instance)
        {
            if (GhostSession.Current is null)
            {
                return;
            }
            GhostHookOwnerScope.Pop(__instance);
        }
    }

    /// <summary>
    /// P12: confirmed live (2026-09-02) — StoneArmor granted the Ghost 12 Plated Armor instead of 4.
    /// Root cause: <c>PowerCmd.Apply</c>'s multiplayer-scaling branch (<c>PowerCmd.cs:128</c>:
    /// <c>if (combatState.Players.Count &gt; 1 &amp;&amp; (target.IsPrimaryEnemy ||
    /// target.IsSecondaryEnemy) &amp;&amp; power.ShouldScaleInMultiplayer)</c>) exists for real co-op
    /// runs (2+ human players sharing a combat) and inflates powers like <c>PlatingPower</c>
    /// (<c>PlatingPower.cs:85-88</c>: <c>((Players.Count - 1) * 2 + 1) * amount</c>) accordingly. In a
    /// Ghost Duel, <c>CombatState.Players</c> is <c>IsPlayer</c>-derived (ENGINE-NOTES.md §7 Q2) and
    /// counts the Ghost as a second "player", so this combat is misread as 2-human co-op:
    /// <c>((2-1)*2+1) * 4 = 12</c>, exactly the observed value. There is genuinely only one real human
    /// in this combat, so multiplayer-scaling should never fire here at all, for any power, regardless
    /// of which side it targets — this is not a per-power bug and must not be fixed per-power (that
    /// would be exactly the content-by-id special-casing CLAUDE.md forbids). <c>PowerCmd.Apply</c> is
    /// <c>async</c> (compiled to a state-machine <c>MoveNext</c>, located via its
    /// <see cref="AsyncStateMachineAttribute"/> rather than a guessed compiler-generated name, so a
    /// game update that changes this fails loudly at load); a narrow transpiler ANDs a zero-argument,
    /// session-only guard onto the existing <c>power.ShouldScaleInMultiplayer</c> read (matched by
    /// <c>MethodInfo</c> operand, not IL offsets — the branch's other two conjuncts, and every other
    /// caller of this method outside a Ghost Duel combat, are untouched). Per CLAUDE.md's "patch the
    /// consuming call site, not an `AsyncLocal`-redefined property" guidance: this targets the one
    /// call site all multiplayer-scaling funnels through, not <c>CombatState.Players</c> itself (read
    /// pervasively elsewhere for legitimate turn/hand-draw purposes where counting the Ghost is
    /// correct) and not each power's <c>GetScaledAmountForMultiplayer</c> override (content-specific).
    /// Known NOT fixed by this patch: <c>PlatingPower.AfterApplied</c> (<c>PlatingPower.cs:29-36</c>)
    /// separately sets its per-turn decrement from <c>RunState.Players.Count</c>, unconditionally, so
    /// Stone Armor's block will still decay at 2/turn instead of the correct 1/turn in a Ghost Duel —
    /// a quieter, secondary consequence of the same root cause, not yet fixed (no common non-per-power
    /// chokepoint found for it), noted here rather than silently missed.
    /// <b>PLAN.md M9 gap, also noted rather than silently missed</b>: this guard is still a blunt
    /// "any Ghost session at all" check, unconditional on real human count. For a genuine N-human
    /// party (M9), native multiplayer scaling is *supposed* to apply for the real humans — this patch
    /// currently suppresses it for them too, whenever any Ghost is present, which under-scales rather
    /// than over-scales. The correct fix needs the actual human count *excluding* Ghosts at the call
    /// site, which this zero-argument transpiled guard doesn't have access to (it only ANDs a bool
    /// onto the existing getter read) — fixing this would need a differently-shaped transpiler
    /// injection that also captures <c>combatState</c>/<c>target</c>, not attempted here.
    /// </summary>
    [HarmonyPatch]
    private static class P12_SuppressMultiplayerScalingForGhostDuel
    {
        private static MethodBase TargetMethod()
        {
            // AccessTools.Method(type, name) alone is ambiguous here: PowerCmd also declares a
            // generic Apply<T>(PlayerChoiceContext, IEnumerable<Creature>, decimal, Creature,
            // CardModel, bool) overload for multi-creature targeting (confirmed live 2026-09-02 —
            // Harmony.PatchAll threw AmbiguousMatchException at mod load, silently aborting every
            // patch after this one in assembly scan order). Disambiguate by exact parameter list.
            MethodInfo outer = AccessTools.Method(
                typeof(PowerCmd),
                nameof(PowerCmd.Apply),
                new[] { typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool) })
                ?? throw new InvalidOperationException("P12: PowerCmd.Apply(PlayerChoiceContext, PowerModel, Creature, decimal, Creature, CardModel, bool) not found - game update?");
            Type stateMachine = outer.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                ?? throw new InvalidOperationException("P12: PowerCmd.Apply is no longer an async state machine - game update?");
            return AccessTools.Method(stateMachine, "MoveNext")
                ?? throw new InvalidOperationException("P12: MoveNext not found on PowerCmd.Apply's state machine - game update?");
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo shouldScaleGetter = AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.ShouldScaleInMultiplayer))
                ?? throw new InvalidOperationException("P12: PowerModel.ShouldScaleInMultiplayer getter not found - game update?");
            MethodInfo guard = AccessTools.Method(typeof(P12_SuppressMultiplayerScalingForGhostDuel), nameof(ShouldScale));

            CodeMatcher matcher = new(instructions);
            matcher.MatchStartForward(new CodeMatch(instruction => instruction.Calls(shouldScaleGetter)));
            if (!matcher.IsValid)
            {
                throw new InvalidOperationException("P12: PowerCmd.Apply no longer reads ShouldScaleInMultiplayer at the expected point - game update?");
            }
            matcher.Advance(1);
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call, guard));
            return matcher.InstructionEnumeration();
        }

        private static bool ShouldScale(bool original) => original && GhostSession.Current is null;
    }

    /// <summary>
    /// P13: confirmed live (2026-09-02) — a Ghost with <c>VeryHotCocoa</c> (turn-1-only, +4 energy via
    /// <c>AfterSideTurnStart</c>) never actually got the bonus; playing only 2 of 5 drawn cards on
    /// round 1 was the visible symptom. Root cause, confirmed by reading
    /// <c>CombatManager.StartTurn</c> in full (<c>CombatManager.cs:440-604</c>): native
    /// <c>Hook.AfterSideTurnStart</c> (<c>:522</c>) fires unconditionally for whichever side is
    /// starting, *before* the method branches into the enemy-turn path (<c>:588-604</c>) that
    /// eventually reaches <c>Creature.TakeTurn</c> (P2). For a human, the per-player
    /// <c>SetupPlayerTurn</c> loop (<c>:509-519</c>, hard-gated to <c>CurrentSide == Player</c>) — which
    /// does the energy reset — runs *before* line 522, so a turn-1 energy relic's grant lands on top of
    /// an already-reset pool. That per-player loop never runs at all for the Ghost's own (Enemy-side)
    /// turn, so nothing had reset its energy by the time line 522 fired; <c>GhostTurnController</c>
    /// used to do that reset itself, but only once <c>RunTurnAsync</c> ran — *after* line 522, via
    /// <c>Creature.TakeTurn</c> — so its `ResetEnergy()` call (`Energy = MaxEnergy`, an absolute
    /// assignment, not additive) silently discarded whatever `VeryHotCocoa` had just granted.
    /// <c>GhostTurnController.SetupTurn</c> (moved out of <c>RunTurnAsync</c>, see its own doc comment)
    /// now runs from a prefix on this exact hook — the one place a human's and the Ghost's sequencing
    /// diverge — restoring the same relative order a human gets, without special-casing
    /// <c>VeryHotCocoa</c> or any other energy relic by name. The prefix cannot simply run its own
    /// async setup then call <c>Hook.AfterSideTurnStart</c> again by name (that would recurse into this
    /// same patch); <see cref="Original"/> is a <c>HarmonyReversePatch</c> stub Harmony rewrites at
    /// patch time into the true, unpatched method body, giving a safe way to call through.
    /// <b>P31, folded in here rather than as a separate patch</b>: confirmed live — a Ghost with
    /// <c>NoxiousFumesPower</c> (<c>NoxiousFumesPower.cs:26-44</c>) poisoned *itself* instead of the
    /// human. Root cause: <c>NoxiousFumesPower.AfterSideTurnStart</c> reads
    /// <c>base.CombatState.HittableEnemies</c>, which resolves from the fixed, non-relative
    /// <c>CombatState.Enemies</c> (<c>CombatState.cs:65,142,237-238</c> — literally "whichever side is
    /// not <c>CombatSide.Player</c>", i.e. the Ghost's own side). P10 already flips
    /// <c>HittableEnemies</c> for a Ghost-owned reader, but only while
    /// <c>CombatManager.IsExecutingCardOrPotionEffect</c> is true (a card/potion body) or
    /// <see cref="GhostHookOwnerScope.Current"/> is set (fed by P11 for <c>BeforeSideTurnStart</c>/
    /// <c>AfterDeath</c>/<c>AfterDiedToDoom</c>, the only three hooks that construct a
    /// <c>HookPlayerChoiceContext</c>). <c>AfterSideTurnStart</c>/<c>AfterSideTurnStartLate</c>
    /// (<c>Hook.cs:1163-1175</c>) call <c>model.AfterSideTurnStart(...)</c> directly with no such
    /// context object, so neither guard was ever true for Noxious Fumes — the exact same shape of gap
    /// P11 already fixed for <c>RedMask.BeforeSideTurnStart</c>, just on a hook P11 doesn't cover.
    /// <b>Q3</b> (narrower target considered): a per-listener fix mirroring P11 exactly would need a
    /// transpiler on this method's compiler-generated async state machine (no constructor call to
    /// piggyback on here) — rejected as more invasive than the confirmed risk justifies. The two
    /// side-turn-start hooks audited so far that read <c>Enemies</c>/<c>Allies</c>/<c>HittableEnemies</c>
    /// (<c>NoxiousFumesPower.cs:28</c>, <c>RedMask.cs:25</c>) both self-filter with
    /// <c>participants.Contains(Owner)</c> before touching any of those collections — a non-Ghost
    /// listener already returns before this scope could affect it — so flipping for the *whole*
    /// <c>side == Enemy</c> dispatch (this prefix already only enters that branch), rather than
    /// per-listener, is safe in practice without the extra machinery. This wrap wholly contains
    /// <see cref="Original"/>'s real awaited body (both the <c>AfterSideTurnStart</c> and
    /// <c>AfterSideTurnStartLate</c> loops), so the pop always runs after every listener's hook Task —
    /// including ones with their own internal <c>await</c>s — has actually finished, not merely after
    /// the async method's first <c>await</c> returns control (the standard Harmony async-postfix
    /// pitfall this file already avoids elsewhere via the same reverse-patch-and-await-through shape).
    /// Uses <see cref="GhostHookOwnerScope.PushRaw"/>/<see cref="GhostHookOwnerScope.PopRaw"/>, not the
    /// model-keyed <see cref="GhostHookOwnerScope.Push"/>/<see cref="GhostHookOwnerScope.Pop"/> P11 uses
    /// — see that method's own doc comment for why the two never collide.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnStart))]
    private static class P13_GhostEnergySetupBeforeAfterSideTurnStart
    {
        [HarmonyPrefix]
        private static bool Prefix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants, ref Task __result)
        {
            if (GhostSession.Current is not { } session
                || side != CombatSide.Enemy
                || !session.Party.Any(m => participants.Contains(m.Player.Creature)))
            {
                return true;
            }
            __result = RunSetupThenOriginal(combatState, side, participants, session);
            return false;
        }

        private static async Task RunSetupThenOriginal(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants, GhostSession session)
        {
            // PLAN.md M9c: every Ghost party member present in this side-turn-start batch gets its
            // own setup, in the party's own stable order — still exactly one iteration for the
            // still-primary 1v1 case.
            foreach (GhostPartyMember member in session.Party)
            {
                if (participants.Contains(member.Player.Creature))
                {
                    await GhostTurnController.SetupTurn(member.Player, combatState, session);
                }
            }
            // P31: flip Enemies/Allies-derived HittableEnemies reads for the whole of this side's
            // AfterSideTurnStart + AfterSideTurnStartLate dispatch — see this class's own doc comment.
            Creature? previousOwnerScope = GhostHookOwnerScope.PushRaw(session.GhostPlayer.Creature);
            try
            {
                await Original(combatState, side, participants);
            }
            finally
            {
                GhostHookOwnerScope.PopRaw(previousOwnerScope);
            }
        }

        [HarmonyReversePatch]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task Original(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
        {
            throw new NotImplementedException("P13: replaced by HarmonyReversePatch at patch time - should never actually run.");
        }
    }

    /// <summary>
    /// P14: confirmed live (2026-09-02) — a Ghost with <c>PlatingPower</c> (Stone Armor) had its Block
    /// grant fire twice per round: once (correctly) at the end of the Ghost's own turn, once more
    /// (spuriously) at the end of the *human's* turn. Not a decay/decrement question (that part,
    /// <c>PlatingPower.AfterSideTurnStart</c>, already reads a correctly Side-filtered participants
    /// list, per ENGINE-NOTES.md §0) — this is the separate Block-granting hook,
    /// <c>PlatingPower.BeforeSideTurnEndEarly</c> (<c>PlatingPower.cs:61-68</c>), which checks only
    /// <c>participants.Contains(base.Owner)</c> with no side filter of its own. Root cause, confirmed
    /// by reading <c>CombatManager.cs</c> in full: <c>EndPlayerTurnPhaseOneInternal</c> and
    /// <c>EndPlayerTurnPhaseTwoInternal</c> (both hard-gated to <c>CurrentSide == Player</c>, i.e. only
    /// ever called during the *human's* turn-end) each build `playersEndingTurn` from
    /// `_state.Players.ToList()`, the same <c>IsPlayer</c>-derived, not Side-filtered, list P9 already
    /// found and fixed for turn *start* (ENGINE-NOTES.md §7 Q2) — and pass
    /// `playersEndingTurn.Select(p => p.Creature)` straight through as `participants` to the shared
    /// dispatchers. That list includes the Ghost even though it is unambiguously not the Ghost's turn
    /// ending. The Ghost's own actual turn-end (<c>EndEnemyTurnInternal</c>) already builds its
    /// `enemies` list from `_state.CreaturesOnCurrentSide` — genuinely Side-filtered — so that call is
    /// correct and untouched. Rather than patch each call site (two async methods) or special-case
    /// <c>PlatingPower</c>, this filters the Ghost out of `participants` at the one place both mis-
    /// scoped calls funnel through — the shared, non-async dispatcher methods themselves — fixing the
    /// whole class of "Ghost-owned relic/power reacts to the human's turn-end as if it were its own"
    /// bugs generically, the same way P9 did for turn-start.
    /// <b>Revised 2026-09-02</b>: an intermediate Steam auto-update briefly renamed these to
    /// <c>Hook.BeforeSideTurnEnd</c>/<c>Hook.AfterSideTurnEnd</c>; a subsequent update the same
    /// evening reverted to the original names below, which is what the game's standard release build
    /// (Steam auto-updates now paused by the user, ENGINE-NOTES.md M3 section) actually ships. Back to
    /// the original names — same body/order/signature throughout, name churn only.
    /// </summary>
    [HarmonyPatch]
    private static class P14_ExcludeGhostFromHumanTurnEndParticipants
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Hook), nameof(Hook.BeforeTurnEnd))
                ?? throw new InvalidOperationException("P14: Hook.BeforeTurnEnd not found - game update?");
            yield return AccessTools.Method(typeof(Hook), nameof(Hook.AfterTurnEnd))
                ?? throw new InvalidOperationException("P14: Hook.AfterTurnEnd not found - game update?");
        }

        [HarmonyPrefix]
        private static void Prefix(CombatSide side, ref IEnumerable<Creature> participants)
        {
            if (GhostSession.Current is not { } session || side != CombatSide.Player)
            {
                return;
            }
            participants = participants.Where(c => !session.IsGhostCreature(c)).ToList();
        }
    }

    /// <summary>
    /// P15: M3's queued-damage core (ENGINE-NOTES.md M3 section, COMBAT-RULES.md §5 "Weak — late-
    /// bound to the attacker"). Forces <c>WeakPower.ModifyDamageMultiplicative</c> to return 1 while
    /// <see cref="GhostWeakSuppressionScope"/> is active, so P17 can compute a
    /// <see cref="QueuedDamagePacket.LockedAmount"/> that honestly excludes Weak — never baked in and
    /// later divided out (POSTMORTEM F14), genuinely never folded in for this one call. Named directly
    /// because this project's own duel design names Weak specifically as needing this treatment, not
    /// because a card/relic/character is being special-cased (CLAUDE.md's forbidden pattern is about
    /// routing around a bug per content item, not implementing one documented, universal status
    /// effect's own rule). No real state is mutated — only one pure function's return value, for the
    /// exact duration of the call the scope brackets.
    /// </summary>
    [HarmonyPatch(typeof(WeakPower), nameof(WeakPower.ModifyDamageMultiplicative))]
    private static class P15_SuppressWeakForLockedDamageComputation
    {
        [HarmonyPostfix]
        private static void Postfix(ref decimal __result)
        {
            if (GhostSession.Current is null || !GhostWeakSuppressionScope.IsActive)
            {
                return;
            }
            __result = 1m;
        }
    }

    /// <summary>
    /// P28: generalizes P15 from Weak to Strength, per the user's 2026-09-04 request. Confirmed by
    /// reading <c>StrengthPower.cs</c> and <c>Hook.ModifyDamageInternal</c>: Strength is folded in via
    /// exactly the same kind of per-power hook method as Weak (<c>ModifyDamageAdditive</c> — an
    /// additive-pass listener, run before the multiplicative pass Weak/Vulnerable participate in), not
    /// via `AttackCommand`/`CalculatedDamageVar` reading `Amount` inline. Strength is a property of
    /// the *dealer*, exactly like Weak and unlike Vulnerable — a Mangle-style Strength loss (or any
    /// gain) on an attacker after their attack is already queued must still affect that queued packet
    /// when it resolves, or the debuff can only ever hit cards not yet played, a full turn later than
    /// intended (confirmed live: Mangle played by the Ghost mid-turn only reduced the human's *next*
    /// turn, not the human's already-queued attack about to resolve that same Ghost turn). Forces
    /// <c>StrengthPower.ModifyDamageAdditive</c> to return 0 while
    /// <see cref="GhostStrengthSuppressionScope"/> is active, so P17 can compute a
    /// <see cref="QueuedDamagePacket.LockedAmount"/> that excludes Strength the same way it already
    /// excludes Weak; <see cref="QueuedDamagePacket.DisplayAmount"/> re-adds the dealer's live Strength
    /// (scaled by the target's locked Vulnerable multiplier) at display/resolution time. Confirmed by
    /// reading every subclass: none of the 14 <c>TemporaryStrengthPower</c> subclasses (Mangle, Crush
    /// Under, Dying Star, Enfeebling Touch, Dark Shackles, Monarch's Gaze's strength-down half, etc.)
    /// override a damage hook themselves — they all route through this one <c>StrengthPower</c> method,
    /// so this single patch covers all of them without naming any by id. <c>Debilitate</c> also rides
    /// along for free, since it amplifies Weak's/Vulnerable's multiplier from inside their own hooks,
    /// not via a separate damage hook of its own. Confirmed NOT applicable to Flanking/Knockdown
    /// (target-side, like Vulnerable — they check `target == base.Owner`, the opposite of Strength's
    /// `dealer == base.Owner`) or to Strangle/Oblivion/Sic Em (reactive triggers with no
    /// <c>ModifyDamage*</c> hook at all, unrelated to the queued-damage lock).
    /// </summary>
    [HarmonyPatch(typeof(StrengthPower), nameof(StrengthPower.ModifyDamageAdditive))]
    private static class P28_SuppressStrengthForLockedDamageComputation
    {
        [HarmonyPostfix]
        private static void Postfix(ref decimal __result)
        {
            if (GhostSession.Current is null || !GhostStrengthSuppressionScope.IsActive)
            {
                return;
            }
            __result = 0m;
        }
    }

    /// <summary>
    /// P16: the other half of P15/P17. When resolving a queued packet, P17's caller
    /// (<c>GhostDamageQueue.ResolvePendingFor</c>) calls the real <c>CreatureCmd.Damage</c> with the
    /// packet's already-finalized amount — but that method's own internal <c>Hook.ModifyDamage</c>
    /// call (<c>CreatureCmd.cs:283</c>) would otherwise re-apply Vulnerable/Weak/Strength/etc a second
    /// time on top of an already-modified number. While <see cref="GhostQueuedResolutionScope"/> is
    /// active, this makes <c>Hook.ModifyDamage</c> return its input unchanged — the "explicit token
    /// carried through the resolution call" COMBAT-RULES.md §5 asks for, in place of the old mod's
    /// ambient <c>(target, dealer, amount, props, cardSource)</c> tuple-matching (POSTMORTEM F14),
    /// which could collide or miss. Everything else in <c>CreatureCmd.Damage</c> — Block subtraction,
    /// HP loss, hook order, VFX, kill-check, history — still runs natively, exactly once.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    private static class P16_SuppressModifierRecomputationAtResolution
    {
        [HarmonyPrefix]
        private static bool Prefix(decimal damage, out IEnumerable<AbstractModel> modifiers, ref decimal __result)
        {
            modifiers = Array.Empty<AbstractModel>();
            if (GhostSession.Current is null || !GhostQueuedResolutionScope.IsActive)
            {
                return true;
            }
            __result = damage;
            return false;
        }
    }

    /// <summary>
    /// P17: queues direct damage instead of resolving it immediately, per COMBAT-RULES.md §2-§3 (only
    /// direct damage queues; Block/status application/Strength/etc. are unaffected) — <b>both
    /// directions</b> (§3: "Both directions"), not just the Ghost's own attacks: the human's damage
    /// must queue too, so it resolves against the Ghost's *post-turn* Block/HP (§2 step 5) instead of
    /// immediately, and so a future AI (M5) can read the incoming queued amount when deciding how much
    /// Block to leave up. Scoped to this Ghost Duel's own combat only
    /// (<c>dealer.CombatState == the Ghost's CombatState</c> — a plain 1v1, so "either side of this
    /// combat" is unambiguous and does not require special-casing which side is the Ghost), excluding
    /// self-damage (<c>target != dealer</c> — Bloodletting/Offering-style resource cards stay
    /// immediate, matching COMBAT-RULES.md §3's "everything else" bucket in spirit even though
    /// self-damage isn't named explicitly). Confirmed via full reads of <c>CreatureCmd.Damage</c> and
    /// <c>ThornsPower.BeforeDamageReceived</c> that this exact 6-parameter overload
    /// (<c>CreatureCmd.cs:240</c>) is the one true chokepoint — disambiguated by explicit parameter
    /// types since <c>CreatureCmd</c> has ten other overloads named <c>Damage</c>.
    /// <see cref="GhostQueuedResolutionScope"/> guards against re-queuing the very call P16/
    /// <c>GhostDamageQueue</c> makes to resolve a packet later.
    /// <b>Revised 2026-09-03</b>: originally also required <c>cardSource != null</c>, deliberately
    /// leaving orb/relic/power/pet-sourced direct damage immediate "until a concrete card/relic in the
    /// test decks proves the gap." Confirmed live (Defect's Lightning orb): orb damage funnels through
    /// this exact same overload — <c>LightningOrb.ApplyLightningDamage</c> calls the 5-argument
    /// <c>CreatureCmd.Damage(..., dealer)</c> overload (<c>LightningOrb.cs:58</c>), which itself just
    /// forwards to this one with <c>cardSource: null</c> (<c>CreatureCmd.cs:141-144</c>) — so the old
    /// <c>cardSource is null</c> clause was silently exempting orb damage from queueing entirely, not
    /// narrowing to "card-sourced," and COMBAT-RULES.md §3 already names orbs as queueable ("Both
    /// directions"). Dropped the clause; <c>QueuedDamagePacket.CardSource</c> was already nullable and
    /// <c>GhostDamageQueue.ResolvePendingFor</c> already passes it straight back into the same
    /// overload, which itself already tolerates <c>null</c> — no downstream change needed.
    /// <b>Revised 2026-09-03 (Necrobinder)</b>: pet-dealt damage (Unleash's Osty attack,
    /// <c>AttackCommand.FromOsty</c>) also flows through this exact chokepoint with <c>dealer</c> set
    /// to the pet's own <c>Creature</c>, not its owner's — already queued correctly by this gate
    /// (<c>dealer.CombatState</c> still matches), but previously left permanently unresolved since
    /// neither <c>ResolvePendingFor</c> call site's <c>dealer</c> reference-equaled a pet. Fixed at
    /// <c>GhostDamageQueue.BelongsTo</c>, not here — this patch's own gate needed no change once that
    /// was understood. Relic/power-sourced direct damage (not pet/orb) remains deliberately out of
    /// scope until its own concrete failing observation.
    /// <b>Revised 2026-09-04</b>: per the user's request, only the Ghost's own direct damage stays
    /// queued going forward — the human's own damage now applies immediately, matching normal STS
    /// (COMBAT-RULES.md §2-§3, rewritten). Added <c>!session.IsGhostOwnedDealer(dealer)</c> to this
    /// gate's early-return list: a human (or a human's pet) dealing damage now falls straight through
    /// to the real, completely untouched native path, exactly like any other STS fight. Everything
    /// else about this patch — Weak/Strength suppression, the Ghost's own queueing — is unchanged.
    /// <b>Revised 2026-09-04 (Brand)</b>: confirmed live from <c>ghostduel.log</c> — the Ghost's own
    /// HP was unchanged (63/80 before and after) across playing <c>BRAND</c>
    /// (<c>Brand.cs:41</c>: 1 unblockable self-damage, then gains Strength and exhausts a card). Root
    /// cause: the loop below already special-cased <c>target == dealer</c> to skip queueing it (per
    /// the "excluding self-damage" note above), but the whole method still unconditionally returned
    /// <c>false</c> afterward — so a self-damage call never reached the real native
    /// <c>CreatureCmd.Damage</c> at all, and the <c>results</c> entry added for it was a bare
    /// <c>new DamageResult(target, props)</c> with no actual HP-loss computation behind it: exactly
    /// the "fabricated result to satisfy a caller" CLAUDE.md's code-shape rule forbids. The Ghost paid
    /// none of Brand's cost while still getting its full benefit. Fixed by checking, before entering
    /// the interception logic at all, whether every target in this call *is* the dealer — no known
    /// STS card mixes self-damage with damage to an opponent in one <c>Damage(...)</c> call (Brand/
    /// Bloodletting/Offering-style effects all target only themselves), so this is not a partial fix
    /// for a case that's been observed: a purely-self-damage call now falls all the way through to
    /// <c>return true</c>, exactly like a human's own damage does, letting the untouched native path
    /// (relic hooks like Tungsten Rod included) apply it for real.
    /// <b>Revised 2026-09-04 (Thorns)</b>: confirmed live/reported — the Ghost's Thorns retaliation
    /// (<c>ThornsPower.BeforeDamageReceived</c>, fires as a hook *inside* the same
    /// <c>CreatureCmd.Damage</c> call resolving the human's own attack landing on the Ghost — dealer =
    /// Ghost, target = the attacking human) was queuing instead of hitting back immediately. Queuing
    /// only ever made sense to telegraph a card the Ghost *chose* to play on *its own* turn
    /// (COMBAT-RULES.md §1-§2) — Thorns is a passive reaction to the human's own (now-immediate) attack
    /// landing, which only ever happens during the human's turn, so there is nothing to telegraph: the
    /// human already committed the action that triggered it. Generic fix, not a Thorns-specific one:
    /// added <c>dealer.CombatState.CurrentSide != CombatSide.Enemy</c> to this gate's early-return
    /// list — Ghost-dealt damage now only queues while it's genuinely the Ghost's *own* turn; any
    /// Ghost-dealt damage during the human's turn (Thorns today, any similar reactive power tomorrow)
    /// falls straight through to the untouched native path, same as a human's own damage already does.
    /// <b>Revised 2026-09-05 (Fistcuffs)</b>: confirmed live/reported — the Ghost Regent played
    /// FISTCUFFS (an attack that also gains Block equal to damage dealt) and gained no Block at all.
    /// Root cause, confirmed by reading <c>DamageResult.cs</c>: the <c>results.Add(new
    /// DamageResult(target, props))</c> below left <c>UnblockedDamage</c> at its default of <c>0</c> —
    /// this interception's whole point is to defer the *real* damage (and hence the real post-block
    /// figure) until later, but whatever called this <c>Damage(...)</c> overload still gets handed a
    /// <c>DamageResult</c> back *synchronously*, in the same card-play, and Fistcuffs' own "gain Block"
    /// step reads that returned figure immediately — not a later-replayed hook, so queuing the real
    /// resolution doesn't defer this step's read of it. Fixed by populating <c>UnblockedDamage</c> with
    /// <c>QueuedDamagePacket.DisplayAmount</c> (the same pre-block figure the queued-damage intent
    /// indicator already shows the player) instead of leaving it at a misleading absolute zero — a
    /// best-effort estimate, not the true post-resolution amount (this call site cannot know the
    /// target's Block at actual resolution time), documented as such rather than treated as fully
    /// solved. <c>WasFullyBlocked</c>/<c>BlockedDamage</c>/<c>WasTargetKilled</c> are deliberately left
    /// at their safe defaults (false/0/false) for the same reason — CLAUDE.md: surface what's
    /// unavailable rather than fabricate it, and these three would need genuine post-block knowledge to
    /// answer honestly, which <c>UnblockedDamage</c>'s pre-block estimate does not.
    /// </summary>
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel))]
    private static class P17_QueueGhostDirectDamage
    {
        [HarmonyPrefix]
        private static bool Prefix(IEnumerable<Creature> targets, decimal amount, ValueProp props, Creature dealer, CardModel? cardSource, ref Task<IEnumerable<DamageResult>> __result)
        {
            if (GhostSession.Current is not { } session
                || GhostQueuedResolutionScope.IsActive
                || dealer is null
                || dealer.CombatState is null
                || !ReferenceEquals(dealer.CombatState, session.GhostPlayer.Creature.CombatState)
                || !session.IsGhostOwnedDealer(dealer)
                || dealer.CombatState.CurrentSide != CombatSide.Enemy)
            {
                return true;
            }

            List<Creature> targetList = targets.ToList();
            if (targetList.Count > 0 && targetList.TrueForAll(target => ReferenceEquals(target, dealer)))
            {
                // Self-damage only (Brand, Bloodletting/Offering-style cards): nothing to telegraph
                // against an opponent, so let the real native path apply it for real instead of
                // fabricating an empty result and silently skipping the actual HP loss.
                return true;
            }

            List<DamageResult> results = new(targetList.Count);
            foreach (Creature target in targetList)
            {
                if (ReferenceEquals(target, dealer) || target.IsDead)
                {
                    results.Add(new DamageResult(target, props));
                    continue;
                }
                decimal lockedAmount;
                using (GhostWeakSuppressionScope.Enter())
                using (GhostStrengthSuppressionScope.Enter())
                using (GhostGuardedSuppressionScope.Enter())
                using (GhostTankSuppressionScope.Enter())
                {
                    lockedAmount = Hook.ModifyDamage(IRunState.GetFrom(new[] { dealer, target }), dealer.CombatState, target, dealer, amount, props, cardSource, ModifyDamageHookType.All, CardPreviewMode.None, out _);
                }
                decimal lockedVulnerableMultiplier = target.GetPower<VulnerablePower>()?.ModifyDamageMultiplicative(target, 1m, props, dealer, cardSource) ?? 1m;
                QueuedDamagePacket packet = new(dealer, target, lockedAmount, lockedVulnerableMultiplier, props, cardSource);
                session.DamageQueue.Enqueue(packet);
                // Best-effort figure for whatever downstream card effect reads this same call's
                // returned DamageResult synchronously (e.g. Regent's FISTCUFFS, "gain Block equal to
                // damage dealt") — see this method's own doc comment ("Revised 2026-09-05, Fistcuffs")
                // for why this is an estimate (pre-block, matching the queued-damage intent preview),
                // not the true post-resolution figure, which isn't known until this packet actually
                // resolves later.
                results.Add(new DamageResult(target, props) { UnblockedDamage = (int)decimal.Round(packet.DisplayAmount) });
                string dealerLabel = DescribeDealer(dealer, session);
                GhostLog.Queued(dealerLabel, cardSource?.Id.Entry ?? "<orb/other>", target.LogName, lockedAmount);
            }
            __result = Task.FromResult<IEnumerable<DamageResult>>(results);
            return false;
        }

        /// <summary>Labels a queued packet's dealer for <c>GhostLog</c>, correctly attributing a
        /// pet's damage (e.g. Osty's Unleash attack) to whichever side owns it, instead of the
        /// previous <c>dealer.Player?.NetId ?? 0</c> fallback, which always mislabeled a pet as
        /// "Human#0" regardless of which side actually owned it.</summary>
        private static string DescribeDealer(Creature dealer, GhostSession session)
        {
            if (session.FindByCreature(dealer) is { } ghostMember)
            {
                return $"Ghost#{ghostMember.Player.NetId}";
            }
            if (dealer.PetOwner is not null && session.FindByPlayer(dealer.PetOwner) is { } petOwnerMember)
            {
                return $"Ghost#{petOwnerMember.Player.NetId}'s {dealer.LogName}";
            }
            if (dealer.PetOwner is not null)
            {
                return $"Human#{dealer.PetOwner.NetId}'s {dealer.LogName}";
            }
            return $"Human#{dealer.Player?.NetId ?? 0}";
        }
    }

    /// <summary>
    /// P18: M6's status-expiry fix. Confirmed live (2026-09-03) — Red Mask's Weak on the human
    /// persisted through the human's *second* turn instead of decaying after the first, so a
    /// one-turn debuff had a two-turn effect. Root cause, confirmed from the engine's own doc
    /// comment on <c>PowerModel.SkipNextDurationTick</c> (<c>PowerModel.cs:242-245</c>): "enables the
    /// behavior of duration-type powers (Vulnerable, Weak, etc.) ticking down at the end of the
    /// *monster* side turn, but skipping the first tick if a *monster* applied the power to the
    /// player" — a native grace tick, set by <c>PowerCmd.Apply</c> whenever
    /// <c>target.Side == CombatSide.Player</c> (<c>PowerCmd.cs:144-147</c>), assuming debuffs land on
    /// the human mid-fight from a monster's own move and deserve one full turn before their first
    /// decay check. <c>WeakPower</c>/<c>VulnerablePower</c>/<c>FrailPower</c> (confirmed identical
    /// shape in all three, `Type => PowerType.Debuff`, `StackType => PowerStackType.Counter`,
    /// `AfterSideTurnEnd`: `if (side == CombatSide.Enemy) PowerCmd.TickDownDuration(this);`) all tick
    /// down at exactly one fixed, native checkpoint — the *Enemy* (Ghost) side's own turn end,
    /// regardless of who applied or who holds the stack. Red Mask applies Weak to the human at the
    /// very *start* of the Ghost's own turn (`BeforeSideTurnStart`) — immediately before that same
    /// turn's decay checkpoint — so the grace tick (meant to buy one honest extra turn) instead
    /// swallows the Ghost turn's own decay entirely, pushing the real first decay a full round later.
    /// This grace only ever fires for `target.Side == Player` — a debuff landing on the *Ghost*
    /// (e.g. Vulnerable from the human's Bash) never receives it and already decays on schedule at
    /// that same fixed checkpoint, which is the asymmetry the user asked to make consistent.
    /// <b>Per the user's explicit 2026-09-03 decision, recorded in COMBAT-RULES.md §6 as rule (a)</b>
    /// ("decay at the end of the *applying* side's turn") — for this duel that fixed checkpoint
    /// already *is* the applying side's (Ghost's) turn end for every debuff a Ghost-owned effect can
    /// apply, so the fix is exactly to stop granting the human-only grace, not to rebuild decay
    /// timing from scratch. A postfix on the one native chokepoint every debuff application already
    /// funnels through (<c>PowerCmd.Apply</c>, disambiguated from the generic <c>Apply&lt;T&gt;</c>
    /// overload by explicit parameter types per the P12 precedent) resets the flag the native method
    /// just set, gated only on <c>power.Type == PowerType.Debuff</c> and the target belonging to this
    /// Ghost Duel's own combat — no reference to <c>WeakPower</c>/<c>VulnerablePower</c>/
    /// <c>FrailPower</c> by name, so it applies uniformly to any current or future debuff sharing this
    /// shape, matching the user's "make similar stacked debuffs consistent" request generically
    /// rather than patching each power individually. Everything past that one flag — the actual
    /// decrement, removal at zero stacks, VFX — stays entirely native and untouched.
    /// </summary>
    [HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.Apply), typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool))]
    private static class P18_NoExtraGraceTickForDuelDebuffs
    {
        /// <summary>
        /// Fixed 2026-09-04 — confirmed live from the user's "Redmask no decay" log
        /// (<c>P26 DECAY-CHECK ... skipFlag=True</c> immediately after a fresh application, racing
        /// with an otherwise-identical later application where <c>skipFlag=False</c>): a plain
        /// <c>[HarmonyPostfix]</c> on an <c>async Task</c> method runs once the method's synchronous
        /// prefix returns its <c>Task</c> handle, at the first genuinely-asynchronous <c>await</c> —
        /// not once that <c>Task</c> actually finishes. <c>PowerCmd.Apply</c>'s own
        /// <c>SkipNextDurationTick = true</c> assignment (<c>PowerCmd.cs:144-147</c>) runs *later* in
        /// that same async body, after several awaited hooks — so the old plain-void postfix reset
        /// the flag to <c>false</c> and then, nondeterministically, lost a race against the native
        /// code's own <c>= true</c> assignment finishing after it. Only ever observable in Direction
        /// A (a debuff the Ghost applies to the human): the native flag is only ever set when
        /// <c>target.Side == CombatSide.Player</c>, so Direction B never had a flag to race over.
        /// Fixed the standard Harmony way for postfixing an async method: replace <c>__result</c>
        /// with a continuation that awaits the *real* original task first, so the fix-up genuinely
        /// runs after <c>Apply</c>'s entire body — including its own late
        /// <c>SkipNextDurationTick = true</c> — has finished.
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(PowerModel power, Creature target, ref Task __result)
        {
            if (GhostSession.Current is not { } session
                || power.Type != PowerType.Debuff
                || target.CombatState is null
                || !ReferenceEquals(target.CombatState, session.GhostPlayer.Creature.CombatState))
            {
                return;
            }
            __result = ResetGraceTickAfterApplyCompletes(__result, power, target);
        }

        /// <summary>
        /// Also fixes a second, independent issue: <c>PowerCmd.Apply</c>'s own `power`/`target`
        /// parameters are not necessarily the instance actually attached to the target.
        /// VulnerablePower/WeakPower/FrailPower don't override <c>InstanceType</c>, so all three use
        /// the default <c>PowerInstanceType.None</c> (<c>PowerModel.cs:144</c>) — a *stacking*
        /// application (this target already holds one) takes <c>Apply</c>'s early-return branch
        /// (<c>PowerCmd.cs:112-117</c>), which calls <c>ModifyAmount</c> on the *existing* instance
        /// found via <c>target.GetPower(basePower.Id)</c> and never touches the freshly-constructed
        /// `power` argument at all. Re-fetches the real attached instance instead of trusting the
        /// parameter.
        /// </summary>
        private static async Task ResetGraceTickAfterApplyCompletes(Task original, PowerModel power, Creature target)
        {
            await original;
            PowerModel? attached = target.GetPower(power.Id);
            if (attached is not null)
            {
                attached.SkipNextDurationTick = false;
            }
        }
    }

    /// <summary>
    /// P26: revisits M6/COMBAT-RULES.md §6 per the user's 2026-09-03 clarification, then again per
    /// their 2026-09-04 combat-rules rework. Confirmed by reading <c>VulnerablePower</c>/
    /// <c>WeakPower</c>/<c>FrailPower</c>'s identical <c>AfterSideTurnEnd</c> override in full:
    /// <c>if (side == CombatSide.Enemy) { await PowerCmd.TickDownDuration(this); }</c> — one fixed
    /// native checkpoint (the Enemy/Ghost side's own turn end), regardless of who applied or holds the
    /// stack.
    /// <b>Revised 2026-09-04</b>: superseded the 2026-09-03 applier-side rule (<c>side ==
    /// power.Applier.Side</c>) with a holder's-own-turn-end rule instead, decided both ways with the
    /// user (see `PLAN.md`'s dated note). Concretely: a single stack of Weak the Ghost applies to the
    /// human mid-turn, under the applier-side rule, ticked at the *same* Ghost turn's end — the very
    /// first checkpoint after application — expiring before the human ever got to act under it. What
    /// the user actually wants is "a debuff is up for its holder's own next turn, then decays" — i.e.
    /// keyed to <b>who holds it</b>, not who cast it. This is also, independently, exactly how the
    /// engine's own <c>TemporaryStrengthPower.AfterSideTurnEnd</c> (Mangle etc.) already decays,
    /// unmodified: <c>if (participants.Contains(base.Owner)) { ... }</c> — ticks whenever the *holder*
    /// is a participant in the side whose turn is ending, with no side-check at all. Mirrored here
    /// instead of inventing a new formula: <c>participants.Contains(__instance.Owner)</c>, reusing the
    /// <c>participants</c> parameter this method already receives rather than recomputing side
    /// membership by hand. For a debuff the Ghost applies to the human, this now ticks at the human's
    /// own next turn end (one checkpoint later than the applier-side rule gave it — the fix); for a
    /// debuff the human applies to the Ghost, this now ticks at the Ghost's own turn end (reverting to
    /// what native's original fixed check already did for that direction, since Owner.Side == Enemy
    /// there) — the "both directions" half of the user's decision. P18 (grace-tick suppression) still
    /// needed, unchanged: the native <c>SkipNextDurationTick</c> grace is orthogonal to whichever
    /// side-check formula gates the tick, and would otherwise swallow the *first* opportunity here too.
    /// Cannot be a single patch on <c>Hook.AfterSideTurnEnd</c> (the common dispatch point): that would
    /// also have to filter out <c>PoisonPower</c>/Doom, which share the identical <c>Type</c>/
    /// <c>StackType</c> shape (confirmed by reading <c>PoisonPower.cs:17-21</c>) but the user
    /// explicitly wants left on their existing afflicted-side periodic timing — three <c>sealed</c>
    /// classes with no common intermediate base, so three target methods via <c>TargetMethods()</c>
    /// rather than three separate <c>[HarmonyPatch]</c> classes duplicating this same logic.
    /// </summary>
    [HarmonyPatch]
    private static class P26_ApplierSideDebuffDecay
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VulnerablePower), nameof(PowerModel.AfterSideTurnEnd))
                ?? throw new InvalidOperationException("P26: VulnerablePower.AfterSideTurnEnd not found - game update?");
            yield return AccessTools.Method(typeof(WeakPower), nameof(PowerModel.AfterSideTurnEnd))
                ?? throw new InvalidOperationException("P26: WeakPower.AfterSideTurnEnd not found - game update?");
            yield return AccessTools.Method(typeof(FrailPower), nameof(PowerModel.AfterSideTurnEnd))
                ?? throw new InvalidOperationException("P26: FrailPower.AfterSideTurnEnd not found - game update?");
        }

        [HarmonyPrefix]
        private static bool Prefix(PowerModel __instance, CombatSide side, IReadOnlyList<Creature> participants, ref Task __result)
        {
            if (GhostSession.Current is not { } session
                || __instance.Owner?.CombatState is null
                || !ReferenceEquals(__instance.Owner.CombatState, session.GhostPlayer.Creature.CombatState))
            {
                return true;
            }
            bool shouldTick = participants.Contains(__instance.Owner);
            GhostLog.Info($"P26 DECAY-CHECK {__instance.Id.Entry} on {__instance.Owner.LogName} stack={__instance.Amount} "
                + $"applier={__instance.Applier?.LogName ?? "null"} turnEndSide={side} skipFlag={__instance.SkipNextDurationTick} -> {(shouldTick ? "TICK" : "skip (not holder's turn)")}");
            __result = shouldTick ? PowerCmd.TickDownDuration(__instance) : Task.CompletedTask;
            return false;
        }
    }

    /// <summary>
    /// P27: diagnostic-only, added 2026-09-04 to directly verify P26's decay schedule from a log
    /// instead of reverse-engineering it from damage numbers (which is how the round-6 SetupStrike
    /// discrepancy in the user's "Vulnerable bad" log was first found — 7 base damage resolving as
    /// 10.5, proving Vulnerable-on-human was still active a round after it should have expired under
    /// rule (a)). Confirmed reachable for <b>both</b> sides: <c>Hook.AfterSideTurnStart</c>
    /// (<c>Hook.cs:1163</c>, already P13's target — fires for the human's own turn start natively
    /// and for the Ghost's via <c>GhostTurnController.SetupTurn</c>'s reverse-patch call) and
    /// <c>Hook.AfterTurnEnd</c> (<c>Hook.cs:1267</c>, P14's target — reached for the human via
    /// <c>EndPlayerTurnPhaseTwoInternal</c>, and for the Ghost via <c>CombatManager
    /// .ExecuteEnemyTurn</c> -&gt; <c>EndEnemyTurn</c> -&gt; <c>EndEnemyTurnInternal</c>
    /// (<c>CombatManager.cs:1061-1093,1248-1257</c>) — a call chain P2's <c>Creature.TakeTurn</c>
    /// patch does not touch, since it only replaces what runs *inside* that one method, not the
    /// surrounding per-side orchestration). Purely observational — never alters state, never gated
    /// on anything but <c>GhostSession.Current</c>.
    /// </summary>
    [HarmonyPatch]
    private static class P27_LogDebuffStacksAtTurnBoundaries
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Hook), nameof(Hook.AfterSideTurnStart))
                ?? throw new InvalidOperationException("P27: Hook.AfterSideTurnStart not found - game update?");
            yield return AccessTools.Method(typeof(Hook), nameof(Hook.AfterTurnEnd))
                ?? throw new InvalidOperationException("P27: Hook.AfterTurnEnd not found - game update?");
        }

        [HarmonyPostfix]
        private static void Postfix(MethodBase __originalMethod, ICombatState combatState, CombatSide side)
        {
            if (GhostSession.Current is not { } session)
            {
                return;
            }
            string when = __originalMethod.Name == nameof(Hook.AfterSideTurnStart) ? "TURN-START" : "TURN-END";
            Creature? human = LocalContext.GetMe(combatState)?.Creature;
            Creature ghost = session.GhostPlayer.Creature;
            // Added 2026-09-04 alongside P33: directly verifies whether a real human's
            // PlayerCombatState.TurnNumber is 1 on their own actual first turn (the thing P33 exists
            // to guarantee) instead of inferring it from whether a TurnNumber-gated relic fired.
            int? humanTurnNumber = human?.Player?.PlayerCombatState?.TurnNumber;
            int? ghostTurnNumber = session.GhostPlayer.PlayerCombatState?.TurnNumber;
            GhostLog.Info($"DEBUFF-SNAPSHOT {when} side={side} round={session.RoundNumber} "
                + $"humanTurnNumber={humanTurnNumber?.ToString() ?? "?"} ghostTurnNumber={ghostTurnNumber?.ToString() ?? "?"}: "
                + $"Human[{DescribeDebuffs(human)}] Ghost[{DescribeDebuffs(ghost)}]");
        }

        private static string DescribeDebuffs(Creature? creature)
        {
            if (creature is null)
            {
                return "?";
            }
            int vulnerable = creature.GetPower<VulnerablePower>()?.Amount ?? 0;
            int weak = creature.GetPower<WeakPower>()?.Amount ?? 0;
            int frail = creature.GetPower<FrailPower>()?.Amount ?? 0;
            return $"Vulnerable={vulnerable} Weak={weak} Frail={frail}";
        }
    }

    /// <summary>
    /// P21: confirmed live (2026-09-03, Necrobinder) — the Ghost's own health/Block display, and its
    /// pet Osty's, were both hidden by default, only revealing on mouse hover (and hiding the human's
    /// own bar while doing so). Root cause, confirmed by reading <c>NCreature._Ready()</c> in full
    /// (<c>NCreature.cs:456-500</c>): <c>bool flag2 = Entity.IsPlayer &amp;&amp;
    /// !LocalContext.IsMe(Entity); bool flag = Entity.PetOwner != null &amp;&amp;
    /// !LocalContext.IsMe(Entity.PetOwner); _isRemotePlayerOrPet = flag2 || flag;</c> — logic authored
    /// for genuine co-op multiplayer ("don't clutter my screen with a remote ally's or their pet's
    /// bar"), misfiring here because <c>LocalContext.IsMe</c> checks a creature's owning
    /// <c>Player.NetId</c> against the one real local human's, and the Ghost's `Player` (and anything
    /// it owns) is never that — by design, it is not a remote co-op teammate, it is the Ghost. Fixes
    /// the private, no-setter <c>_isRemotePlayerOrPet</c> field directly (<see cref="NCreatureRemoteFlagAccess"/>,
    /// this project's established reflection-accessor pattern) rather than the far broader
    /// <c>LocalContext.IsMe</c>/<c>NetId</c>, which is read pervasively elsewhere (action-queue
    /// routing, choice-context ownership) for reasons unrelated to display and risk of the same class
    /// of untraceable regression the reverted original P9 caused. Narrower still: only the Ghost's own
    /// creature and creatures it owns as pets are touched; a real monster's own pets (if any exist
    /// natively) are untouched, since <c>Entity.PetOwner</c> won't reference-equal
    /// <c>session.GhostPlayer</c> for those. Fixing only the flag is not enough on its own — `_Ready()`
    /// already called <c>_stateDisplay.HideImmediately()</c> by the time this postfix runs, so the
    /// display must also be explicitly revealed once, the same way the native "not remote" branch
    /// would have (<c>NCreature.cs:496-499</c>).
    /// </summary>
    [HarmonyPatch(typeof(NCreature), "_Ready")]
    private static class P21_ShowGhostAndPetStateDisplay
    {
        [HarmonyPostfix]
        private static void Postfix(NCreature __instance)
        {
            if (GhostSession.Current is not { } session)
            {
                return;
            }
            Creature entity = __instance.Entity;
            bool isGhostOwned = ReferenceEquals(entity, session.GhostPlayer.Creature)
                || (entity.PetOwner is not null && ReferenceEquals(entity.PetOwner, session.GhostPlayer));
            if (!isGhostOwned)
            {
                return;
            }
            NCreatureRemoteFlagAccess.SetIsRemotePlayerOrPet(__instance, false);
            NCreatureStateDisplay stateDisplay = __instance.GetNode<NCreatureStateDisplay>("%HealthBar");
            stateDisplay.AnimateIn(HealthBarAnimMode.SpawnedDuringCombat);
        }
    }

    /// <summary>
    /// P22: the same <c>playersEndingTurn</c> bug P9/P14 already found for turn-start and the human's
    /// hook-dispatch turn-end, confirmed live for a third consumer of that same <c>IsPlayer</c>-derived
    /// list (ENGINE-NOTES.md §7 Q2): <c>CombatManager.EndPlayerTurnPhaseOneInternal</c>
    /// (<c>CombatManager.cs:1143-1209</c>, hard-gated to the *human's own* turn end,
    /// `CurrentSide == Player`) builds `playersEndingTurn` from `_state.Players.ToList()`
    /// (`CombatManager.cs:1158`) and calls `DoTurnEnd(item, ...)` for every entry
    /// (`CombatManager.cs:1191`) — including the Ghost. `DoTurnEnd` (`CombatManager.cs:1216-1246`)
    /// calls `player.PlayerCombatState.OrbQueue.BeforeTurnEnd(...)` as its first line — so the Ghost's
    /// own orb passives (e.g. Frost's end-of-turn Block) were firing once, spuriously, at the *human's*
    /// turn end (native, unpatched), and a second time, correctly, at the Ghost's *own* turn end
    /// (<c>GhostTurnController</c>'s own mirrored call) — confirmed live as Defect's orb passives
    /// firing at the end of every turn instead of only their owner's. `Hook.BeforeTurnEnd` (this same
    /// method's line `:1179`) already had this exact problem and was already fixed by P14 — this is
    /// the same root list feeding a second, direct (non-hook-dispatch) consumer P14 doesn't reach.
    /// Skipped entirely for the Ghost, the same way P9 skips <c>SetupPlayerTurn</c>;
    /// <see cref="GhostTurnController"/>'s own end-of-turn sequence now replicates <c>DoTurnEnd</c>'s
    /// full body (OrbQueue, Ethereal exhaust, `OnTurnEndInHandEffect`) at the Ghost's actual turn end,
    /// so nothing is lost. <c>Hook.BeforeFlush</c>/<c>Hook.AfterAutoPostPlayPhaseEntered</c>
    /// (this same method's other two per-player loops, `:1167`, `:1204`) read the identical list and
    /// are likely the same bug shape, but are not yet confirmed live-broken for anything in the current
    /// test decks — left as a known, undecided risk rather than patched speculatively.
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), "DoTurnEnd")]
    private static class P22_SkipGhostNativeDoTurnEnd
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player, ref Task __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostPlayer(player))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
    }

    /// <summary>
    /// P23: confirmed live (2026-09-03, Necrobinder) — after the Ghost's Osty died once, every
    /// subsequent Bodyguard play created a brand-new Osty instead of reviving the corpse,
    /// producing an unbounded pile of duplicates (4+ observed by turn 4). Root cause, confirmed
    /// by reading <c>OstyCmd.Summon</c> in full (<c>OstyCmd.cs:36-93</c>): its revival lookup —
    /// <c>combatState.Allies.FirstOrDefault(c =&gt; c.Monster is Osty &amp;&amp; c.PetOwner ==
    /// summoner)</c> (<c>OstyCmd.cs:48</c>) — reads the Side-literal <c>Allies</c> list
    /// (ENGINE-NOTES.md §7 Q2 shape), which can never contain a Ghost-owned creature (Side ==
    /// Enemy). <c>DieForYouPower.ShouldCreatureBeRemovedFromCombatAfterDeath</c> deliberately
    /// returns false for Osty's own death, so a dead Osty is never cleaned out of combat or out
    /// of <c>PlayerCombatState._pets</c> — meaning the corpse is genuinely still there to find,
    /// just unreachable through this one Side-literal read. (<c>summoner.IsOstyAlive</c>, the
    /// method's other Osty lookup at line 49, already goes through the player-scoped
    /// <c>Player.Osty =&gt; PlayerCombatState?.GetPet&lt;Osty&gt;()</c> — <c>Pets.FirstOrDefault(p
    /// =&gt; p.Monster is T)</c>, no Side filter at all — and already finds it correctly; only
    /// this second, separately-written lookup is broken.)
    /// <b>Q1</b> (what breaks without it): every Bodyguard play after the Ghost's first Osty
    /// death permanently takes the "create new Osty" branch instead of "revive," per the above.
    /// <b>Q2</b> (mode guard first): the inserted call is unconditional at the IL level (like
    /// P12's precedent) but its own guard, <c>GhostSession.Current is null</c>, is the first and
    /// only condition the helper checks, short-circuiting to the original list for every other
    /// combat. <b>Q3</b> (narrower target considered): yes — patching <c>CombatState.Allies</c>'s
    /// getter itself, even scoped to an ambient flag active only during this one method, was
    /// considered and rejected: that is structurally the same shape as the reverted original P9
    /// (an ambient-scoped Allies/Enemies patch that caused an untraced regression across every
    /// Ghost attack) and would affect every <c>Allies</c> read during this method's own awaited
    /// sub-calls (<c>Hook.AfterSummon</c> fans out to arbitrary relic/power listeners), not just
    /// this one line. A transpiler touching only the single IL call site inside
    /// <c>OstyCmd.Summon</c>'s own compiled body cannot affect any other caller of
    /// <c>CombatState.Allies</c> anywhere, at any time — strictly narrower. Concatenating
    /// <c>Enemies</c> onto the <c>Allies</c> result (rather than substituting <c>summoner</c>'s
    /// side in some other way) also sidesteps needing the <c>summoner</c> parameter at all — which
    /// would otherwise require guessing a compiler-generated async-state-machine field name — since
    /// the very next step, <c>.FirstOrDefault(c =&gt; ... &amp;&amp; c.PetOwner == summoner)</c>,
    /// already narrows correctly to the true owner regardless of which side's list the candidate
    /// came from; safe for a human's own Osty lookup too (a Ghost-owned candidate never has
    /// <c>PetOwner == summoner</c> for a human <c>summoner</c>). <b>Q4</b> (patch count): 23 total;
    /// independently justified on this one confirmed live failure.
    /// </summary>
    [HarmonyPatch]
    private static class P23_FindGhostOwnedOstyAcrossSides
    {
        private static MethodBase TargetMethod()
        {
            MethodInfo outer = AccessTools.Method(
                typeof(OstyCmd),
                nameof(OstyCmd.Summon),
                new[] { typeof(PlayerChoiceContext), typeof(Player), typeof(decimal), typeof(AbstractModel) })
                ?? throw new InvalidOperationException("P23: OstyCmd.Summon(PlayerChoiceContext, Player, decimal, AbstractModel) not found - game update?");
            Type stateMachine = outer.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                ?? throw new InvalidOperationException("P23: OstyCmd.Summon is no longer an async state machine - game update?");
            return AccessTools.Method(stateMachine, "MoveNext")
                ?? throw new InvalidOperationException("P23: MoveNext not found on OstyCmd.Summon's state machine - game update?");
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo alliesGetter = AccessTools.PropertyGetter(typeof(ICombatState), nameof(ICombatState.Allies))
                ?? throw new InvalidOperationException("P23: ICombatState.Allies getter not found - game update?");
            MethodInfo helper = AccessTools.Method(typeof(P23_FindGhostOwnedOstyAcrossSides), nameof(IncludeEnemiesForOstyLookup));

            CodeMatcher matcher = new(instructions);
            matcher.MatchStartForward(new CodeMatch(instruction => instruction.Calls(alliesGetter)));
            if (!matcher.IsValid)
            {
                throw new InvalidOperationException("P23: OstyCmd.Summon no longer reads CombatState.Allies at the expected point - game update?");
            }
            // Duplicate the CombatState receiver already on the stack before the callvirt
            // consumes it, so it is still available afterward to pass into the helper alongside
            // the Allies result — no need to locate any compiler-generated field for `summoner`
            // or `combatState` themselves.
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Dup));
            matcher.Advance(1);
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call, helper));
            return matcher.InstructionEnumeration();
        }

        private static IReadOnlyList<Creature> IncludeEnemiesForOstyLookup(ICombatState combatState, IReadOnlyList<Creature> allies) =>
            GhostSession.Current is null ? allies : allies.Concat(combatState.Enemies).ToList();
    }

    /// <summary>
    /// P29: confirmed live (2026-09-04) — GnarledHammer's <c>AfterObtained</c> (an enchant-pick prompt
    /// fired the instant the Ghost's M8.5 random deck configuration obtains it) crashed with
    /// <c>System.ArgumentOutOfRangeException</c> inside
    /// <c>PlayerChoiceSynchronizer.ReserveChoiceId(Player)</c>. Root cause, confirmed by reading that
    /// method and its callees in full: it calls <c>IPlayerCollection.GetPlayerSlotIndex(player)</c>,
    /// whose own doc comment states it returns -1 "if the player is not in Players"
    /// (<c>RunState.GetPlayerSlotIndex(Player)</c>, <c>RunState.cs:354-357</c>:
    /// <c>Players.IndexOf(player)</c>) — then indexes a <c>List&lt;uint&gt;</c> with that -1 directly,
    /// with no negative-index guard. The Ghost is deliberately never added to <c>RunState.Players</c>
    /// (<c>GhostPlayerFactory.JoinRun</c>'s own doc comment) — the same root cause as
    /// P8/P9/P12/P14/P22/P25's "Ghost missing from a native per-player collection" family, just a new
    /// call site, not GnarledHammer-specific: <c>PlayerChoiceSynchronizer</c>'s own class doc comment
    /// names Survivor's discard pick, Discovery's card-add pick and Toolbox's card-add pick as the same
    /// mechanism, so any of those cards, played by the Ghost, would hit this identically. Its sibling
    /// <c>GetChoiceId(Player)</c> (called from <c>ValidateChoiceId</c>, in turn called from
    /// <c>SyncLocalChoice</c> — the very next step after a successful <c>ReserveChoiceId</c> — and from
    /// <c>WaitForRemoteChoice</c>) has the identical unguarded-negative-index bug
    /// (<c>PlayerChoiceSynchronizer.cs:198-206</c>: its own <c>playerSlotIndex &gt;= _choiceIds.Count</c>
    /// guard is false for a negative index, so it falls through to the same out-of-range read) —
    /// confirmed by direct read, not just inferred from the one observed crash, so both are fixed
    /// together rather than only the one that happened to be hit first.
    ///
    /// This whole mechanism exists purely for real cross-client choice synchronization (its own class
    /// doc comment: "all players... generate an ID... the owning player brings up the UI... sends a
    /// message to all other peers"), which has no real meaning for a Ghost making its own choice
    /// (nothing else is a genuine remote peer needing to learn about it the way a human's card play is
    /// broadcast via M9e's <c>GhostCardPlayEvent</c>). <see cref="P29_SupplyGhostChoiceId"/> supplies a
    /// fixed dummy id instead of the real per-slot reservation (P8's exact "detached value instead of
    /// the real lookup" shape); <see cref="P29_SkipGhostChoiceIdValidation"/> makes the corresponding
    /// validation always succeed for a Ghost, so <c>SyncLocalChoice</c> can still proceed to its own
    /// <c>_netService.SendMessage(message)</c> normally afterward (a no-op in singleplayer; a genuine,
    /// correct broadcast in real multiplayer, so other humans can see what the Ghost selected).
    /// </summary>
    [HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.ReserveChoiceId))]
    private static class P29_SupplyGhostChoiceId
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player, ref uint __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostPlayer(player))
            {
                return true;
            }
            __result = 0u;
            return false;
        }
    }

    /// <summary>P29 (continued): see <see cref="P29_SupplyGhostChoiceId"/>'s doc comment.</summary>
    [HarmonyPatch(typeof(PlayerChoiceSynchronizer), "ValidateChoiceId")]
    private static class P29_SkipGhostChoiceIdValidation
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player, ref bool __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostPlayer(player))
            {
                return true;
            }
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// P30: confirmed live (2026-09-04, same test round as P29, a different random relic this time) —
    /// <c>Cauldron.AfterObtained()</c> (offers a bonus potion reward) crashed with the identical
    /// <c>System.ArgumentOutOfRangeException</c> shape as P29, this time inside
    /// <c>RewardsSetSynchronizer.GetRewardStateForPlayer(Player)</c>
    /// (<c>RewardsSetSynchronizer.cs:143-146</c>: <c>_rewardStates[_playerCollection
    /// .GetPlayerSlotIndex(player)]</c>) — same root cause as P29 (a Ghost missing from
    /// <c>RunState.Players</c>, so <c>GetPlayerSlotIndex</c> returns -1), but **not fixable the same
    /// "supply a dummy id" way**: unlike P29's <c>_choiceIds</c> (which grows on demand), confirmed by
    /// reading the constructor (<c>RewardsSetSynchronizer.cs:119-131</c>) that <c>_rewardStates</c> is
    /// a fixed-size list, exactly one entry per real player in <c>_playerCollection.Players</c>,
    /// allocated once at construction — there is no index this method could return for the Ghost that
    /// wouldn't itself be a second out-of-range read (too high this time, not negative). Root-caused
    /// broadly, not just for this one relic: grepping every caller of <c>GetPlayerSlotIndex</c>
    /// confirms it is also read for <b>RNG seeding</b> (<c>Player.cs:326</c>,
    /// <c>EventModel.cs:238</c>, <c>Rng.cs:48</c>) — patching the shared root
    /// (<c>RunState.GetPlayerSlotIndex</c>) to return a different value for the Ghost was considered
    /// and rejected: it would silently reseed the Ghost's own already-working <c>PlayerRng</c>
    /// (constructed once, at <c>GhostConstructionScope</c> time, using whatever this method returns),
    /// risking an untraceable draw-order regression across every already-verified M1-M8 scenario for a
    /// fix that, per the fixed-size-list finding above, would not even work for every synchronizer
    /// anyway. Fixed at this one call site's actual caller instead: <c>BeginRewardsSet</c>
    /// (<c>RewardsSetSynchronizer.cs:154-186</c>, "called when a reward set is spawned and offered to
    /// the owning player," returning "a Task which completes when the player is done taking the
    /// rewards") is skipped entirely for a Ghost, the same "skip entirely, nothing real is lost" shape
    /// as P9/P22 — a Ghost has no UI to take a reward through in the first place, so completing
    /// immediately without tracking anything is not a narrower version of the real behavior, it is the
    /// correct behavior. Known, explicit trade-off: the Ghost's M8.5 random-deck grant does not realize
    /// whatever bonus reward a relic like Cauldron would otherwise offer — an acceptable, stated gap for
    /// a debug-only feature, not a real gameplay regression (M9's own Ghost-construction paths,
    /// <c>CreateDebugGhost</c>/<c>CreateGhostFromSnapshot</c>, do not call <c>ConfigureDeckAsync</c> and
    /// so never reach this at all).
    /// </summary>
    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
    private static class P30_SkipGhostRewardsSet
    {
        [HarmonyPrefix]
        private static bool Prefix(RewardsSet set, ref Task __result)
        {
            if (GhostSession.Current is not { } session || !session.IsGhostPlayer(set.Player))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
    }

    /// <summary>
    /// P32: per the user's 2026-09-04 request — the Ghost takes the opening turn instead of the human,
    /// matching a normal STS fight (the enemy's queued intent is already visible before you act; right
    /// now, post the combat-rules rework, the Ghost has nothing queued yet on the human's first turn).
    /// Root fact: <c>CombatState</c>'s constructor hardcodes the opening side unconditionally —
    /// <c>CombatState.cs:152-162</c>: <c>RoundNumber = 1; CurrentSide = CombatSide.Player;</c> — no
    /// ambush/acts-first mechanic or per-encounter variation point exists anywhere in
    /// <c>CombatManager.cs</c>/<c>CombatState.cs</c> (confirmed by search). <c>docs/COMBAT-RULES.md</c>'s
    /// old "the human acts first" line was simply describing this constant, not something the mod set
    /// up.
    /// Safety: the one native setup-time reader of <c>CurrentSide</c> (<c>CombatManager.cs:865</c>,
    /// inside <c>CombatManager.AfterCreatureAdded</c>, gated <c>IsEnemy &amp;&amp; CurrentSide ==
    /// CombatSide.Player</c>) is already fully bypassed for the Ghost by P4
    /// (<c>P4_SkipMonsterMoveRoll</c>, above) — a prefix that skips the native method body entirely for
    /// any Ghost creature. A Ghost Duel encounter has no real monsters, so the Ghost is the only
    /// creature where <c>IsEnemy</c> is ever true during setup, and that path never reaches the check
    /// this patch could otherwise affect. Flipping the constructor's initial value is therefore safe
    /// with respect to every currently-known setup-time reader of <c>CurrentSide</c>.
    /// Gate ordering confirmed live from <c>ghostduel.log</c>: <c>Session begin: party=[...]</c> is
    /// logged before <c>Ghost added to CombatState</c> (P5) on every run, so <see cref="GhostSession.Current"/>
    /// already exists by the time any Ghost-Duel <c>CombatState</c> is constructed — this postfix never
    /// fires for an ordinary (non-Ghost) combat in the same run.
    /// Everything downstream is already side-order-agnostic by design: <c>GhostTurnController</c> reacts
    /// to whichever side is currently active, not to a specific round number, and P13's
    /// <c>Hook.AfterSideTurnStart</c> wrapper gates on <c>side == CombatSide.Enemy</c> plus participants,
    /// not round number — no other change is needed for "the Ghost's turn runs first" to work.
    /// </summary>
    [HarmonyPatch(typeof(CombatState), MethodType.Constructor,
        typeof(EncounterModel), typeof(IRunState), typeof(IReadOnlyList<ModifierModel>),
        typeof(IReadOnlyList<BadgeModel>), typeof(MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel))]
    private static class P32_GhostGoesFirst
    {
        [HarmonyPostfix]
        private static void Postfix(CombatState __instance)
        {
            if (GhostSession.Current is null)
            {
                return;
            }
            __instance.CurrentSide = CombatSide.Enemy;
        }
    }

    /// <summary>
    /// P33: confirmed live — Defect's Cracked Core (starting relic, channels a Lightning orb via
    /// <c>BeforeSideTurnStart</c> gated on <c>Owner.PlayerCombatState.TurnNumber &lt;= 1</c>,
    /// <c>CrackedCore.cs:29-38</c>) never channeled its orb for the human once P32 made the Ghost go
    /// first. Root cause: <c>CombatManager.SwitchSides</c> (private, called at the end of every side's
    /// turn to decide who goes next) increments *every real player's*
    /// <c>PlayerCombatState.TurnNumber</c> specifically on the transition *into* the Player side
    /// (<c>CombatManager.cs:1404-1418</c>) — under the native assumption that this transition only
    /// ever happens once a full round has actually passed for the player, true when the Player side
    /// always goes first (the very first such transition follows the player's *own* completed first
    /// turn). With P32, the very first such transition now follows the *Ghost's* opening turn instead
    /// — bumping every real human's <c>TurnNumber</c> from 1 to 2 before they have taken a single
    /// turn, so any relic/mechanic keyed on <c>TurnNumber &lt;= 1</c>/<c>== 1</c> (Cracked Core, and by
    /// the same shape — confirmed by grep, not individually verified broken — `Bread`/`BlessedAntler`/
    /// `FuneraryMask`/`IceCream`/`FestivePopper`/`HistoryCourse`/`LetterOpener`/`Pocketwatch`/
    /// `RadiantPearl`/`ToastyMittens`/`VexingPuzzlebox`/`Toolbox`, and every later-turn check such as
    /// `Candelabra`/`HornCleat`'s <c>==2</c> or `Chandelier`/`CaptainsWheel`/`SparklingRouge`'s
    /// <c>==3</c>) fires one turn later than it should, permanently, for the rest of the fight.
    /// <b>Revised 2026-09-04</b>: the original fix (a Harmony prefix on
    /// <c>PlayerCombatState.IncrementTurnNumber</c>, skipping the call for real humans while a scope
    /// opened by a <c>SwitchSides</c> prefix was active) confirmed live *not* to work — diagnostic
    /// logging added to both patches showed the <c>SwitchSides</c> scope opening and closing
    /// correctly, but the <c>IncrementTurnNumber</c> prefix's own log line never appeared even once,
    /// despite <c>TurnNumber</c> visibly changing (1 → 2) across that exact call. The only explanation
    /// consistent with that evidence: <c>IncrementTurnNumber</c>'s one-line body (<c>TurnNumber++</c>,
    /// <c>PlayerCombatState.cs:157-160</c>) gets inlined by the JIT directly into <c>SwitchSides</c>'s
    /// own compiled code, so the method call Harmony patched was never actually reached at runtime — a
    /// known failure mode for trivial one-line methods that a prefix on the callee cannot defend
    /// against.
    /// Fixed instead by undoing the mutation at its one caller, immune to whether the increment
    /// happens via a real call or an inlined one: a prefix on <c>SwitchSides</c> that, when this is the
    /// Ghost's opening-turn-end transition (<c>CurrentSide == CombatSide.Enemy &amp;&amp; RoundNumber
    /// == 1</c>, both still holding their pre-transition values at this point), snapshots every real
    /// human's current <c>TurnNumber</c> (<c>combatState.Players</c>, excluding the Ghost via
    /// <see cref="GhostSession.IsGhostPlayer"/> — confirmed <c>CombatState.Players</c> includes the
    /// Ghost alongside real humans, ENGINE-NOTES.md §7 Q2, "<c>IsPlayer</c>-derived"); a postfix
    /// restores each snapshotted value afterward via <see cref="PlayerCombatStateTurnNumberAccess"/>
    /// (a reflected setter — <c>TurnNumber</c> is <c>{ get; private set; }</c>). The Ghost's own
    /// <c>TurnNumber</c> is never touched, so it still advances normally — its first turn really did
    /// just complete.
    /// Not verified: whether an "extra turn" mechanic (<c>Hook.ShouldTakeExtraTurn</c>,
    /// <c>CombatManager.cs:1360-1373</c>) granted to the Ghost during its own opening turn could
    /// re-enter <c>SwitchSides</c> with <c>CurrentSide</c> still <c>Enemy</c> a second time — no known
    /// card/relic in the test decks exercises this; the snapshot/restore shape is robust to a repeat
    /// call either way (each restore is idempotent against the same snapshotted value), so this is
    /// flagged as unverified rather than as a known gap.
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), "SwitchSides")]
    private static class P33_SkipHumanTurnNumberBumpOnGhostOpeningTurn_SwitchSides
    {
        [HarmonyPrefix]
        private static void Prefix(out Dictionary<PlayerCombatState, int>? __state)
        {
            __state = null;
            if (GhostSession.Current is not { } session
                || session.GhostPlayer.Creature.CombatState is not { } combatState
                || combatState.CurrentSide != CombatSide.Enemy
                || combatState.RoundNumber != 1)
            {
                return;
            }
            __state = combatState.Players
                .Where(player => !session.IsGhostPlayer(player) && player.PlayerCombatState is not null)
                .ToDictionary(player => player.PlayerCombatState!, player => player.PlayerCombatState!.TurnNumber);
            GhostLog.Info($"P33 SwitchSides-prefix: Ghost opening turn ending — snapshotted TurnNumber for "
                + $"{__state.Count} real human(s) before the transition.");
        }

        [HarmonyPostfix]
        private static void Postfix(Dictionary<PlayerCombatState, int>? __state)
        {
            if (__state is null)
            {
                return;
            }
            foreach ((PlayerCombatState state, int snapshotted) in __state)
            {
                GhostLog.Info($"P33 SwitchSides-postfix: restoring TurnNumber {state.TurnNumber} -> {snapshotted}.");
                PlayerCombatStateTurnNumberAccess.Set(state, snapshotted);
            }
        }
    }

    /// <summary>
    /// P34: confirmed live/reported — the Ghost's own queued-damage indicator didn't update when Weak
    /// was applied to the Ghost during the human's turn. Root cause: <c>QueuedDamagePacket
    /// .DisplayAmount</c> already re-reads the dealer's *current* Weak and Strength fresh on every read
    /// (COMBAT-RULES.md §5's "late-bound to the attacker" rule, generalized to Strength 2026-09-04) —
    /// the underlying number was never wrong, but nothing ever told the presenter to *re-read* it,
    /// since applying or decaying Weak/Strength on a creature never touches
    /// <c>GhostDamageQueue</c>'s own pending list (no enqueue/resolve, hence no existing
    /// <see cref="GhostDamageQueue.Changed"/>).
    /// Fixed at the one native chokepoint every power amount-change funnels through, not per-source
    /// (not "when Bash is played", not "when Weak decays") — <c>PowerModel.SetAmount</c>
    /// (<c>PowerModel.cs:542-553</c>) is called by <c>Amount</c>'s own private setter and is the only
    /// place <c>_amount</c> is ever mutated; it already only proceeds past its own
    /// <c>if (num != 0)</c> guard when the value genuinely changed, and already raises a native
    /// <c>DisplayAmountChanged</c> event for exactly this reason. A postfix, gated on
    /// <c>__instance is WeakPower or StrengthPower</c> (a structural check by *kind*, not any specific
    /// card/relic by id) so every other power type's far more frequent amount changes (Block-adjacent
    /// powers, stacking counters, etc.) exit immediately after one cheap type check, calls
    /// <see cref="GhostDamageQueue.NotifyDisplayChanged"/> so the presenter re-renders with the live
    /// number. Deliberately not narrowed further to "only if this creature currently has pending
    /// queued damage" — the presenter's own refresh is already a cheap no-op when nothing is queued for
    /// anyone, and this only ever runs while a Ghost Duel session is active in the first place.
    /// <b>Revised 2026-09-07</b> (user report: "playing tank does not update incoming damage
    /// indicators properly"): extended from <c>WeakPower or StrengthPower</c> to also include
    /// <c>GuardedPower or TankPower</c> — both now live-read in <c>QueuedDamagePacket.DisplayAmount</c>
    /// (see P35/P36 below), so applying either one needs the exact same "tell the presenter to
    /// re-render" nudge, even though both apply to the *target* of a queued packet rather than the
    /// dealer — <c>GhostDamageIndicatorPresenter</c>'s refresh re-evaluates every pending packet
    /// regardless of which side changed, so no further narrowing is needed here.
    /// </summary>
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SetAmount))]
    private static class P34_RefreshQueuedDamageIndicatorOnDealerModifierChange
    {
        [HarmonyPostfix]
        private static void Postfix(PowerModel __instance)
        {
            if (GhostSession.Current is null || __instance is not (WeakPower or StrengthPower or GuardedPower or TankPower))
            {
                return;
            }
            GhostSession.Current.DamageQueue.NotifyDisplayChanged();
        }
    }

    /// <summary>
    /// P35: generalizes P15/P28's queue-time-suppress-then-live-re-read treatment to Guarded, per the
    /// user's 2026-09-07 report ("playing tank does not update incoming damage indicators properly").
    /// Confirmed by reading <c>GuardedPower.cs</c>: it halves damage from a "powered attack"
    /// (<c>props.IsPoweredAttack()</c> — true for any ordinary card/monster-move attack, confirmed via
    /// <c>ValuePropExtensions.IsPoweredAttack</c>) landing on its own holder — a target-side hook, like
    /// Vulnerable, but unlike Vulnerable it is meant to be played *reactively*, by a teammate, against
    /// an already-queued/visible threat (see <see cref="GhostGuardedSuppressionScope"/>'s own doc
    /// comment for the full reasoning on why that rules out Vulnerable's "locked at queue time"
    /// treatment). Forces <c>GuardedPower.ModifyDamageMultiplicative</c> to return 1 while
    /// <see cref="GhostGuardedSuppressionScope"/> is active, so P17 can compute a
    /// <see cref="QueuedDamagePacket.LockedAmount"/> that excludes Guarded the same way it already
    /// excludes Weak/Strength; <see cref="QueuedDamagePacket.DisplayAmount"/> re-multiplies the
    /// target's live Guarded factor back in at display/resolution time.
    /// </summary>
    [HarmonyPatch(typeof(GuardedPower), nameof(GuardedPower.ModifyDamageMultiplicative))]
    private static class P35_SuppressGuardedForLockedDamageComputation
    {
        [HarmonyPostfix]
        private static void Postfix(ref decimal __result)
        {
            if (GhostSession.Current is null || !GhostGuardedSuppressionScope.IsActive)
            {
                return;
            }
            __result = 1m;
        }
    }

    /// <summary>
    /// P36: the same treatment as P35, for <c>TankPower</c>'s own self-multiplier. Confirmed by reading
    /// <c>TankPower.cs</c>: it doubles damage from a "powered attack" landing on whoever played Tank —
    /// also target-side, also meant to be a reactive commitment (the player accepting a real cost the
    /// instant they see a queued attack coming, per <c>Tank</c>'s own card text), so it needs the exact
    /// same live-at-resolution treatment as Guarded rather than Vulnerable's locked-at-queue-time one.
    /// Kept as its own patch/scope rather than sharing P35's — see
    /// <see cref="GhostTankSuppressionScope"/>'s own doc comment for why. Forces <c>TankPower
    /// .ModifyDamageMultiplicative</c> to return 1 while <see cref="GhostTankSuppressionScope"/> is
    /// active; <see cref="QueuedDamagePacket.DisplayAmount"/> re-multiplies the target's live Tank
    /// factor back in at display/resolution time.
    /// </summary>
    [HarmonyPatch(typeof(TankPower), nameof(TankPower.ModifyDamageMultiplicative))]
    private static class P36_SuppressTankForLockedDamageComputation
    {
        [HarmonyPostfix]
        private static void Postfix(ref decimal __result)
        {
            if (GhostSession.Current is null || !GhostTankSuppressionScope.IsActive)
            {
                return;
            }
            __result = 1m;
        }
    }

    /// <summary>
    /// P37: user report — "legion of bones summoned osty's for the ghosts instead of only players."
    /// Confirmed by reading <c>LegionOfBone.cs</c> (a <c>MultiplayerOnly</c> Necrobinder card,
    /// <c>TargetType.AllAllies</c>): its own <c>OnPlay</c> computes its target set as
    /// <c>CombatState.PlayerCreatures.Where(c => c.IsAlive)</c> — every <c>IsPlayer</c>-backed creature
    /// in the whole encounter, regardless of side — then summons an Osty for each one. That is correct
    /// in vanilla co-op multiplayer, where no enemy is ever <c>Player</c>-backed, but wrong the instant a
    /// real <c>Player</c> sits on <c>CombatSide.Enemy</c> (this mod's entire premise) — exactly the
    /// "<c>IsPlayer</c>-derived collection quietly means 'real human' until a Ghost exists" bug class
    /// already fixed in native code paths by P8-P14/P22/P25 (ENGINE-NOTES.md §7 Q2), just not yet found
    /// in a *card's own* logic. Confirmed by reading every other <c>Cards</c>/<c>Relics</c>/<c>Powers</c>
    /// source file that this is the *only* content item using <c>.PlayerCreatures</c> directly for ally
    /// semantics — <c>TankPower.AfterApplied</c>, the other "bless my allies" effect checked for
    /// comparison, already uses the correct side-relative <c>CombatState.GetTeammatesOf(Creature)</c>
    /// (<c>GetCreaturesOnSide(creature.Side)</c>, confirmed by reading <c>CombatState.cs</c>) — so this
    /// is a genuine, isolated inconsistency in this one native method, not a systemic pattern needing a
    /// broader fix. Per CLAUDE.md's Harmony-patch question 3 ("could a narrower target work — a specific
    /// call site instead of a property getter"): yes — <c>CombatState.PlayerCreatures</c> itself is one
    /// of the hot properties CLAUDE.md explicitly forbids postfixing to reallocate, and changing its
    /// global meaning would also break this mod's own correct uses of it elsewhere (e.g. both choosers'
    /// <c>AnyEnemy</c> target resolution, which relies on it including the Ghost). Prefixes
    /// <c>LegionOfBone.OnPlay</c> itself instead, replacing its whole body only while a Ghost session is
    /// active: identical native calls (<c>CreatureCmd.TriggerAnim</c>, <c>OstyCmd.Summon</c> per ally,
    /// same <c>DynamicVars.Summon</c> amount) with the one substitution — <c>GetTeammatesOf(owner)</c>
    /// instead of the raw <c>PlayerCreatures</c> list. Not a content reimplementation: the effect itself
    /// (summon an Osty per ally) is untouched; only which creatures count as "ally" is corrected, using
    /// the same helper the base game's own equivalent effect already uses correctly.
    /// </summary>
    [HarmonyPatch(typeof(LegionOfBone), "OnPlay")]
    private static class P37_LegionOfBoneRealAlliesOnly
    {
        [HarmonyPrefix]
        private static bool Prefix(LegionOfBone __instance, PlayerChoiceContext choiceContext, ref Task __result)
        {
            if (GhostSession.Current is null)
            {
                return true;
            }
            __result = RunUnsafe(__instance, choiceContext);
            return false;
        }

        private static async Task RunUnsafe(LegionOfBone card, PlayerChoiceContext choiceContext)
        {
            Creature ownerCreature = card.Owner.Creature;
            await CreatureCmd.TriggerAnim(ownerCreature, Necrobinder.GetSummonAnimIfApplicable(card.Owner.Character), Necrobinder.GetSummonDelayIfApplicable(card.Owner.Character));
            // IsPlayer as well as IsAlive: GetTeammatesOf returns every creature on the side (pets
            // included, e.g. another ally's own Osty), matching PlayerCreatures' own original
            // Where(c => c.IsPlayer) filter — only real Player-backed allies get their own Osty.
            List<Player> allies = (ownerCreature.CombatState?.GetTeammatesOf(ownerCreature) ?? Array.Empty<Creature>())
                .Where(c => c.IsAlive && c.IsPlayer && c.Player is not null)
                .Select(c => c.Player!)
                .ToList();
            foreach (Player ally in allies)
            {
                await OstyCmd.Summon(choiceContext, ally, card.DynamicVars.Summon.BaseValue, card);
            }
        }
    }

    // A different, unrelated patch was also numbered P9 earlier the same day (2026-09-02):
    // CombatState.Allies/Enemies actor-relative flip, added to fix JuggernautPower.AfterBlockGained
    // resolving "a random enemy" to the Ghost itself, then REVERTED the same day when the very next
    // live test showed a much worse regression — none of the Ghost's attacks (including plain
    // single-target Strikes, whose targeting per AttackCommand.GetPossibleTargets()'s
    // IsSingleTargeted branch never reads Enemies/Allies/HittableEnemies at all) damaged the human
    // anymore. The exact mechanism was not pinned down before reverting — CreatureCmd.Damage,
    // DamageCmd.cs and AttackCommand.cs were all grepped and read in full and none of them read
    // Allies/Enemies/HittableEnemies directly, so the breakage is via some Hook.ModifyDamage listener
    // (walked via CombatState.IterateHookListeners, which correctly uses the raw _allies/_enemies
    // fields, not the patched properties) reacting to the flipped view in a way not yet traced.
    // Reverting restored the known-good (if Juggernaut-incomplete) M2 baseline rather than shipping a
    // fix worse than the bug. See ENGINE-NOTES.md §0 M2 section for the full writeup — Juggernaut
    // (and the ~19 similarly-shaped classes) remain "known, not yet fixed"; this freed P9 slot is now
    // the unrelated SetupPlayerTurn fix above.
}
