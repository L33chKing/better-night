using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Batch-mode entry point: validates our fullscreen shaders, then bundles a MATERIAL for each (bundling
// only a shader lets Unity strip its GPU variants -> isSupported=False at runtime; a material keeps the
// variant alive). Run:
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod BuildBundles.Build -logFile build.log
public static class BuildBundles
{
    const string outDir = "AssetBundlesOut";
    const string bundleName = "realisticnight_fx";

    // shader asset path -> material asset path (both ride in the one bundle).
    // Only the shaders currently USED by the mod are bundled. Thermal.shader / ENVG.shader are archived
    // source (shelved) — re-add their (shader, mat) tuples here to rebuild them. See README-SHADERS.md.
    static readonly (string shader, string mat)[] items = new[]
    {
        ("Assets/StreetLightFX.shader", "Assets/StreetLightMat.mat"),
        ("Assets/StreetOrb.shader",     "Assets/StreetOrbMat.mat"),
        ("Assets/HeadlightSpotFX.shader", "Assets/HeadlightSpotMat.mat"), // v2: directional headlight spot pools
        ("Assets/InstancedGlow.shader", "Assets/InstancedGlowMat.mat"), // instanced cones + lens decals
        ("Assets/ShipBeam.shader", "Assets/ShipBeamMat.mat"), // soft searchlight beam cones
        ("Assets/MissileFX.shader", "Assets/MissileMat.mat"), // per-lamp-range missile exhaust pools
    };

    public static void Build()
    {
        // D3D11 and D3D12 share the same compiled DXBC program in Unity, so a D3D11-compiled variant runs
        // fine on the game's forced-D3D12 device. Build for D3D11 ONLY (the batch-mode D3D12 backend can
        // emit an empty/stripped program the runtime then can't use).
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { GraphicsDeviceType.Direct3D11 });

        var matPaths = new System.Collections.Generic.List<string>();
        foreach (var it in items)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(it.shader);
            if (shader == null) { Debug.LogError("[BuildBundles] shader not found at " + it.shader); return; }
            foreach (var m in ShaderUtil.GetShaderMessages(shader))
                Debug.Log($"[ShaderMsg] {shader.name} {m.severity} p={m.platform} line={m.line}: {m.message}");
            Debug.Log($"[ShaderCheck] '{shader.name}' isSupported={shader.isSupported}");

            var mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(it.mat) };
            AssetDatabase.CreateAsset(mat, it.mat);
            matPaths.Add(it.mat);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Directory.CreateDirectory(outDir);
        var build = new AssetBundleBuild
        {
            assetBundleName = bundleName,
            assetNames = matPaths.ToArray() // shaders ride along as dependencies, variants intact
        };
        BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        Debug.Log("[BuildBundles] wrote material bundle -> " + Path.GetFullPath(Path.Combine(outDir, bundleName)));

        TestLoad();
    }

    // Loads the freshly-built bundle back and reports each shader's REAL state, so we can tell whether the
    // compiled programs survived bundling WITHOUT launching the game.
    public static void TestLoad()
    {
        string path = Path.Combine(outDir, bundleName);
        var ab = AssetBundle.LoadFromFile(path);
        if (ab == null) { Debug.LogError("[TestLoad] could not load bundle at " + path); return; }
        foreach (var it in items)
        {
            var m = ab.LoadAsset<Material>(it.mat);
            var sh = m != null ? m.shader : null;
            Debug.Log($"[TestLoad] {it.mat}: material={(m != null)} shader='{(sh != null ? sh.name : "NULL")}' " +
                      $"isSupported={(sh != null && sh.isSupported)} passCount={(sh != null ? sh.passCount : -1)} " +
                      $"propCount={(sh != null ? sh.GetPropertyCount() : -1)}");
        }
        ab.Unload(false);
    }
}
