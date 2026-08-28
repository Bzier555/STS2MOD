# Contributing

Thank you for helping with this Slay the Spire 2 mod. Keep changes portable, focused, and easy for another contributor to reproduce.

## Workflow

1. Create a focused branch from `main`, such as `feature/card-impact` or `fix/save-load`.
2. Keep each commit to one logical change.
3. Update localization with player-facing content.
4. Update design or architecture documentation when decisions change.
5. Run `./scripts/build.ps1` and the relevant tests.
6. Run `./scripts/publish.ps1` when assets, localization, scenes, or other PCK content changes.
7. Open a pull request and request review before merging.

Use descriptive commit messages such as `feat: add Impact test card` or `docs: clarify Windows setup`.

## Repository safety

Never commit:

- Slay the Spire 2 or Steam binaries
- `sts2.dll`, `0Harmony.dll`, or other proprietary game assemblies
- BaseLib or Workshop installation files
- MegaDot/Godot executables
- Steam credentials, GitHub tokens, API keys, or other secrets
- `.env` files or `Directory.Build.local.props`
- absolute paths tied to one machine

Before staging, review `git status`, `git diff`, and the staged file list. If a change works only with an untracked local dependency, document how each contributor supplies that dependency.

## Code and content organization

Follow the generated Alchyr template. Prefer one card, relic, power, potion, event, mechanic, or patch per file. Use supported game APIs and BaseLib hooks before considering Harmony. Put unavoidable Harmony patches under `Patches/` and document why no supported hook was sufficient and which game version was tested.

Keep user-facing text in localization files. Keep externally sourced asset licenses and credits with the project.

## Pull requests

Describe what changed, why it changed, how it was tested, the game branch/version, the BaseLib version, known limitations, and gameplay or balance implications. Include screenshots or short recordings when they make visual changes easier to review.

