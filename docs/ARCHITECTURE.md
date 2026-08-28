# Architecture

The generated Alchyr content-mod template defines the concrete file names. Preserve its Godot project layout while maintaining the following boundaries:

- `<ModId>Code/` contains C# entry points and gameplay code.
- `<ModId>/` contains localization, images, scenes, and other packaged resources.
- `Patches/` contains only unavoidable Harmony patches with compatibility notes.
- `Mechanics/` contains stable shared gameplay logic after a mechanic has been validated.
- `Utilities/` contains narrowly scoped helpers rather than gameplay content.
- `docs/` records setup, design, architecture, and test expectations.
- `scripts/` contains portable PowerShell entry points for diagnostics, builds, and publishing.

## Dependency order

Prefer the least invasive integration available:

1. Slay the Spire 2 modding API
2. BaseLib API
3. Existing hooks
4. Inheritance or extension
5. Harmony patch

The project references installed game assemblies for compilation but does not redistribute them. Local path overrides belong in ignored configuration. Stable mod IDs, model IDs, localization keys, save keys, and config keys are treated as public APIs once released.

## Early Access compatibility

Record the tested game branch/version and BaseLib version for compatibility work. When an update breaks the mod, reproduce against the contributor's installed assemblies, make the smallest fix, and document meaningful compatibility changes. Do not vendor decompiled game source.

