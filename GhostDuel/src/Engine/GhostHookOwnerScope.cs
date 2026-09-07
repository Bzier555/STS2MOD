using System.Runtime.CompilerServices;
using System.Threading;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace GhostDuel.Engine;

/// <summary>
/// Tracks, via an ambient <see cref="AsyncLocal{T}"/>, whose owner-Player's relic/power lifecycle hook
/// is currently executing — for the three call sites in <c>Hook.cs</c> that construct a
/// <c>HookPlayerChoiceContext(AbstractModel, ulong, ICombatState, GameActionType)</c>
/// (<c>BeforeSideTurnStart</c>, <c>AfterDeath</c>, <c>AfterDiedToDoom</c>). <c>CombatManager
/// .IsExecutingCardOrPotionEffect</c> only covers <c>CardModel.OnPlay</c>/<c>PotionModel.OnUse</c>
/// bodies (ENGINE-NOTES.md §0) and does not become true for these hooks, which is why
/// <c>RedMask.BeforeSideTurnStart</c> — a relic hook, not a card/potion effect — was still
/// misapplying Weak after P10. <see cref="EnginePatches"/>'s P11 pushes/pops this scope from
/// <c>HookPlayerChoiceContext</c>'s constructor and <c>AbstractModel.InvokeExecutionFinished</c>;
/// P10's guard reads <see cref="Current"/> in addition to the native card/potion flag.
/// </summary>
internal static class GhostHookOwnerScope
{
    private static readonly AsyncLocal<Creature?> _current = new();

    private static readonly ConditionalWeakTable<AbstractModel, StrongBox<Creature?>> _saved = new();

    internal static Creature? Current => _current.Value;

    internal static void Push(AbstractModel model, Creature? ownerCreature)
    {
        _saved.AddOrUpdate(model, new StrongBox<Creature?>(_current.Value));
        _current.Value = ownerCreature;
    }

    internal static void Pop(AbstractModel model)
    {
        if (_saved.TryGetValue(model, out StrongBox<Creature?>? saved))
        {
            _current.Value = saved.Value;
            _saved.Remove(model);
        }
    }

    /// <summary>
    /// P31 (<c>EnginePatches.cs</c>): a second, keyless save/restore pair for
    /// <c>Hook.AfterSideTurnStart</c>'s own whole-dispatch wrap — that hook (and the
    /// <c>AfterSideTurnStartLate</c> loop nested inside it) never constructs a
    /// <c>HookPlayerChoiceContext</c>, so there is no per-listener object to key <see cref="Push"/>/
    /// <see cref="Pop"/> off of the way P11 does for <c>BeforeSideTurnStart</c>/<c>AfterDeath</c>/
    /// <c>AfterDiedToDoom</c>. Safe to use a plain save/restore here instead of the model-keyed table:
    /// this method's only caller (P13's existing reverse-patch wrapper) brackets the *entire* real
    /// method body in one matched pair, on a call path that never nests with P11's three methods (none
    /// of them call <c>Hook.AfterSideTurnStart</c>), so the two mechanisms never interleave on the same
    /// <see cref="_current"/> slot.
    /// </summary>
    internal static Creature? PushRaw(Creature? ownerCreature)
    {
        Creature? previous = _current.Value;
        _current.Value = ownerCreature;
        return previous;
    }

    internal static void PopRaw(Creature? previous) => _current.Value = previous;
}
