using System.Collections.Generic;

namespace GhostDuel.Ai.Cards;

internal sealed class CardSemanticProfile
{
    public IReadOnlyList<NormalizedEffectDescriptor> Effects { get; init; } = [];
}
