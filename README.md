# 🌙 Better Night

**A darker, more realistic night — with procedural street lighting — for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/).**

Vanilla night is near-pure black. **Better Night** lifts the night to a plausible moonlit floor, makes the lights that already exist (city windows, nav lights, beacons) actually read, and lines the map's road network with low-poly **cobra-head street lamps** that cast real light pools on the ground. Blow one up and the pole physically topples.

Everything is **100% client-side and multiplayer-safe** — no server install — and every effect fully **reverts** when you switch the mod off.

<!-- Add a screenshot or two here once you have them, e.g.:
![Night city](docs/city.jpg)
-->

---

## ✨ Highlights

- 🌑 **Realistic night floor** — raises the black night via world ambient (your cockpit is a separate camera, so it stays dark inside).
- 💡 **Better existing lights** — city building windows, vehicle/ship/aircraft nav & engine lights, windmill/antenna/beacon glows, with a master intensity and a building draw-distance lever so the city glow doesn't cull too soon.
- 🛣️ **Procedural street lights** along the whole road network — automatically classified **City / Main / Rural** (per-class spacing; cool white LED vs warm sodium).
- 🔩 **Low-poly cobra-head lamp posts** — round tapered shaft, swept mast arm, cobra-head luminaire — one instanced mesh across thousands of poles.
- 🔦 **Real ground-light pools** via a custom URP full-screen pass that rebuilds the ground from depth and adds each lamp's light — **bypassing URP's 8-lights-per-surface cap**, so the whole map can be lit.
- 💥 **Destructible poles** — knocked down by the game's real explosion shockwaves using its own overpressure model: violent near the blast, standing at the weak far edge.
- 🎛️ **Tune everything live** with **F1** (ConfigurationManager). Toggle the whole mod off and the world snaps back to vanilla.

> **File-name note:** the plugin DLL is `RealisticNight.dll` / GUID `com.leech.realisticnight` (historical — the project began as "RealisticNight"). The display name is **Better Night**. The rank-gated NVG/thermal optics live in a separate mod, *Tiered NVG*.

---

## 📦 Requirements

- **Nuclear Option** `0.34.1` (built & tested against this build)
- **BepInEx 5.x**
- **[BepInEx.ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager)** `18.4.1` — the in-game config UI (press **F1**)

## ⬇️ Installation

**Via NOMM (recommended):** install through the [Nuclear Option Mod Manager](https://github.com/Combat787/NOMM) and pick **Better Night** from the list.

**Manual:**
1. Install BepInEx 5 + ConfigurationManager.
2. Download the latest `BetterNight-vX.Y.Z.zip` from [Releases](../../releases).
3. Extract so both files land **together** in `BepInEx/plugins/RealisticNight/`:
   ```
   BepInEx/plugins/RealisticNight/RealisticNight.dll
   BepInEx/plugins/RealisticNight/realisticnight_fx
   ```
   > The DLL loads the shader bundle (`realisticnight_fx`) from its own folder — keep them in the same directory.
4. Launch a mission and press **F1** to configure.

## 🎛️ Configuration (F1)

| Section | Controls |
| --- | --- |
| `1. General` | Master enable · diagnostics logging |
| `2. Night` | Night ambient floor + colour tint |
| `3. Lights` | Master intensity · city windows · vehicle lights · other lights · building draw distance |
| `4. Street Lights` | Enable · day/night behaviour · light sources · poles · spacing · height · haze range |
| `5. …Road Types` | City/Main/Rural classification + per-class spacing |
| `6. …Appearance` | Lens orb brightness / size / falloff |
| `7. …Ground Light` | Pool intensity · range · shading · falloff · colour preservation |
| `8. …Range & Destruction` | Render distance · pole LOD distance · destructible toggle |

Changes apply live.

---

## 🛠️ Building from source

**Plugin DLL** — a .NET Framework 4.7.2 library compiled directly with Roslyn against the game's own assemblies (no NuGet). Edit the paths at the top of [`src/build.sh`](src/build.sh) if your install differs, then:

```bash
bash src/build.sh
```

**Shader bundle (`realisticnight_fx`)** — the ground-light and orb shaders ride in an AssetBundle built with **Unity 2022.3.62f2** in batch mode. A prebuilt bundle ships in every release, so you only need this if you edit the shaders. See [`shader/README.md`](shader/README.md).

---

## 🚀 Publishing to NOMM

The manager's list comes from the **[NOMNOM](https://github.com/KopterBuzz/NOMNOM)** manifest repo. A ready-to-submit manifest is at [`nomnom/RealisticNight.json`](nomnom/RealisticNight.json).

1. Push this repo to GitHub and cut a **release tagged `v0.9.39`**, attaching the exact `BetterNight-v0.9.39.zip` (its `sha256` is baked into the manifest — re-zipping changes it).
2. In `nomnom/RealisticNight.json`, replace `REPLACE_ME_GITHUB_USER` with your GitHub username (and the repo name / download URL if different).
3. Fork NOMNOM, drop the edited `RealisticNight.json` into `modManifests/` (filename must equal the `id`), and open a PR to `main`. A GitHub Action validates it; a maintainer merges. With `autoUpdateArtifacts: "True"`, later releases are picked up automatically.

---

## 🙏 Credits & License

Spiritual successor to **NVGConfig** by *Domiyaaa*. Built on **BepInEx** + **HarmonyX**.
Released under the [MIT License](LICENSE).
