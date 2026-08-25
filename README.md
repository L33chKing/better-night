# Better Night

Vanilla night in Nuclear Option is basically pure black. Better Night lifts it to a plausible moonlit level, mines the map's road network with street lamps that cast real light pools on the ground. Blow one up and the pole physically topples.

It's fully client-side and multiplayer-safe.

The plugin file is `RealisticNight.dll` (GUID `com.leech.realisticnight`) because the project started life as "RealisticNight" — the display name is Better Night. The rank-gated NVG/thermal optics are a separate mod, Tiered NVG.

<!-- drop a screenshot or two here once you have them -->

---

## Features

**Night**
- Raises the near-black night with world-space ambient. Your cockpit is a separate camera so it stays dark inside. Tunable strength + colour tint.

**Existing lights**
- Boosts city building windows (only the already-lit ones, dark roofs left alone), vehicle/ship/aircraft nav and engine lights, and everything else emissive (windmills, antennas, beacons). Master intensity over the lot, plus a building draw-distance multiplied so the cities are visible more than 10km way.
**Street lights** (the map ships none, these are spawned)
- Placed along the whole road network and classified City / Main / Rural by local road density, which sets per-class spacing and colour (cool white LED for city/main, warm sodium for rural).
- Low-poly cobra-head lamp posts (round tapered shaft, swept mast arm, cobra-head luminaire) as one instanced mesh across thousands of poles.
- Real ground-light pools drawn by a custom URP full-screen pass that rebuilds the ground from depth and adds each lamp's light. This sidesteps URP's 8-lights-per-surface cap, so the whole map can be lit.

**Destructible poles**
- Poles are physically knocked down by the game's real explosion shockwaves, using its own overpressure model. Violent near the blast, still standing at the weak far edge.

---

## Requirements

- Nuclear Option 0.34.1 (built and tested on this build)
- BepInEx 5.x
- BepInEx.ConfigurationManager 18.4.1 (the in-game config UI, F1)

## Installation

Via NOMM: install through the [Nuclear Option Mod Manager](https://github.com/Combat787/NOMM) and pick Better Night from the list.

Manual:
1. Install BepInEx 5 and ConfigurationManager.
2. Grab the latest `BetterNight-vX.Y.Z.zip` from Releases.
3. Extract so both files land together in `BepInEx/plugins/RealisticNight/`:

```
BepInEx/plugins/RealisticNight/RealisticNight.dll
BepInEx/plugins/RealisticNight/realisticnight_fx
```

The DLL loads the shader bundle from its own folder, so keep the two files together. Launch a mission and press F1 to configure.

## Building from source

Plugin DLL — a .NET Framework 4.7.2 library compiled straight with Roslyn against the game's own assemblies, no NuGet. Fix the paths at the top of `src/build.sh` if your install differs, then:

```
bash src/build.sh
```

Shader bundle (`realisticnight_fx`) — the ground-light and orb shaders ride in an AssetBundle built with Unity 2022.3.62f2 in batch mode. A prebuilt bundle ships in every release, so you only need this if you touch the shaders. See `shader/README.md`.

## License

MIT
