using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace EnchantedRewards;

/// <summary>
/// Custom-run modifier model. BaseLib discovers this type and adds its canonical instance to the
/// Custom Run modifier list.
///
/// Reward-side responsibility: for RoomType.Monster, swaps the normal CardReward for a choice
/// between (up to) two different enchantments, each only offered if the current deck actually has
/// a valid target for it; for RoomType.Elite, removes the RelicReward (elites still grant their
/// CardReward - only the bonus relic is cut). Boss rewards are left completely untouched.
///
/// Combat-side responsibility: because this modifier is always an active hook listener whenever the
/// "Enchanted Rewards" Custom Run modifier is enabled (CombatState.IterateHookListeners() walks
/// combatState.Modifiers unconditionally), it doubles as the place that gives ExtraEnchantments
/// (see that class) their combat-math effect: the base game only ever asks a card's single native
/// Enchantment for EnchantDamageAdditive/EnchantBlockAdditive/EnchantPlayCount, so any *extra*
/// enchantment needs something else to invoke those same methods - these overrides of the generic,
/// non-enchantment-specific AbstractModel hooks do exactly that, replicating the same combination
/// order MegaCrit.Sts2.Core.Hooks.Hook.ModifyDamage/ModifyBlock/CardModel.GetEnchantedReplayCount use
/// for the native slot. (ModifyShuffleOrder, AfterCardDrawn, BeforeFlush, and
/// AfterAutoPrePlayPhaseEntered don't need an override here - EnchantedRewardsMod registers extra
/// enchantments directly as hook listeners via ModHelper.SubscribeForCombatStateHooks, and those
/// hooks are dispatched generically to every listener already. AfterCardPlayed *is* overridden below
/// - see its own doc comment for why, unlike those others, it needs to be here instead.)
/// </summary>
public sealed class EnchantedRewardsModifier : CustomModifierModel, ILocalizationProvider
{
    public override ModifierAlignment Alignment => ModifierAlignment.Good;

    public override int SortOrder => 100;

    public List<(string, string)> Localization => new ModifierLoc(
        "Enchanted Rewards",
        "Normal fights offer an enchantment for a card you already own instead of a new card. Elites no longer grant a relic. Bosses are unchanged.");

    // IMPORTANT: unlike ModifyCardPlayCount below, these four are NOT "pass the running total
    // through" hooks - AbstractModel's own defaults return 0m (additive) / 1m (multiplicative), and
    // real base-game listeners (e.g. StrengthPower.ModifyDamageAdditive returns just `base.Amount`;
    // VulnerablePower.ModifyDamageMultiplicative returns just `1.5m`) only ever return *this
    // listener's own contribution*. The caller (Hook.ModifyDamage/ModifyBlock) is what sums/
    // multiplies every listener's contribution together into a running total.
    //
    // An earlier version of this code returned the running total itself (originalAmount plus/times
    // our contribution) instead of just the contribution. Since this modifier is a hook listener
    // for every card play - not only ones with extra enchantments - that meant the caller added our
    // *entire* (already-inflated) return value on top of the real total again, for every single
    // block/damage instance in the game, compounding on every recomputation (e.g. live UI preview
    // recalculating on hover) into the runaway "Defend cards gain 100 block" bug. Never do that
    // again here: always return only the delta/factor these extra enchantments contribute, and
    // default to 0m/1m exactly like AbstractModel itself does.

    public override decimal ModifyDamageAdditive(Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (cardSource == null)
        {
            return 0m;
        }

        decimal contribution = 0m;
        decimal running = amount;
        foreach (EnchantmentModel extra in ExtraEnchantments.Get(cardSource))
        {
            decimal delta = extra.EnchantDamageAdditive(running, props);
            contribution += delta;
            running += delta;
        }

        return contribution;
    }

    public override decimal ModifyDamageMultiplicative(Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (cardSource == null)
        {
            return 1m;
        }

        decimal contribution = 1m;
        decimal running = amount;
        foreach (EnchantmentModel extra in ExtraEnchantments.Get(cardSource))
        {
            decimal factor = extra.EnchantDamageMultiplicative(running, props);
            contribution *= factor;
            running *= factor;
        }

        return contribution;
    }

    public override decimal ModifyBlockAdditive(Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (cardSource == null)
        {
            return 0m;
        }

        decimal contribution = 0m;
        decimal running = block;
        foreach (EnchantmentModel extra in ExtraEnchantments.Get(cardSource))
        {
            decimal delta = extra.EnchantBlockAdditive(running);
            contribution += delta;
            running += delta;
        }

        return contribution;
    }

    public override decimal ModifyBlockMultiplicative(Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (cardSource == null)
        {
            return 1m;
        }

        decimal contribution = 1m;
        decimal running = block;
        foreach (EnchantmentModel extra in ExtraEnchantments.Get(cardSource))
        {
            decimal factor = extra.EnchantBlockMultiplicative(running);
            contribution *= factor;
            running *= factor;
        }

        return contribution;
    }

