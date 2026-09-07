# STS2MOD

A monorepo of independent Slay the Spire 2 mod projects. Each subfolder is a self-contained mod with
its own build, dependencies, and documentation — there is no shared solution or shared build step
across projects.

## Projects

| Project | What it is |
| --- | --- |
| [`GhostDuel/`](GhostDuel/) | Fight an AI-piloted Ghost of a previous run's build — a real enemy-side `Player` using your own cards and relics. Pure C#/Harmony mod, no Godot project/PCK. Start with [`GhostDuel/PLAN.md`](GhostDuel/PLAN.md). |
| [`UnstableHexTest/`](UnstableHexTest/) | A collaborative content-mod prototype adding `Unstable Hex`, a colorless card. Built on Alchyr's Slay the Spire 2 Godot template (has its own `project.godot`, PCK, and localization). Start with [`UnstableHexTest/README.md`](UnstableHexTest/README.md). |

## Working in this repo

Each project folder is independent:

- Build/run/test instructions live in that project's own README (and `docs/`, `CONTRIBUTING.md`, or
  `CLAUDE.md`/`AGENTS.md` where present).
- Each project resolves its own Slay the Spire 2 / BaseLib / Godot paths via its own
  `Sts2PathDiscovery.props` and (where applicable) `Directory.Build.local.props` — these are
  machine-specific and git-ignored per project.
- A change to one project should not require touching another. Cross-project shared tooling does not
  exist yet; if two projects genuinely need to share code, that should be a deliberate decision, not
  an accident of folder proximity.

Do not commit game/Steam binaries, BaseLib or Workshop installation files, Godot/MegaDot executables,
credentials, or machine-specific local override files — see each project's own contributing docs for
specifics.

## Adding a new mod project

Create a new top-level folder named for the mod, self-contained (its own project file(s), source,
docs, and path-discovery props). Add a row to the table above.
