# Gameplay design

This document is the source of truth for gameplay decisions. The permanent concept has not been selected yet.

## Identity

- Mod ID: `UnstableHexTest` (temporary)
- Display name: Unstable Hex Test (temporary)
- Author/team: Local Test Team (temporary)
- Short description: Temporary test mod adding Unstable Hex, a colorless card that applies two random enemy debuffs.

## Theme and play style

The initial prototype is a small shared-content mod centered on unpredictable debuffs. A broader theme is pending playtest feedback.

## Core mechanics

The first card uses the run's seeded RNG to select effects, so saving and replaying a seed remains deterministic. No custom mechanic has been approved. Describe each future mechanic in this order:

```text
STATE -> TRIGGER -> EFFECT -> PLAYER FEEDBACK
```

## Content roadmap

1. Make the content mod load.
2. Add and validate `Unstable Hex`.
3. Playtest the one-card prototype before expanding its scope.

## Card specification

### Unstable Hex

- Colorless uncommon Skill available through the shared colorless pool
- Costs 2 Energy
- Targets one enemy
- Applies two distinct randomly selected debuff outcomes
- Random pool: 1 Weak, 1 Vulnerable, 5 Poison, 5 Doom, and 1 Debilitate
- Upgrade: costs 1 Energy
- Uses seeded run RNG rather than nondeterministic system randomness
- Artifact and other normal power-application hooks remain effective

## Balance assumptions

No balance assumptions have been validated yet. Record the game version, relevant unlocks, test deck, encounter context, and BaseLib version with playtest conclusions.

## Decisions

| Date | Decision | Rationale |
| --- | --- | --- |
| 2026-08-28 | Start with an Alchyr content-mod template and BaseLib. | This is the repository baseline and supports a minimal content prototype. |
| 2026-08-28 | Make `Unstable Hex` the first and only prototype card. | It directly implements the requested shared colorless random-debuff concept while keeping initial scope small. |
| 2026-08-28 | Roll two distinct effects from 1 Weak, 1 Vulnerable, 5 Poison, 5 Doom, and 1 Debilitate. | These effects are meaningful on enemies, while distinct rolls ensure the card visibly attempts two debuffs through the game's normal power hooks. |
