using BaseLib.Utils;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Powers;

namespace UnstableHexTest.UnstableHexTestCode.Cards;

[Pool(typeof(ColorlessCardPool))]
public sealed class UnstableHex() : UnstableHexTestCard(2, CardType.Skill, CardRarity.Uncommon, TargetType.AnyEnemy)
{
    private enum Debuff
    {
        Weak,
        Vulnerable,
        Poison,
        Doom,
        Debilitate
    }

    private static readonly Debuff[] DebuffPool = Enum.GetValues<Debuff>();

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
    [
        HoverTipFactory.FromPower<WeakPower>(),
        HoverTipFactory.FromPower<VulnerablePower>(),
        HoverTipFactory.FromPower<PoisonPower>(),
        HoverTipFactory.FromPower<DoomPower>(),
        HoverTipFactory.FromPower<DebilitatePower>()
    ];

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        Debuff[] choices = (Debuff[])DebuffPool.Clone();
        Owner.RunState.Rng.Niche.Shuffle(choices);

        await ApplyDebuff(choiceContext, cardPlay.Target, choices[0]);
        await ApplyDebuff(choiceContext, cardPlay.Target, choices[1]);
    }

    protected override void OnUpgrade()
    {
        EnergyCost.UpgradeBy(-1);
    }

    private async Task ApplyDebuff(PlayerChoiceContext choiceContext, Creature target, Debuff debuff)
    {
        switch (debuff)
        {
            case Debuff.Weak:
                await PowerCmd.Apply<WeakPower>(choiceContext, target, 1, Owner.Creature, this);
                break;
            case Debuff.Vulnerable:
                await PowerCmd.Apply<VulnerablePower>(choiceContext, target, 1, Owner.Creature, this);
                break;
            case Debuff.Poison:
                await PowerCmd.Apply<PoisonPower>(choiceContext, target, 5, Owner.Creature, this);
                break;
            case Debuff.Doom:
                await PowerCmd.Apply<DoomPower>(choiceContext, target, 5, Owner.Creature, this);
                break;
            case Debuff.Debilitate:
                await PowerCmd.Apply<DebilitatePower>(choiceContext, target, 1, Owner.Creature, this);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(debuff), debuff, null);
        }
    }
}
