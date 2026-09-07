# AGENTS.md — Slay the Spire 2 Mod

## Mission

This repository is a collaborative **Slay the Spire 2** mod.

Codex should optimize for a project that another contributor can clone from GitHub, configure locally, build, test, and modify without needing files from the original developer's computer.

Priorities:

1. Keep the repository portable.
2. Never commit proprietary game binaries, Steam files, secrets, or machine-specific paths.
3. Get a minimal playable mod working before adding lots of content.
4. Prefer supported APIs/BaseLib hooks over fragile Harmony patches.
5. Keep changes small, reviewable, documented, and Git-friendly.
6. Treat Slay the Spire 2 as an Early Access target whose internals may change.

---

## Technology baseline

Use this stack unless the user explicitly decides otherwise:

- C#
- .NET 9+
- Slay the Spire 2 native mod loader
- MegaDot or the exact compatible Godot .NET version required by the current template
- BaseLib
- Alchyr's Slay the Spire 2 templates
- Git + GitHub

References:

- Template: https://github.com/Alchyr/ModTemplate-StS2
- Template setup: https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup
- BaseLib: https://github.com/Alchyr/BaseLib-StS2
- Community docs: https://tutorials.sts2modding.com/en/
- MegaDot: https://megadot.megacrit.com/
- Workshop: https://steamcommunity.com/workshop/about/?appid=2868840

Do not switch base libraries unless the user explicitly chooses to or a documented compatibility problem requires it.

---

# Before changing files

Inspect the environment and repository first.

At minimum check:

```powershell
git status
git --version
dotnet --version
dotnet --info
Get-ChildItem
```

Also inspect:

- existing `.sln` / `.csproj`
- existing `AGENTS.md`
- current uncommitted work
- generated template files
- existing documentation
- installed STS2/template versions where relevant

Never overwrite an existing mod project just because it differs from this guide.

If the repository contains user changes, preserve them.

---

# Bootstrap mode

Use bootstrap mode only when no working STS2 mod project exists.

## Required identity

A real mod needs:

- `MOD_ID` — stable internal identifier, no spaces
- display name
- author/team name
- short description

Do not invent a permanent public mod identity if the user has not provided one.

Do not casually rename `MOD_ID` after development starts because it can affect model IDs, manifests, saves, and dependencies.

---

## Install/check Alchyr templates

Normal install command:

```powershell
dotnet new install Alchyr.Sts2.Templates
```

Inspect current templates/options:

```powershell
dotnet new list
dotnet new alchyrsts2contentmod --help
```

Do not repeatedly reinstall a working template.

---

## Choose a template

Default first prototype: **content mod**.

```powershell
dotnet new alchyrsts2contentmod --ModAuthor "AUTHOR" -o MOD_ID
```

If the project is explicitly a playable character:

```powershell
dotnet new alchyrsts2charmod --ModAuthor "AUTHOR" -o MOD_ID
```

For a minimal non-content mod:

```powershell
dotnet new alchyrsts2mod --ModAuthor "AUTHOR" -o MOD_ID
```

Use the current template's `--help` output as the source of truth.

Requirements:

- project name has no spaces
- use `.sln`, not `.slinx`
- avoid accidentally nesting `RepoName/RepoName/...`
- prefer solution/project at repository root when practical

---

# Local dependencies are NOT repository dependencies

Every developer supplies their own local copies of:

- Slay the Spire 2
- game assemblies such as `sts2.dll`
- MegaDot/Godot
- Steam Workshop content
- BaseLib installation
- Steam library path
- local mod output
- local saves/logs

Never commit or redistribute proprietary STS2 binaries.

Never fetch game DLLs from unofficial mirrors.

Never commit:

- Steam credentials
- GitHub tokens
- API keys
- Workshop credentials
- `.env` secrets
- local absolute paths

---

# Portable local configuration

Prefer the path discovery already provided by the current template.

If manual paths are required, keep them local.

Recommended pattern:

```text
Directory.Build.props
Directory.Build.local.props.example
Directory.Build.local.props    # ignored
```

