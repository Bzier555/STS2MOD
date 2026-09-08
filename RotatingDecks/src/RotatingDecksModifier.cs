using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace RotatingDecks;

/// <summary>
/// Custom-run modifier model. BaseLib discovers this type and adds its canonical
/// instance to the Custom Run modifier list.
/// </summary>
public sealed class RotatingDecksModifier : CustomModifierModel, ILocalizationProvider
{
    public override ModifierAlignment Alignment => ModifierAlignment.Good;

    public override int SortOrder => 100;

    /// <summary>
    /// Diagnostic run-lifetime count. Modifier saved properties are included in
    /// both normal run saves and STS2's multiplayer packet serialization.
    /// </summary>
    [SavedProperty]
    public int CombatRotationCount { get; set; }

    public List<(string, string)> Localization => new ModifierLoc(
        "Rotating Decks",
        "At the start of each combat, every player's current deck is permanently passed to the next player.");
}
