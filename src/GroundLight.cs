using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RoadPathfinding;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RealisticNight
{
    // Deferred ground-light pass (street + headlight + ship pools). Reconstructs world pos from depth, adds lamp light. Bypasses URP's 8-light cap.
    internal static class StreetLightFX
    {
        public static volatile bool Active;
        static Material mat;
        static bool triedLoad, installed;
        static float nextLoadRetry;
        static StreetLightRendererFeature feature;
        // Lamps live in a StructuredBuffer, so the whole map can be fed (nearest MaxLamps, 10 Hz refresh).
        public const int MaxLamps = 8192;
        static readonly Vector4[] lamps = new Vector4[MaxLamps];
        static ComputeBuffer lampBuf;
        static int lampCount;
        public static int LampCount => lampCount; // instance count for the light-volume draw

        static readonly int PIntensity = Shader.PropertyToID("_Intensity");
        static readonly int PIntensityWarm = Shader.PropertyToID("_IntensityWarm");
        static readonly int PAtmos = Shader.PropertyToID("_AtmosRange");
        static readonly int PAtmosCurve = Shader.PropertyToID("_AtmosCurve");
        static readonly int PRange = Shader.PropertyToID("_Range");
        static readonly int PFalloff = Shader.PropertyToID("_FalloffK");
        static readonly int PNdotL = Shader.PropertyToID("_NdotL");
        static readonly int PTintStrength = Shader.PropertyToID("_TintStrength");
        static readonly int PColor = Shader.PropertyToID("_Color");
        static readonly int PColorWarm = Shader.PropertyToID("_ColorWarm");
        static readonly int PLampsBuf = Shader.PropertyToID("_LampsBuf");
        static readonly int PLampCount = Shader.PropertyToID("_LampCount");

        public static Material Mat { get { EnsureLoaded(); return mat; } }
        static Material orbMat;
        public static Material OrbMat { get { EnsureLoaded(); return orbMat; } } // shared billboard orb material (same bundle)
        static Material spotSrcMat; // v2 bundle only: source for directional headlight spot pools (null on v1)
        internal static Material SpotMat { get { EnsureLoaded(); return spotSrcMat; } }
        static Material shipBeamMat; // searchlight beam shader (null on old bundles -> SoftGlow fallback)
        internal static Material ShipBeamMat { get { EnsureLoaded(); return shipBeamMat; } }
        static Material mslSrcMat; // missile per-lamp-range pools (null on old bundles -> street omni fallback)
        internal static Material MissileMat { get { EnsureLoaded(); return mslSrcMat; } }


        static bool lampsDirty;
        public static void SetLamps(Vector4[] src, int count)
        {
            count = Mathf.Clamp(count, 0, MaxLamps);
            for (int i = 0; i < count; i++) lamps[i] = src[i];
            lampCount = count;
            lampsDirty = true;
        }

        // Free the GPU lamp buffer on full shutoff (Active is cleared first, so the pass can't touch it).
        public static void ReleaseBuffer()
        {
            if (lampBuf != null) { lampBuf.Release(); lampBuf = null; }
            lampsDirty = true; // force a re-upload if we come back
        }

        static void EnsureLoaded()
        {
            // Self-heal: retry bundle load every ~30 s if it was missing (a missing bundle kills ALL pools).
            if (triedLoad)
            {
                if (mat != null || Time.realtimeSinceStartup < nextLoadRetry) return;
                triedLoad = false;
            }
            triedLoad = true;
            nextLoadRetry = Time.realtimeSinceStartup + 30f;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(dir, "realisticnight_fx");
                AssetBundle ab = AssetBundle.LoadFromFile(path);
                if (ab == null) { RealisticNightPlugin.Log.LogWarning($"[StreetLightFX] could not load bundle at {path}"); return; }
                mat = ab.LoadAsset<Material>("Assets/StreetLightMat.mat");
                orbMat = ab.LoadAsset<Material>("Assets/StreetOrbMat.mat");
                spotSrcMat = ab.LoadAsset<Material>("Assets/HeadlightSpotMat.mat"); // headlight spot pools (null on v1 bundles -> omni fallback)
                shipBeamMat = ab.LoadAsset<Material>("Assets/ShipBeamMat.mat"); // soft beam cones (null on old bundles)
                mslSrcMat = ab.LoadAsset<Material>("Assets/MissileMat.mat"); // per-lamp-range missile pools (null on old bundles -> street omni)
            }
            catch (Exception e) { RealisticNightPlugin.Log.LogWarning($"[StreetLightFX] load error: {e.Message}"); }
        }

        public static void Apply(bool on)
        {
            if (!on || lampCount == 0) { Active = false; return; }
            EnsureLoaded();
            if (mat == null) { Active = false; return; }
            mat.SetFloat(PIntensity, RealisticNightPlugin.StreetGroundIntensity.Value);          // white/city lamps
            mat.SetFloat(PIntensityWarm, RealisticNightPlugin.StreetGroundIntensityYellow.Value); // yellow main+rural lamps
            mat.SetFloat(PAtmos, RealisticNightPlugin.StreetLightAtten.Value);                    // shared haze fade
            mat.SetFloat(PAtmosCurve, Mathf.Clamp(RealisticNightPlugin.StreetLightAttenCurve.Value, 0.2f, 3f)); // shared haze curve
            mat.SetFloat(PRange, RealisticNightPlugin.StreetGroundRange.Value);
            // Range-normalized falloff: pool SHAPE is identical at any Range (extending Range re-brightens lit ground).
            float gf = Mathf.Clamp01(RealisticNightPlugin.StreetGroundFalloff.Value);
            float gRange = Mathf.Max(1f, RealisticNightPlugin.StreetGroundRange.Value);
            mat.SetFloat(PFalloff, (gf * gf * 25f) / (gRange * gRange));
            mat.SetFloat(PNdotL, RealisticNightPlugin.StreetGroundShading.Value);
            mat.SetFloat(PTintStrength, Mathf.Clamp01(RealisticNightPlugin.StreetGroundTint.Value));
            // Per-lamp tints (white LED vs warm sodium via _LampsBuf w). Colour picker Alpha trims strength.
            Color whiteC = RealisticNightPlugin.StreetWhite.Value;
            Color yellowC = RealisticNightPlugin.StreetYellow.Value;
            float whiteTrim = Mathf.Clamp01(whiteC.a), yellowTrim = Mathf.Clamp01(yellowC.a);
            mat.SetColor(PColor, new Color(whiteC.r * whiteTrim, whiteC.g * whiteTrim, whiteC.b * whiteTrim, 1f));
            mat.SetColor(PColorWarm, new Color(yellowC.r * yellowTrim, yellowC.g * yellowTrim, yellowC.b * yellowTrim, 1f));
            if (lampBuf == null || !lampBuf.IsValid()) { lampBuf = new ComputeBuffer(MaxLamps, 16, ComputeBufferType.Structured); lampsDirty = true; }
            if (lampsDirty) { lampBuf.SetData(lamps, 0, 0, lampCount); lampsDirty = false; } // lampCount>0 here; upload only on change
            mat.SetBuffer(PLampsBuf, lampBuf);
            mat.SetInt(PLampCount, lampCount);
            Install();
            Active = true;
        }

        static void Install()
        {
            if (installed) return;
            try
            {
                var a = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset ?? QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
                if (a == null) { RealisticNightPlugin.Log.LogWarning("[StreetLightFX] install FAILED: no URP asset (GraphicsSettings.currentRenderPipeline and QualitySettings.renderPipeline both null/not-URP)."); return; }
                var fList = AccessTools.Field(typeof(UniversalRenderPipelineAsset), "m_RendererDataList");
                var fIdx = AccessTools.Field(typeof(UniversalRenderPipelineAsset), "m_DefaultRendererIndex");
                if (fList == null || fIdx == null) { RealisticNightPlugin.Log.LogWarning("[StreetLightFX] install FAILED: m_RendererDataList/m_DefaultRendererIndex fields not found on UniversalRenderPipelineAsset (game URP version mismatch?)."); return; }
                var list = fList?.GetValue(a) as ScriptableRendererData[];
                int idx = fIdx != null ? (int)fIdx.GetValue(a) : 0;
                if (list == null || idx < 0 || idx >= list.Length || list[idx] == null) { RealisticNightPlugin.Log.LogWarning($"[StreetLightFX] install FAILED: renderer data unusable (listNull={list == null} len={(list != null ? list.Length : -1)} idx={idx})."); return; }
                feature = ScriptableObject.CreateInstance<StreetLightRendererFeature>();
                feature.name = "RN_StreetLightFeature";
                list[idx].rendererFeatures.Add(feature);
                list[idx].SetDirty();
                installed = true;
            }
            catch (Exception e) { RealisticNightPlugin.Log.LogWarning($"[StreetLightFX] install error: {e.Message}"); }
        }

        // Pool-pipeline status in one line (diagnostics toggle, 30 s): lamp counts actually FED to
        // each backend this frame, whether its pass is armed, and whether its material exists.
        // fed=0 while its lamps are plainly visible as orbs/fixtures = feed-side stall (build/cull);
        // fed>0 + active but nothing on the ground = composite-side failure (install/pass/blit).
        internal static void LogPoolStatus(bool night)
        {
            try
            {
                bool datumNull = true;
                try { datumNull = Datum.origin == null; } catch { }
                bool streetMatNull = true, headMatNull = true, deckMatNull = true, searchMatNull = true, mslMatNull = true;
                try { streetMatNull = Mat == null; } catch { }
                try { headMatNull = HeadlightPools.Mat == null; } catch { }
                try { deckMatNull = ShipLights.DeckMat == null; } catch { }
                try { searchMatNull = ShipLights.SearchMat == null; } catch { }
                try { mslMatNull = MissileExhaust.PoolMat == null; } catch { }
                RealisticNightPlugin.Log.LogInfo($"[Pools] night={night} datumNull={datumNull} installed={installed} | street fed={LampCount} active={Active} matNull={streetMatNull} | head fed={HeadlightPools.LampCount} active={HeadlightPools.Active} matNull={headMatNull} | deck={ShipLights.DeckLamps} deckMatNull={deckMatNull} search={ShipLights.SearchLamps} searchMatNull={searchMatNull} | msl={MissileExhaust.PoolLamps} mslMatNull={mslMatNull}");
            }
            catch { }
        }
    }

    internal class StreetLightRendererFeature : ScriptableRendererFeature
    {
        StreetLightPass pass;
        public override void Create() { pass = new StreetLightPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing }; }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
        {
            bool streetOn = StreetLightFX.Active && StreetLightFX.Mat != null;
            bool headOn = HeadlightPools.Active && HeadlightPools.Mat != null;
            bool shipDeckOn = ShipLights.DeckMat != null;
            bool shipSearchOn = ShipLights.SearchMat != null;
            bool mslOn = MissileExhaust.PoolMat != null;
            if (!streetOn && !headOn && !shipDeckOn && !shipSearchOn && !mslOn) return;
            if (data.cameraData.cameraType != CameraType.Game) return;
            if (data.cameraData.renderType != CameraRenderType.Base) return;
            pass.Setup(streetOn ? StreetLightFX.Mat : null, headOn ? HeadlightPools.Mat : null,
                       shipDeckOn ? ShipLights.DeckMat : null, shipSearchOn ? ShipLights.SearchMat : null,
                       mslOn ? MissileExhaust.PoolMat : null);
            renderer.EnqueuePass(pass);
        }
    }

    internal class StreetLightPass : ScriptableRenderPass
    {
        Material mat;
        Material headMat;
        Material deckMat;   // ship deck floods (own omni backend)
        Material searchMat; // ship bow searchlight (own spot backend)
        Material mslMat;    // missile exhaust omni pools (own backend, same shaders)
        RTHandle temp;      // full-res scene copy (composite input)
        RTHandle volRT;     // full-res per-lamp volume light
        static readonly int PInvVP = Shader.PropertyToID("_RN_InvVP");
        static readonly int POriginPos = Shader.PropertyToID("_RN_OriginPos");
        static readonly int PVolLight = Shader.PropertyToID("_VolLight");
        static readonly int PPreserve = Shader.PropertyToID("_Preserve");
        static readonly int PVP = Shader.PropertyToID("_RN_VP");
        static readonly int PCamRight = Shader.PropertyToID("_RN_CamRight");
        static readonly int PCamUp = Shader.PropertyToID("_RN_CamUp");
        static readonly int PCamPos = Shader.PropertyToID("_RN_CamPos");
        static readonly int PRNTime = Shader.PropertyToID("_RN_Time"); // missile flame flicker clock
        const int VolumePass = 0;    // RN_StreetLight_Volume
        const int CompositePass = 1; // RN_StreetLight_Composite
        // Skip degenerate (0-sized) camera targets (UI overlays) — a 0x0 volume RT crashes natively.
        static bool Degenerate(ref RenderingData data)
        {
            var d = data.cameraData.cameraTargetDescriptor;
            return d.width < 4 || d.height < 4;
        }
        public void Setup(Material m, Material hm, Material dm, Material sm, Material mm)
        {
            mat = m;
            headMat = hm;
            deckMat = dm;
            searchMat = sm;
            mslMat = mm;
            ConfigureInput(ScriptableRenderPassInput.Depth); // need _CameraDepthTexture for world reconstruction
        }
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
        {
            if (Degenerate(ref data)) return;
            RenderTextureDescriptor d = data.cameraData.cameraTargetDescriptor;
            d.depthBufferBits = 0;
            RenderingUtils.ReAllocateIfNeeded(ref temp, d, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_RN_StreetTemp");
            // Full-res volume light buffer (RGBA-half HDR). The lamps draw into this, then we composite.
            RenderTextureDescriptor vd = d;
            vd.colorFormat = RenderTextureFormat.ARGBHalf;
            RenderingUtils.ReAllocateIfNeeded(ref volRT, vd, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_RN_StreetVolRT");
        }
        // Singular/invalid view-projection (camera mid-teleport/hitch): drawing pools would flash garbage.
        static bool VpValid(Matrix4x4 m)
        {
            try
            {
                Vector4 r0 = m.GetRow(0), r1 = m.GetRow(1), r2 = m.GetRow(2), r3 = m.GetRow(3);
                float s = r0.x + r0.y + r0.z + r0.w + r1.x + r1.y + r1.z + r1.w + r2.x + r2.y + r2.z + r2.w + r3.x + r3.y + r3.z + r3.w;
                if (float.IsNaN(s) || float.IsInfinity(s)) return false;
                float d = m.determinant;
                if (float.IsNaN(d) || float.IsInfinity(d) || Mathf.Abs(d) < 1e-12f) return false;
                return true;
            }
            catch { return false; }
        }
        static bool BadCamPose(Camera c) // NaN camera transform poisons every uniform below
        {
            try
            {
                if (c == null) return true;
                Vector3 p = c.transform.position, r = c.transform.right, u = c.transform.up;
                float s = p.x + p.y + p.z + r.x + r.y + r.z + u.x + u.y + u.z;
                return float.IsNaN(s) || float.IsInfinity(s);
            }
            catch { return true; }
        }
        public override void Execute(ScriptableRenderContext context, ref RenderingData data)
        {
            if (Degenerate(ref data)) return;
            int count = (mat != null) ? StreetLightFX.LampCount : 0;
            int hcount = (headMat != null) ? HeadlightPools.LampCount : 0;
            int dcount = (deckMat != null) ? ShipLights.DeckLamps : 0;
            int scount = (searchMat != null) ? ShipLights.SearchLamps : 0;
            int mcount = (mslMat != null) ? MissileExhaust.PoolLamps : 0;
            if (count <= 0 && hcount <= 0 && dcount <= 0 && scount <= 0 && mcount <= 0) return;

            Camera cam = data.cameraData.camera;
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            if (!VpValid(vp) || BadCamPose(cam)) return; // never composite garbage: skip the frame
            Vector3 originPos = Datum.origin != null ? Datum.origin.position : Vector3.zero;
            // Floating origin: ground - origin = RAW GLOBAL, matching the lamp buffer (read at render time).
            if (mat != null)
            {
                mat.SetMatrix(PInvVP, vp.inverse); // world reconstruction: ComputeWorldSpacePosition(uv, rawDepth, _RN_InvVP)
                mat.SetMatrix(PVP, vp);
                mat.SetVector(PCamRight, cam.transform.right);
                mat.SetVector(PCamUp, cam.transform.up);
                mat.SetVector(PCamPos, cam.transform.position);
                mat.SetVector(POriginPos, originPos);
                mat.SetFloat(PPreserve, Mathf.Clamp01(RealisticNightPlugin.StreetGroundColorPreserve.Value));
            }
            if (headMat != null)
            {
                headMat.SetMatrix(PInvVP, vp.inverse);
                headMat.SetMatrix(PVP, vp);
                headMat.SetVector(PCamRight, cam.transform.right);
                headMat.SetVector(PCamUp, cam.transform.up);
                headMat.SetVector(PCamPos, cam.transform.position);
                headMat.SetVector(POriginPos, originPos);
                // NOTE: headlight _Preserve stays 0 (set in HeadlightPools.Apply) - additive by design.
            }
            // Ship backends share the frame uniforms; their look lives in ShipLights.ApplyDeckLook/SearchLook.
            if (deckMat != null)
            {
                deckMat.SetMatrix(PInvVP, vp.inverse);
                deckMat.SetMatrix(PVP, vp);
                deckMat.SetVector(PCamRight, cam.transform.right);
                deckMat.SetVector(PCamUp, cam.transform.up);
                deckMat.SetVector(PCamPos, cam.transform.position);
                deckMat.SetVector(POriginPos, originPos);
            }
            if (searchMat != null)
            {
                searchMat.SetMatrix(PInvVP, vp.inverse);
                searchMat.SetMatrix(PVP, vp);
                searchMat.SetVector(PCamRight, cam.transform.right);
                searchMat.SetVector(PCamUp, cam.transform.up);
                searchMat.SetVector(PCamPos, cam.transform.position);
                searchMat.SetVector(POriginPos, originPos);
            }
            // Missile pools share the frame uniforms; look lives in MissileExhaust.ApplyLook.
            if (mslMat != null)
            {
                mslMat.SetMatrix(PInvVP, vp.inverse);
                mslMat.SetMatrix(PVP, vp);
                mslMat.SetVector(PCamRight, cam.transform.right);
                mslMat.SetVector(PCamUp, cam.transform.up);
                mslMat.SetVector(PCamPos, cam.transform.position);
                mslMat.SetVector(POriginPos, originPos);
                try { mslMat.SetFloat(PRNTime, Time.time); } catch { } // flame flicker clock (no-op on street fallback)
            }
            CommandBuffer cmd = CommandBufferPool.Get("RN_StreetLightFX");
            RTHandle src = data.cameraData.renderer.cameraColorTargetHandle;

            // Fixtures (orbs, poles, cones, decals) are regular merged-mesh GameObjects now — the scene
            // renders them; this pass only composites the deferred ground pools below.

            // One volRT sum per preserve mode (street keeps hue, headlights add) -> separate composites.
            if (count > 0 && mat != null)
            {
                int sVol = VolumePass, sComp = CompositePass;
                CoreUtils.SetRenderTarget(cmd, volRT, ClearFlag.Color, Color.clear);
                cmd.DrawProcedural(Matrix4x4.identity, mat, sVol, MeshTopology.Triangles, 6, count);
                mat.SetTexture(PVolLight, volRT);
                Blitter.BlitCameraTexture(cmd, src, temp);                    // scene -> temp (full res copy)
                Blitter.BlitCameraTexture(cmd, temp, src, mat, sComp); // temp(scene) + volRT -> src
            }
            if (hcount > 0 && headMat != null)
            {
                int hVol = HeadlightPools.VolumePassIndex, hComp = HeadlightPools.CompositePassIndex;
                CoreUtils.SetRenderTarget(cmd, volRT, ClearFlag.Color, Color.clear);
                cmd.DrawProcedural(Matrix4x4.identity, headMat, hVol, MeshTopology.Triangles, 6, hcount);
                headMat.SetTexture(PVolLight, volRT);
                Blitter.BlitCameraTexture(cmd, src, temp);
                Blitter.BlitCameraTexture(cmd, temp, src, headMat, hComp);
            }
            // Ship deck floods, then the searchlight on top (separate composites, same reason).
            if (dcount > 0 && deckMat != null)
            {
                CoreUtils.SetRenderTarget(cmd, volRT, ClearFlag.Color, Color.clear);
                cmd.DrawProcedural(Matrix4x4.identity, deckMat, ShipLights.DeckVol, MeshTopology.Triangles, 6, dcount);
                deckMat.SetTexture(PVolLight, volRT);
                Blitter.BlitCameraTexture(cmd, src, temp);
                Blitter.BlitCameraTexture(cmd, temp, src, deckMat, ShipLights.DeckComp);
            }
            if (scount > 0 && searchMat != null)
            {
                CoreUtils.SetRenderTarget(cmd, volRT, ClearFlag.Color, Color.clear);
                cmd.DrawProcedural(Matrix4x4.identity, searchMat, ShipLights.SearchVol, MeshTopology.Triangles, 6, scount);
                searchMat.SetTexture(PVolLight, volRT);
                Blitter.BlitCameraTexture(cmd, src, temp);
                Blitter.BlitCameraTexture(cmd, temp, src, searchMat, ShipLights.SearchComp);
            }
            // Missile exhaust pools on top (own omni backend, same shaders).
            if (mcount > 0 && mslMat != null)
            {
                CoreUtils.SetRenderTarget(cmd, volRT, ClearFlag.Color, Color.clear);
                cmd.DrawProcedural(Matrix4x4.identity, mslMat, MissileExhaust.PoolVol, MeshTopology.Triangles, 6, mcount);
                mslMat.SetTexture(PVolLight, volRT);
                Blitter.BlitCameraTexture(cmd, src, temp);
                Blitter.BlitCameraTexture(cmd, temp, src, mslMat, MissileExhaust.PoolComp);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
    }
}
