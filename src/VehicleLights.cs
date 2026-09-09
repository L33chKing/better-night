using System;
using System.Collections.Generic;
using System.Linq;
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
    // Vehicle headlights: mounts, rigs, rear lights, pools feed.

    // Volumetric headlights for ground vehicles (vanilla models the lenses but emits no light): parented emitters per vehicle (modeled lenses or bumper fallback) + cone + deferred pool.
    internal static class VehicleHeadlights
    {
        // Layout providers per family (ground here, ships below, air later).
        interface IHeadlightLayout { bool TryMounts(Unit unit, out VehicleHeadlights.AnchorResolver.Mount[] mounts, out Renderer[] rear); }
        sealed class GroundLayout : IHeadlightLayout
        {
            public bool TryMounts(Unit unit, out VehicleHeadlights.AnchorResolver.Mount[] mounts, out Renderer[] rear)
            {
                return AnchorResolver.TryResolve(unit, out mounts, out rear);
            }
        }

        static readonly IHeadlightLayout ground = new GroundLayout();
        // CPU-era fixtures: shared meshes + shared SoftGlow materials (Sprites/Default: built-in,
        // always included, proven). Rigs own plain child GOs; colours pushed once per tick below.
        static Material coneSoftMat, lensSoftMat, rearDimSoftMat, rearBrightSoftMat;
        static Mesh coneMesh, quadMesh; // shared drawn templates (live forever once built; never destroyed)
        static Texture2D coneTex, dotTex;
        internal static Material ConeSoftMat => coneSoftMat;
        internal static Material LensSoftMat => lensSoftMat;
        internal static Material RearDimSoftMat => rearDimSoftMat;
        internal static Material RearBrightSoftMat => rearBrightSoftMat;
        internal static Mesh ConeMesh => coneMesh;
        internal static Mesh QuadMesh => quadMesh;
        static readonly int ColorID = Shader.PropertyToID("_Color");
        static bool glowWarned;
        static readonly Dictionary<int, HeadlightRig> rigs = new Dictionary<int, HeadlightRig>();
        static float nextScan;
        static Vector4[] poolBuf;
        static Vector4[] spotDirBuf;
        static bool lastOn;
        // Hitch guards: hold last camera pose + damp shared colours so one bad tick can't blink everything.
        static Vector3 lastCamPos = Vector3.zero;
        static bool haveCamPos;
        static Color smCone = Color.white, smLens = Color.white;
        static float smHaze = 1f;
        static bool smInit;

        static Color HeadCol(float i)
        {
            float w = Mathf.Clamp01(RealisticNightPlugin.HeadlightWarmth.Value);
            return new Color(1.0f * i, Mathf.Lerp(0.97f, 0.80f, w) * i, Mathf.Lerp(1.0f, 0.60f, w) * i, 1f);
        }
        // NaN/Inf positions slip past range culls (NaN compares false) and poison every pool sharing volRT.
        internal static bool BadVec(Vector3 v)
        {
            float s = v.x + v.y + v.z;
            return float.IsNaN(s) || float.IsInfinity(s);
        }

        public static void Apply(bool on, bool lightsActive)
        {
            if (!on)
            {
                if (lastOn || rigs.Count > 0) Teardown();
                HeadlightPools.Apply(false);
                lastOn = false;
                return;
            }
            lastOn = true;
            EnsureSoftMats(); // shared meshes + fixture materials first (rigs reference them at Build)
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 1f; // 1 Hz registry sweep; rigs themselves follow free
                Scan();
            }
            // Push shared uniforms (cheap, live). Haze + damping guard against hitch-frame blinking.
            Camera cam = RealisticNightPlugin.MainCamera(out Vector3 freshPos);
            Vector3 camPos;
            if (cam != null) { camPos = freshPos; lastCamPos = camPos; haveCamPos = true; }
            else if (haveCamPos) camPos = lastCamPos;
            else camPos = Vector3.zero;
            float nearestRig2 = float.PositiveInfinity;
            foreach (var kv in rigs)
            {
                HeadlightRig r0 = kv.Value;
                if (r0 == null) continue;
                try
                {
                    float dd = (r0.transform.position - camPos).sqrMagnitude;
                    if (dd < nearestRig2) nearestRig2 = dd;
                }
                catch { }
            }
            float rawHaze = StreetLights.HazeFade(float.IsPositiveInfinity(nearestRig2) ? float.PositiveInfinity : Mathf.Sqrt(nearestRig2));
            Color rawCone = HeadCol(RealisticNightPlugin.HeadlightIntensity.Value) * rawHaze;
            Color rawLens = HeadCol(RealisticNightPlugin.HeadlightIntensity.Value * 2f) * rawHaze;
            float dtSm = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f); // clamped: low-FPS spikes must not snap
            float kSm = 1f - Mathf.Exp(-dtSm * 6f);
            if (!smInit) { smHaze = rawHaze; smCone = rawCone; smLens = rawLens; smInit = true; }
            else { smHaze = Mathf.Lerp(smHaze, rawHaze, kSm); smCone = Color.Lerp(smCone, rawCone, kSm); smLens = Color.Lerp(smLens, rawLens, kSm); }
            HeadlightRig.SharedHaze = smHaze;
            HeadlightRig.SharedConeColor = smCone;
            HeadlightRig.SharedLensColor = smLens;
            float maxD = RealisticNightPlugin.HeadlightMaxDist.Value;
            float maxD2 = maxD * maxD;
            // Pixels-per-world-unit at distance 1 (for decal anti-shimmer sizing). Zero = grow pass off.
            float pxScale = 0f;
            try
            {
                if (cam != null && cam.fieldOfView > 1f && Screen.height > 0)
                    pxScale = (Screen.height * 0.5f) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            }
            catch { pxScale = 0f; }
            bool showFx = lightsActive && RealisticNightPlugin.HeadlightCones.Value;
            foreach (var kv in rigs)
            {
                HeadlightRig rig = kv.Value;
                if (rig == null) continue;
                rig.UpdateRig(showFx, lightsActive, camPos, maxD2, pxScale);
            }
            ComposeInstances();
            UpdatePools(lightsActive, camPos, maxD2);
        }

        // Shared fixture meshes + SoftGlow materials (Sprites/Default: built-in, always included).
        // Colours pushed once per tick (identical for every rig). No bundle needed for fixtures.
        static bool EnsureSoftMats()
        {
            if (coneSoftMat != null && lensSoftMat != null && rearDimSoftMat != null && rearBrightSoftMat != null)
                return true;
            if (quadMesh == null) quadMesh = GlowFX.Billboard();
            if (coneMesh == null) coneMesh = GlowFX.BeamConeMesh(10f, 1.75f, 12);
            if (coneTex == null) coneTex = GlowFX.BeamFadeTex();
            if (dotTex == null) dotTex = GlowFX.RadialTex(64, 0.35f, 1.2f);
            if (quadMesh == null || coneMesh == null || coneTex == null || dotTex == null) return false;
            if (coneSoftMat == null) coneSoftMat = GlowFX.SoftGlow(coneTex, "RN_HeadlightCone");
            if (lensSoftMat == null) lensSoftMat = GlowFX.SoftGlow(dotTex, "RN_HeadlightLens");
            if (rearDimSoftMat == null) rearDimSoftMat = GlowFX.SoftGlow(dotTex, "RN_RearDim");
            if (rearBrightSoftMat == null) rearBrightSoftMat = GlowFX.SoftGlow(dotTex, "RN_RearBright");
            if (coneSoftMat == null || lensSoftMat == null || rearDimSoftMat == null || rearBrightSoftMat == null)
            {
                if (!glowWarned)
                {
                    glowWarned = true;
                    RealisticNightPlugin.Log.LogWarning("[Headlights] fixture materials need Sprites/Default - cones/decals off (pools unaffected).");
                }
                return false;
            }
            return true;
        }

        // Push shared fixture colours once (identical for every rig).
        static void ComposeInstances()
        {
            if (!EnsureSoftMats()) return;
            // Beam density normalized by range (1/sqrt), so longer cones don't blow out to white.
            float norm = Mathf.Clamp(1f / Mathf.Sqrt(Mathf.Max(5f, RealisticNightPlugin.HeadlightRange.Value) / 20f), 0.25f, 1f);
            Color cc = HeadlightRig.SharedConeColor;
            cc.a = norm;
            coneSoftMat.SetColor(ColorID, cc);
            lensSoftMat.SetColor(ColorID, HeadlightRig.SharedLensColor);
            float runBoost = Mathf.Max(0f, RealisticNightPlugin.RearRunningBoost.Value);
            float brakeBoost = Mathf.Max(0f, RealisticNightPlugin.RearBrakeBoost.Value);
            rearDimSoftMat.SetColor(ColorID, new Color(1f, 0.07f, 0.05f) * runBoost * HeadlightRig.SharedHaze);
            rearBrightSoftMat.SetColor(ColorID, new Color(1f, 0.07f, 0.05f) * brakeBoost * HeadlightRig.SharedHaze);
        }

        static void Scan()
        {
            // Prune dead rigs (unit destroyed / scene reload took the GameObject).
            var dead = new List<int>();
            foreach (var kv in rigs) if (kv.Value == null) dead.Add(kv.Key);
            foreach (int k in dead) rigs.Remove(k);

            List<Unit> units;
            try { units = UnitRegistry.allUnits; } catch { return; }
            if (units == null) return;
            // Snapshot: registry mutates on disable during iteration.
            Unit[] snap;
            try { snap = units.ToArray(); } catch { return; }
            foreach (Unit u in snap)
            {
                if (u == null || u.disabled) continue;
                if (!(u is GroundVehicle)) continue; // ground-first; Aircraft/Ship plugs in via layouts later
                int id = u.GetInstanceID();
                if (rigs.ContainsKey(id)) continue;
                if (!ground.TryMounts(u, out VehicleHeadlights.AnchorResolver.Mount[] mounts, out Renderer[] rear)) continue;
                var rig = u.gameObject.AddComponent<HeadlightRig>();
                rig.Build(mounts, rear, u);
                rigs[id] = rig;
            }
        }

        // Hitch cover (not the flicker fix): hold the last buffer while rigs are still near.
        static int poolEmptyStreak;
        const int PoolHoldFrames = 8;
        static void UpdatePools(bool lightsActive, Vector3 camPos, float maxD2)
        {
            if (!lightsActive || !RealisticNightPlugin.HeadlightPools.Value) { HeadlightPools.Apply(false); poolEmptyStreak = 0; return; }
            // No throttle (unlike streets): moving lamps refresh every tick or pools stutter behind bumpers.
            // camPos/maxD2 arrive guarded from Apply (a lone Camera.main miss must not zero the whole feed).
            int cap = HeadlightPools.MaxLamps;
            if (poolBuf == null || poolBuf.Length != cap) poolBuf = new Vector4[cap];
            if (spotDirBuf == null || spotDirBuf.Length != cap) spotDirBuf = new Vector4[cap];
            bool wantDirs = true; // headlights are always spot-form; dirs always fed
            int found = 0;
            foreach (var kv in rigs)
            {
                HeadlightRig rig = kv.Value;
                // Flicker fix: dead rigs linger until the 1 Hz prune; skip one, only stop when full.
                if (rig == null) continue;
                if (found >= cap) break;
            // Raw-global feed (Datum.origin-local), matching the shader's _RN_OriginPos subtraction.
                rig.AppendPoolLamps(poolBuf, spotDirBuf, ref found, cap, camPos, maxD2, wantDirs);
            }
            if (found == 0 && HeadlightPools.LampCount > 0)
            {
                float nearest2 = float.PositiveInfinity;
                foreach (var kv in rigs)
                {
                    HeadlightRig rig = kv.Value;
                    if (rig == null) continue;
                    try
                    {
                        float d2 = (rig.transform.position - camPos).sqrMagnitude;
                        if (d2 < nearest2) nearest2 = d2;
                    }
                    catch { }
                }
                float maxD = Mathf.Sqrt(maxD2);
                float nearest = float.IsPositiveInfinity(nearest2) ? float.PositiveInfinity : Mathf.Sqrt(nearest2);
                if (nearest <= maxD && poolEmptyStreak < PoolHoldFrames) // rigs near but fed nothing: hold, else clear at once
                {
                    poolEmptyStreak++;
                    HeadlightPools.Apply(true); return;
                }
                poolEmptyStreak = 0;
            }
            else poolEmptyStreak = 0;
            HeadlightPools.SetLamps(poolBuf, found);
            if (wantDirs) HeadlightPools.SetDirs(spotDirBuf, found);
            HeadlightPools.Apply(true);
        }

        static void Teardown()
        {
            foreach (var kv in rigs) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            rigs.Clear();
            poolEmptyStreak = 0;
            if (coneSoftMat != null) UnityEngine.Object.Destroy(coneSoftMat); coneSoftMat = null;
            if (lensSoftMat != null) UnityEngine.Object.Destroy(lensSoftMat); lensSoftMat = null;
            if (rearDimSoftMat != null) UnityEngine.Object.Destroy(rearDimSoftMat); rearDimSoftMat = null;
            if (rearBrightSoftMat != null) UnityEngine.Object.Destroy(rearBrightSoftMat); rearBrightSoftMat = null;
        }

        // Anchor resolution: modeled mounts first, bumper fallback. One-time probe dump per prefab.
        internal static class AnchorResolver
        {
            // A headlight mount: local position + the modeled lens renderer (null = improvised/ synthetic point).
            public struct Mount { public Vector3 local; public Renderer rend; }
            static readonly int EmiID = Shader.PropertyToID("_EmissionColor");
            static readonly string[] Want = { "headlight", "headlamp", "head", "lamp", "lens", "beam", "driving", "fog", "grille", "bumper", "fender" };
            static readonly string[] Ban = { "tail", "rear", "brake", "navlight", "cockpit", "mfd", "screen", "display", "turret", "barrel", "muzzle", "cannon", "radar", "dish" };
            static readonly string[] RearWant = { "taillight", "tail", "brake", "stoplight", "stop", "rearlight", "rear" };
            static readonly string[] RearBan = { "front", "head", "navlight", "cockpit", "mfd", "screen", "display", "turret", "barrel", "muzzle", "cannon", "radar", "dish" };

            public static bool TryResolve(Unit unit, out Mount[] mounts, out Renderer[] rear)
            {
                mounts = null; rear = null;
                Transform root = unit.transform;
                if (root == null) return false;
                float length = 6f, width = 3f, height = 2.5f;
                try
                {
                    if (unit.definition != null)
                    {
                        length = Mathf.Max(2f, unit.definition.length);
                        width = Mathf.Max(1.5f, unit.definition.width);
                        height = Mathf.Max(1f, unit.definition.height);
                    }
                }
                catch { }

                // Candidate modeled mounts: name hit, not banned, in the FRONT half (local +Z).
                var cands = new List<Transform>();
                try
                {
                    Transform[] all = root.GetComponentsInChildren<Transform>(true);
                    foreach (Transform t in all)
                    {
                        if (t == null || t == root) continue;
                        string n = t.name.ToLowerInvariant();
                        bool ban = false;
                        foreach (string b in Ban) if (n.Contains(b)) { ban = true; break; }
                        if (ban) continue;
                        bool want = false;
                        foreach (string k in Want) if (n.Contains(k)) { want = true; break; }
                        if (!want) continue;
                        Vector3 lp;
                        try { lp = root.InverseTransformPoint(t.position); } catch { continue; }
                        if (lp.z < length * 0.1f) continue; // must be front half
                        cands.Add(t);
                    }
                }
                catch { }

                if (cands.Count > 0)
                {
                    // Front-most symmetric pair: sort by z desc, then pair mirrors across local x.
                    cands.Sort((a, b) =>
                    {
                        Vector3 la = root.InverseTransformPoint(a.position);
                        Vector3 lb = root.InverseTransformPoint(b.position);
                        return lb.z.CompareTo(la.z);
                    });
                    Transform best = cands[0];
                    Vector3 lb0 = root.InverseTransformPoint(best.position);
                    Transform mirror = null;
                    float mirrorScore = float.MaxValue;
                    for (int i = 1; i < cands.Count; i++)
                    {
                        Vector3 li = root.InverseTransformPoint(cands[i].position);
                        if (Mathf.Abs(li.z - lb0.z) > Mathf.Max(1f, length * 0.15f)) continue;
                        float sym = Mathf.Abs(li.x + lb0.x) + Mathf.Abs(li.y - lb0.y) * 0.5f;
                        if (sym < mirrorScore) { mirrorScore = sym; mirror = cands[i]; }
                    }
                    var list = new List<Mount>();
                    list.Add(new Mount { local = lb0, rend = LensRenderer(best) });
                    if (mirror != null) list.Add(new Mount { local = root.InverseTransformPoint(mirror.position), rend = LensRenderer(mirror) });
                    else list.Add(new Mount { local = new Vector3(-lb0.x, lb0.y, lb0.z), rend = null }); // synthetic mirror: orb
                    mounts = list.ToArray();
                    rear = FindRear(root, length);
                    return true;
                }

                // Improvised bumper pair (unit origins are mid-hull, so just below centre).
                float y = -Mathf.Max(0.2f, height * 0.15f);
                float hx = Mathf.Max(0.5f, width * 0.28f);
                float z = length * 0.5f + 0.25f;
                mounts = new[] { new Mount { local = new Vector3(-hx, y, z), rend = null }, new Mount { local = new Vector3(hx, y, z), rend = null } };
                rear = FindRear(root, length);
                return true;
            }

            // Usable lens renderer: single-material + _EmissionColor (shared materials are cloned, never written).
            static Renderer LensRenderer(Transform t)
            {
                try
                {
                    Renderer r = t.GetComponent<Renderer>();
                    if (UsableLens(r)) return r;
                    foreach (Renderer c in t.GetComponentsInChildren<Renderer>(true))
                        if (UsableLens(c)) return c;
                }
                catch { }
                return null;
            }

            static bool UsableLens(Renderer r)
            {
                try
                {
                    if (r == null || r.sharedMaterials == null || r.sharedMaterials.Length != 1) return false;
                    Material m = r.sharedMaterial;
                    return m != null && m.shader != null && m.HasProperty(EmiID);
                }
                catch { return false; }
            }

            // STRICT rear search: tail/brake lenses in the REAR half, no fallback, cap 4.
            static Renderer[] FindRear(Transform root, float length)
            {
                var found = new List<Renderer>();
                try
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null || t == root || found.Count >= 4) break;
                        string n = t.name.ToLowerInvariant();
                        bool ban = false;
                        foreach (string b in RearBan) if (n.Contains(b)) { ban = true; break; }
                        if (ban) continue;
                        bool want = false;
                        foreach (string k in RearWant) if (n.Contains(k)) { want = true; break; }
                        if (!want) continue;
                        Vector3 lp;
                        try { lp = root.InverseTransformPoint(t.position); } catch { continue; }
                        if (lp.z > -length * 0.1f) continue; // must be rear half
                        Renderer r = LensRenderer(t);
                        if (r != null && !found.Contains(r)) found.Add(r);
                    }
                }
                catch { }
                return found.ToArray();
            }
        }
    }

    // === SHIP NAVAL LIGHTING: deck flood pools + turret-slaved searchlight (ships keep own nav lights) ===
    internal enum ShipMountRole { DeckFlood, Searchlight }
    internal struct ShipMount
    {
        public Vector3 local;         // rig-root space (deck mounts, mast fallback)
        public Vector3 face;          // fixture facing (rig-root space)
        public ShipMountRole role;
        public Transform anchor;      // turret elevation pivot, or null for rig-root mounting
        public Vector3 anchorOffset;  // fixture offset in anchor space
        public Turret turret;         // owning turret for target gating (null = mast fallback, always on)
        public float mountSize;       // mesh-bounds diagonal (size scaling + LMG gate, 0 = unmeasured)
    }

    internal static class ShipLayout
    {
        // Deck-flood count scales with hull length (carriers up to 4, small craft 1).
        static int DeckCount(float length) { return Mathf.Clamp(Mathf.RoundToInt(length / 70f), 1, 4); }

        // Fixed vertical cells are not trainable turrets (usually not Turret components at all).
        static bool IsVlsName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("vls", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("silo", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("mk41", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("mk57", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A searchlight mount must TRACK (slew zone) and be bigger than a pintle: fixed VLS/CM farms
        // and LMG points fail one of the two. Unmeasurable mounts pass (never darken a legit turret).
        const float MinTraverseDeg = 5f;
        const float MinMountSize = 2.5f; // mesh-bounds diagonal (m)
        static bool TurretEligible(Turret t, out float mountSize)
        {
            mountSize = 0f;
            try
            {
                float tr = 360f;
                try { tr = t.GetTraverseRange(); } catch { tr = 360f; }
                if (tr < MinTraverseDeg) return false; // fixed cell/farm/marker, not a tracking mount
                try // fixed-vertical cell (VLS-style): pinned near straight-up, never tracks anything
                {
                    ShipLightRig.EnsureTurretRefs();
                    if (ShipLightRig.elMinRef != null && ShipLightRig.elMaxRef != null)
                    {
                        float lo = ShipLightRig.elMinRef(t), hi = ShipLightRig.elMaxRef(t);
                        if (hi - lo < 5f && Mathf.Abs((lo + hi) * 0.5f) > 60f) return false;
                    }
                }
                catch { }
                Bounds b = new Bounds();
                bool has = false, anyMesh = false;
                Transform root = null;
                try { root = t.transform; } catch { return true; }
                if (root == null) return true;
                anyMesh |= Accumulate<MeshRenderer>(root, ref b, ref has);
                anyMesh |= Accumulate<SkinnedMeshRenderer>(root, ref b, ref has);
                if (!anyMesh) return true;
                mountSize = b.size.magnitude;
                return mountSize >= MinMountSize;
            }
            catch { return true; }
        }
        static bool Accumulate<T>(Transform root, ref Bounds b, ref bool has) where T : Renderer
        {
            bool any = false;
            try
            {
                foreach (T r in root.GetComponentsInChildren<T>(true))
                {
                    if (r == null) continue;
                    Vector3 sz;
                    try { sz = r.bounds.size; } catch { continue; }
                    if (float.IsNaN(sz.x + sz.y + sz.z) || float.IsInfinity(sz.x + sz.y + sz.z)) continue;
                    if (sz.sqrMagnitude < 1e-6f) continue;
                    any = true;
                    if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
                }
            }
            catch { }
            return any;
        }

        // Every trainable turret's elevation pivot (Harmony fieldref, else turret body).
        static bool TryTurretAnchors(Unit unit, out List<(Transform anchor, Turret turret, string name, float size)> found)
        {
            found = new List<(Transform anchor, Turret turret, string name, float size)>();
            Turret[] turrets = null;
            try { turrets = unit.GetComponentsInChildren<Turret>(true); } catch { return false; }
            if (turrets == null) return false;
            foreach (Turret t in turrets)
            {
                if (t == null) continue;
                string tn = "turret";
                try { tn = t.name; } catch { }
                if (IsVlsName(tn)) continue; // VLS silo, not a searchlight mount
                if (!TurretEligible(t, out float mountSize)) continue; // fixed/vertical farm or pintle-sized mount
                Transform elev = null;
                try
                {
                    var r = AccessTools.FieldRefAccess<Turret, Transform>("elevationTransform");
                    elev = r(t);
                }
                catch { elev = null; }
                if (elev == null) { try { elev = t.transform; } catch { continue; } }
                found.Add((elev, t, tn, mountSize));
            }
            return found.Count > 0;
        }

        public static bool TryMounts(Unit unit, out ShipMount[] mounts)
        {
            mounts = null;
            if (!(unit is Ship)) return false;
            float length = 60f, width = 8f, height = 12f;
            try
            {
                if (unit.definition != null)
                {
                    length = Mathf.Max(6f, unit.definition.length);
                    width = Mathf.Max(2f, unit.definition.width);
                    height = Mathf.Max(3f, unit.definition.height);
                }
            }
            catch { }
            var list = new List<ShipMount>();
            // Deck floods ride the centreline, elevated so omni pools land on the deck below.
            int nDeck = DeckCount(length);
            for (int k = 0; k < nDeck; k++)
            {
                float z = nDeck == 1 ? 0f : Mathf.Lerp(length * 0.30f, -length * 0.30f, k / (float)(nDeck - 1));
                list.Add(new ShipMount { local = new Vector3(0f, height * 0.50f, z), face = Vector3.down, role = ShipMountRole.DeckFlood, anchor = null });
            }
            // Turret-slaved searchlights (aim-tracked): one per trainable turret, throw follows that
            // turret's weapon max range (CIWS short, missiles long), capped by the cone-length knob.
            if (TryTurretAnchors(unit, out var tanchors) && tanchors.Count > 0)
            {
                float up = Mathf.Max(0.5f, length * 0.006f);
                float fwd = Mathf.Max(0.8f, length * 0.008f);
                foreach (var ta in tanchors)
                    list.Add(new ShipMount { local = Vector3.zero, face = Vector3.forward, role = ShipMountRole.Searchlight, anchor = ta.anchor, anchorOffset = new Vector3(0f, up, fwd), turret = ta.turret, mountSize = ta.size });
            }
            else
            {
                list.Add(new ShipMount { local = new Vector3(0f, height * 0.80f, length * 0.10f), face = Vector3.forward, role = ShipMountRole.Searchlight, anchor = null });
            }
            mounts = list.ToArray();
            return true;
        }
    }

    // Per-ship rig: searchlight lens/cone + deferred pools, parented to the unit (zero-CPU tracking).
    internal sealed class ShipLightRig : MonoBehaviour
    {
        internal ShipMount[] mounts;
        Unit unit;
        public int bowSign; // 0 = unlatched (+Z baked), +1 = +Z confirmed, -1 = mirrored to -Z
        Vector3 lastPos;
        bool hasLast;
        internal float searchLensScale = 1f; // per-tick anti-shimmer scale (manager composes the matrix)
        internal bool showSearchFx;          // LOD + on/off state for the manager draw pass
        internal Vector3 freeLensPos = Vector3.zero;  // snapped mast-fallback fixture (rig space)
        internal Quaternion freeLensRot = Quaternion.identity;
        internal bool hasFreeLens;
        readonly List<GameObject> fxGOs = new List<GameObject>(); // CPU-era search fixtures (anchor- or rig-parented)
        readonly List<MeshRenderer> fxMRs = new List<MeshRenderer>();
        readonly List<bool> fxCone = new List<bool>(); // true = cone (coneScale), false = lens (searchLensScale)
        readonly List<bool> fxFree = new List<bool>(); // true = mast-fallback pair (pose refreshes per tick)
        internal int shownFx; // fixtures posed visible this tick
        readonly List<ShipMount> fxMount = new List<ShipMount>(); // owning mount per fixture entry (turret gating)
        readonly List<Material> fxConeMat = new List<Material>(); // per-mount cone clones (size dimming)
        float maxMountSize; // biggest searchlight mount on this hull (size scaling reference)
        internal static AccessTools.FieldRef<Turret, bool> manualRef; // human-operated turret flag (cached, optional)
        internal static AccessTools.FieldRef<Turret, float> rangeRef; // weapon max range, mirrors the game's engage gate
        internal static AccessTools.FieldRef<Turret, float> elMinRef, elMaxRef; // elevation stops (fixed-vertical cells)
        internal static bool turretRefsTried;
        internal static void EnsureTurretRefs()
        {
            if (turretRefsTried) return;
            turretRefsTried = true;
            try { manualRef = AccessTools.FieldRefAccess<Turret, bool>("manual"); } catch { manualRef = null; }
            try { rangeRef = AccessTools.FieldRefAccess<Turret, float>("maxRange"); } catch { rangeRef = null; }
            try { elMinRef = AccessTools.FieldRefAccess<Turret, float>("minElevation"); } catch { elMinRef = null; }
            try { elMaxRef = AccessTools.FieldRefAccess<Turret, float>("maxElevation"); } catch { elMaxRef = null; }
        }
        static bool ManualMode(Turret t) // human-aimed turrets count as tracking (no target object needed)
        {
            try { EnsureTurretRefs(); return manualRef != null && manualRef(t); }
            catch { return false; }
        }
        // Size scaling: small mounts throw weaker beams (dimmer + shorter) vs the hull's biggest mount.
        float SizeFactor(ShipMount m)
        {
            float k = 0f;
            try { k = Mathf.Clamp01(RealisticNightPlugin.ShipSearchSizeScaling.Value); } catch { k = 0f; }
            if (k <= 0.001f || maxMountSize <= 0.01f) return 1f;
            float norm = (m.mountSize > 0.01f ? m.mountSize : maxMountSize) / maxMountSize;
            return Mathf.Lerp(1f, Mathf.Clamp(norm, 0.15f, 1f), k);
        }

        // Per-turret throw: weapon max range capped by the cone-length knob (mast fallback = full cap).
        static float MountThrow(ShipMount m, float cap)
        {
            try
            {
                Turret t = m.turret;
                if (t != null)
                {
                    float r = 0f;
                    try { EnsureTurretRefs(); if (rangeRef != null) r = rangeRef(t); } catch { r = 0f; }
                    if (r > 1f) return Mathf.Min(r, cap);
                }
            }
            catch { }
            return cap;
        }
        // Per-mount searchlight gate: turret must track a live target (or be human-operated).
        internal static bool MountTracking(ShipMount m)
        {
            if (!RealisticNightPlugin.ShipSearchTargetGate.Value) return true; // toggle off: always on
            try
            {
                Turret t = m.turret;
                if (t == null) return true; // mast fallback (no turret): always on
                if (ManualMode(t)) return true;
                Unit tgt = null;
                try { tgt = t.GetTarget(); } catch { return true; } // fail-open on API mismatch
                if (tgt == null) return false;
                try { if (tgt.disabled) return false; } catch { }
                return true;
            }
            catch { return true; }
        }

        // Beam spread, live from config (same convention as headlight cone angle).
        internal static float SearchHalfDeg() { return Mathf.Clamp(RealisticNightPlugin.ShipSearchConeAngle.Value, 3f, 40f); }

        public void Build(ShipMount[] shipMounts, Unit owner)
        {
            mounts = shipMounts;
            maxMountSize = 0f;
            if (mounts != null) foreach (ShipMount sm in mounts) if (sm.role == ShipMountRole.Searchlight && sm.mountSize > maxMountSize) maxMountSize = sm.mountSize;
            unit = owner;
            bowSign = 0;
            hasLast = false;
            searchLensScale = 1f;
            showSearchFx = false;
            hasFreeLens = false;
            try { ResolveFreeFixture(); } catch { }
            // Pose immediately so the first frame is correct even before the next manager tick.
            try { UpdateRig(true, true, transform.position, float.MaxValue, 0f); } catch { }
        }

        // Mast-fallback fixture: hull-snapped once (shared raycast helper); re-snapped on bow flip.
        // Turret mounts need nothing built (anchor + offset ride the pivot directly).
        void ResolveFreeFixture()
        {
            hasFreeLens = false;
            try
            {
                float bowF = bowSign < 0 ? -1f : 1f;
                foreach (ShipMount m in mounts)
                {
                    if (m.role != ShipMountRole.Searchlight || m.anchor != null) continue;
                    Vector3 lp = m.local; lp.z *= bowF;
                    Vector3 face = m.face; face.z *= bowF;
                    HeadlightRig.SnapPose(transform, lp, face, 30f, out freeLensPos, out freeLensRot);
                    hasFreeLens = true;
                    break; // single fallback mount by construction
                }
            }
            catch { hasFreeLens = false; }
        }

        // Plain child search fixtures (CPU path): anchor children track the turret free; one rig-child
        // pair covers the mast fallback (mirrors the old feed: every anchor-less mount shares it).
        void EnsureFixtureGOs()
        {
            if (fxGOs.Count > 0) return;
            Mesh cm = ShipLights.SearchConeMesh, qm = ShipLights.SearchQuadMesh;
            if (cm == null || qm == null || mounts == null) return;
            foreach (ShipMount m in mounts)
            {
                if (m.role != ShipMountRole.Searchlight) continue;
                if (m.anchor != null) AddFixturePair(m.anchor, m.anchorOffset, Quaternion.identity, false, m);
            }
            ShipMount freeMount = default(ShipMount);
            bool needFree = false;
            foreach (ShipMount m in mounts) if (m.role == ShipMountRole.Searchlight && m.anchor == null) { needFree = true; freeMount = m; break; }
            if (needFree && hasFreeLens) AddFixturePair(transform, freeLensPos, freeLensRot, true, freeMount);
        }
        void AddFixturePair(Transform parent, Vector3 lp, Quaternion lr, bool free, ShipMount mount)
        {
            try
            {
                GameObject cg = new GameObject("shcone"), lg = new GameObject("shlens");
                cg.transform.SetParent(parent, worldPositionStays: false);
                lg.transform.SetParent(parent, worldPositionStays: false);
                cg.AddComponent<MeshFilter>().sharedMesh = ShipLights.SearchConeMesh;
                lg.AddComponent<MeshFilter>().sharedMesh = ShipLights.SearchQuadMesh;
                MeshRenderer cmr = cg.AddComponent<MeshRenderer>(), lmr = lg.AddComponent<MeshRenderer>();
                cmr.shadowCastingMode = ShadowCastingMode.Off; cmr.receiveShadows = false;
                lmr.shadowCastingMode = ShadowCastingMode.Off; lmr.receiveShadows = false;
                cg.transform.localPosition = lp; cg.transform.localRotation = lr;
                lg.transform.localPosition = lp; lg.transform.localRotation = lr;
                cg.SetActive(false); lg.SetActive(false);
                fxGOs.Add(cg); fxMRs.Add(cmr); fxCone.Add(true); fxFree.Add(free); fxMount.Add(mount);
                Material coneClone = null; // per-mount dimming needs its own material (shared can't vary)
                try
                {
                    Material srcm = ShipLights.SearchConeBeamMat ?? ShipLights.SearchConeSoftMat;
                    if (srcm != null) { coneClone = UnityEngine.Object.Instantiate(srcm); coneClone.name = "RN_ShipConeFx"; }
                }
                catch { coneClone = null; }
                fxConeMat.Add(coneClone);
                fxGOs.Add(lg); fxMRs.Add(lmr); fxCone.Add(false); fxFree.Add(free); fxMount.Add(mount);
                fxConeMat.Add(null);
            }
            catch { }
        }

        // Bow latch: resolve fore/aft from motion (needs >3 m/s fore-aft; sway never latches).
        public void LatchTick(float dt)
        {
            if (bowSign != 0) return;
            try
            {
                Vector3 p = transform.position;
                if (!hasLast || dt <= 0.001f) { lastPos = p; hasLast = true; return; }
                Vector3 vel = (p - lastPos) / dt;
                lastPos = p;
                float fwd = Vector3.Dot(transform.forward, vel);
                if (Mathf.Abs(fwd) <= 3f) return;
                bowSign = fwd >= 0f ? 1 : -1;
                if (bowSign < 0) { try { ResolveFreeFixture(); } catch { } } // baked +Z was stern-first
            }
            catch { try { lastPos = transform.position; hasLast = true; } catch { } }
        }

        // Beam per mount: pivot forward for turrets (tracks aim incl. skyward), bow-forward for mast fallback.
        bool BeamFor(ShipMount m, out Vector3 dirW, out float cosHalf)
        {
            cosHalf = Mathf.Cos(SearchHalfDeg() * 0.5f * Mathf.Deg2Rad);
            dirW = Vector3.forward;
            try
            {
                if (m.anchor != null) { dirW = m.anchor.forward; return m.anchor.gameObject.activeInHierarchy; }
                float bowF = bowSign < 0 ? -1f : 1f;
                dirW = transform.rotation * (Vector3.forward * bowF);
                return true;
            }
            catch { return false; }
        }

        // World position of a mount (anchor space or mirrored rig-root space).
        bool MountWorld(ShipMount m, out Vector3 w)
        {
            w = Vector3.zero;
            try
            {
                if (m.anchor != null)
                {
                    if (!m.anchor.gameObject.activeInHierarchy) return false; // turret gone: light out
                    w = m.anchor.TransformPoint(m.anchorOffset);
                }
                else
                {
                    Vector3 lp = m.local;
                    if (bowSign < 0) lp.z *= -1f;
                    w = transform.TransformPoint(lp);
                }
                return true;
            }
            catch { return false; }
        }

        // Deck-flood feed: raw-global positions (same floating-origin convention as everything else).
        public void AppendDeckLamps(Vector4[] posBuf, ref int idx, int cap, Vector3 camPos, float maxD2)
        {
            if (mounts == null || idx >= cap) return;
            try { if (unit == null || unit.disabled) return; } catch { return; } // wrecks feed no pools
            bool hasOrigin = Datum.origin != null;
            float bowF = bowSign < 0 ? -1f : 1f;
            foreach (ShipMount m in mounts)
            {
                if (m.role != ShipMountRole.DeckFlood) continue;
                if (idx >= cap) break;
                Vector3 lp = m.local; lp.z *= bowF;
                Vector3 w;
                try { w = transform.TransformPoint(lp); } catch { continue; }
                if (VehicleHeadlights.BadVec(w)) continue;
                if ((w - camPos).sqrMagnitude > maxD2) continue;
                Vector3 raw = hasOrigin ? Datum.origin.InverseTransformPoint(w) : w;
                posBuf[idx] = new Vector4(raw.x, raw.y, raw.z, 0f);
                idx++;
            }
        }

        // Searchlight feed: positions + beam dirs for the spot backend. Dead anchors feed nothing.
        public void AppendSearchLamps(Vector4[] posBuf, Vector4[] dirBuf, ref int idx, int cap, Vector3 camPos, float maxD2)
        {
            if (mounts == null || idx >= cap) return;
            try { if (unit == null || unit.disabled) return; } catch { return; }
            bool hasOrigin = Datum.origin != null;
            foreach (ShipMount m in mounts)
            {
                    if (m.role != ShipMountRole.Searchlight) continue;
                    if (idx >= cap) break;
                    if (!MountTracking(m)) continue; // dark until its turret tracks (toggle)
                    if (!MountWorld(m, out Vector3 w)) continue;
                    if (VehicleHeadlights.BadVec(w)) continue;
                if ((w - camPos).sqrMagnitude > maxD2) continue;
                if (!BeamFor(m, out Vector3 dirW, out float cosHalf)) continue;
                    if (VehicleHeadlights.BadVec(dirW)) continue; // broken rotation: drop the mount
                Vector3 raw = hasOrigin ? Datum.origin.InverseTransformPoint(w) : w;
                posBuf[idx] = new Vector4(raw.x, raw.y, raw.z, 0f);
                if (dirBuf != null) dirBuf[idx] = new Vector4(dirW.x, dirW.y, dirW.z, cosHalf);
                idx++;
            }
        }

        // Per-tick rig state (no GameObjects): visibility flag + lens scale for the manager draw pass.
        public void UpdateRig(bool searchFxOn, bool lightsActive, Vector3 camPos, float maxD2, float pxScale)
        {
            bool alive = false;
            try { alive = unit != null && !unit.disabled; } catch { alive = false; }
            float lensSize = Mathf.Max(0.1f, RealisticNightPlugin.HeadlightLensSize.Value);
            float minPx = Mathf.Max(0f, RealisticNightPlugin.HeadlightLensMinPx.Value);
            Vector3 lensW = camPos;
            try
            {
                // Scale from the search fixture's world pos (turret pivot or snapped mast point).
                bool gotW = false;
                if (mounts != null)
                {
                    foreach (ShipMount m in mounts)
                    {
                        if (m.role != ShipMountRole.Searchlight) continue;
                        if (m.anchor != null) { lensW = m.anchor.TransformPoint(m.anchorOffset); gotW = true; }
                        else if (hasFreeLens) { lensW = transform.TransformPoint(freeLensPos); gotW = true; }
                        break;
                    }
                }
                if (!gotW) lensW = transform.position;
            }
            catch { try { lensW = transform.position; } catch { } }
            searchLensScale = HeadlightRig.DecalScale(lensSize, minPx, lensW, camPos, pxScale);
            bool near = true;
            try { near = (transform.position - camPos).sqrMagnitude <= maxD2; }
            catch { near = true; }
            showSearchFx = searchFxOn && near && alive;
            // CPU fixtures: anchor children track the turret free; the mast pair re-snaps (bow flips move it).
            if (fxGOs.Count == 0) EnsureFixtureGOs(); // lazy (shared meshes first)
            // Cone dims live in the shared mesh (reshaped on knob change above); GOs ride at identity scale.
            Material cm = ShipLights.SearchConeBeamMat ?? ShipLights.SearchConeSoftMat, lm = ShipLights.SearchLensSoftMat;
            shownFx = 0;
            float capLen = Mathf.Clamp(RealisticNightPlugin.ShipSearchConeLength.Value, 2f, 10000f);
            for (int i = 0; i < fxGOs.Count; i++)
            {
                GameObject go = fxGOs[i];
                if (go == null) continue;
                bool isCone = i < fxCone.Count && fxCone[i];
                ShipMount fxm = i < fxMount.Count ? fxMount[i] : default(ShipMount);
                bool vis = showSearchFx && (isCone ? cm != null : lm != null) && MountTracking(fxm);
                try
                {
                    if (go.activeSelf != vis) go.SetActive(vis);
                    if (!vis) continue;
                    if (i < fxFree.Count && fxFree[i]) // mast fallback: refresh snapped pose
                    {
                        go.transform.localPosition = freeLensPos;
                        go.transform.localRotation = freeLensRot;
                    }
                    MeshRenderer mr = i < fxMRs.Count ? fxMRs[i] : null;
                    Material want = isCone ? cm : lm;
                    if (isCone) // per-turret throw x size scaling (shared mesh is built at cap length)
                    {
                        float sizeF = SizeFactor(fxm);
                        float s = MountThrow(fxm, capLen) / capLen * sizeF;
                        go.transform.localScale = new Vector3(1f, 1f, Mathf.Clamp(s, 0.02f, 1f));
                        Material clone = i < fxConeMat.Count ? fxConeMat[i] : null;
                        if (mr != null && clone != null)
                        {
                            if (mr.sharedMaterial != clone) mr.sharedMaterial = clone;
                            if (ShipLights.ConeColorChanged)
                            {
                                Color sc = Color.white;
                                try { Material sm = ShipLights.SearchConeBeamMat ?? ShipLights.SearchConeSoftMat; if (sm != null) sc = sm.GetColor(GlowFX.ColorID); } catch { }
                                clone.SetColor(GlowFX.ColorID, new Color(sc.r * sizeF, sc.g * sizeF, sc.b * sizeF, sc.a));
                            }
                            shownFx++;
                            continue;
                        }
                    }
                    else go.transform.localScale = Vector3.one * searchLensScale;
                    if (mr != null && want != null && mr.sharedMaterial != want) mr.sharedMaterial = want;
                    shownFx++;
                }
                catch { }
            }
        }

        void OnDestroy()
        {
            // Same orphan rule as ground rigs: fixture GOs (rig- or turret-parented) must die with us,
            // or they render purple on the destroyed shared materials after a mod toggle.
            try { if (fxGOs != null) foreach (GameObject go in fxGOs) if (go != null) UnityEngine.Object.Destroy(go); } catch { }
            fxGOs.Clear(); fxMRs.Clear(); fxCone.Clear(); fxFree.Clear(); fxMount.Clear();
            try { if (fxConeMat != null) foreach (Material mm in fxConeMat) if (mm != null) UnityEngine.Object.Destroy(mm); } catch { }
            fxConeMat.Clear();
        }
    }

    // One deferred pool backend per ship family (deck omni / search spot clones). Own uniforms, zero bundle work.
    internal sealed class ShipPoolMat
    {
        internal static readonly int PIntensity = Shader.PropertyToID("_Intensity");
        internal static readonly int PIntensityWarm = Shader.PropertyToID("_IntensityWarm");
        internal static readonly int PAtmos = Shader.PropertyToID("_AtmosRange");
        internal static readonly int PAtmosCurve = Shader.PropertyToID("_AtmosCurve");
        internal static readonly int PRange = Shader.PropertyToID("_Range");
        internal static readonly int PFalloff = Shader.PropertyToID("_FalloffK");
        internal static readonly int PNdotL = Shader.PropertyToID("_NdotL");
        internal static readonly int PColor = Shader.PropertyToID("_Color");
        internal static readonly int PColorWarm = Shader.PropertyToID("_ColorWarm");
        internal static readonly int PLampsBuf = Shader.PropertyToID("_LampsBuf");
        internal static readonly int PLampCount = Shader.PropertyToID("_LampCount");
        internal static readonly int PSpotDirs = Shader.PropertyToID("_SpotDirs");
        internal static readonly int PPreserveID = Shader.PropertyToID("_Preserve");
        internal static readonly int PTintStrength = Shader.PropertyToID("_TintStrength");

        const int Cap = 2048; // ships are few; 2K lamps each is far beyond any fleet
        public Material mat;
        ComputeBuffer buf, dirBuf;
        readonly Vector4[] lamps = new Vector4[Cap];
        readonly Vector4[] dirs;
        int count;
        bool dirty, dirsDirty;
        readonly bool wantDirs;
        float nextTry;
        public int VolPass = -1, CompPass = -1;

        public ShipPoolMat(bool spot) { wantDirs = spot; if (spot) dirs = new Vector4[Cap]; }
        public Material Mat => mat;
        public int LampCount => count;
        public bool Ready => mat != null && VolPass >= 0 && CompPass >= 0;

        public bool Ensure(Material src, string volName, string compName, string name)
        {
            if (mat != null) return true;
            if (Time.realtimeSinceStartup < nextTry) return false;
            nextTry = Time.realtimeSinceStartup + 5f;
            try
            {
                if (src == null || src.shader == null || !src.shader.isSupported) return false;
                int vp = src.FindPass(volName), cp = src.FindPass(compName);
                if (vp < 0 || cp < 0) return false;
                VolPass = vp;
                CompPass = cp;
                mat = UnityEngine.Object.Instantiate(src);
                mat.name = name;
                return true;
            }
            catch (Exception e)
            {
                RealisticNightPlugin.Log.LogWarning($"[ShipLights] pool backend '{name}' failed: {e.Message}");
                return false;
            }
        }

        public void SetLamps(Vector4[] pos, Vector4[] dir, int n)
        {
            n = Mathf.Clamp(n, 0, Cap);
            for (int i = 0; i < n; i++) lamps[i] = pos[i];
            if (wantDirs && dir != null) for (int i = 0; i < n; i++) dirs[i] = dir[i];
            count = n;
            dirty = true;
            dirsDirty = true;
        }

        public void Upload()
        {
            if (mat == null || count <= 0) return;
            if (buf == null || !buf.IsValid()) { buf = new ComputeBuffer(Cap, 16, ComputeBufferType.Structured); dirty = true; }
            if (dirty) { buf.SetData(lamps, 0, 0, count); dirty = false; }
            mat.SetBuffer(PLampsBuf, buf);
            mat.SetInt(PLampCount, count);
            if (wantDirs)
            {
                if (dirBuf == null || !dirBuf.IsValid()) { dirBuf = new ComputeBuffer(Cap, 16, ComputeBufferType.Structured); dirsDirty = true; }
                if (dirsDirty) { dirBuf.SetData(dirs, 0, 0, count); dirsDirty = false; }
                mat.SetBuffer(PSpotDirs, dirBuf);
            }
        }

        public void Clear() { count = 0; }
    }

    // Ship light manager: 1 Hz sweep, per-tick rig + pool updates. Same structure as headlights.
    internal static class ShipLights
    {
        static readonly Dictionary<int, ShipLightRig> rigs = new Dictionary<int, ShipLightRig>();
        static float nextScan;
        static bool lastOn;
        static Vector4[] deckBuf, searchBuf, searchDirBuf;
        // Same hitch guards as ground (hold last camera, damp colours, debounced feeds).
        static Vector3 slastCamPos = Vector3.zero;
        static bool shaveCamPos;
        static Color ssmLens = Color.white, ssmCone = Color.white;
        static float ssmHaze = 1f;
        static bool ssmInit;
        static int deckStreak, searchStreak;
        static readonly ShipPoolMat deckMat = new ShipPoolMat(false); // omni clone of the street ground mat
        static readonly ShipPoolMat searchMat = new ShipPoolMat(true); // spot clone of HeadlightSpotMat
        // CPU-era fixtures: shared meshes + shared SoftGlow materials (Sprites/Default: built-in,
        // always included, proven). Rigs own plain child GOs; colours pushed once per tick below.
        static Material searchConeSoftMat, searchLensSoftMat;
        static Material searchConeBeamMat; // bundle beam shader clone (null = SoftGlow look, never invisible)
        static Mesh coneMesh, quadMesh; // shared drawn templates (live forever once built; never destroyed)
        static Texture2D coneTex, dotTex;
        internal static Material SearchConeSoftMat => searchConeSoftMat;
        internal static Material SearchLensSoftMat => searchLensSoftMat;
        internal static Material SearchConeBeamMat => searchConeBeamMat; // null when the bundle lacks it (rigs fall back)
        internal static Mesh SearchConeMesh => coneMesh;
        internal static Mesh SearchQuadMesh => quadMesh;
        static float bakedApexR, bakedBaseR, bakedLen; // shared cone dims (mesh rebuilt on knob change only)
        static readonly int ColorID = Shader.PropertyToID("_Color");
        static readonly int PConeLen = Shader.PropertyToID("_ConeLen");
        static readonly int PConeBaseR = Shader.PropertyToID("_ConeBaseR");
        static readonly int PConeApexR = Shader.PropertyToID("_ConeApexR");
        static bool glowWarned;

        // Render-pass gates (mirrors HeadlightPools accessors).
        public static Material DeckMat => (deckMat.Ready && deckMat.LampCount > 0) ? deckMat.Mat : null;
        public static Material SearchMat => (searchMat.Ready && searchMat.LampCount > 0) ? searchMat.Mat : null;
        public static int DeckLamps => deckMat.LampCount;
        public static int SearchLamps => searchMat.LampCount;
        public static int DeckVol => deckMat.VolPass;
        public static int DeckComp => deckMat.CompPass;
        public static int SearchVol => searchMat.VolPass;
        public static int SearchComp => searchMat.CompPass;

        public static void Apply(bool on, bool lightsActive)
        {
            if (!on)
            {
                if (lastOn || rigs.Count > 0) Teardown();
                deckMat.Clear();
                searchMat.Clear();
                lastOn = false;
                return;
            }
            lastOn = true;
            EnsureSoftMats(); // shared meshes + fixture materials first (rigs reference them at Build)
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 1f; // 1 Hz registry sweep; rigs follow free
                Scan();
            }
            Camera cam = RealisticNightPlugin.MainCamera(out Vector3 freshPos);
            Vector3 camPos;
            if (cam != null) { camPos = freshPos; slastCamPos = camPos; shaveCamPos = true; }
            else if (shaveCamPos) camPos = slastCamPos;
            else camPos = Vector3.zero;
            float nearest2 = float.PositiveInfinity;
            foreach (var kv in rigs)
            {
                ShipLightRig r = kv.Value;
                if (r == null) continue;
                try
                {
                    float dd = (r.transform.position - camPos).sqrMagnitude;
                    if (dd < nearest2) nearest2 = dd;
                }
                catch { }
            }
            float rawHaze = StreetLights.HazeFade(float.IsPositiveInfinity(nearest2) ? float.PositiveInfinity : Mathf.Sqrt(nearest2));
            float scb = Mathf.Max(0f, RealisticNightPlugin.ShipSearchConeBrightness.Value);
            Color scc = RealisticNightPlugin.ShipSearchConeColour.Value; // cone tint (alpha fades the beam)
            Color rawLens = new Color(1f, 0.96f, 0.88f) * scb * rawHaze;
            Color rawCone = new Color(scc.r * scb, scc.g * scb, scc.b * scb, scc.a * scb) * rawHaze;
            float dtSm = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f);
            float kSm = 1f - Mathf.Exp(-dtSm * 6f);
            if (!ssmInit) { ssmHaze = rawHaze; ssmLens = rawLens; ssmCone = rawCone; ssmInit = true; }
            else { ssmHaze = Mathf.Lerp(ssmHaze, rawHaze, kSm); ssmLens = Color.Lerp(ssmLens, rawLens, kSm); ssmCone = Color.Lerp(ssmCone, rawCone, kSm); }
            ConeColorChanged = !coneChInit || !NearCol(ssmCone, lastConeC) || !NearCol(ssmLens, lastLensC);
            if (ConeColorChanged) { lastConeC = ssmCone; lastLensC = ssmLens; coneChInit = true; }
            float maxD = RealisticNightPlugin.ShipMaxDist.Value;
            float maxD2 = maxD * maxD;
            float pxScale = 0f;
            try
            {
                if (cam != null && cam.fieldOfView > 1f && Screen.height > 0)
                    pxScale = (Screen.height * 0.5f) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            }
            catch { pxScale = 0f; }
            float dt = Time.deltaTime;
            bool showSearch = lightsActive && RealisticNightPlugin.ShipSearchlight.Value;
            foreach (var kv in rigs)
            {
                ShipLightRig rig = kv.Value;
                if (rig == null) continue;
                try { rig.LatchTick(dt); } catch { }
                rig.UpdateRig(showSearch, lightsActive, camPos, maxD2, pxScale);
            }
            ComposeInstances();
            UpdatePools(lightsActive, camPos, maxD2);
        }

        // Shared fixture meshes + SoftGlow materials (Sprites/Default: built-in, always included).
        // Colours pushed once per tick. No bundle needed for fixtures.
        static bool EnsureSoftMats()
        {
            if (searchConeSoftMat != null && searchLensSoftMat != null) return true;
            if (quadMesh == null) quadMesh = GlowFX.Billboard();
            if (coneMesh == null) coneMesh = GlowFX.BeamConeMesh(10f, 1.75f, 12);
            if (coneTex == null) coneTex = GlowFX.ShipBeamTex(); // searchlight-only beam (slow falloff + streaks)
            if (dotTex == null) dotTex = GlowFX.RadialTex(64, 0.35f, 1.2f);
            if (quadMesh == null || coneMesh == null || coneTex == null || dotTex == null) return false;
            if (searchConeSoftMat == null) searchConeSoftMat = GlowFX.SoftGlow(coneTex, "RN_ShipSearchCone");
            if (searchLensSoftMat == null) searchLensSoftMat = GlowFX.SoftGlow(dotTex, "RN_ShipSearchLens");
            if (searchConeBeamMat == null) // bundle beam shader (soft edges); missing/unsupported = SoftGlow look
            {
                Material src = StreetLightFX.ShipBeamMat;
                if (src != null && src.shader != null && src.shader.isSupported)
                {
                    searchConeBeamMat = UnityEngine.Object.Instantiate(src);
                    searchConeBeamMat.name = "RN_ShipSearchBeam";
                    searchConeBeamMat.SetTexture(GlowFX.MainTexID, coneTex);
                }
            }
            if (searchConeSoftMat == null || searchLensSoftMat == null)
            {
                if (!glowWarned)
                {
                    glowWarned = true;
                    RealisticNightPlugin.Log.LogWarning("[ShipLights] fixture materials need Sprites/Default - searchlight cone/lens off (pools unaffected).");
                }
                return false;
            }
            return true;
        }

        // Shared search cone, reshaped in place when knobs change (start width + angle end + length are
        // baked as real dims; GOs ride at identity scale). One 72-vert rebuild per change, never per tick.
        static void EnsureConeMesh(float apexR, float baseR, float len)
        {
            if (coneMesh != null && Mathf.Approximately(bakedApexR, apexR) && Mathf.Approximately(bakedBaseR, baseR) && Mathf.Approximately(bakedLen, len)) return;
            try
            {
                Mesh src = GlowFX.BeamConeMesh(len, baseR, 12, apexR); // single shell (edge fade lives in the beam shader)
                if (coneMesh == null) { coneMesh = new Mesh { name = "RN_ShipSearchCone" }; coneMesh.MarkDynamic(); }
                coneMesh.Clear();
                coneMesh.SetVertices(src.vertices);
                coneMesh.SetUVs(0, src.uv);
                coneMesh.SetColors(src.colors);
                coneMesh.SetNormals(src.normals);
                coneMesh.SetTriangles(src.triangles, 0);
                coneMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200000f); // never culled
                UnityEngine.Object.Destroy(src);
                bakedApexR = apexR; bakedBaseR = baseR; bakedLen = len;
                if (searchConeBeamMat != null) // interior pass reads live dims (shared mesh = shared dims)
                {
                    searchConeBeamMat.SetFloat(PConeLen, len);
                    searchConeBeamMat.SetFloat(PConeBaseR, baseR);
                    searchConeBeamMat.SetFloat(PConeApexR, apexR);
                }
            }
            catch { }
        }

        internal static bool ConeColorChanged = true; // per-mount clone push gate (epsilon: damped values converge)
        static bool coneChInit;
        static Color lastConeC = Color.white, lastLensC = Color.white;
        static bool NearCol(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 1e-3f && Mathf.Abs(a.g - b.g) < 1e-3f && Mathf.Abs(a.b - b.b) < 1e-3f && Mathf.Abs(a.a - b.a) < 1e-3f;
        }

        // Push fixture colours once (rigs pose their own child GOs).
        static void ComposeInstances()
        {
            if (!EnsureSoftMats()) return;
            float apexR = Mathf.Max(0.02f, RealisticNightPlugin.ShipSearchConeStartWidth.Value * 0.5f);
            float coneLenC = Mathf.Clamp(RealisticNightPlugin.ShipSearchConeLength.Value, 2f, 10000f);
            float baseR = Mathf.Tan(ShipLightRig.SearchHalfDeg() * Mathf.Deg2Rad) * coneLenC; // true cone: the slider reads honest at any length
            EnsureConeMesh(apexR, baseR, coneLenC);
            searchConeSoftMat.SetColor(ColorID, ssmCone);
            searchLensSoftMat.SetColor(ColorID, ssmLens);
            if (searchConeBeamMat != null) searchConeBeamMat.SetColor(ColorID, ssmCone); // same values: seamless fallback
        }

        static void Scan()
        {
            // Prune dead rigs (unit destroyed / scene reload took the GameObject).
            var dead = new List<int>();
            foreach (var kv in rigs) if (kv.Value == null) dead.Add(kv.Key);
            foreach (int k in dead) rigs.Remove(k);

            List<Unit> units;
            try { units = UnitRegistry.allUnits; } catch { return; }
            if (units == null) return;
            // Snapshot: registry mutates on disable during iteration.
            Unit[] snap;
            try { snap = units.ToArray(); } catch { return; }
            foreach (Unit u in snap)
            {
                if (u == null || u.disabled) continue;
                if (!(u is Ship)) continue;
                int id = u.GetInstanceID();
                if (rigs.ContainsKey(id)) continue;
                if (!ShipLayout.TryMounts(u, out ShipMount[] mounts)) continue;
                var rig = u.gameObject.AddComponent<ShipLightRig>();
                rig.Build(mounts, u);
                rigs[id] = rig;
            }
        }

        static void UpdatePools(bool lightsActive, Vector3 camPos, float maxD2)
        {
            // Deck omni pools (own intensity/range). Lone empty feed keeps the last buffer (no hitch-blink).
            if (!lightsActive || !RealisticNightPlugin.ShipDeckFloods.Value) { deckMat.Clear(); deckStreak = 0; }
            else
            {
                if (deckBuf == null) deckBuf = new Vector4[2048];
                int found = 0;
                foreach (var kv in rigs)
                {
                    ShipLightRig rig = kv.Value;
                    // Flicker fix: skip dead rigs, only stop when full (see headlights).
                    if (rig == null) continue;
                    if (found >= 2048) break;
                    rig.AppendDeckLamps(deckBuf, ref found, 2048, camPos, maxD2);
                }
                if (found == 0 && deckMat.LampCount > 0 && deckStreak < 3) deckStreak++;
                else
                {
                    deckStreak = 0;
                    if (found > 0 && deckMat.Ensure(StreetLightFX.Mat, "RN_StreetLight_Volume", "RN_StreetLight_Composite", "RN_ShipDeck"))
                        ApplyDeckLook();
                    deckMat.SetLamps(deckBuf, null, found);
                    deckMat.Upload();
                }
            }
            // Searchlight spot pools (own intensity/range).
            if (!lightsActive || !RealisticNightPlugin.ShipSearchlight.Value) { searchMat.Clear(); searchStreak = 0; }
            else
            {
                if (searchBuf == null) searchBuf = new Vector4[2048];
                if (searchDirBuf == null) searchDirBuf = new Vector4[2048];
                int found = 0;
                foreach (var kv in rigs)
                {
                    ShipLightRig rig = kv.Value;
                    // Flicker fix: skip dead rigs, only stop when full (see headlights).
                    if (rig == null) continue;
                    if (found >= 2048) break;
                    rig.AppendSearchLamps(searchBuf, searchDirBuf, ref found, 2048, camPos, maxD2);
                }
                if (found == 0 && searchMat.LampCount > 0 && searchStreak < 3) searchStreak++;
                else
                {
                    searchStreak = 0;
                    if (found > 0 && searchMat.Ensure(StreetLightFX.SpotMat, "RN_Headlight_Spot", "RN_Headlight_SpotComposite", "RN_ShipSearch"))
                        ApplySearchLook();
                    searchMat.SetLamps(searchBuf, searchDirBuf, found);
                    searchMat.Upload();
                }
            }
        }

        static void ApplyDeckLook()
        {
            Material m = deckMat.Mat;
            if (m == null) return;
            float inten = Mathf.Max(0f, RealisticNightPlugin.ShipDeckIntensity.Value);
            m.SetFloat(ShipPoolMat.PIntensity, inten);
            m.SetFloat(ShipPoolMat.PIntensityWarm, inten);
            m.SetFloat(ShipPoolMat.PAtmos, Mathf.Max(100f, RealisticNightPlugin.StreetLightAtten.Value)); // UNIVERSAL haze
            m.SetFloat(ShipPoolMat.PAtmosCurve, Mathf.Clamp(RealisticNightPlugin.StreetLightAttenCurve.Value, 0.2f, 3f));
            float pr = Mathf.Max(1f, RealisticNightPlugin.ShipDeckRange.Value);
            m.SetFloat(ShipPoolMat.PRange, pr);
            float gf = Mathf.Clamp01(RealisticNightPlugin.ShipDeckPoolFalloff.Value);
            m.SetFloat(ShipPoolMat.PFalloff, (gf * gf * 25f) / (pr * pr)); // same range-normalized shape as street
            m.SetFloat(ShipPoolMat.PNdotL, Mathf.Clamp01(RealisticNightPlugin.ShipDeckPoolShading.Value)); // same N.L as street
            Color dc = RealisticNightPlugin.ShipDeckPoolColour.Value;
            float dtrim = Mathf.Clamp01(dc.a); // alpha trims the tint strength (street convention)
            Color dcol = new Color(dc.r * dtrim, dc.g * dtrim, dc.b * dtrim, 1f);
            m.SetColor(ShipPoolMat.PColor, dcol);
            m.SetColor(ShipPoolMat.PColorWarm, dcol);
            m.SetFloat(ShipPoolMat.PPreserveID, Mathf.Clamp01(RealisticNightPlugin.StreetGroundColorPreserve.Value)); // shared omni behaviour
            m.SetFloat(ShipPoolMat.PTintStrength, Mathf.Clamp01(RealisticNightPlugin.StreetGroundTint.Value)); // shared tint knob
        }

        static void ApplySearchLook()
        {
            Material m = searchMat.Mat;
            if (m == null) return;
            float inten = Mathf.Max(0f, RealisticNightPlugin.ShipSearchIntensity.Value);
            m.SetFloat(ShipPoolMat.PIntensity, inten);
            m.SetFloat(ShipPoolMat.PIntensityWarm, inten);
            m.SetFloat(ShipPoolMat.PAtmos, Mathf.Max(100f, RealisticNightPlugin.StreetLightAtten.Value)); // UNIVERSAL haze
            m.SetFloat(ShipPoolMat.PAtmosCurve, Mathf.Clamp(RealisticNightPlugin.StreetLightAttenCurve.Value, 0.2f, 3f));
            float pr = Mathf.Max(1f, RealisticNightPlugin.ShipSearchRange.Value);
            m.SetFloat(ShipPoolMat.PRange, pr);
            float gf = Mathf.Clamp01(RealisticNightPlugin.ShipSearchPoolFalloff.Value);
            m.SetFloat(ShipPoolMat.PFalloff, (gf * gf * 25f) / (pr * pr)); // same range-normalized shape as street
            m.SetFloat(ShipPoolMat.PNdotL, Mathf.Clamp01(RealisticNightPlugin.ShipSearchPoolShading.Value)); // same N.L as street
            Color sc = RealisticNightPlugin.ShipSearchPoolColour.Value;
            float scrim = Mathf.Clamp01(sc.a); // alpha trims the tint strength (street convention)
            Color scol = new Color(sc.r * scrim, sc.g * scrim, sc.b * scrim, 1f);
            m.SetColor(ShipPoolMat.PColor, scol);
            m.SetColor(ShipPoolMat.PColorWarm, scol);
            m.SetFloat(ShipPoolMat.PPreserveID, 0f); // additive: must read on black water
            m.SetFloat(ShipPoolMat.PTintStrength, Mathf.Clamp01(RealisticNightPlugin.StreetGroundTint.Value)); // shared tint knob
        }

        static void Teardown()
        {
            foreach (var kv in rigs) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            rigs.Clear();
            if (searchConeSoftMat != null) UnityEngine.Object.Destroy(searchConeSoftMat); searchConeSoftMat = null;
            if (searchLensSoftMat != null) UnityEngine.Object.Destroy(searchLensSoftMat); searchLensSoftMat = null;
            if (searchConeBeamMat != null) UnityEngine.Object.Destroy(searchConeBeamMat); searchConeBeamMat = null;
        }
    }

    // Per-vehicle rig: hull-snapped lens decals + modeled emissive + cones + pools. Parented, zero-CPU tracking.
    internal sealed class HeadlightRig : MonoBehaviour
    {
        public static Color SharedConeColor = Color.white;
        public static Color SharedLensColor = Color.white;
        public static float SharedHaze = 1f; // universal haze factor for this tick (manager-owned)
        static readonly int EmiID = Shader.PropertyToID("_EmissionColor");
        struct LensFx { public Renderer rend; public Material mat; public Color orig; }

        static readonly int ColorID = Shader.PropertyToID("_Color");

        internal VehicleHeadlights.AnchorResolver.Mount[] mounts;
        internal Vector3[] lensPos;      // snapped fixture positions (rig space), parallel to mounts
        internal Quaternion[] lensRot;
        internal float[] lensScales;     // per-tick anti-shimmer scales, parallel to mounts
        internal Vector3 rearLPos, rearRPos;
        internal Quaternion rearLRot, rearRRot;
        internal float rearLScale, rearRScale;
        internal bool showFx, showRearFx, brakingNow; // fixture visibility (child GOs below)
        GameObject[] coneGOs, lensGOs; // CPU-era fixtures: plain child GOs (proven path, zero instancing)
        MeshRenderer[] coneMRs, lensMRs;
        GameObject rearLGO, rearRGO;
        MeshRenderer rearLMR, rearRMR;
        internal int shownFx; // fixtures posed visible this tick
        readonly List<LensFx> frontFx = new List<LensFx>();
        readonly List<LensFx> rearFx = new List<LensFx>();
        readonly HashSet<Material> clones = new HashSet<Material>();
        Unit unit;
        float prevSpeed;
        float brakeHoldUntil;

        public void Build(VehicleHeadlights.AnchorResolver.Mount[] frontMounts, Renderer[] rearRends, Unit owner)
        {
            mounts = frontMounts;
            unit = owner;
            try { prevSpeed = unit != null ? unit.speed : 0f; } catch { prevSpeed = 0f; }
            float length = 6f, width = 3f, height = 2.5f;
            try
            {
                var d = unit != null ? unit.definition : null;
                if (d != null) { length = Mathf.Max(2f, d.length); width = Mathf.Max(1.5f, d.width); height = Mathf.Max(1f, d.height); }
            }
            catch { }
            int n = mounts != null ? mounts.Length : 0;
            lensPos = new Vector3[n]; lensRot = new Quaternion[n]; lensScales = new float[n];
            for (int i = 0; i < n; i++) { lensScales[i] = 1f; lensRot[i] = Quaternion.identity; }
            float backoff = length * 0.5f + 5f;
            for (int i = 0; i < n; i++)
            {
                // Modeled lens renderer => bonus true emissive surface on top of the decal.
                if (mounts[i].rend != null && TryCloneEmissive(mounts[i].rend, out LensFx fx)) frontFx.Add(fx);
                if (!SnapPose(transform, mounts[i].local, Vector3.forward, backoff, out lensPos[i], out lensRot[i]))
                { lensPos[i] = mounts[i].local; lensRot[i] = Quaternion.LookRotation(Vector3.forward); }
            }
            // Rear pair snapped once (same raycast); brake state swaps dim/bright instance lists per tick.
            float ry = -Mathf.Max(0.2f, height * 0.15f);
            float hx = Mathf.Max(0.5f, width * 0.28f);
            float rz = -(length * 0.5f + 0.25f);
            if (!SnapPose(transform, new Vector3(-hx, ry, rz), Vector3.back, backoff, out rearLPos, out rearLRot))
            { rearLPos = new Vector3(-hx, ry, rz); rearLRot = Quaternion.LookRotation(Vector3.back); }
            if (!SnapPose(transform, new Vector3(hx, ry, rz), Vector3.back, backoff, out rearRPos, out rearRRot))
            { rearRPos = new Vector3(hx, ry, rz); rearRRot = Quaternion.LookRotation(Vector3.back); }
            rearLScale = rearRScale = 1f;
            if (rearRends != null)
                foreach (Renderer r in rearRends)
                    if (r != null && TryCloneEmissive(r, out LensFx fx2) && !HasClone(fx2.mat)) rearFx.Add(fx2);
            // Pose + colour immediately so first frame is correct even before the next manager tick.
            try { UpdateRig(true, true, transform.position, float.MaxValue, 0f); } catch { }
        }

        // Plain child fixtures (CPU path): shared meshes + shared manager materials, posed in rig space.
        void EnsureFixtureGOs()
        {
            if (coneGOs != null) return;
            Mesh cm = VehicleHeadlights.ConeMesh, qm = VehicleHeadlights.QuadMesh;
            int n = mounts != null ? mounts.Length : 0;
            coneGOs = new GameObject[n]; lensGOs = new GameObject[n];
            coneMRs = new MeshRenderer[n]; lensMRs = new MeshRenderer[n];
            for (int i = 0; i < n; i++)
            {
                coneGOs[i] = NewFixtureGO("hlcone", cm, out coneMRs[i]);
                lensGOs[i] = NewFixtureGO("hllens", qm, out lensMRs[i]);
            }
            rearLGO = NewFixtureGO("hlrearL", qm, out rearLMR);
            rearRGO = NewFixtureGO("hlrearR", qm, out rearRMR);
        }
        GameObject NewFixtureGO(string name, Mesh mesh, out MeshRenderer mr)
        {
            mr = null;
            try
            {
                GameObject go = new GameObject(name);
                go.transform.SetParent(transform, worldPositionStays: false);
                if (mesh != null) go.AddComponent<MeshFilter>().sharedMesh = mesh; // shared template (never per-rig)
                mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off; // never cast (same as every glow draw)
                mr.receiveShadows = false;
                go.SetActive(false);
                return go;
            }
            catch { return null; }
        }
        static void PoseFixture(GameObject go, MeshRenderer mr, Material m, bool vis, Vector3 lp, Quaternion lr, Vector3 s, ref int shown)
        {
            if (go == null) return;
            try
            {
                if (go.activeSelf != vis) go.SetActive(vis);
                if (!vis) return;
                go.transform.localPosition = lp;
                go.transform.localRotation = lr;
                go.transform.localScale = s;
                if (mr != null && m != null && mr.sharedMaterial != m) mr.sharedMaterial = m;
                shown++;
            }
            catch { }
        }

        // Clone the lens material per rig (shared materials are never written).
        bool TryCloneEmissive(Renderer rend, out LensFx fx)
        {
            fx = default(LensFx);
            try
            {
                Material mat = rend.material; // instances on access
                if (mat == null || !mat.HasProperty(EmiID)) return false;
                mat.EnableKeyword("_EMISSION");
                Color orig = mat.GetColor(EmiID);
                clones.Add(mat);
                fx = new LensFx { rend = rend, mat = mat, orig = orig };
                return true;
            }
            catch { return false; }
        }

        bool HasClone(Material m)
        {
            foreach (LensFx f in frontFx) if (f.mat == m) return true;
            foreach (LensFx f in rearFx) if (f.mat == m) return true;
            return false;
        }

        // One-time hull snap shared by ground + ship rigs: raycast from outside along -faceDir,
        // keep the closest SELF hit, else the raw mount point. Never creates anything.
        internal static bool SnapPose(Transform host, Vector3 lp, Vector3 faceDir, float backoff, out Vector3 pos, out Quaternion rot)
        {
            pos = lp;
            rot = Quaternion.LookRotation(faceDir);
            try
            {
                Vector3 mountW = host.TransformPoint(lp);
                Vector3 dirW = host.TransformDirection(faceDir);
                Vector3 origin = mountW + dirW * backoff;
                RaycastHit[] hits = Physics.RaycastAll(origin, -dirW, backoff + 3f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                bool got = false;
                Vector3 bp = mountW, bn = dirW;
                foreach (RaycastHit h in hits)
                {
                    if (h.collider == null || h.distance > best) continue;
                    try { if (h.collider.transform != null && h.collider.transform.IsChildOf(host)) { best = h.distance; bp = h.point; bn = h.normal; got = true; } }
                    catch { }
                }
                if (!got) return false;
                Vector3 nLocal = host.InverseTransformDirection(bn).normalized;
                pos = host.InverseTransformPoint(bp) + nLocal * 0.04f;
                rot = Quaternion.LookRotation(nLocal);
                return true;
            }
            catch { return false; }
        }

        // Pool feed: raw-global positions + spot beam dirs. Returns via ref idx.
        public void AppendPoolLamps(Vector4[] posBuf, Vector4[] dirBuf, ref int idx, int cap, Vector3 camPos, float maxD2, bool wantDirs)
        {
            if (mounts == null || idx >= cap) return;
            try { if (unit == null || unit.disabled) return; } catch { return; } // wrecks feed no pools
            float tilt = Mathf.Clamp(RealisticNightPlugin.HeadlightDownTilt.Value, 0f, 45f);
            Vector3 dirW = (transform.rotation * Quaternion.Euler(tilt, 0f, 0f)) * Vector3.forward;
            float cosHalf = Mathf.Cos(Mathf.Clamp(RealisticNightPlugin.HeadlightConeAngle.Value, 3f, 60f) * 0.5f * Mathf.Deg2Rad);
            // Hardening (not the flicker fix): one NaN dir + fullscreen quad writes NaN into the
            // shared volRT and blanks ALL headlight pools for that frame (street uses its own
            // clear/composite, so it stays up). Same guard ships already had.
            if (VehicleHeadlights.BadVec(dirW) || float.IsNaN(cosHalf) || float.IsInfinity(cosHalf)) return;
            bool hasOrigin = Datum.origin != null;
            foreach (var m in mounts)
            {
                if (idx >= cap) break;
                Vector3 w;
                try { w = transform.TransformPoint(m.local); } catch { continue; }
                if (VehicleHeadlights.BadVec(w)) continue;
                if ((w - camPos).sqrMagnitude > maxD2) continue;
                Vector3 raw = hasOrigin ? Datum.origin.InverseTransformPoint(w) : w;
                if (VehicleHeadlights.BadVec(raw)) continue; // origin mid-shift: skip one mount, hold covers the frame
                posBuf[idx] = new Vector4(raw.x, raw.y, raw.z, 0f); // w=0 white path
                if (wantDirs && dirBuf != null) dirBuf[idx] = new Vector4(dirW.x, dirW.y, dirW.z, cosHalf);
                idx++;
            }
        }


        // Per-tick rig state (no GameObjects): brake/LOD flags + decal scales for the manager draw pass.
        public void UpdateRig(bool fxOn, bool lightsActive, Vector3 camPos, float maxD2, float pxScale)
        {
            bool alive = unit != null && !unit.disabled;
            bool on = lightsActive && alive;
            float lensSize = Mathf.Max(0.1f, RealisticNightPlugin.HeadlightLensSize.Value);
            float minPx = Mathf.Max(0f, RealisticNightPlugin.HeadlightLensMinPx.Value);
            for (int i = 0; i < lensScales.Length; i++)
            {
                Vector3 wpos;
                try { wpos = transform.TransformPoint(lensPos[i]); } catch { wpos = camPos; }
                lensScales[i] = DecalScale(lensSize, minPx, wpos, camPos, pxScale);
            }
            try
            {
                rearLScale = DecalScale(lensSize, minPx, transform.TransformPoint(rearLPos), camPos, pxScale);
                rearRScale = DecalScale(lensSize, minPx, transform.TransformPoint(rearRPos), camPos, pxScale);
            }
            catch { rearLScale = rearRScale = lensSize; }
            // Braking via decel heuristic on Unit.speed (vanilla exposes no brake signal), 1.5 s hold.
            bool rearOn = on && RealisticNightPlugin.RearLightsEnabled.Value;
            bool braking = false;
            try
            {
                float sp = unit != null ? unit.speed : 0f;
                float dt = Time.deltaTime;
                float decel = dt > 0.0001f ? (prevSpeed - sp) / dt : 0f;
                if (rearOn && decel > 1.5f && sp > 0.5f) brakeHoldUntil = Time.realtimeSinceStartup + 1.5f;
                braking = rearOn && Time.realtimeSinceStartup < brakeHoldUntil;
                prevSpeed = sp;
            }
            catch { }
            // Distance LOD (manager passes camPos — no per-rig Camera.main lookup).
            bool near = true;
            try { near = (transform.position - camPos).sqrMagnitude <= maxD2; }
            catch { near = true; }
            showFx = fxOn && near && alive; // dead/abandoned hulls go dark (cones too, not just emission)
            showRearFx = rearOn && near;
            brakingNow = braking;
            // CPU fixtures: pose child GOs in rig space (rides the hull free; same TRS the bake used).
            if (coneGOs == null && VehicleHeadlights.ConeMesh != null && VehicleHeadlights.QuadMesh != null) EnsureFixtureGOs(); // lazy (shared meshes first)
            float range = Mathf.Max(5f, RealisticNightPlugin.HeadlightRange.Value);
            float halfDeg = Mathf.Clamp(RealisticNightPlugin.HeadlightConeAngle.Value, 3f, 40f);
            float tilt = Mathf.Clamp(RealisticNightPlugin.HeadlightDownTilt.Value, 0f, 45f);
            float baseR = Mathf.Tan(halfDeg * Mathf.Deg2Rad) * range;
            Vector3 coneScale = new Vector3(baseR / 1.75f, baseR / 1.75f, range / 10f); // template baked: 10 m, r=1.75
            Quaternion tiltQ = Quaternion.Euler(tilt, 0f, 0f); // pitch beam down in local space
            Material cm = VehicleHeadlights.ConeSoftMat, lm = VehicleHeadlights.LensSoftMat;
            shownFx = 0;
            if (mounts != null && coneGOs != null)
            {
                for (int i = 0; i < mounts.Length && i < coneGOs.Length; i++)
                {
                    bool vis = showFx && cm != null && lm != null;
                    PoseFixture(coneGOs[i], coneMRs[i], cm, vis, mounts[i].local, tiltQ, coneScale, ref shownFx);
                    Vector3 lp = i < lensPos.Length ? lensPos[i] : mounts[i].local;
                    Quaternion lr = i < lensRot.Length ? lensRot[i] : Quaternion.identity;
                    float ls = i < lensScales.Length ? lensScales[i] : 1f;
                    PoseFixture(lensGOs[i], lensMRs[i], lm, vis, lp, lr, Vector3.one * ls, ref shownFx);
                }
            }
            Material rm = brakingNow ? VehicleHeadlights.RearBrightSoftMat : VehicleHeadlights.RearDimSoftMat;
            bool rearVis = showRearFx && rm != null;
            PoseFixture(rearLGO, rearLMR, rm, rearVis, rearLPos, rearLRot, Vector3.one * rearLScale, ref shownFx);
            PoseFixture(rearRGO, rearRMR, rm, rearVis, rearRPos, rearRRot, Vector3.one * rearRScale, ref shownFx);
            // Front modeled-lens bonus: HDR white at night, original material by day/off.
            float lensBoost = Mathf.Max(0f, RealisticNightPlugin.HeadlightLensBoost.Value);
            float runBoost = Mathf.Max(0f, RealisticNightPlugin.RearRunningBoost.Value);
            float brakeBoost = Mathf.Max(0f, RealisticNightPlugin.RearBrakeBoost.Value);
            Color lensOn = HeadColStatic(lensBoost) * SharedHaze;
            foreach (LensFx f in frontFx)
            {
                if (f.mat == null) continue;
                try { f.mat.SetColor(EmiID, on ? lensOn : f.orig); } catch { }
            }
            // Rear modeled-lens bonus follows the same dim/bright state.
            Color rearCol = braking ? new Color(1f, 0.07f, 0.05f) * brakeBoost * SharedHaze
                                    : new Color(1f, 0.07f, 0.05f) * runBoost * SharedHaze;
            foreach (LensFx f in rearFx)
            {
                if (f.mat == null) continue;
                try { f.mat.SetColor(EmiID, rearOn ? rearCol : f.orig); } catch { }
            }
        }

        static Color HeadColStatic(float i)
        {
            float w = Mathf.Clamp01(RealisticNightPlugin.HeadlightWarmth.Value);
            return new Color(1.0f * i, Mathf.Lerp(0.97f, 0.80f, w) * i, Mathf.Lerp(1.0f, 0.60f, w) * i, 1f);
        }

        // Anti-shimmer scale for one decal (shared helper; manager composes the matrix).
        internal static float DecalScale(float baseSize, float minPx, Vector3 worldPos, Vector3 camPos, float pxScale)
        {
            float s = baseSize;
            if (minPx > 0.01f && pxScale > 1f)
            {
                float dist = (worldPos - camPos).magnitude;
                if (dist > 1f)
                {
                    float pxR = (baseSize * 0.5f / dist) * pxScale;
                    s = baseSize * Mathf.Clamp(minPx / Mathf.Max(pxR, 1e-3f), 1f, 8f);
                }
            }
            return s;
        }

        // Teardown destroys this component, not the unit: kill our child GOs here or their renderers
        // go purple on the (also destroyed) shared materials and pile up every toggle cycle.
        void DestroyFixtures()
        {
            try
            {
                if (coneGOs != null) foreach (GameObject go in coneGOs) if (go != null) UnityEngine.Object.Destroy(go);
                if (lensGOs != null) foreach (GameObject go in lensGOs) if (go != null) UnityEngine.Object.Destroy(go);
                if (rearLGO != null) UnityEngine.Object.Destroy(rearLGO);
                if (rearRGO != null) UnityEngine.Object.Destroy(rearRGO);
            }
            catch { }
            coneGOs = lensGOs = null; coneMRs = lensMRs = null; rearLGO = rearRGO = null; rearLMR = rearRMR = null;
        }

        void OnDestroy()
        {
            DestroyFixtures();
            foreach (Material m in clones) if (m != null) UnityEngine.Object.Destroy(m);
            clones.Clear();
            frontFx.Clear(); rearFx.Clear();
        }
    }

    // Headlight ground pools: own buffer + material, independent intensity/range, always spot-form.
    internal static class HeadlightPools
    {
        public static volatile bool Active;
        public const int MaxLamps = 2048;
        static readonly Vector4[] lamps = new Vector4[MaxLamps];
        static readonly Vector4[] dirs = new Vector4[MaxLamps]; // spot mode: beam dir (xyz) + cos(half-angle) in w
        static ComputeBuffer buf;
        static ComputeBuffer dirBuf;
        static Material mat;      // omni instance (clone of the street ground material)
        static Material spotMat;  // spot instance (clone of HeadlightSpotMat, v2 bundle only)
        static int count;
        static bool dirty;
        static bool dirsDirty;
        static bool warned;
        static bool spotWarned;
        static float nextSpotTry;
        static bool usingSpot;
        static int spotVol = -1, spotComp = -1;

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
        static readonly int PSpotDirs = Shader.PropertyToID("_SpotDirs");
        static readonly int PPreserveID = Shader.PropertyToID("_Preserve");

        public static Material Mat => usingSpot ? spotMat : mat; // active pool material (render-pass gate)
        public static int LampCount => count;
        public static bool UsingSpot => usingSpot;
        // Headlights are always spot-form (omni fallback on old bundles without the spot material).
        public static int VolumePassIndex => usingSpot ? spotVol : 0;
        public static int CompositePassIndex => usingSpot ? spotComp : 1;

        public static void SetLamps(Vector4[] src, int n)
        {
            n = Mathf.Clamp(n, 0, MaxLamps);
            for (int i = 0; i < n; i++) lamps[i] = src[i];
            count = n;
            dirty = true;
        }

        public static void SetDirs(Vector4[] src, int n)
        {
            n = Mathf.Clamp(n, 0, MaxLamps);
            for (int i = 0; i < n; i++) dirs[i] = src[i];
            dirsDirty = true;
        }

        // Resolve the spot material (throttled 5 s).
        static bool ResolveSpot()
        {
            if (spotMat != null) return true;
            if (Time.realtimeSinceStartup < nextSpotTry) return false;
            nextSpotTry = Time.realtimeSinceStartup + 5f;
            try
            {
                Material src = StreetLightFX.SpotMat;
                if (src == null) return false; // v1 bundle: spot not shipped yet
                if (src.shader == null || !src.shader.isSupported) return false;
                int vp = src.FindPass("RN_Headlight_Spot");
                int cp = src.FindPass("RN_Headlight_SpotComposite");
                if (vp < 0 || cp < 0) return false;
                spotVol = vp;
                spotComp = cp;
                spotMat = UnityEngine.Object.Instantiate(src);
                spotMat.name = "RN_HeadlightSpot";
                return true;
            }
            catch (Exception e)
            {
                RealisticNightPlugin.Log.LogWarning($"[Headlights] spot material resolve failed: {e.Message}");
                return false;
            }
        }

        public static void Apply(bool on)
        {
            if (!on) { Active = false; usingSpot = false; return; }
            // No hold here: the manager already frame-holds hitch empties (poolEmptyStreak).
            // A zero count arriving here means truly empty for N frames -> deactivate at once.
            if (count == 0) { Active = false; usingSpot = false; return; }
            // Headlights are always spot-form; old bundles without the spot material fall back to omni.
            Material useMat = null;
            if (ResolveSpot()) useMat = spotMat;
            if (useMat == null)
            {
                if (!spotWarned)
                {
                    spotWarned = true;
                    RealisticNightPlugin.Log.LogWarning("[Headlights] bundle lacks the spot material - Omni fallback. Rebuild the bundle (see shader/README.md).");
                }
                if (StreetLightFX.Mat == null) { Active = false; usingSpot = false; return; } // bundle missing -> cones/decals only
                if (mat == null)
                {
                    try { mat = UnityEngine.Object.Instantiate(StreetLightFX.Mat); mat.name = "RN_HeadlightGroundMat"; }
                    catch (Exception e)
                    {
                        if (!warned) { warned = true; RealisticNightPlugin.Log.LogWarning($"[Headlights] pool material clone failed: {e.Message}"); }
                        Active = false;
                        usingSpot = false;
                        return;
                    }
                }
                useMat = mat;
            }
            usingSpot = (useMat == spotMat);
            float w = Mathf.Clamp01(RealisticNightPlugin.HeadlightWarmth.Value);
            float inten = Mathf.Max(0f, RealisticNightPlugin.HeadlightPoolIntensity.Value);
            useMat.SetFloat(PIntensity, inten);
            useMat.SetFloat(PIntensityWarm, inten);
            useMat.SetFloat(PAtmos, Mathf.Max(100f, RealisticNightPlugin.StreetLightAtten.Value)); // UNIVERSAL haze (same slider as street)
            useMat.SetFloat(PAtmosCurve, Mathf.Clamp(RealisticNightPlugin.StreetLightAttenCurve.Value, 0.2f, 3f)); // shared curve (ranges stay separate)
            float pr = Mathf.Max(1f, RealisticNightPlugin.HeadlightPoolRange.Value);
            useMat.SetFloat(PRange, pr);
            float hf = Mathf.Clamp01(RealisticNightPlugin.HeadlightPoolFalloff.Value);
            float hRange = Mathf.Max(1f, RealisticNightPlugin.HeadlightPoolRange.Value);
            useMat.SetFloat(PFalloff, (hf * hf * 25f) / (hRange * hRange)); // range-normalized (same look at any range)
            useMat.SetFloat(PNdotL, Mathf.Clamp01(RealisticNightPlugin.HeadlightPoolShading.Value));
            useMat.SetFloat(PTintStrength, Mathf.Clamp01(RealisticNightPlugin.StreetGroundTint.Value));
            // Headlight pools LIGHTEN the road (surface colour x reflectivity); high intensity can't white-veil.
            useMat.SetFloat(PPreserveID, 1f);
            useMat.SetColor(PColor, new Color(1f, Mathf.Lerp(0.97f, 0.80f, w), Mathf.Lerp(1f, 0.60f, w), 1f));
            useMat.SetColor(PColorWarm, new Color(1f, Mathf.Lerp(0.97f, 0.80f, w), Mathf.Lerp(1f, 0.60f, w), 1f));
            if (buf == null || !buf.IsValid()) { buf = new ComputeBuffer(MaxLamps, 16, ComputeBufferType.Structured); dirty = true; }
            if (dirty) { buf.SetData(lamps, 0, 0, count); dirty = false; }
            useMat.SetBuffer(PLampsBuf, buf);
            useMat.SetInt(PLampCount, count);
            if (usingSpot)
            {
                if (dirBuf == null || !dirBuf.IsValid()) { dirBuf = new ComputeBuffer(MaxLamps, 16, ComputeBufferType.Structured); dirsDirty = true; }
                if (dirsDirty) { dirBuf.SetData(dirs, 0, 0, count); dirsDirty = false; }
                useMat.SetBuffer(PSpotDirs, dirBuf);
            }
            Active = true;
        }
    }

}