    // No ModifyCardPlayCount override here (unlike the four above): extras' EnchantPlayCount
    // contribution is handled by ReplayCountPatch instead, folded directly into
    // CardModel.GetEnchantedReplayCount(). That method's result feeds into Hook.ModifyCardPlayCount
    // (which is what would otherwise call an override here), so adding the contribution in both
    // places would double-count it. See ReplayCountPatch's doc comment for the full reasoning,
    // including why that method needed patching anyway (its result is also what a card's "Replay N"
    // hover tip and description text read directly, with no hook dispatch involved at all).

    /// <summary>
    /// Gives extra enchantments' own OnPlay override (Swift's bonus draw, Sown's bonus energy,
    /// Adroit's block, Corrupted's self-damage, Momentum's ratcheting damage, Inky's Weak) a chance
    /// to run, without needing a Harmony patch on CardModel.OnPlayWrapper (originally planned as
    /// Phase 5's "single highest-risk patch in this project" - turned out to be unnecessary).
    ///
    /// CardModel.OnPlayWrapper calls the *native* slot-1 Enchantment's OnPlay directly, inline,
    /// inside its per-replay loop - never through any Hook.* dispatch, which is exactly why extras
    /// (which don't live in that slot) needed something else. AfterCardPlayed, by contrast, already
    /// *is* dispatched generically (confirmed via a fresh decompile of OnPlayWrapper:
    /// `await Hook.AfterCardPlayed(combatState, choiceContext, cardPlay);` sits inside the same
    /// per-iteration loop, right after the native slot-1 OnPlay call for that same iteration) - so
    /// overriding it here and manually invoking each extra's OnPlay reaches the same "once per actual
    /// play/replay" granularity the native path has, with no IL-level surgery.
    ///
    /// Safe to call unconditionally for every extra regardless of type: EnchantmentModel.OnPlay's
    /// base implementation is a no-op, so this does nothing for extras whose effect lives in a
    /// different hook (Sharp, Nimble, etc.) and only actually matters for the ones that override it.
    ///
    /// One accepted, minor behavioral difference from the native slot-1 case: an extra's OnPlay runs
    /// *after* Hook.AfterCardPlayed's other native effects for that same play (slot-1's own OnPlay
    /// included), rather than inline before them like the native path. Not expected to matter for any
    /// enchantment currently in the pool - none of their effects (draw, energy, block, self-damage,
    /// a ratcheting damage counter, Weak) are order-sensitive relative to unrelated slot-1 effects.
    /// </summary>
    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        foreach (EnchantmentModel extra in ExtraEnchantments.Get(cardPlay.Card).ToList())
        {
            await extra.OnPlay(choiceContext, cardPlay);
            if (cardPlay.Card.Owner?.Creature?.IsDead == true)
            {
                return;
            }

            extra.InvokeExecutionFinished();
        }
    }

    public override bool TryModifyRewards(Player player, List<Reward> rewards, AbstractRoom? room)
    {
        return room?.RoomType switch
        {
            RoomType.Monster => ReplaceCardRewardWithEnchant(player, rewards),
            RoomType.Elite => RemoveEliteRelic(rewards),
            _ => false,
        };
    }

    private static bool RemoveEliteRelic(List<Reward> rewards)
    {
        return rewards.RemoveAll(r => r is RelicReward) > 0;
    }

    private static bool ReplaceCardRewardWithEnchant(Player player, List<Reward> rewards)
    {
        CardReward? cardReward = rewards.OfType<CardReward>().FirstOrDefault();
        if (cardReward == null)
        {
            return false;
        }

        rewards.Remove(cardReward);

        IReadOnlyList<(EnchantmentModel Enchantment, IReadOnlyList<CardModel> Candidates)> rolls =
            EnchantmentPool.RollUpToTwoWithCandidates(player.Deck.Cards);

        switch (rolls.Count)
        {
            case 0:
                // Nothing in the deck can currently take any pool enchantment (e.g. a very early,
                // small/homogeneous deck) - never offer a reward the player can't take.
                EnchantedRewardsMod.Logger.Info(
                    $"No valid enchant target for player {player.NetId}; substituting a gold reward.");
                rewards.Add(new GoldReward(10, 20, player));
                break;

            case 1:
                rewards.Add(new EnchantReward(rolls[0].Enchantment, rolls[0].Candidates, player));
                break;

            default:
                List<Reward> options = rolls
                    .Select(roll => (Reward)new EnchantReward(roll.Enchantment, roll.Candidates, player))
                    .ToList();
                rewards.Add(new LinkedRewardSet(options, player));
                break;
        }

        return true;
    }
}
