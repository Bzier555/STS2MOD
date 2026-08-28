# Local setup

Every contributor supplies their own legal installation of the game and development tools. Do not copy game or Workshop binaries into this repository.

## 1. Install prerequisites

1. Install Slay the Spire 2 through Steam.
2. Install the .NET 9 SDK or newer from Microsoft. The SDK is required; a runtime alone is insufficient.
3. Download MegaDot 4.5.1 from Mega Crit. If MegaDot is unavailable, use only the Godot .NET version that exactly matches the template's requirement.
4. Subscribe to BaseLib in the Slay the Spire 2 Steam Workshop. As a fallback, use an official BaseLib GitHub release and install it in the game's local `mods` directory.
5. Install Git and clone this repository.

BaseLib should ultimately provide its `.dll`, `.pck`, and `.json` at runtime. Do not add those files to Git.

## 2. Install the project template

The generated project records the compatible template structure, so this is mainly needed when creating a new project or comparing template options:

```powershell
dotnet new install Alchyr.Sts2.Templates
dotnet new list
dotnet new alchyrsts2contentmod --help
```

Do not repeatedly reinstall a working template.

## 3. Configure local paths

The Alchyr template normally discovers a default Steam installation. Keep that behavior when it works.

MegaDot is not normally auto-discovered. Copy the example only when local overrides are needed:

```powershell
Copy-Item Directory.Build.local.props.example Directory.Build.local.props
```

Edit the copied file with paths from your own machine. `Directory.Build.local.props` is ignored and must never be committed. Environment variables `STS2_PATH` and `GODOT_PATH` can also be used by the diagnostic script.

## 4. Diagnose and build

```powershell
./scripts/doctor.ps1
./scripts/build.ps1
```

The build restores NuGet dependencies, compiles the mod, and may copy code output to the game's local mods folder according to the generated template.

## 5. Publish assets

Localization, images, scenes, and other resources are packaged into the PCK only during publish/export:

```powershell
./scripts/publish.ps1
```

A deployed content mod normally contains its `.dll`, `.pck`, and manifest `.json` in a mod-specific folder beneath the game's `mods` directory.

## 6. Verify in game

1. Launch Slay the Spire 2 through Steam.
2. Accept the modded-mode prompt and restart if asked.
3. Open `Settings -> Mod Settings` and confirm the mod appears.
4. Check the game log for startup exceptions.
5. Spawn or acquire the example content and test its behavior.

A successful `dotnet build` is not proof that the mod loads or behaves correctly. Record the game and BaseLib versions used for every meaningful in-game test.

## Troubleshooting

- If `dotnet --version` fails or reports no SDK, install the .NET SDK and reopen PowerShell.
- If the game assembly is missing, verify the game files through Steam.
- If publish reports a missing Godot path, configure the MegaDot executable in the untracked local props file.
- If the mod compiles but does not load, verify that BaseLib is installed and enabled and inspect the game logs.