Example local file:

```xml
<Project>
  <PropertyGroup>
    <Sts2Path>C:\Path\To\Steam\steamapps\common\Slay the Spire 2</Sts2Path>
    <GodotPath>C:\Path\To\MegaDot\MegaDot.exe</GodotPath>
  </PropertyGroup>
</Project>
```

If compatible with the template, the shared props file may optionally import it:

```xml
<Import
  Project="Directory.Build.local.props"
  Condition="Exists('Directory.Build.local.props')" />
```

Do not break working template auto-discovery just to force this pattern.

The important rule is: **machine-specific paths must not be committed**.

---

# `.gitignore`

Preserve the template `.gitignore` and add missing rules rather than replacing it blindly.

Ensure applicable entries include:

```gitignore
# Local machine config
Directory.Build.local.props
local.props
.env
.env.*

# .NET
bin/
obj/

# Godot
.godot/

# IDE
.vs/
.idea/
.vscode/
*.user
*.suo

# Generated output
.builds/
dist/
release/
artifacts/

# Logs/temp
*.log
tmp/
temp/
```

Do not ignore source assets, localization, manifests, design docs, or scripts needed to reproduce the mod.

---

# Expected project organization

Follow the generated template when names differ, but maintain these conceptual boundaries:

```text
/
├─ AGENTS.md
├─ README.md
├─ CONTRIBUTING.md
├─ CHANGELOG.md
├─ .gitignore
├─ Directory.Build.props
├─ Directory.Build.local.props.example
├─ *.sln
├─ *.csproj
├─ <ModId>.json
│
├─ <ModId>Code/
│  ├─ entry point
│  ├─ Cards/
│  ├─ Relics/
│  ├─ Powers/
│  ├─ Potions/
│  ├─ Events/
│  ├─ Mechanics/
│  ├─ Patches/
│  └─ Utilities/
│
├─ <ModId>/
│  ├─ localization/
│  ├─ images/
│  └─ scenes/resources/
│
├─ docs/
│  ├─ SETUP.md
│  ├─ DESIGN.md
│  ├─ ARCHITECTURE.md
│  └─ TESTING.md
│
└─ scripts/
```

One content class per file is preferred.

Examples:

```text
Cards/Impact.cs
Relics/PrototypeRelic.cs
Powers/MomentumPower.cs
Mechanics/Momentum.cs
```

Do not create giant files containing unrelated cards, relics, patches, and helpers.

---

# Documentation to establish during bootstrap

## `README.md`

Include:

- mod concept
- status
- dependencies
- supported STS2 branch/version when known
- quick setup/build instructions
- contribution link
- current milestone

## `docs/SETUP.md`

Document:

1. install STS2 through Steam
2. install .NET
3. install MegaDot/current compatible Godot .NET
4. install BaseLib
5. clone repo
6. configure local paths if auto-discovery fails
7. restore/build
8. publish locally
9. launch STS2
10. verify mod appears under Settings -> Mod Settings
11. test the example content

Never make one developer's absolute Steam path the official setup.

## `CONTRIBUTING.md`

Document:

- feature branches
- focused commits
- pull requests
- build/test expectations
- no proprietary binaries
- no secrets
- code/content organization

## `docs/DESIGN.md`

Use it as the gameplay-design source of truth.

Track:

- theme
- intended play style
- mechanics
- card/relic concepts
- balance assumptions
- important design decisions

Do not leave important design decisions only in chat history.

---

# Build and publish

Use the current generated template's supported commands.

At minimum:

```powershell
dotnet restore
dotnet build
```

Important:

- C#-only edits may require only a build.
- localization/images/scenes/assets usually require the publish/export process that rebuilds the PCK.

A typical deployed mod contains:

```text
<ModId>.dll
<ModId>.pck
<ModId>.json
```

depending on the manifest.

A successful `dotnet build` is **not** proof that the mod works in game.

End-to-end verification means STS2 actually loads it.

---

# Optional helper scripts

If useful and not already provided by the template, create:

