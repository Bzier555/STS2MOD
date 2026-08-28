# Unstable Hex Test

This repository contains a collaborative Slay the Spire 2 content-mod prototype. It adds one shared colorless card, `Unstable Hex`, which applies two distinct random enemy debuffs.

The current mod ID (`UnstableHexTest`), display name, and `Local Test Team` authorship are deliberately temporary. Test saves and model IDs created with this identity should be treated as disposable before the project receives its permanent public identity.

## Status

The Alchyr content-mod project builds with no warnings or errors and publishes locally with BaseLib 3.4.5. The generated DLL, PCK, and manifest are installed, and the game log confirms the mod loads in modded mode. Card behavior still needs hands-on combat testing.

The local bootstrap environment contains Slay the Spire 2 v0.107.1 on the Steam public branch. That is an environment observation, not a compatibility claim.

## Dependencies

- Slay the Spire 2 installed through Steam
- .NET 9 SDK or newer
- MegaDot 4.5.1, or the exactly compatible Godot .NET build
- BaseLib
- Alchyr's Slay the Spire 2 templates
- Git

Game files, Steam content, BaseLib binaries, MegaDot, local paths, and credentials are developer-supplied and must not be committed.

## Quick start

1. Follow [the setup guide](docs/SETUP.md).
2. Run `./scripts/doctor.ps1` from PowerShell.
3. Run `./scripts/build.ps1` for code changes.
4. Run `./scripts/publish.ps1` after localization or asset changes.
5. Launch Slay the Spire 2 and verify the mod under `Settings -> Mod Settings`.

See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Gameplay decisions belong in [docs/DESIGN.md](docs/DESIGN.md).

## Sharing development

After the repository is connected to GitHub, friends can clone it and supply their own local STS2, BaseLib, and Godot installations:

```powershell
git clone <repository-url>
cd STS2MOD
Copy-Item Directory.Build.local.props.example Directory.Build.local.props
# Edit Directory.Build.local.props with local paths if auto-discovery fails.
./scripts/doctor.ps1 -RequireGodot -RequireBaseLib
./scripts/build.ps1
```

Use feature branches and pull requests for changes. Do not share `Directory.Build.local.props`, game files, generated output, or save files; they are machine-specific and ignored by Git.

## Current milestone

Milestone A: generate the content-mod project, build and publish it, then confirm that it loads without startup exceptions. The first content milestone is `Unstable Hex`, the single colorless card specified in [docs/DESIGN.md](docs/DESIGN.md).
