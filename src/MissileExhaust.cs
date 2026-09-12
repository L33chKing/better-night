using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealisticNight
{
    // Missile exhaust: vanilla flame boost + ground pools. All visual-only, MP-safe.

    internal sealed class MissileFx : MonoBehaviour
    {
        public Missile missile;
        public float len = 4f;
        public Vector3 localTail = new Vector3(0f, 0f, -2f);
        public readonly List<ParticleSystem> pss = new List<ParticleSystem>();
        public readonly List<Vector3> psOrigScale = new List<Vector3>(); // vanilla flame size (glow slider scales it)
        public readonly List<ParticleSystem.MinMaxGradient> psOrigColor = new List<ParticleSystem.MinMaxGradient>(); // vanilla flame colour (glow slider brightens it, no extra particles)
        public float glowApplied = float.NaN; // last slider value pushed (NaN = never: apply once, not per tick)
        public readonly List<Light> lights = new List<Light>();
        public readonly List<float> lightOrig = new List<float>();
        public readonly List<float> lightRangeOrig = new List<float>();
        public readonly List<TrailEmitter> trails = new List<TrailEmitter>();
            // Per-weapon pool params. mirrorLight = prefab ships a Motor Light: baseRange/brightScale
            // MIRROR its authored range/intensity and the sliders amplify them. Otherwise (light-less
            // modded missiles): thrust/size fallback formula (ComputeBaseRange).
            public float baseRange = 800f;
            public float lightRangeMax;          // authored Light.range max (mirror source)
            public float lightIntensityMax;      // authored Light.intensity max (pool brightness weight)
            public bool mirrorLight;
            public float thrustMax;              // fallback path + log line only
            public float irMax = 1f;
            public float brightScale = 1f;       // per-lamp pool brightness weight (authored, or thrust fallback)
            public float flickSeed;              // per-lamp exhaust flicker phase (shader _Time driven)
            public string jsonKey = "?";

        public void Build(Missile m)
        {
            missile = m;
            try
            {
                if (m != null && m.definition != null)
                {
                    len = Mathf.Max(2f, m.definition.length);
                    try { jsonKey = m.definition.jsonKey ?? "?"; } catch { }
                }
            }
            catch { len = 4f; }
            localTail = new Vector3(0f, 0f, -len * 0.5f);
            glowApplied = float.NaN;
            pss.Clear(); psOrigScale.Clear(); psOrigColor.Clear();
            lights.Clear(); lightOrig.Clear(); lightRangeOrig.Clear(); trails.Clear();
            try
            {
                foreach (var ps in m.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps == null) continue;
                    pss.Add(ps);
                    Vector3 s = Vector3.one;
                    try { s = ps.transform.localScale; } catch { }
                    psOrigScale.Add(s);
                    ParticleSystem.MinMaxGradient sc = new ParticleSystem.MinMaxGradient(Color.white);
                    try { sc = ps.main.startColor; } catch { }
                    psOrigColor.Add(sc);
                }
            }
            catch { }
            try
            {
                foreach (var l in m.GetComponentsInChildren<Light>(true))
                {
                    if (l == null) continue;
                    lights.Add(l);
                    float io = 1f;
                    try { io = l.intensity; } catch { }
                    lightOrig.Add(io);
                    float ro = 0f;
                    try { ro = l.range; } catch { }
                    lightRangeOrig.Add(ro);
                    try { lightRangeMax = Mathf.Max(lightRangeMax, l.range); } catch { }
                    try { lightIntensityMax = Mathf.Max(lightIntensityMax, l.intensity); } catch { }
                }
            }
            catch { }
            try
            {
                foreach (var te in m.GetComponentsInChildren<TrailEmitter>(true))
                    if (te != null) trails.Add(te);
            }
            catch { }
            // Author-tuned mirror (user direction): a prefab that ships a Motor Light already encodes
            // how big that weapon's exhaust should read (AShM 500 m vs AAM 100-200 m) — thrust gets
            // that badly wrong (a low-thrust turbojet AShM outshines a high-thrust AAM in flame size).
            // Mirror the authored range + intensity; the sliders AMPLIFY them. The thrust/size formula
            // (ComputeBaseRange) stays only as the fallback for light-less modded missiles.
            try
            {
                if (lights.Count > 0 && lightRangeMax > 1f)
                {
                    mirrorLight = true;
                    baseRange = lightRangeMax;                                  // authored radius (Range Boost scales it)
                    brightScale = Mathf.Clamp(lightIntensityMax, 0.25f, 2.5f);  // authored brightness weight for the pools
                    MissileExhaust.ComputeBaseRange(m, len, lightRangeMax, out thrustMax, out irMax); // log data only
                }
                else
                {
                    mirrorLight = false;
                    baseRange = MissileExhaust.ComputeBaseRange(m, len, lightRangeMax, out thrustMax, out irMax);
                    // Fallback brightness (no authored light): 30 kN reference -> 1.0.
                    try { brightScale = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, thrustMax) / 30000f), 0.25f, 2.5f); }
                    catch { brightScale = 1f; }
                }
            }
            catch { baseRange = 800f; brightScale = 1f; mirrorLight = false; }
            try { flickSeed = UnityEngine.Random.Range(0f, 100f); } catch { flickSeed = 0f; }
            try { MissileExhaust.LogWeaponOnce(jsonKey, len, thrustMax, irMax, lights.Count, lightRangeMax, lightIntensityMax, baseRange, brightScale, mirrorLight); }
            catch { }
        }

        void OnDestroy()
        {
            pss.Clear(); psOrigScale.Clear(); psOrigColor.Clear();
            lights.Clear(); lightOrig.Clear(); lightRangeOrig.Clear(); trails.Clear();
        }
    }

    internal static class MissileExhaust
    {
        static readonly Dictionary<int, MissileFx> fxById = new Dictionary<int, MissileFx>();
        static float nextScan;
        static bool lastOn;

        // Deferred pools: per-lamp range in _LampsBuf.w + per-lamp brightness/seed in _SpotDirs.
        // MissileFX (own shader) reads both; street omni is the fallback on old bundles (no _SpotDirs there,
        // so it gets a backend WITHOUT dirs to avoid setting unknown uniforms).
        public const int PoolCap = 512;
        static Vector4[] poolBuf;
        static Vector4[] poolDirBuf; // x = thrust brightScale, y = flicker seed
        static readonly ShipPoolMat missilePool = new ShipPoolMat(true);
        static readonly ShipPoolMat missileFallback = new ShipPoolMat(false);
        static bool MissileActive(ShipPoolMat p) { try { return p.Ready && p.LampCount > 0; } catch { return false; } }
        public static Material PoolMat => MissileActive(missilePool) ? missilePool.Mat : (MissileActive(missileFallback) ? missileFallback.Mat : null);
        public static int PoolLamps => MissileActive(missilePool) ? missilePool.LampCount : (MissileActive(missileFallback) ? missileFallback.LampCount : 0);
        public static int PoolVol => MissileActive(missilePool) ? missilePool.VolPass : missileFallback.VolPass;
        public static int PoolComp => MissileActive(missilePool) ? missilePool.CompPass : missileFallback.CompPass;

        // Dynamic pool range: per-weapon base (ComputeBaseRange, thrust-primary) carried per lamp in
        // buffer w (base x Ground Range Boost). The MissileFX shader sizes EACH quad from w; global curRange
        // below (= max(active) x boost) is only the fallback for old bundles + the far-cull reference.
        static float curRange = 800f; // last applied global range (for cull + ApplyLook)
        const float PoolFalloff = 0.5f;
        const float PoolShading = 0.75f;
        const float RangeMin = 150f;
        const float RangeMax = 5000f;

        static readonly HashSet<string> loggedWeapons = new HashSet<string>();
        static FieldInfo motorsField;
        static FieldInfo blastYieldField;
        static bool reflTried;

        static void EnsureRefl()
        {
            if (reflTried) return;
            reflTried = true;
            try { motorsField = typeof(Missile).GetField("motors", BindingFlags.NonPublic | BindingFlags.Instance); } catch { }
            try { blastYieldField = typeof(Missile).GetField("blastYield", BindingFlags.NonPublic | BindingFlags.Instance); } catch { }
        }

        // FALLBACK ONLY (missiles whose prefab ships no Motor Light — e.g. some modded weapons):
        // thrust/size/IR estimate of the exhaust throw. For missiles WITH an authored light, Build
        // mirrors that light directly (range + intensity) and the sliders amplify it — the author
        // already tuned the exhaust look per weapon, and thrust reads it wrong (a low-thrust turbojet
        // AShM has a far bigger flame than a high-thrust AAM booster).
        // blastYield stays excluded (exhaust != explosion flash; glide bombs can have huge yield and
        // no exhaust). Formula: base = max(vanillaRangeIfAny, 120 + 3.5*sqrt(thrust) + len*25 + 30*IR),
        // clamped 200-2500.
        internal static float ComputeBaseRange(Missile m, float length, float vanillaRange, out float thrustOut, out float irOut)
        {
            thrustOut = 0f; irOut = 1f;
            float yield = 0f;
            try
            {
                EnsureRefl();
                if (motorsField != null && m != null)
                {
                    var arr = motorsField.GetValue(m) as Array;
                    if (arr != null)
                    {
                        foreach (object motor in arr)
                        {
                            if (motor == null) continue;
                            try
                            {
                                Type t = motor.GetType();
                                var fT = t.GetField("thrust", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (fT != null)
                                {
                                    object v = fT.GetValue(motor);
                                    float f = (v is float) ? (float)v : 0f;
                                    if (f > thrustOut) thrustOut = f;
                                }
                                var fI = t.GetField("IR_intensity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (fI != null)
                                {
                                    object v = fI.GetValue(motor);
                                    float f = (v is float) ? (float)v : 0f;
                                    if (f > irOut) irOut = f;
                                }
                            }
                            catch { }
                        }
                    }
                }
                // Public API fallback (some modded missiles override GetThrust).
                try
                {
                    float g = m.GetThrust();
                    if (g > thrustOut) thrustOut = g;
                }
                catch { }
                try
                {
                    if (blastYieldField != null)
                    {
                        object v = blastYieldField.GetValue(m);
                        if (v is float) yield = (float)v;
                    }
                    else yield = m.GetYield();
                }
                catch { try { yield = m.GetYield(); } catch { } }
                // stash yield for the log line via irOut packing? No: keep pure, log reads yield separately.
                lastYieldSeen = yield;
            }
            catch { }
            float sizeTerm = 120f + Mathf.Max(2f, length) * 25f;
            float thrustTerm = thrustOut > 0f ? 3.5f * Mathf.Sqrt(Mathf.Max(0f, thrustOut)) : 0f;
            float irTerm = Mathf.Clamp(irOut, 0f, 10f) * 30f;
            float want = Mathf.Max(vanillaRange, sizeTerm + thrustTerm + irTerm);
            return Mathf.Clamp(want, 200f, 2500f);
        }
        static float lastYieldSeen;

        internal static void LogWeaponOnce(string key, float length, float thrust, float ir, int lightN, float lightRange, float lightIntensity, float baseRange, float bright, bool mirror)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) key = "?";
                lock (loggedWeapons)
                {
                    if (!loggedWeapons.Add(key)) return;
                }
                if (RealisticNightPlugin.DiagOn())
                    RealisticNightPlugin.Log.LogInfo($"[Missiles] type {key}: len={length:F1}m lights={lightN} vanillaRange={lightRange:F0}m vanillaIntensity={lightIntensity:F2} thrustMax={thrust:F0} yield~{lastYieldSeen:F0} -> baseRange={baseRange:F0}m brightScale={bright:F2} ({(mirror ? "MIRROR: authored light x sliders" : "FALLBACK: thrust/size formula (no vanilla light)")}, range = base x Ground Range Boost)");
            }
            catch { }
        }

        static bool BadVec(Vector3 v)
        {
            float s = v.x + v.y + v.z;
            return float.IsNaN(s) || float.IsInfinity(s);
        }

        // Brighten a flame colour (RGB only, alpha untouched; >1 HDR values bloom).
        static Color Hot(Color c, float k) { return new Color(c.r * k, c.g * k, c.b * k, c.a); }

        // Clone a gradient with brightened keys (never mutates the cached original, which may be
        // a shared asset also used by other missiles).
        static Gradient HotGradient(Gradient g, float k)
        {
            if (g == null) return null;
            try
            {
                var ck = g.colorKeys;
                var nck = new GradientColorKey[ck.Length];
                for (int i = 0; i < ck.Length; i++)
                    nck[i] = new GradientColorKey(Hot(ck[i].color, k), ck[i].time);
                var ng = new Gradient();
                ng.SetKeys(nck, g.alphaKeys);
                try { ng.mode = g.mode; } catch { }
                return ng;
            }
            catch { return g; }
        }

        // Scale any startColor mode by k (constant, two-colour, single/double gradient; anything
        // else best-effort, never throws — worst case that system's flame stays vanilla).
        static ParticleSystem.MinMaxGradient ScaledGlow(ParticleSystem.MinMaxGradient g, float k)
        {
            try
            {
                switch (g.mode)
                {
                    case ParticleSystemGradientMode.Color:
                        g.color = Hot(g.color, k);
                        return g;
                    case ParticleSystemGradientMode.TwoColors:
                        g.colorMin = Hot(g.colorMin, k);
                        g.colorMax = Hot(g.colorMax, k);
                        return g;
                    case ParticleSystemGradientMode.Gradient:
                        g.gradient = HotGradient(g.gradient, k);
                        return g;
                    case ParticleSystemGradientMode.TwoGradients:
                        g.gradientMin = HotGradient(g.gradientMin, k);
                        g.gradientMax = HotGradient(g.gradientMax, k);
                        return g;
                    default:
                        try { g.color = Hot(g.color, k); } catch { }
                        try { g.gradient = HotGradient(g.gradient, k); } catch { }
                        return g;
                }
            }
            catch { return g; }
        }

        public static void Apply(bool on, bool nightActive)
        {
            if (!on)
            {
                if (lastOn || fxById.Count > 0) Teardown();
                try { missilePool.Clear(); } catch { }
                try { missileFallback.Clear(); } catch { }
                lastOn = false;
                return;
            }
            lastOn = true;

            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 0.2f; // fast pickup: launches boost at once
                try { Scan(); } catch { }
            }

            Camera cam = RealisticNightPlugin.MainCamera(out Vector3 camPos);
            if (cam == null) camPos = Vector3.zero; // camPos only used for distance culls below

            float glow = 0f, ground = 0f, boost = 1f;
            try { glow = Mathf.Max(0f, RealisticNightPlugin.MissileGlowIntensity.Value); } catch { }
            try { ground = Mathf.Max(0f, RealisticNightPlugin.MissileGroundIntensity.Value); } catch { }
            try { boost = Mathf.Clamp(RealisticNightPlugin.MissileGroundRangeBoost.Value, 0.2f, 5f); } catch { boost = 1f; }

            bool wantPools = nightActive && ground > 0.01f;
            if (poolBuf == null || poolBuf.Length != PoolCap) poolBuf = new Vector4[PoolCap];
            if (poolDirBuf == null || poolDirBuf.Length != PoolCap) poolDirBuf = new Vector4[PoolCap];
            // Global range = max per-missile base among live missiles x boost. The MissileFX shader
            // takes TRUE per-lamp ranges from _LampsBuf.w; this global feeds only the far-cull cutoff
            // and the street-omni fallback backend (old bundles without MissileMat).
            try
            {
                float maxBase = 0f;
                foreach (var kv in fxById)
                {
                    var fx0 = kv.Value;
                    if (fx0 == null || fx0.missile == null) continue;
                    try
                    {
                        if (fx0.missile.disabled) continue;
                        if (fx0.missile.transform.position.y < Datum.LocalSeaY) continue;
                    }
                    catch { continue; }
                    if (fx0.baseRange > maxBase) maxBase = fx0.baseRange;
                }
                if (maxBase > 1f)
                    curRange = Mathf.Clamp(maxBase * boost, RangeMin, RangeMax);
            }
            catch { }
            int found = 0;
            bool hasOrigin = false;
            try { hasOrigin = Datum.origin != null; } catch { }

            foreach (var kv in fxById)
            {
                var fx = kv.Value;
                if (fx == null) continue;
                Missile m = fx.missile;
                if (m == null) continue;
                bool disabled = true;
                try { disabled = m.disabled; } catch { continue; }
                if (disabled) continue;
                bool under = false;
                try { under = m.transform.position.y < Datum.LocalSeaY; } catch { continue; }
                if (under) continue; // underwater: engine out, let vanilla stay off
                bool thrusting = false;
                try { thrusting = m.EngineOn(); } catch { }
                if (!thrusting) continue;
                // Enforce visuals (covers missiles spawned before toggle + non-looping flames).
                try
                {
                    foreach (var ps in fx.pss)
                    {
                        if (ps == null) continue;
                        try
                        {
                            if (!ps.isPlaying)
                            {
                                var mm = ps.main;
                                mm.loop = true;
                                ps.Play();
                            }
                        }
                        catch { }
                    }
                    foreach (var te in fx.trails)
                    {
                        if (te == null) continue;
                        try { if (!te.enabled) te.enabled = true; }
                        catch { }
                    }
                    // Deferred pools handle ALL ground lighting (bypasses URP 8-light cap).
                    // Vanilla Motor.Light[] is disabled when pools are active to prevent double lighting.
                    // When pools are off (daytime / slider=0), vanilla lights stay at authored values
                    // with no boost (slider affects pools only).
                    if (wantPools)
                    {
                        foreach (var l in fx.lights)
                        {
                            try { if (l != null && l.enabled) l.enabled = false; } catch { }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < fx.lights.Count; i++)
                        {
                            Light l = fx.lights[i];
                            if (l == null) continue;
                            try
                            {
                                l.enabled = true;
                                float o = (i < fx.lightOrig.Count) ? fx.lightOrig[i] : 1f;
                                l.intensity = o;  // pure authored — slider affects pools only
                                float or_ = (i < fx.lightRangeOrig.Count) ? fx.lightRangeOrig[i] : 0f;
                                if (or_ > 0f) l.range = or_;  // pure authored
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                // Exhaust Glow slider: boost the missile's OWN flame (vanilla ParticleSystems).
                // 0 = vanilla; higher = bigger + brighter flame. Same particle count (no rate change,
                // so no extra CPU/overdraw): size via transform scale, brightness via startColor (HDR
                // values bloom). Orig values cached at Build. Applied only when the slider moved (or
                // first sight) so gradient clones never churn per tick. The missile root transform
                // itself is never scaled (that would resize the whole model).
                try
                {
                    if (glow != fx.glowApplied)
                    {
                        fx.glowApplied = glow;
                        float sizeK = 1f + glow * 0.15f;
                        float brightK = 1f + glow * 0.25f;
                        for (int pi = 0; pi < fx.pss.Count; pi++)
                        {
                            ParticleSystem ps = fx.pss[pi];
                            if (ps == null) continue;
                            try
                            {
                                if (ps.transform != fx.transform)
                                {
                                    Vector3 os = (pi < fx.psOrigScale.Count) ? fx.psOrigScale[pi] : ps.transform.localScale;
                                    ps.transform.localScale = os * sizeK;
                                }
                                if (pi < fx.psOrigColor.Count)
                                {
                                    var main = ps.main;
                                    main.startColor = ScaledGlow(fx.psOrigColor[pi], brightK);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                // Pool feed (slider 2, night only).
                if (wantPools && found < PoolCap)
                {
                    try
                    {
                        Vector3 tailW = m.transform.TransformPoint(fx.localTail);
                        if (BadVec(tailW)) continue;
                        // Far cull: beyond 60 km + current range cannot touch the frame.
                        try
                        {
                            float cull = 60000f + curRange;
                            if ((tailW - camPos).sqrMagnitude > cull * cull) continue;
                        }
                        catch { }
                        Vector3 raw = hasOrigin ? Datum.origin.InverseTransformPoint(tailW) : tailW;
                        if (BadVec(raw)) continue;
                        // w = THIS missile's range (base x boost). Brightness/seed ride _SpotDirs.
                        float perR = Mathf.Clamp(fx.baseRange * boost, 100f, 4000f);
                        poolBuf[found] = new Vector4(raw.x, raw.y, raw.z, perR);
                        poolDirBuf[found] = new Vector4(fx.brightScale, fx.flickSeed, 0f, 0f);
                        found++;
                    }
                    catch { }
                }
            }

            UpdatePools(wantPools, found);
        }

        static void UpdatePools(bool want, int found)
        {
            if (!want || found <= 0) { try { missilePool.Clear(); } catch { } try { missileFallback.Clear(); } catch { } return; }
            try
            {
                // Prefer the per-lamp missile backend; fall back to street omni on old bundles.
                bool ok = false;
                try
                {
                    Material src = StreetLightFX.MissileMat;
                    if (src != null) ok = missilePool.Ensure(src, "RN_Missile_Volume", "RN_Missile_Composite", "RN_MissilePool");
                }
                catch { ok = false; }
                if (ok)
                {
                    try { missileFallback.Clear(); } catch { }
                    ApplyLook(missilePool.Mat);
                    missilePool.SetLamps(poolBuf, poolDirBuf, found);
                    missilePool.Upload();
                }
                else
                {
                    try { missilePool.Clear(); } catch { }
                    if (missileFallback.Ensure(StreetLightFX.Mat, "RN_StreetLight_Volume", "RN_StreetLight_Composite", "RN_MissilePoolFB"))
                        ApplyLook(missileFallback.Mat);
                    missileFallback.SetLamps(poolBuf, null, found);
                    missileFallback.Upload();
                }
            }
            catch { try { missilePool.Clear(); } catch { } try { missileFallback.Clear(); } catch { } }
        }

        static readonly int PFalloff01 = Shader.PropertyToID("_Falloff01");

        static void ApplyLook(Material m)
        {
            if (m == null) return;
            float inten = 0f;
            try { inten = Mathf.Max(0f, RealisticNightPlugin.MissileGroundIntensity.Value); } catch { }
            m.SetFloat(ShipPoolMat.PIntensity, inten);
            m.SetFloat(ShipPoolMat.PIntensityWarm, inten);
            m.SetFloat(ShipPoolMat.PAtmos, Mathf.Max(100f, RealisticNightPlugin.StreetLightAtten.Value));
            m.SetFloat(ShipPoolMat.PAtmosCurve, Mathf.Clamp(RealisticNightPlugin.StreetLightAttenCurve.Value, 0.2f, 3f));
            float r = Mathf.Clamp(curRange, RangeMin, RangeMax);
            m.SetFloat(ShipPoolMat.PRange, r); // global fallback (old path) + fullscreen-cull reference
            m.SetFloat(ShipPoolMat.PFalloff, (PoolFalloff * PoolFalloff * 25f) / (r * r)); // legacy K (street path)
            m.SetFloat(PFalloff01, Mathf.Clamp01(PoolFalloff)); // per-lamp shape (missile path)
            m.SetFloat(ShipPoolMat.PNdotL, PoolShading);
            Color warm = new Color(1f, 0.6f, 0.3f, 1f); // fire tint for ground
            m.SetColor(ShipPoolMat.PColor, warm);
            m.SetColor(ShipPoolMat.PColorWarm, warm);
            m.SetFloat(ShipPoolMat.PPreserveID, 1f); // keep surface colour like headlights (no white veil)
            float tint = 1f;
            try { tint = Mathf.Clamp01(RealisticNightPlugin.StreetGroundTint.Value); } catch { }
            m.SetFloat(ShipPoolMat.PTintStrength, tint);
        }

        static void Scan()
        {
            var dead = new List<int>();
            foreach (var kv in fxById) if (kv.Value == null) dead.Add(kv.Key);
            foreach (int k in dead) fxById.Remove(k);

            List<Unit> units;
            try { units = UnitRegistry.allUnits; } catch { return; }
            if (units == null) return;
            Unit[] snap;
            try { snap = units.ToArray(); } catch { return; }
            foreach (Unit u in snap)
            {
                if (u == null) continue;
                bool dis = false;
                try { dis = u.disabled; } catch { continue; }
                if (dis) continue;
                if (!(u is Missile)) continue;
                int id = 0;
                try { id = u.GetInstanceID(); } catch { continue; }
                if (fxById.ContainsKey(id)) continue;
                try
                {
                    var fx = u.gameObject.AddComponent<MissileFx>();
                    fx.Build((Missile)u);
                    // Unpowered gliders / guided shells: no vanilla light + no thrust = no exhaust
                    // flame → skip entirely (no pool, no tracking, no per-tick cost).
                    if (fx.lights.Count == 0 && fx.thrustMax <= 0f)
                    {
                        UnityEngine.Object.Destroy(fx);
                        continue;
                    }
                    fxById[id] = fx;
                }
                catch { }
            }
        }

        static void Teardown()
        {
            foreach (var kv in fxById)
            {
                try { if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value); } catch { }
            }
            fxById.Clear();
            poolBuf = null; poolDirBuf = null;
        }
    }
}