```text
scripts/doctor.ps1
scripts/build.ps1
scripts/publish.ps1
```

`doctor.ps1` should check without modifying the machine:

- Git
- .NET
- solution/project
- STS2 path
- expected game assembly path
- MegaDot/Godot path
- BaseLib presence when detectable
- local props/config

`build.ps1` should validate and build.

`publish.ps1` should validate, publish/export, deploy locally, and print the output path.

Do not duplicate or fight template scripts that already do this well.

---

# First development milestones

Do not generate dozens of cards immediately.

## Milestone A — mod loads

Done when:

- project builds
- project publishes locally
- STS2 launches
- mod appears in mod list
- no startup exception is caused by the mod

## Milestone B — one simple card

Use a boring test card first.

Suggested placeholder:

**Impact**

- Colorless
- 1 energy
- Deal 12 damage
- Upgrade: deal 16 damage

Verify:

- model/class discovery
- localization
- art/placeholder resource
- acquisition or console spawning
- energy cost
- target selection
- damage
- upgrade
- no play-time exception

Do not add the custom central mechanic before this works.

## Milestone C — one relic

Add one simple relic to test a separate lifecycle.

Example:

- at combat start, gain 1 Strength

Verify:

- acquisition
- localization/tooltip
- combat trigger
- save/load behavior where applicable

## Milestone D — core mechanic prototype

Only after A-C:

- implement one mechanic
- make roughly three cards use it in different ways
- test interactions
- then refactor repeated stable logic

---

# Modding architecture

Use the least invasive mechanism possible.

Preferred order:

1. game/modding API
2. BaseLib API
3. existing hooks
4. inheritance/extension
5. Harmony patch

Do not Harmony-patch internal methods merely because it is possible.

If a Harmony patch is necessary:

- place it under `Patches/`
- document why a supported hook was insufficient
- minimize patched surface area
- record the tested game version
- re-test after STS2 updates

---

# Early Access compatibility

Assume updates can break internal APIs.

When an update causes a failure:

1. reproduce it
2. record game version/branch
3. record BaseLib version
4. inspect the user's current installed game signatures/API as needed
5. inspect current template/BaseLib docs
6. make the smallest compatibility fix
7. document meaningful compatibility changes

Do not permanently vendor decompiled game source into Git.

---

# Stable identifiers

Treat these as APIs once public:

- mod ID
- model IDs
- save keys
- config keys
- localization keys

Do not casually rename released IDs or reuse old IDs for different content.

Preserve save compatibility where practical.

---

# Localization and assets

Keep user-facing text in localization files rather than hard-coded throughout gameplay code.

When adding content, update localization in the same change.

Verify names, descriptions, upgrades, variables, keywords, and tooltips.

Use placeholder art early.

For assets:

- use consistent filenames
- keep license/credit information for external assets
- do not commit copyrighted STS2 assets without permission
- publish/export after resource changes
- verify packaged resources resolve in game

---

# Testing levels

## 1. Isolated

Spawn/grant the new content and test its primary behavior.

## 2. Interaction

Test applicable combinations with:

- powers
- relics
- upgrades
- generated/duplicated cards
- exhaust/discard
- empty piles
- full hand
- multiple enemies

## 3. Real run

Play without debug spawning to find design/progression problems.

## 4. Edge cases

Test applicable cases such as:

- zero stacks
- high stacks
- combat ending during an effect
- player/enemy death during chained effects
- save/reload
- multiple copies
- multiplayer if supported/affected

Compilation alone is not gameplay validation.

---

# Git workflow

Use Git from the beginning.

Preferred model:

```text
main
  ├─ feature/card-impact
  ├─ feature/momentum
  ├─ feature/relic-prototype
  └─ fix/save-load
```

Keep `main` reasonably playable.

For each feature:

1. update from `main`
2. use a focused branch
3. make one logical change
4. build/test
5. commit
6. push
7. open PR
8. review before merge

Good commit messages:

