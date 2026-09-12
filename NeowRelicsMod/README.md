# Neow Relics Restored

One of several mod projects in the [`STS2MOD`](../README.md) repository. Adds a selectable Custom
Run modifier, **Neow's Blessing**, that restores Neow's usual relic/curse blessing choice at the
start of Custom and Daily runs — vanilla ends Neow's visit as soon as the run's last modifier
(Draft, Insanity, etc.) is picked, skipping the blessing entirely. With this modifier active, the
run gets both: any other modifier's reward, and Neow's normal blessing choice, just like a standard
run.

Pure C#/Harmony mod (via BaseLib's `CustomModifierModel`), no custom Godot assets — `has_pck: false`
in the manifest, so building is just `dotnet build`, no MegaDot/Godot export step required.

## How it works

- [`NeowRelicsModCode/NeowBlessingModifier.cs`](NeowRelicsModCode/NeowBlessingModifier.cs) — the
  selectable modifier itself. BaseLib discovers `CustomModifierModel` subtypes automatically and
  adds them to the Custom Run modifier list; this one is a pure flag with no combat effect of its
  own, and reuses the game's native Neow/Ancient icon (`ui/run_history/neow.png`) rather than
  shipping art.
- [`NeowRelicsModCode/NeowModifierBlessingPatch.cs`](NeowRelicsModCode/NeowModifierBlessingPatch.cs) —
  Harmony prefix on `Neow.OnModifierOptionSelected`. When the last active modifier is chosen and
  `NeowBlessingModifier` is active, it runs the modifier as normal, then temporarily clears the
  run's modifiers and calls the game's own `Neow.GenerateInitialOptions()` again to produce the
  identical blessing choice a standard run would get, and shows that page instead of ending the
  event.
- [`NeowRelicsModCode/NeowNoModifierPageFallbackPatch.cs`](NeowRelicsModCode/NeowNoModifierPageFallbackPatch.cs) —
  covers picking `NeowBlessingModifier` with no other modifier active: since it contributes no
  Neow page of its own, vanilla would otherwise leave Neow with zero options. Postfix on
  `Neow.GenerateInitialOptions` that shows the blessing choice immediately in that case.
- [`NeowRelicsModCode/NeowMultiplayerSyncPatch.cs`](NeowRelicsModCode/NeowMultiplayerSyncPatch.cs) —
  the blessing page above is applied asynchronously; in multiplayer, each peer mirrors every other
  player's Neow event and applies choices as they arrive over the network
  (`EventSynchronizer.ChooseOptionForEvent`), which rejects an option index that's out of range for
  the peer's current page. This patches `EventSynchronizer.HandleEventOptionChosenMessage` so a
  peer finishes applying a player's pending Neow choice before applying that player's next one,
  scoped to Neow events only.

All of the above was verified against the actual game/BaseLib assemblies (decompiled sources under
a local `.tools/sts2-src` reference, not checked into this repo) rather than guessed from behavior;
`dotnet build` compiles cleanly against the real `sts2.dll`/`BaseLib.dll`. The multiplayer patch's
reasoning has not been confirmed with a live two-client session.

## Dependencies

- Slay the Spire 2 installed through Steam (built and tested against v0.107.1)
- .NET 9 SDK or newer
- BaseLib (installed as its own mod; this project references `mods/BaseLib/BaseLib.dll` directly
  rather than a NuGet package, so it always compiles against whatever BaseLib build is actually
  installed — referencing a different version than what's installed causes a
  `ReflectionTypeLoadException` when the mod loader scans this assembly)

## Quick start

1. Install Slay the Spire 2 and the BaseLib mod through Steam.
2. `dotnet build` from this folder — `Sts2PathDiscovery.props` auto-detects the Steam library and
   game/BaseLib paths; override `Sts2Path`/`SteamLibraryPath` there if auto-detection fails.
3. The build copies `NeowRelicsMod.dll`/`.pdb`/`.json` into the game's `mods/NeowRelicsMod/` folder
   automatically.
4. Launch the game, enable "Neow Relics Restored" under Settings → Mod Settings, restart, and start
   a Custom run with the "Neow's Blessing" modifier active.
