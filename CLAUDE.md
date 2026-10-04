# CLAUDE.md

A buildings mod for **Kitten Space Agency** (KSA, RocketWerkz). There is one building: the
**Kessler Systems office tower**, 190 m west of launch complex 39A on Earth. It has a lift between a
garage, a lobby and an office; an underground garage reached by an open ramp; a deep spiral ramp
down a shaft into a hall below sea level; ceiling lamps that cast shadows; a chair to sit in and a
computer the game can be played on; and a first-person view for a kitten on foot.

KSA has **no official code-modding API**. Everything here is community tooling against a pre-release
game, so the API moves between builds. The mod is loaded by
[StarMap](https://github.com/StarMapLoader/StarMap), which also ships Harmony.

Sibling mods built from the same tooling: KSArmory, KSACars and KSAGolf. Small shared code is
**vendored**, never referenced, so each mod loads on its own.

## Comments and documentation

**Docs are part of the change.** If a change makes a line here, in `README.md` or in a comment
untrue, fix it in the same commit.

**Comment why, never what.** A comment earns its place only when the reason is not recoverable from
the code: an engine contract, a measured number, an ordering that looks arbitrary and is not. A
sentence or two; anything longer belongs in this file.

**State the fact, not the history.** A comment says what is true now, not what the code used to do
or what broke. `tools/check-comments.sh` fails on history in a comment, on `///` blocks over private
members, and on a doc block with two summaries.

## Committing

Every commit message is a [Conventional Commit](https://www.conventionalcommits.org/):
`feat(lift): ...`, `fix(garage): ...`, `docs: ...`. `tools/check-commit-msg.sh` enforces it as a
`commit-msg` hook (`./tools/install-hooks.sh`) and in CI. No `Co-Authored-By` trailer and no other
attribution footer.

**Do not commit a behaviour fix as a fix until it has been seen in game.** Compiling and passing the
suite are not evidence: nearly everything here is a claim about what the engine does.

## Environment

- **KSA install**: `/mnt/c/Program Files/Kitten Space Agency` (Windows game, WSL dev)
- **KSA build this is built against**: `2026.10.7.5541`, recorded in `ksa-assemblies.lock`
- The system `dotnet` is 8.0 and **cannot build this**: the mod targets **net10.0**. A .NET 10 SDK is
  at `~/.dotnet`. **Use `tools/build.sh` / `tools/test.sh`**, which source `tools/env.sh`.
- The game's assemblies are RocketWerkz's and are **never committed**. `Directory.Build.props`
  finds them, first match wins: `KSA_DLL_DIR`, then `Import/` (`./tools/sync-import.sh`), then a
  sibling `ksa-game-assemblies` checkout, then the game install.
- **A developer's install is marked by a `developer` file beside the DLL**, which `tools/deploy.sh`
  writes and `tools/package.sh` never carries. Only then does the bridge start.
- **The mod writes its own log** to `<KSA user dir>/Logs/KSAStructures.log`, with the session before
  kept as `KSAStructures.prev.log`. `./tools/run.sh --attach` follows it. KSA's own log, the newest
  `KittenSpaceAgency.*.log` in the same folder, is where asset and XML errors appear.
- **An optional vault** under the lift is dug when a file `office-vault.txt` beside the DLL holds a
  depth in metres greater than ten. The lift then has a fourth stop on key **0**.

## Commands

```bash
./tools/check-all.sh                       # everything CI runs; also the pre-push hook
./tools/build.sh                           # build the mod (handles the SDK PATH)
./tools/test.sh                            # the headless tests over Sim/; needs the assemblies, not the game
./tools/validate-parts.py                  # asset XML ids and paths, and the ids the C# names; runs in deploy.sh
./tools/model/checkmesh.py src/KSAStructures/Meshes/KSAOffice.glb --near-max 0   # z-fighting and degenerate UVs
./tools/model/office-deep.py               # regenerate the deep ramp: mesh, caster, colliders, lamps
./tools/check-shaders.sh                   # compile the shader against the game's shader library
./tools/check-boundary.sh                  # Sim/ must not reference KSA types
./tools/check-comments.sh                  # history in comments, XML docs on privates
./tools/check-assemblies.sh                # do the resolved assemblies match the lock?
./tools/package.sh                         # release zip into dist/ -- no symbols, no game DLLs
./tools/deploy.sh                          # build and install into the KSA mods folder
./tools/run.sh                             # build, deploy, launch, show the mod's output
python3 tools/ksa-mcp/server.py cli status # drive a running game through the bridge
```

## Layout

**The source is split by whether it touches KSA.** `Sim/` cannot; `Ksa/` does. The test project
links `Sim/**` wholesale and references no KSA assembly, so a `using KSA;` under `Sim/` fails the
test build.

| Path | What |
| --- | --- |
| **`src/KSAStructures/Sim/`** | **no KSA types, linked into the tests wholesale** |
| `Sim/OfficeDeep.cs` | the deep ramp and hall as numbers, and **what a wheel or a camera stands on over them**: one pit lies under the cover, several turns of road and the hall, so the answer depends on how high the asker stands |
| `Sim/OfficeDeepLayout.cs` | **generated** by `tools/model/office-deep.py`: the deep ramp's lamps |
| `Sim/Settings.cs` | what the player sets: first person, the garage lamps, the verbose log |
| `Sim/HeadLook.cs` | looking round from a kitten's eyes, measured from the world rather than from the kitten |
| `Sim/IMouseDrag.cs` | a view the player turns by dragging with the right button |
| `Sim/StableUp.cs` | an up vector that turns smoothly as a view passes the vertical |
| `Sim/StepGate.cs` | hands a simulation step out once and only once |
| `Sim/FrameLatch.cs` | hands a frame's work to whichever hook reaches it first |
| `Sim/BridgeCommand.cs` | one bridge command, read from text, and a setting set by name |
| `Sim/Vec.cs` | vector helpers |
| **`src/KSAStructures/Ksa/`** | **everything that binds to the game** |
| `Ksa/KSAStructuresMod.cs` | StarMap entry point and frame hooks |
| `Ksa/Office.cs` | places the two landmarks, draws the lift car, and the building's frame |
| `Ksa/OfficeTerrain.cs` | the pits dug at load, the rim's rock held down, and the terrain height answered as roof, ramp or floor |
| `Ksa/OfficeLift.cs` | the lift: the car's motion, who is aboard, the floor keys, and the riders carried with it |
| `Ksa/OfficeFurniture.cs` | the chair and the computer, set down as craft of one part each, once per world |
| `Ksa/OfficeUnderground.cs` | the shadow casters put in the sun's and the lamps' shadow maps, and the lamps themselves |
| `Ksa/OfficeDry.cs` | the volume below sea level in which there is no ocean, to physics and to the renderer |
| `Ksa/OfficeComputer.cs` | the computer: the controls go to another craft and the monitor shows it |
| `Ksa/DarkPass.cs` | the compute pass that takes the sky's ambient out of what is under the ground |
| `Ksa/CoreShaderInclude.cs` | writes the header that lets the shader include KSA's own library, at load, against the player's install |
| `Ksa/FirstPerson.cs` | the main view through the eyes of the kitten being flown on foot |
| `Ksa/FirstPersonHook.cs` | keeps the kitten's own body out of that view and its shadow in it |
| `Ksa/KittenFrame.cs` | a kitten's model space against the world |
| `Ksa/LevelHorizonController.cs` | KSA's fixed camera controller with a supplied up vector, asked for its pose inside the engine's frame pass |
| `Ksa/SceneCamera.cs` | a camera held for a composed shot, from the bridge |
| `Ksa/KsaWorld.cs` | the rest of the contact with KSA: the clock, the vehicles, the main view, the camera windows |
| `Ksa/VehicleCommand.cs` | the one write to a craft's physics state |
| `Ksa/CraftSpawner.cs` | builds a craft of one part and sets it down |
| `Ksa/PreRenderHook.cs` | steps a frame that draws no UI before the render |
| `Ksa/WorldReloadHook.cs` | notices a save being loaded |
| `Ksa/Bridge.cs` | commands from outside the game, as files under `Logs/bridge/KSAStructures` |
| `Ksa/Build.cs` | the version, the KSA build, and whether this is a developer's install |
| `Ksa/Log.cs` | the mod's own log file |
| `Ksa/Ui/Ui.cs` | the menu entry and the settings window |
| `Ksa/Ui/ModMenuEntry.cs` | a copied attribute so ModMenu can list this mod |
| `src/KSAStructures/KSAStructuresOffice.xml` | the static objects and their colliders, the materials, the chair and the computer, the shader. **The block between the `office-deep.py` markers is generated** |
| `src/KSAStructures/Shaders/KSAStructuresDark.comp` | the dark pass's shader. **The garage's layout is restated in it**: pits, air, pillars and lamps |
| `src/KSAStructures/Meshes/`, `Textures/` | art. `KSAOffice.glb` is authored; `KSAOfficeDeep.glb`, `KSAOffice_DeepCaster.glb` and the `KSAOffice_Deep_*` textures are generated |
| `src/KSAStructures/mod.toml` | serves as both the content-mod and StarMap manifest |
| `tests/KSAStructures.Tests/` | links `Sim/**` and runs it headlessly |
| `tools/model/office-deep.py`, `office-deep.json` | the deep ramp's generator and the layout it wrote |
| `tools/model/checkmesh.py` | finds coplanar faces and zero-UV-area triangles in a `.glb` |
| `tools/validate-parts.py` | asset Ids, paths, shared textures, and the Ids the C# names |
| `tools/check-shaders.sh` | compiles the shader with Khronos' `glslangValidator`; skips where there is no install |
| `tools/ksa-mcp/server.py` | an MCP server over the bridge, registered in `.mcp.json`; `cli <tool>` runs one from a shell |
| `tools/vis/vis.py` | what the bridge's pictures are judged with: sheets, animations, the temporal-noise map |

## Asset Ids are fixed

**The two part Ids are `KSArmory_Prefab_OfficeChair` and `KSArmory_Prefab_OfficeMonitor`, and they
stay that.** A save names a part by its Id, and loading one whose part no longer exists throws
`PartTemplate is null` and terminates the game. They carry another mod's prefix because that is
where they were first shipped. `tools/validate-parts.py` treats the prefix as this mod's own.

Every other Id, mesh and texture is `KSAOffice_*`. The shader is `KSAStructuresDarkCompute`.

## Engine facts this stands on

These are what the design follows from.

**Terrain decals can only be added at load.** The planet's renderer sizes its buffers from the
modifier count when it is built, so a decal added later overruns them. `Office.Place` therefore
runs from `[StarMapAllModsLoaded]` and nowhere else, and the vault's depth is read from a file
rather than set in the game. A decal goes into both `ProceduralModifiers.Modifiers` and
`TerrainModifiers`: the count is read off one list and the modifiers are walked off the other.

**A kitten or a craft collides with one launch pad only: the nearest landmark flagged
`IsLaunchPad`, and only within 300 m of it.** A static object is drawn and collided only for such a
landmark. So the tower is one landmark and the lift car is a second, whose `GroundOffset` is the
car's height. The two stand 3 m apart, and the plane halfway between them is the lift doorway: past
it a kitten is nearer the lift's landmark and stands on the car alone.

**A static object stands at the terrain height of its landmark's point.** Both landmarks are 7.6 m
south of the building's line, on ground nothing is dug under, and the lift's height is corrected by
the difference between the two points' terrain.

**The engine draws a landmark's object only to a camera above the tangent plane at that point of the
raw height map.** A camera low in the tunnel is under it, so `Office.AfterRenderData` draws the
tower itself whenever the engine has declined to.

**Static objects cast no sun shadow, take the sky's ambient whatever stands over them (their
shader's ambient occlusion is forced to one), and have no emissive term.** Three things follow:

- The dark comes from **invisible shadow casters**: meshes inside the roof and walls that are put in
  the sun's shadow pool and the lamps' without ever being drawn (`OfficeUnderground.cs`).
- The ambient is taken out afterwards by **the dark pass**. It only scales ambient out; it adds no
  light of its own except what the rooms return onto their own faces and a fixture's glow.
- The **monitor is a part**, not a static object, because only a part's shader glows.

**The lamps are the engine's own spot lights**, submitted every frame per viewport. The nearest ones
cast shadows, as many as the player's shadow slots allow less four left for everything else.

**A texture path shared by two materials loads for only one of them.** Each material has files of
its own, however alike. `validate-parts.py` fails a shared path.

**Wheels follow the terrain-height function, not colliders.** A car over the garage would reach for
the floor of the pit. So `Celestial.GetTerrainHeightFromDirCce` is patched to answer the roof, a
ramp's slab, the deep road's turn or the floor, by where the asker stands. Who is asking is the
craft whose `Vehicle.PrepareWorker` is running on that thread, or else the main camera. Physics
reads the fixed-frame call underneath, which is left alone: it needs the pit.

**A craft set down by latitude and longitude stands on the highest ground under its box plus the
launch pad's `SurfaceHeight`.** `Vehicle.TeleportToLocation` is patched to lend the tower a surface
height for the length of the call, so a craft placed over the garage lands on its roof.

**A write to a craft's physics state survives only from a prefix on `Vehicle.PrepareWorker`.** The
engine copies the state for its worker straight after, and the worker's result overwrites anything
written elsewhere. The lift moves its riders there. A sleeping kitten's launch-pad collider is not
moved, so a rider is kept awake while the car moves.

**The engine's ground contact pushes a body out at 1.5 m/s.** The lift runs at 1.3 m/s so its floor
carries a rider by contact alone, and skips the blind shaft between doorways in one step, car and
riders together.

**Pits render badly from more than about 100 m away.** The ground over everything dug is levelled
by a decal first, and a cover mesh drawn as terrain lies a hand above it. Within 50 m of the camera
the rim's cliff material is displaced up to 0.3 m, through that cover; `OfficeTerrain.TameTheRim`
holds it to the grass's displacement while the camera is near, in the renderer's live material
buffer, and puts it back when the camera leaves.

**The ocean is one sphere round the planet, with no land under it.** A pit below sea level is under
water to everything. Inside one volume (`OfficeDry.cs`) a craft is told the body has no ocean, and
while the camera is in it the ocean and the underwater fog are not drawn.

**`SunbloomRenderer.Render` is where a mod's compute pass goes.** It is public and hands over the
command buffer. Just before it the engine has put the scene colour in a storage layout and the depth
in a sampled one. The dark pass is first among the prefixes there and leaves a compute-write to
compute-read barrier behind it, because another mod may dispatch into the same image next.

**KSA compiles a `<Shader>` asset from any mod's folder**, and resolves an include against the
including file. KSA's own shader library is reached through a generated header holding absolute
paths, written at load into `Shaders/Content/KSAStructuresCore.glsl`. The folder must be called
`Content` and the file is named for this mod, so no other mod's header is found in its place.

## What the mod patches

Every patch is a Harmony patch under an id beginning `com.kesslersystems.ksastructures.`. Nothing in
a prefix or postfix may throw: each runs inside the engine's frame.

| Id | Target | What |
| --- | --- | --- |
| `office` | `LocationReference.UpdateStaticObjectRenderData` | postfix: draws the lift car and the tower, and submits the lamps |
| `office` | `Vehicle.PrepareWorker(SimStep)` | prefix, first: marks the craft being stepped and carries lift riders; postfix, last: clears the mark |
| `office` | `Celestial.GetTerrainHeightFromDirCce(double3, bool)` | postfix: the roof, ramps, deep road and floor |
| `office` | `Vehicle.TeleportToLocation(Celestial, double, double)` | prefix and finalizer: a craft set down over the garage stands on its roof |
| `office` | `SuperMeshRenderSystem.AddPoolToShadowBucket`, `SuperMeshShadowCasterProvider.PrepareShadowCasters` | prefix: the shadow casters |
| `office` | `PhysicsEnvironment.RecomputePositionalValues` | postfix: no ocean inside the dry volume |
| `office` | `OceanRenderer.Render`, `RenderUnderwater`, `GetMaxVisibleDepth` | the sea not drawn from inside the dry volume |
| `darkpass` | `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | prefix, first: the dark pass |
| `firstperson` | `KittenEva.UpdateRenderData`, `AnimatedRenderable.Draw`, `StaticMeshRenderable.Draw`, `KittenLocomotion.StepGrounded` | the kitten hidden from its own eyes, its shadow kept, its walk turned to the look |
| `prerender` | `Program.OnFrameCelestials` | prefix: the step, on a frame with no UI |
| `worldreload` | `Program.OnGameLoaded` | postfix: a save was loaded |

## Frame plumbing

StarMap has three per-frame hooks. The mod's step runs from whichever reaches it first, latched by
`Sim/FrameLatch.cs`: the GUI pass, then the pre-render patch, then the frame postfix. The GUI pass is
skipped while the UI is hidden (F2), which is why there are three.

- **`Office.Place`** at `[StarMapAllModsLoaded]`.
- **`Office.Update(step)`** in the step, on the engine's own simulated step
  (`KsaWorld.ConsumeSimStep`), never on player time: player time runs through a pause.
- **The cameras** (first person, the computer, the bridge's held view) with the step, outside the
  flight gate. A borrowed view nothing restates freezes, and KSA's fixed camera mode reads no input.
- **The lift keys, V, Enter and the monitor's picture** in the GUI pass: ImGui is what sees the keys.

## The bridge

On a developer's install the mod reads JSON commands from `<KSA user dir>/Logs/bridge/KSAStructures/in`
and answers in `out`. Files rather than a socket; a folder of its own so another mod's bridge never
answers. Commands: `status`, `pause`, `resume`, `speed`, `set`, `get`, `step`, `load`, `capture`,
`camera`, `reload_shaders`, `office_view`, `office_light`, `office_put`.

**The bridge reaches any running game with this build.** Ask before sending a command to a game
somebody is playing.

## Regenerating the deep ramp

`tools/model/office-deep.py` writes `KSAOfficeDeep.glb`, `KSAOffice_DeepCaster.glb`, the three
`KSAOffice_Deep_*` textures, the block between the markers in `KSAStructuresOffice.xml`,
`Sim/OfficeDeepLayout.cs` and `tools/model/office-deep.json`. It needs numpy and Pillow, not
Blender. It prints what it wrote: 1788 structure triangles, 76 of cover, 1476 of caster, 730
colliders and 71 lamps as committed.

Three places restate the layout by hand and have to move with it: the constants in
`Sim/OfficeDeep.cs`, the deep shaft and hall bounds in the shader, and the garage's own lamps, which
are in both `OfficeUnderground.cs` and the shader.
