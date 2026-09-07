# Testing

## Required checks

Run the repository diagnostic and build scripts:

```powershell
./scripts/doctor.ps1
./scripts/build.ps1
```

When localization, images, scenes, or other packaged resources change, also run:

```powershell
./scripts/publish.ps1
```

Before handoff, inspect `git diff` and confirm that no proprietary binaries, secrets, local configuration, or absolute machine paths were added.

## Gameplay levels

1. Isolated: spawn or grant the new content and test its primary behavior.
2. Interaction: test relevant powers, relics, upgrades, generated or duplicated cards, pile transitions, hand limits, and multiple enemies.
3. Real run: acquire and use the content without debug spawning.
4. Edge cases: test zero and high stacks, combat-ending effects, deaths during chains, save/reload, and multiple copies where applicable.

## Test record

For each in-game test, record:

- date
- game branch and version
- BaseLib version
- mod commit
- scenario and result
- relevant log excerpts or screenshots
- known limitations

Compilation and publishing are necessary checks, but only an actual game launch validates that the mod loads.

## Unstable Hex checklist

- The card appears in the shared colorless pool and can be spawned with `card UNSTABLEHEXTEST-UNSTABLE_HEX`.
- It costs 2 Energy before upgrading and 1 Energy after upgrading.
- It requires one enemy target.
- Each play selects two different entries from 1 Weak, 1 Vulnerable, 5 Poison, 5 Doom, and 1 Debilitate.
- Each selected debuff applies its specified amount through the normal power command.
- Artifact blocks applications normally.
- Random results remain deterministic across a save/reload of the same run state.
- The card behaves safely when the target or combat ends during effect resolution.

## Quick in-game test

1. Publish with `./scripts/publish.ps1` while the game is closed.
2. Launch Slay the Spire 2 through Steam and confirm BaseLib and Unstable Hex Test are enabled.
3. Start a run with any character and enter combat.
4. Open the dev console with `~` and enter `card UNSTABLEHEXTEST-UNSTABLE_HEX`.
5. Enter `energy 20`, close the console, and play the card on one enemy.
6. Confirm two distinct outcomes from the documented pool appear and no error is logged.
7. Spawn another copy, then use `upgrade 0` (or its zero-based hand position) and confirm the upgraded card costs 1 Energy.
8. Enter `showlog` and check for exceptions mentioning `UnstableHexTest` or `UnstableHex`.
