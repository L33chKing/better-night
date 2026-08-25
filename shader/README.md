# Shader bundle — `realisticnight_fx`

This folder holds the URP shader sources for Better Night's ground-light and glow-orb effects, plus the
batch-mode build script. The compiled bundle (`realisticnight_fx`) is shipped in each release, so you only
need to rebuild it if you change a shader.

## Contents (`Assets/`)
- `StreetLightFX.shader` — the full-screen ground-light pass (reconstructs the ground from depth and adds
  each lamp's light volume + composite).
- `StreetOrb.shader` — the GPU-billboarded glow "lens" orb.
- `StreetLightMat.mat`, `StreetOrbMat.mat` — materials that keep each shader's GPU variants alive through
  bundling (bundling a bare shader lets Unity strip its variants → `isSupported == false` at runtime).
- `Editor/BuildBundles.cs` — batch-mode entry point.
- `.meta` files — keep the asset GUIDs stable across rebuilds.

> Only the two shaders **used by Better Night** are here. The Thermal / ENVG shaders belong to the separate
> *Tiered NVG* mod and are intentionally not included.

## Build (Unity 2022.3.62f2)
1. Create (or reuse) a URP project on **Unity 2022.3.62f2**.
2. Copy this `Assets/` content into the project's `Assets/`.
3. Run:
   ```
   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod BuildBundles.Build -logFile build.log
   ```
4. Grab the output `AssetBundlesOut/realisticnight_fx` and place it next to `RealisticNight.dll`.

`BuildBundles.Build` compiles for **D3D11 only** on purpose (Unity's batch-mode D3D12 backend can emit an
empty/stripped program the runtime can't use; the D3D11 DXBC runs fine on the game's D3D12 device). It also
reloads the bundle and logs each shader's real `isSupported` / `passCount` so you can confirm the variants
survived without launching the game.
