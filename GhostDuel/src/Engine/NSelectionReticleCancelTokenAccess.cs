using System.Reflection;
using System.Threading;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace GhostDuel.Engine;

/// <summary>
/// <c>NSelectionReticle._cancelToken</c> (confirmed: <c>NSelectionReticle.cs:65</c>) is a private,
/// readonly <c>CancellationTokenSource</c> created once in the field initializer and cancelled by
/// <c>_ExitTree()</c> (<c>NSelectionReticle.cs:75-79</c>) — after which <c>OnDeselect()</c>
/// (<c>NSelectionReticle.cs:96-109</c>) becomes a permanent no-op, since its entire body is gated on
/// <c>!_cancelToken.IsCancellationRequested</c> and the token is never replaced. Confirmed live
/// (2026-09-03): P6's <c>Reparent(...)</c> calls for the Ghost's own node and its pets fire
/// <c>_ExitTree()</c> on every descendant, including this reticle, as an unavoidable side effect of
/// reparenting a still-alive node — for any ordinary creature this never happens mid-life, only P6
/// does this. The result: the targeting reticle (the four yellow corner brackets) shows correctly on
/// the first hover, but <c>OnDeselect()</c> silently does nothing from that point on, leaving it
/// permanently visible after the first card target selection. Reflection is the only way to recover
/// from this without touching <c>NSelectionReticle</c>'s own cancellation-on-exit pattern (used
/// correctly elsewhere, for nodes that are actually leaving for good) or patching
/// <c>NCreature.Reparent</c>-adjacent engine code broadly. One file, one reflected field, with a
/// load-time existence assertion, matching <see cref="NCreatureRemoteFlagAccess"/>'s established
/// pattern.
/// </summary>
internal static class NSelectionReticleCancelTokenAccess
{
    private static readonly FieldInfo CancelTokenField =
        typeof(NSelectionReticle).GetField("_cancelToken", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NSelectionReticle).FullName, "_cancelToken");

    public static void ResetCancelToken(NSelectionReticle reticle) =>
        CancelTokenField.SetValue(reticle, new CancellationTokenSource());
}
