using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Rooms;

namespace GhostDuel.Debug;

/// <summary>
/// The M1 stock-Ironclad debug encounter. Declares zero monsters — the Ghost occupies the enemy
/// slot instead, so <c>CombatRoom.StartCombat</c>'s <c>foreach (monsterModel, slot) in
/// Encounter.MonstersWithSlots</c> loop is simply a no-op (confirmed: <c>CombatRoom.cs:207-216</c>,
/// ENGINE-NOTES.md §8) — no engine change is needed to support "no monster." Shaped after the
/// engine's own <c>MockMonsterEncounter</c> (<c>MegaCrit.Sts2.Core.Models.Encounters.Mocks</c>).
/// </summary>
public sealed class GhostDuelEncounter : EncounterModel
{
    public override RoomType RoomType => RoomType.Monster;
    public override bool ShouldGiveRewards => false;
    public override IEnumerable<MonsterModel> AllPossibleMonsters => Array.Empty<MonsterModel>();

    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters() =>
        Array.Empty<(MonsterModel, string?)>();
}