```text
feat: add Impact test card
feat: add Momentum power
fix: preserve Momentum on save reload
docs: document Windows setup
refactor: centralize Momentum spending
```

Avoid meaningless messages such as `stuff`, `changes`, or `codex work`.

Do not force-push shared branches unless the team explicitly agrees.

---

# GitHub publishing

When the user asks to create/push the GitHub repository:

1. verify Git status
2. verify `.gitignore`
3. inspect staged files
4. ensure no game DLLs/secrets/local paths are included
5. create a clean initial commit
6. use `gh` if installed and authenticated
7. otherwise leave the repo ready and provide the next command
8. push `main`
9. use PRs for collaboration

Before first push run:

```powershell
git status
git diff --cached
git ls-files
```

Explicitly look for files that should not be committed:

```text
sts2.dll
0Harmony.dll
*.log
Directory.Build.local.props
.env
```

Do not choose public versus private repository visibility without the user's decision.

---

# GitHub Actions / CI

Do not commit or upload STS2 game binaries merely to make hosted CI compile.

A normal GitHub runner does not contain the user's Steam installation.

Early CI may safely run checks that do not require proprietary assemblies, such as:

- JSON validation
- Markdown checks
- formatting/lint checks that do not require game binaries
- repository consistency checks
- forbidden-binary checks

Only add a full CI build when the project has a legal, documented dependency strategy or a suitable self-hosted runner.

---

# Pull-request expectations

PRs should state:

- what changed
- why
- how it was tested
- game branch/version tested
- BaseLib version when relevant
- screenshots/GIFs for visual changes when useful
- known limitations
- gameplay/balance implications

Avoid unrelated refactors in feature PRs.

---

# Design progression

For a larger character/content project, prefer:

```text
1 card
1 relic
1 mechanic
3 mechanic cards
~10-card vertical slice
playtest
refactor
~30 cards
playtest
full content
balance
polish
release
```

Do not generate a full 70+ card pool before validating the core mechanic.

For mechanics, reason in this order:

```text
STATE
  -> TRIGGER
  -> EFFECT
  -> PLAYER FEEDBACK
```

A mechanic is not finished if players cannot understand its state or why it triggered.

---

# Definition of done for Codex tasks

Before declaring a code task complete:

- inspect `git diff`
- confirm no unrelated changes
- build when the environment permits
- run relevant checks
- update localization for player-facing content
- update docs when setup/architecture changes
- verify no machine paths/proprietary files were added
- state what was actually tested
- state what could not be tested

If assets/localization changed, run the publish/export step when possible.

If the game cannot be launched in the current environment, explicitly say that in-game verification remains.

---

# Bootstrap completion checklist

Initial setup is complete when:

- [ ] Git repository exists
- [ ] STS2 project/solution exists
- [ ] `.sln` is used
- [ ] BaseLib dependency is configured
- [ ] local paths are portable/untracked
- [ ] game DLLs are not committed
- [ ] `.gitignore` is correct
- [ ] `README.md` exists
- [ ] `CONTRIBUTING.md` exists
- [ ] `docs/SETUP.md` exists
- [ ] `docs/DESIGN.md` exists
- [ ] local build succeeds
- [ ] local publish succeeds
- [ ] mod appears in STS2
- [ ] one simple test card works
- [ ] repository is ready to push to GitHub

Do not skip the playable test-card milestone just to make the repository look complete.

---

# Suggested first prompt to Codex

After placing this file in an empty repository directory:

> Set up this repository as a collaborative Slay the Spire 2 content mod following AGENTS.md. Inspect my environment first. Keep machine-specific paths local and do not commit any game binaries. Get the project as far as possible toward a locally publishable mod, create the Git/documentation structure, and verify each step. Do not create or push a public GitHub repository until I explicitly tell you to.

After the scaffold works:

> Add the first test card, Impact: a 1-cost colorless attack that deals 12 damage and upgrades to 16. Follow the repository conventions, add localization, build/publish it, and tell me exactly how to test it in game.

---

# Final rule

If a shortcut makes the project work only on one developer's computer, do not take that shortcut.
