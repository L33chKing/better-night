# Better Night

Vanilla night in Nuclear Option is basically pure black. Better Night lifts it to a plausible moonlit level, lines the road network with street lamps that cast real light pools, and gives ground vehicles headlights and ships deck floods plus turret-slaved searchlights. Blow a lamp post up and it physically topples.

Fully client-side and multiplayer-safe. `RealisticNight.dll` (GUID `com.leech.realisticnight`) plus the `realisticnight_fx` shader bundle, kept together in `BepInEx/plugins/RealisticNight/`. Rank-gated NVG/thermal optics are the separate Tiered NVG mod.

---

## Features

**Night** — world-space ambient lift (cockpit stays dark), tunable strength + tint.

**City windows** — boosts already-lit building windows only, with distance haze and extended draw distance.

**Street lights** (spawned, the map ships none) — placed along all roads, classed City / Main / Rural by road density (spacing + white LED vs warm sodium). Cobra-head posts, glow orbs, and ground-light pools from a custom URP pass (depth-reconstructed, so no 8-light cap). Pools keep the ground's own colour and scale with its reflectivity instead of painting it white. Poles topple with real physics from the game's own shockwaves.

**Headlights** — volumetric cones + hull lens decals + forward spot pools on ground vehicles (modeled lenses where found, bumper mounts where not). Red tail/brake lights included.

**Ship lights** — deck flood pools (count scales with hull length) + a searchlight slaved to the forward-most turret's aim (mast fixture fallback). Ships keep their vanilla nav lights; the mod adds working lights only.

---

## Requirements

- Nuclear Option 0.34.1
- BepInEx 5.x
- BepInEx.ConfigurationManager 18.4.1 (in-game config UI, F1)

## Installation

Via NOMM: pick Better Night from the list.

Manual:
1. Install BepInEx 5 and ConfigurationManager.
2. Grab the latest `BetterNight-vX.Y.Z.zip` from Releases.
3. Extract both files together into `BepInEx/plugins/RealisticNight/`:

```
BepInEx/plugins/RealisticNight/RealisticNight.dll
BepInEx/plugins/RealisticNight/realisticnight_fx
```

Launch a mission, press F1 to configure.

## Building from source

Plugin DLL (Roslyn, no NuGet, against the game's own assemblies):

```
bash src/build.sh
```

Shader bundle (`realisticnight_fx`, Unity 2022.3.62f2 batch mode; only needed if you touch shaders):

See `shader/README.md` (process) and `shader/BUILD.md` (exact commands).

## License

MIT
