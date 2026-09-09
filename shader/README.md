# Shader bundle — `realisticnight_fx`

This folder holds the URP shader sources for Better Night's ground-light and glow-orb effects, plus the
batch-mode build script. The compiled bundle (`realisticnight_fx`) is shipped in each release, so you only
need to rebuild it if you change a shader.

## Contents (`Assets/`)
- `StreetLightFX.shader` — the full-screen ground-light pass (reconstructs the ground from depth and adds
  each lamp's light volume + composite).
- `StreetOrb.shader` — the GPU-billboarded glow "lens" orb.
- `HeadlightSpotFX.shader` — directional headlight spot pools (per-lamp beam dir + cone
  angle). Used automatically for headlight pools; the mod falls back to omni on bundles lacking it.
- `StreetLightMat.mat`, `StreetOrbMat.mat` — materials that keep each shader's GPU variants alive through
  bundling (bundling a bare shader lets Unity strip its variants → `isSupported == false` at runtime).
- `HeadlightSpotMat.mat` — same variant-keeper role for the spot shader (the mod looks it up by name; old
  bundles without it fall back to omni pools).
- `Editor/BuildBundles.cs` — batch-mode entry point.
- `.meta` files — keep the asset GUIDs stable across rebuilds.

> Only the two shaders **used by Better Night** are here. The Thermal / ENVG shaders belong to the separate
> *Tiered NVG* mod and are intentionally not included.

## Build (Unity 2022.3.62f2)
1. Install **Unity 2022.3.62f2** from Unity Hub (Add Modules → just the editor; your Hub already manages
   other versions — 2022.3.x can live side by side with Unity 6).
2. Create (or reuse) a URP project on **Unity 2022.3.62f2**. Its `Packages/manifest.json` MUST include
   `com.unity.modules.assetbundle` (any 1.0.x) alongside URP — without that package the build finishes
   silently but writes a manifest-less bundle the game rejects (`module AssetBundle is disabled in the
   build` in the editor log, `could not load bundle ... not compatible` at runtime). Minimal manifest:
   ```json
   { "dependencies": {
       "com.unity.render-pipelines.universal": "14.0.9",
       "com.unity.modules.assetbundle": "1.0.0" } }
   ```
3. Copy this `Assets/` content into the project's `Assets/` (keep the `.meta` files — the mod finds
   `HeadlightSpotMat` by asset name, and stable GUIDs keep the `.mat` → shader links intact).
4. Run:
   ```
   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod BuildBundles.Build -logFile build.log
   ```
5. Grab the output `AssetBundlesOut/realisticnight_fx` and place it next to `RealisticNight.dll`.
6. In-game, the log shows `Spot pools ARMED` and headlights use the spot shader automatically.

`BuildBundles.Build` compiles for **D3D11 only** on purpose (Unity's batch-mode D3D12 backend can emit an
empty/stripped program the runtime can't use; the D3D11 DXBC runs fine on the game's D3D12 device). It also
reloads the bundle and logs each shader's real `isSupported` / `passCount` so you can confirm the variants
survived without launching the game.
