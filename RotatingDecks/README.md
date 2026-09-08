# Rotating Decks

An STS2 multiplayer Custom Run modifier that permanently passes every participating player's current deck to the next player at the start of each combat.

## Build

Requirements:

- Slay the Spire 2 0.107.1 or newer
- .NET SDK 9
- BaseLib 3.4.5 or newer installed in the game's `mods/BaseLib` directory

Run:

```powershell
dotnet build RotatingDecks.csproj -c Release
```

The compiled assembly is written to `bin/Release/net9.0/`. To install the mod, place `RotatingDecks.dll` and `RotatingDecks.json` in a `mods/RotatingDecks` directory under the game installation.

## Implementation notes

- Registration uses BaseLib's `CustomModifierModel`; localization is injected in code, so no PCK is required.
- Rotation is a prefix on `CombatManager.SetUpCombat`, before STS2 resets player combat state and clones persistent deck cards into draw piles.
- The game-owned `RunState.Players` list supplies deterministic session ordering.
- Persistent card objects are moved intact and re-owned; cards are not cloned or reconstructed.
- A per-`CombatState` weak marker prevents a repeated setup call from rotating twice.
- `CombatRotationCount` is a normal modifier saved property, so STS2 includes it in save files and multiplayer serialization.
- STS2's existing deterministic multiplayer setup and full combat-state checksums synchronize/validate the resulting player decks.
