# KSA Structures

Buildings for **Kitten Space Agency** (RocketWerkz) that a kitten can walk into and a car can drive
under. There is one so far: the **Kessler Systems office tower**, 190 m west of launch complex 39A.

- a tower with a **lift**: stand in the car and press **1** garage, **2** lobby, **3** office
- an **underground garage** reached by a ramp, dark and lit by its own ceiling lamps, with shadows
- a **deep spiral ramp** down a shaft into a hall 54 m under the ground, below sea level and dry
- an **office chair** to sit in and a **computer** on the desk: sit down and press **Enter** to fly
  the nearest craft from the monitor, **Enter** again to log off
- a **first-person view** for a kitten on foot: **V**, then hold the right mouse button to look round

Settings are under **KSA Structures** in the menu bar.

KSA has no official code-modding API; this uses the community
[StarMap](https://github.com/StarMapLoader/StarMap) loader and is built against KSA build
`2026.10.7.5541`. A different build may need a rebuild of the mod.

## Install

You need **Kitten Space Agency** and **[StarMap](https://github.com/StarMapLoader/StarMap/releases)**.

1. Build the archive with `./tools/package.sh` and unzip it into the `mods` folder of KSA's user
   directory, so that `mods/KSAStructures/KSAStructures.dll` sits beside `mod.toml`. On Windows that
   directory is `Documents\My Games\Kitten Space Agency\`.
2. Register it in `manifest.toml`, in the same user directory:

   ```toml
   [[mods]]
   id = "KSAStructures"
   enabled = true
   ```

3. Launch through StarMap, not the game directly.

The mod writes its own log to `Logs/KSAStructures.log` under the KSA user directory. It should say
`office: landmarks placed; tower resolved, lift resolved` once the game has loaded.

The site is dug into the terrain while the game loads, so the mod cannot be switched on or off in a
running game: restart after changing `manifest.toml`.

## Building

```bash
./tools/build.sh        # needs a .NET 10 SDK and KSA's assemblies; says where it looked if not
./tools/test.sh         # the headless tests
./tools/check-all.sh    # everything CI runs
./tools/deploy.sh       # build and install into the KSA mods folder, as a developer's install
```

`CLAUDE.md` has the layout, the engine facts the mod stands on, and the rules for working in it.

## Licence

MIT, see `LICENSE`. The game's assemblies are RocketWerkz's and are never committed here.
