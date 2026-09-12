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
    // Road street lamps, poles, blast destruction. Orbs billboard + fade live; positions bake once.
    internal static class StreetLights
    {
        static GameObject container;
        static Material mat;           // orb material for WHITE-LED lamps (city/main)
        static Material orbYellowMat;  // orb material for SODIUM lamps (rural)
        static Material poleMat;
        internal static Mesh poleMeshRef; // combined cantilever mesh, rebuilt per Build (bakes height/arm/width)
        static Texture2D tex;
        static readonly List<Transform> markers = new List<Transform>(); // fallback ball GOs (no orb shader only)
        static readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        static readonly List<float> lampWarm = new List<float>();                     // parallel to markers: 0 = white LED, 1 = sodium (rural)
        // Merged-mesh records (positions baked at Build; meshes rebuilt at 5 Hz LOD cadence).
        static readonly List<Vector3> ballLocal = new List<Vector3>();      // orb centers, container space
        static readonly List<bool> ballAlive = new List<bool>();            // false after topple
        static readonly List<Vector3> poleLocalPos = new List<Vector3>();   // pole bases, container space
        static readonly List<Quaternion> poleLocalRot = new List<Quaternion>();
        static readonly List<bool> poleAlive = new List<bool>();            // false after topple (debris is a temp GO)
        static GameObject whiteGO, yellowGO, poleGO; // one regular GO per type (plain draws, zero instancing)
        static Mesh whiteMesh, yellowMesh, poleMergedMesh;
        static readonly List<Vector3> mPos = new List<Vector3>(); // orb scratch (POSITION = baked center)
        static readonly List<Vector2> mUv = new List<Vector2>();  // glow uvs
        static readonly List<Vector2> mC = new List<Vector2>();   // TEXCOORD1 = billboard corner
        static readonly List<int> mTri = new List<int>();
        static readonly List<Vector3> pPos = new List<Vector3>(); // pole scratch (container space)
        static readonly List<Vector3> pNrm = new List<Vector3>();
        static readonly List<int> pTri = new List<int>();
        static Vector3[] poleVertSrc; static Vector3[] poleNrmSrc; static int[] poleTriSrc; // template snapshot
        static Mesh poleSrcCached;
        static bool orbActive; // true = GPU-billboard orb shader (no per-frame ball updates)
        static float nextLod;
        static readonly int POrbSize = Shader.PropertyToID("_Size");
        static readonly int POrbColor = Shader.PropertyToID("_Color");
        static readonly int POrbAtmos = Shader.PropertyToID("_AtmosRange");
        static readonly int POrbCurve = Shader.PropertyToID("_AtmosCurve");
        static readonly int POrbMax = Shader.PropertyToID("_MaxDist");
        static MaterialPropertyBlock mpb;
        // Road-classification spatial grid: cell -> road indices touching it.
        static readonly Dictionary<long, HashSet<int>> roadGrid = new Dictionary<long, HashSet<int>>();
        static readonly HashSet<int> nearbyScratch = new HashSet<int>();
        static float roadCell = 1f;
        static bool built;
        // Values the current markers/texture were built with (change => rebuild or regen).
        static float bSpacing, bHeight, bSide, bCore, bFalloff, bPoleW;
        static bool bPoles;
        static bool bSkipWater;
        static float bBuildSearch; // building-lamp search radius (0 = second pass off)
        static Terrain waterRefTerrain; // true-ground reference (heightfield has no decks/water planes)
        static Vector3 waterTPos, waterTSize;
        static bool waterTHas;
        // Road-classification params baked at Build (change => respawn).
        static float bMainMult, bRuralMult, bClassifyRadius;
        static int bCityMin, bMainMin;
        static float nextBuildTry;
        static float nextUnitRescan; // re-scan delay for late-spawning mission units (proximity dedups, never duplicates)

        // Poles stay static meshes until a blast reaches them, then topple with real physics (terrain-only collision).
        const int PoleLayer = 30;                 // unused by the game (its PhysicsLayers enum stops at 18)
        static bool layerMatrixSet;
        static readonly bool[] savedIgnore = new bool[32];

        // Cool white LED (city) vs warm sodium (rural), scaled for HDR bloom. Alpha trims strength.
        static Color LedCol(float i) { Color c = RealisticNightPlugin.StreetWhite.Value; float a = Mathf.Clamp01(c.a); return new Color(c.r * i * a, c.g * i * a, c.b * i * a, 1f); }
        static Color SodiumCol(float i) { Color c = RealisticNightPlugin.StreetYellow.Value; float a = Mathf.Clamp01(c.a); return new Color(c.r * i * a, c.g * i * a, c.b * i * a, 1f); }

        // Universal haze curve shared by every light (same math as the shaders).
        public static float HazeFade(float dist)
        {
            if (float.IsPositiveInfinity(dist) || dist <= 0f) return 1f;
            float R = Mathf.Max(100f, RealisticNightPlugin.StreetLightAtten.Value);
            float p = Mathf.Clamp(RealisticNightPlugin.StreetLightAttenCurve.Value, 0.2f, 3f);
            return Mathf.Exp(-Mathf.Pow(dist / R, p));
        }

        // Distance to nearest CITY lamp (Infinity unknown). Drives the city-window haze fade.
        public static float NearestCityDist = float.PositiveInfinity;

        // on = system exists day AND night (bare poles by day); lightsActive = balls + pools on.
        public static void Apply(bool on, bool lightsActive)
        {
            if (!on) { StreetLightFX.Apply(false); StreetLightFX.ReleaseBuffer(); if (built) Teardown("disabled", instant: true); return; }

            // Container destroyed externally (scene reload) but we think we're built -> reset and respawn.
            if (built && container == null)
            {
                built = false; nextBuildTry = 0f;
            markers.Clear(); renderers.Clear(); lampWarm.Clear();
            ballLocal.Clear(); ballAlive.Clear(); poleLocalPos.Clear(); poleLocalRot.Clear(); poleAlive.Clear();
            whiteGO = yellowGO = poleGO = null; // GOs died with the container
            if (whiteMesh != null) { UnityEngine.Object.Destroy(whiteMesh); whiteMesh = null; }
            if (yellowMesh != null) { UnityEngine.Object.Destroy(yellowMesh); yellowMesh = null; }
            if (poleMergedMesh != null) { UnityEngine.Object.Destroy(poleMergedMesh); poleMergedMesh = null; }
            poleSrcCached = null; poleVertSrc = poleNrmSrc = null; poleTriSrc = null;
            NearestCityDist = float.PositiveInfinity;
        }

            // Baked params changed => respawn.
            if (built &&
                (bSpacing != RealisticNightPlugin.StreetLightSpacing.Value ||
                 bHeight != RealisticNightPlugin.StreetLightHeight.Value ||
                  bSide != RealisticNightPlugin.StreetLightSideOffset.Value ||
                  bPoles != RealisticNightPlugin.StreetLightPoles.Value ||
                  bSkipWater != RealisticNightPlugin.StreetLightSkipWater.Value ||
                  bBuildSearch != RealisticNightPlugin.StreetBuildingLampSearch.Value ||
                 bPoleW != RealisticNightPlugin.StreetLightPoleWidth.Value ||
                 bMainMult != RealisticNightPlugin.StreetSpacingMainMult.Value ||
                 bRuralMult != RealisticNightPlugin.StreetSpacingRuralMult.Value ||
                 bClassifyRadius != RealisticNightPlugin.StreetClassifyRadius.Value ||
                 bCityMin != RealisticNightPlugin.StreetCityMinRoads.Value ||
                 bMainMin != RealisticNightPlugin.StreetMainMinRoads.Value))
                Teardown("params changed", instant: false);

            if (!built)
            {
                if (Time.realtimeSinceStartup < nextBuildTry) return; // throttle (re)builds ~1/s
                nextBuildTry = Time.realtimeSinceStartup + 1f;
                Build();
                if (!built) GateStatus(); // still stalled: throttled one-line reason (see log)
                return;
            }

            // Texture shape (core/falloff) is a live regen — no respawn needed.
            if (mat != null && (bCore != RealisticNightPlugin.StreetLightCore.Value || bFalloff != RealisticNightPlugin.StreetLightFalloff.Value))
                RegenTexture();

            UpdateFrame(lightsActive);
            // Late mission spawns (airfield units arrive after the static build): pick them up.
            if (bBuildSearch >= 1f && Time.realtimeSinceStartup >= nextUnitRescan)
            {
                nextUnitRescan = Time.realtimeSinceStartup + 15f;
                try { RescanUnitLamps(); } catch { }
            }
        }

        // Stalled-build diagnostic: re-reads every build gate (cheap, 1/30 s) so one log run
        // pinpoints which mission-start dependency never arrives (levelInfo / roadNetwork /
        // roads / terrain / floating origin). Silent once built.
        static float nextGateLog;
        static void GateStatus()
        {
            if (Time.realtimeSinceStartup < nextGateLog) return;
            nextGateLog = Time.realtimeSinceStartup + 30f;
            try
            {
                LevelInfo li = null;
                try { li = NetworkSceneSingleton<LevelInfo>.i; } catch { }
                RoadNetwork net = null;
                try { net = (li != null) ? li.roadNetwork : null; } catch { }
                bool exists = false;
                try { exists = net != null && net.Exists(); } catch { }
                int roads = -1;
                try { roads = (net != null && net.roads != null) ? net.roads.Count : -1; } catch { }
                bool terrain = false;
                try { terrain = Terrain.activeTerrains != null && Terrain.activeTerrains.Length > 0; } catch { }
                bool origin = false;
                try { origin = Datum.origin != null; } catch { }
                if (RealisticNightPlugin.DiagOn())
                    RealisticNightPlugin.Log.LogInfo($"[StreetLights] waiting to build: levelInfo={li != null} net={net != null} exists={exists} roads={roads} terrain={terrain} origin={origin} (retrying 1/s)");
            }
            catch { }
        }

        // Orb renders at this fraction of Glow Radius, reading as a lamp lens, not a floating ball.
        const float OrbSizeScale = 0.5f;        const float WaterClearance = 0.8f; // seated point this far above real terrain = water/deck, not ground
        static bool IsWaterName(string n) // water planes by name (catches shallow hits the clearance misses)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("ocean", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("lake", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("river", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("lagoon", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("pond", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("harbor", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("harbour", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        static AccessTools.FieldRef<MapBuilding, float> bldRadiusRef; // game-authored footprint (m)
        static bool bldRadiusTried;
        // Building footprint: the game's own radius, else renderer-bounds measure, else 0 (skip).
        // Renderer (base class) covers Mesh/Skinned/etc; airfield hangars don't always use MeshRenderer.
        static float BuildingRadius(MapBuilding mb)
        {
            try
            {
                if (mb == null) return 0f;
                if (!bldRadiusTried) { bldRadiusTried = true; try { bldRadiusRef = AccessTools.FieldRefAccess<MapBuilding, float>("radius"); } catch { bldRadiusRef = null; } }
                if (bldRadiusRef != null)
                {
                    float r = 0f;
                    try { r = bldRadiusRef(mb); } catch { r = 0f; }
                    if (r >= 2f && r <= 200f) return r;
                }
            }
            catch { }
            try
            {
                Transform t = mb.transform;
                if (t == null) return 0f;
                Bounds b = new Bounds();
                bool has = false;
                foreach (Renderer mr in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (mr == null) continue;
                    Bounds mbb;
                    try { mbb = mr.bounds; } catch { continue; }
                    if (mbb.size.sqrMagnitude < 1e-6f) continue;
                    if (!has) { b = mbb; has = true; } else b.Encapsulate(mbb);
                }
                if (!has) return 0f;
                float d = b.size.magnitude * 0.5f;
                return (d >= 2f && d <= 200f) ? d : 0f;
            }
            catch { return 0f; }
        }

        // Unit footprint: definition size first, renderer-bounds fallback, else 0.
        static float UnitRadius(Unit u)
        {
            try
            {
                if (u == null || u.definition == null) return 0f;
                float fr = Mathf.Max(u.definition.length, u.definition.width) * 0.5f;
                if (fr >= 4f && fr <= 200f) return fr;
            }
            catch { }
            try
            {
                Transform t = u.transform;
                if (t == null) return 0f;
                Bounds b = new Bounds();
                bool has = false;
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    Bounds rb;
                    try { rb = r.bounds; } catch { continue; }
                    if (rb.size.sqrMagnitude < 1e-6f) continue;
                    if (!has) { b = rb; has = true; } else b.Encapsulate(rb);
                }
                if (!has) return 0f;
                float d = b.size.magnitude * 0.5f;
                return (d >= 4f && d <= 200f) ? d : 0f;
            }
            catch { return 0f; }
        }

        // Only mobile Unit parents disqualify a MapBuilding (static parents still get lamps).
        static bool HasMobileUnitParent(Transform t)
        {
            try
            {
                Unit pu = t.GetComponentInParent<Unit>(true);
                if (pu == null) return false;
                return pu is GroundVehicle || pu is Ship || pu is Aircraft || pu is Missile;
            }
            catch { return false; }
        }

        // Fortifications stay dark: AA emplacements, pillboxes, bunkers, hull-downs, blast walls, camo nets, pads, windmills (mission keys + map names).
        static readonly string[] NoLampKeys = { "emplacement", "pillbox", "gabion", "hulldown", "hesco", "camonet", "concretewall", "concretepad", "bunker", "ammunition", "ammodump", "sandbag", "trench", "windmill", "turbine" };
        static bool IsNoLampKey(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            foreach (string k in NoLampKeys) if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        // Stationary fortification units (DEF defenses via type; others via mission jsonKey/scene name).
        static bool IsNoLampUnit(Unit u)
        {
            try
            {
                if (u is Building)
                {
                    BuildingDefinition bd = null;
                    try { bd = u.definition as BuildingDefinition; } catch { bd = null; }
                    if (bd != null) { try { if (bd.buildingType == BuildingType.DEF) return true; } catch { } }
                }
                string key = null;
                try { if (u.definition != null) key = u.definition.jsonKey; } catch { }
                if (IsNoLampKey(key)) return true;
                try { return IsNoLampKey(u.transform.name); } catch { return false; }
            }
            catch { return false; }
        }
        // Thin linear assets (sandbag/hesco lines, walls) read huge via bounds magnitude but are not buildings.
        static bool IsThinFootprint(Transform t)
        {
            try
            {
                if (t == null) return false;
                Bounds b = new Bounds();
                bool has = false;
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    Bounds rb;
                    try { rb = r.bounds; } catch { continue; }
                    if (rb.size.sqrMagnitude < 1e-6f) continue;
                    if (!has) { b = rb; has = true; } else b.Encapsulate(rb);
                }
                if (!has) return false;
                return Mathf.Min(b.size.x, b.size.z) < 2.5f;
            }
            catch { return false; }
        }

        // Shared over-water test (road placer + building emitter use the same rules).
        static bool IsOverWater(Vector3 probeW, float groundY, bool gotGround, Collider groundCol)
        {
            if (!bSkipWater) return false;
            try
            {
                if (waterTHas && probeW.x >= waterTPos.x && probeW.x <= waterTPos.x + waterTSize.x
                    && probeW.z >= waterTPos.z && probeW.z <= waterTPos.z + waterTSize.z)
                {
                    float ty = waterRefTerrain.SampleHeight(probeW);
                    float terrainY = container.transform.InverseTransformPoint(new Vector3(probeW.x, ty, probeW.z)).y;
                    return groundY - terrainY > WaterClearance;
                }
                if (Physics.Raycast(probeW + Vector3.up * 8f, Vector3.down, out RaycastHit th, 90f, 1 << (int)PhysicsLayers.Statics, QueryTriggerInteraction.Ignore))
                {
                    float terrainY = container.transform.InverseTransformPoint(th.point).y;
                    bool different = !gotGround || groundCol == null || groundCol != th.collider;
                    if (different && groundY - terrainY > WaterClearance) return true;
                    if (different && groundCol != null && IsWaterName(groundCol.name)) return true;
                }
            }
            catch { }
            return false;
        }

        enum BuildingLampResult { Placed, Near, Water, NoContainer }
        // Shared building emitter (Build + rescan); LOD/ground feed pick new lamps up live.
        static BuildingLampResult TryPlaceBuildingLamp(Vector3 centerLp, Vector3 centerW, float radius)
        {
            if (container == null || bBuildSearch < 1f || ballLocal.Count == 0) return BuildingLampResult.Near;
            float r2 = bBuildSearch * bBuildSearch;
            float nearest2 = float.PositiveInfinity;
            Vector3 nearest = Vector3.zero;
            for (int k = 0; k < ballLocal.Count; k++)
            {
                float d2 = (ballLocal[k] - centerLp).sqrMagnitude;
                if (d2 < nearest2) { nearest2 = d2; nearest = ballLocal[k]; }
                if (nearest2 <= r2) break;
            }
            if (nearest2 <= (bBuildSearch + radius) * (bBuildSearch + radius)) return BuildingLampResult.Near;
            Vector3 flat = centerLp - nearest; flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.right;
            Vector3 dir = flat.normalized;
            Vector3 edgeLp = centerLp + dir * (radius + 3f);
            float h = bHeight;
            float armReach = Mathf.Clamp(bSide * 0.4f, 1.5f, 5f);
            float ballDrop = (poleMeshRef != null && h > 0.5f) ? (h * 0.05f + bPoleW * 1.1f + 0.1f) : 0f;
            float groundY = centerLp.y;
            bool gotG = false;
            Collider gc = null;
            Vector3 probeW;
            try { probeW = container.transform.TransformPoint(new Vector3(edgeLp.x, centerLp.y, edgeLp.z)); }
            catch { return BuildingLampResult.Near; }
            if (Physics.Raycast(probeW + Vector3.up * 8f, Vector3.down, out RaycastHit bh, 90f, ~0, QueryTriggerInteraction.Ignore))
            { try { groundY = container.transform.InverseTransformPoint(bh.point).y; } catch { } gotG = true; gc = bh.collider; }
            if (IsOverWater(probeW, groundY, gotG, gc)) return BuildingLampResult.Water;
            float warm = 0f;
            try { warm = NearbyRoadCount(centerW, -1) >= bCityMin ? 0f : 1f; } catch { }
            Vector3 shaftLp = new Vector3(edgeLp.x, groundY, edgeLp.z);
            Vector3 ballLp = shaftLp + dir * armReach;
            ballLp.y = groundY + (h - ballDrop);
            ballLocal.Add(ballLp); ballAlive.Add(true);
            lampWarm.Add(warm);
            bool hasPole = poleMeshRef != null && poleMat != null;
            if (hasPole)
            {
                poleLocalPos.Add(shaftLp);
                poleLocalRot.Add(Quaternion.LookRotation(dir, Vector3.up));
            }
            else { poleLocalPos.Add(Vector3.zero); poleLocalRot.Add(Quaternion.identity); }
            poleAlive.Add(hasPole);
            if (!orbActive)
            {
                try
                {
                    Mesh qm = GlowFX.Billboard();
                    Material ballMat = (warm > 0.5f && orbYellowMat != null) ? orbYellowMat : mat;
                    GameObject go = new GameObject("sl");
                    go.transform.SetParent(container.transform, worldPositionStays: false);
                    go.transform.localPosition = ballLp;
                    go.AddComponent<MeshFilter>().sharedMesh = qm;
                    MeshRenderer mrr = go.AddComponent<MeshRenderer>();
                    mrr.sharedMaterial = ballMat;
                    mrr.shadowCastingMode = ShadowCastingMode.Off;
                    mrr.receiveShadows = false;
                    markers.Add(go.transform);
                    renderers.Add(mrr);
                }
                catch { }
            }
            return BuildingLampResult.Placed;
        }

        // Periodic catch-up for mission units that spawn after Build (proximity dedups).
        static void RescanUnitLamps()
        {
            if (container == null || bBuildSearch < 1f || ballLocal.Count == 0) return;
            const int cap = 100000;
            if (ballLocal.Count >= cap) return;
            List<Unit> units = null;
            try { units = UnitRegistry.allUnits; } catch { return; }
            if (units == null) return;
            Unit[] snap = null;
            try { snap = units.ToArray(); } catch { return; }
            if (snap == null) return;
            foreach (Unit u in snap)
            {
                if (ballLocal.Count >= cap) break;
                if (u == null) continue;
                try { if (u.disabled) continue; } catch { continue; }
                if (!(u is Building) && !(u is Scenery)) continue;
                if (IsNoLampUnit(u)) continue;
                Vector3 wpos;
                try { wpos = u.transform.position; } catch { continue; }
                float fr = UnitRadius(u);
                if (fr < 4f || fr > 200f) continue;
                Vector3 centerLp;
                try { centerLp = container.transform.InverseTransformPoint(wpos); } catch { continue; }
                TryPlaceBuildingLamp(centerLp, wpos, fr);
            }
        }

        static void UpdateFrame(bool lightsActive)
        {
            Camera cam = RealisticNightPlugin.MainCamera(out Vector3 camPos);
            if (cam == null) return;
            float size = RealisticNightPlugin.StreetLightSize.Value * OrbSizeScale;
            float range = Mathf.Max(1f, RealisticNightPlugin.StreetLightAtten.Value);
            float maxDist = RealisticNightPlugin.StreetMaxRenderDist.Value;

            float bi = RealisticNightPlugin.StreetLightIntensity.Value;
            float curve = Mathf.Clamp(RealisticNightPlugin.StreetLightAttenCurve.Value, 0.2f, 3f);
            if (orbActive)
            {
                // GPU billboard path: push shared uniforms once, no per-lamp CPU work.
                mat.SetFloat(POrbSize, size); mat.SetColor(POrbColor, LedCol(bi)); mat.SetFloat(POrbAtmos, range); mat.SetFloat(POrbCurve, curve); mat.SetFloat(POrbMax, maxDist);
                if (orbYellowMat != null)
                { orbYellowMat.SetFloat(POrbSize, size); orbYellowMat.SetColor(POrbColor, SodiumCol(bi)); orbYellowMat.SetFloat(POrbAtmos, range); orbYellowMat.SetFloat(POrbCurve, curve); orbYellowMat.SetFloat(POrbMax, maxDist); }
            }
            else
            {
                // Fallback (no orb shader): per-frame CPU billboarding.
                Quaternion camRot = cam.transform.rotation;
                Vector3 camFwd = cam.transform.forward;
                Color emi = LedCol(bi);
                if (mpb == null) mpb = new MaterialPropertyBlock();
                Vector3 scale = new Vector3(size, size, size);
                float maxDist2 = maxDist * maxDist;
                for (int k = 0; k < markers.Count; k++)
                {
                    Transform t = markers[k];
                    if (t == null) continue;
                    Vector3 to = t.position - camPos;
                    if (Vector3.Dot(camFwd, to) < 0f) continue;
                    float d2 = to.sqrMagnitude;
                    if (d2 > maxDist2) continue;
                    t.rotation = camRot; t.localScale = scale;
                    float atten = Mathf.Exp(-Mathf.Pow(Mathf.Sqrt(d2) / range, curve));
                    Color c = emi * atten; c.a = 1f;
                    mpb.SetColor(GlowFX.ColorID, c); mpb.SetColor(GlowFX.BaseColID, c);
                    renderers[k].SetPropertyBlock(mpb);
                }
            }

            LodPass(camPos, maxDist, lightsActive);
            UpdateGroundLight(camPos, lightsActive);
        }

        // Throttled LOD (5 Hz): rebuild merged meshes (orb path) or toggle renderers (fallback).
        static void LodPass(Vector3 camPos, float maxDist, bool lightsActive)
        {
            if (Time.realtimeSinceStartup < nextLod) return;
            nextLod = Time.realtimeSinceStartup + 0.2f;   // ~5 Hz; lamps are static
            if (container == null) return;
            float poleDist = RealisticNightPlugin.StreetPoleDrawDist.Value;
            float maxD2 = maxDist * maxDist;
            float poleD2 = poleDist * poleDist;
            bool ballsOn = lightsActive && RealisticNightPlugin.StreetBallsOn; // off in daytime; dropdown can hide too
            float nearestCity2 = float.PositiveInfinity;
            Matrix4x4 root = Matrix4x4.identity;
            try { root = container.transform.localToWorldMatrix; } catch { }
            if (orbActive)
            {
                // Merged orbs: one regular GO per colour, centers baked in container space
                // (recenter-safe; the orb shader billboards them on the GPU).
                MergeBake.EnsureGO(ref whiteGO, ref whiteMesh, "RN_StreetOrbsWhite", container.transform, mat, false);
                MergeBake.EnsureGO(ref yellowGO, ref yellowMesh, "RN_StreetOrbsYellow", container.transform, orbYellowMat != null ? orbYellowMat : mat, false);
                BakeOrbs(true, root, camPos, maxD2, ballsOn, ref nearestCity2, whiteMesh);
                BakeOrbs(false, root, camPos, maxD2, ballsOn, ref nearestCity2, yellowMesh);
            }
            else
            {
                // Fallback (no orb shader): per-lamp ball GameObjects, toggled here.
                for (int k = 0; k < markers.Count; k++)
                {
                    Transform t = markers[k];
                    if (t == null) continue;
                    float d2 = (t.position - camPos).sqrMagnitude;
                    if (k < lampWarm.Count && lampWarm[k] < 0.5f && d2 < nearestCity2) nearestCity2 = d2; // city lamp distance metric (any range)
                    MeshRenderer br = k < renderers.Count ? renderers[k] : null;
                    if (br != null) { bool on = ballsOn && d2 <= maxD2; if (br.enabled != on) br.enabled = on; }
                }
            }
            // Merged poles: one regular Lit GO (standing + in range only).
            BakePoles(root, camPos, poleD2);
            NearestCityDist = float.IsPositiveInfinity(nearestCity2) ? float.PositiveInfinity : Mathf.Sqrt(nearestCity2);
        }

        // One colour pass over the lamp records into a merged orb mesh; returns baked quad count.
        static int BakeOrbs(bool white, Matrix4x4 root, Vector3 camPos, float maxD2, bool ballsOn, ref float nearestCity2, Mesh mesh)
        {
            mPos.Clear(); mUv.Clear(); mC.Clear(); mTri.Clear();
            int n = 0;
            for (int k = 0; k < ballLocal.Count; k++)
            {
                if (k >= ballAlive.Count || !ballAlive[k]) continue;
                Vector3 wpos = root.MultiplyPoint3x4(ballLocal[k]);
                float d2 = (wpos - camPos).sqrMagnitude;
                bool warm = k < lampWarm.Count && lampWarm[k] > 0.5f;
                if (!warm && d2 < nearestCity2) nearestCity2 = d2; // city lamp distance metric (any range)
                if (!ballsOn || d2 > maxD2) continue;
                if (warm == white) continue; // colour split: one mesh each
                Vector3 c = ballLocal[k]; // POSITION = baked center (container space; shader billboards it)
                int b = mPos.Count;
                mPos.Add(c); mPos.Add(c); mPos.Add(c); mPos.Add(c); mPos.Add(c); mPos.Add(c);
                mUv.Add(new Vector2(0f, 0f)); mUv.Add(new Vector2(1f, 0f)); mUv.Add(new Vector2(1f, 1f));
                mUv.Add(new Vector2(0f, 0f)); mUv.Add(new Vector2(1f, 1f)); mUv.Add(new Vector2(0f, 1f));
                mC.Add(new Vector2(-0.5f, -0.5f)); mC.Add(new Vector2(0.5f, -0.5f)); mC.Add(new Vector2(0.5f, 0.5f));
                mC.Add(new Vector2(-0.5f, -0.5f)); mC.Add(new Vector2(0.5f, 0.5f)); mC.Add(new Vector2(-0.5f, 0.5f));
                for (int q = 0; q < 6; q++) mTri.Add(b + q);
                n++;
            }
            MergeBake.Upload(mesh, mPos, mUv, mC, null, mTri);
            return n;
        }

        // Standing in-range poles merged into one Lit GO (container space; Lit shades it like any child mesh).
        static void BakePoles(Matrix4x4 root, Vector3 camPos, float poleD2)
        {
            bool have = poleMeshRef != null && poleMat != null;
            if (have && container != null)
            {
                MergeBake.EnsureGO(ref poleGO, ref poleMergedMesh, "RN_StreetPoles", container.transform, poleMat, true);
                // Pole merges routinely exceed 65k verts (585+ poles in range): a 16-bit index buffer wraps
                // and stitches distant poles together with giant triangles (the white bars). Force 32-bit.
                if (poleMergedMesh.indexFormat != IndexFormat.UInt32) poleMergedMesh.indexFormat = IndexFormat.UInt32;
            }
            pPos.Clear(); pNrm.Clear(); pTri.Clear();
            if (have)
            {
                if (poleSrcCached != poleMeshRef) CachePoleSrc(); // template snapshot (verts/normals/tris)
                if (poleVertSrc != null && poleNrmSrc != null && poleTriSrc != null)
                {
                    for (int k = 0; k < poleLocalPos.Count; k++)
                    {
                        if (k >= poleAlive.Count || !poleAlive[k]) continue;
                        Vector3 wpos = root.MultiplyPoint3x4(poleLocalPos[k]);
                        if ((wpos - camPos).sqrMagnitude > poleD2) continue;
                        Vector3 p = poleLocalPos[k];
                        Quaternion r = k < poleLocalRot.Count ? poleLocalRot[k] : Quaternion.identity;
                        int b = pPos.Count;
                        for (int i = 0; i < poleVertSrc.Length; i++) { pPos.Add(p + r * poleVertSrc[i]); pNrm.Add(r * poleNrmSrc[i]); }
                        for (int i = 0; i < poleTriSrc.Length; i++) pTri.Add(b + poleTriSrc[i]);
                    }
                }
            }
            if (poleMergedMesh != null) MergeBake.UploadN(poleMergedMesh, pPos, pNrm, pTri);
        }

        static void CachePoleSrc()
        {
            poleSrcCached = poleMeshRef;
            try { poleVertSrc = poleMeshRef.vertices; poleNrmSrc = poleMeshRef.normals; poleTriSrc = poleMeshRef.triangles; }
            catch { poleVertSrc = null; poleNrmSrc = null; poleTriSrc = null; }
            if (poleVertSrc == null || poleNrmSrc == null || poleNrmSrc.Length != poleVertSrc.Length) // corrupt guard
            { poleVertSrc = null; poleNrmSrc = null; poleTriSrc = null; }
        }

        // Feed the nearest N lamp positions to the ground-light shader (no user cap).
        static Vector4[] gbuf; static int[] gidx; static float[] gdist; static float nextGround;
        static void UpdateGroundLight(Vector3 camPos, bool lightsActive)
        {
            if (!lightsActive || !RealisticNightPlugin.StreetGroundOn) { StreetLightFX.Apply(false); return; }
            int cap = StreetLightFX.MaxLamps;
            if (Time.realtimeSinceStartup >= nextGround) // refresh the lamp SET ~10 Hz; lamps are static
            {
                nextGround = Time.realtimeSinceStartup + 0.1f;
                if (gbuf == null || gbuf.Length != cap) gbuf = new Vector4[cap];
                if (gidx == null || gidx.Length != cap) { gidx = new int[cap]; gdist = new float[cap]; }
                float maxDist = RealisticNightPlugin.StreetMaxRenderDist.Value;
                float maxDist2 = maxDist * maxDist;
                // Nearest-N without sorting: append while under cap, else swap out the farthest.
                // Positions come from the instance records (both modes record ballLocal at Build).
                int found = 0;
                float farDist = -1f; int farSlot = -1;
                Matrix4x4 root = container != null ? container.transform.localToWorldMatrix : Matrix4x4.identity;
                int n = orbActive ? ballLocal.Count : markers.Count;
                for (int k = 0; k < n; k++)
                {
                    Vector3 wpos;
                    if (orbActive)
                    {
                        if (k >= ballAlive.Count || !ballAlive[k]) continue;
                        wpos = root.MultiplyPoint3x4(ballLocal[k]);
                    }
                    else
                    {
                        Transform t = markers[k];
                        if (t == null) continue;
                        wpos = t.position;
                    }
                    float d2 = (wpos - camPos).sqrMagnitude;
                    if (d2 > maxDist2) continue;              // outside render distance
                    if (found < cap)
                    {
                        gidx[found] = k; gdist[found] = d2;
                        if (d2 > farDist) { farDist = d2; farSlot = found; }
                        found++;
                    }
                    else if (d2 < farDist)                    // full: swap out the farthest kept lamp
                    {
                        gidx[farSlot] = k; gdist[farSlot] = d2;
                        farDist = -1f;
                        for (int j = 0; j < cap; j++) if (gdist[j] > farDist) { farDist = gdist[j]; farSlot = j; }
                    }
                }
                // RAW GLOBAL positions (Datum.origin-relative), so recenters never desync the pools.
                for (int i = 0; i < found; i++) { int k = gidx[i]; Vector3 p = orbActive ? ballLocal[k] : markers[k].localPosition; gbuf[i] = new Vector4(p.x, p.y, p.z, k < lampWarm.Count ? lampWarm[k] : 0f); }
                StreetLightFX.SetLamps(gbuf, found);
            }
            StreetLightFX.Apply(true);
        }

        static long CellKey(int x, int z) { return ((long)x << 32) ^ (uint)z; }

        // Bin every road point into grid cells (size = classify radius) for density lookups.
        static void BuildRoadGrid(RoadNetwork net)
        {
            roadGrid.Clear();
            roadCell = Mathf.Max(1f, RealisticNightPlugin.StreetClassifyRadius.Value);
            float inv = 1f / roadCell;
            for (int r = 0; r < net.roads.Count; r++)
            {
                Road rd = net.roads[r];
                if (rd == null || rd.points == null) continue;
                for (int pi = 0; pi < rd.points.Count; pi++)
                {
                    Vector3 p = rd.points[pi].AsVector3();
                    long key = CellKey(Mathf.FloorToInt(p.x * inv), Mathf.FloorToInt(p.z * inv));
                    if (!roadGrid.TryGetValue(key, out var set)) { set = new HashSet<int>(); roadGrid[key] = set; }
                    set.Add(r);
                }
            }
        }

        // Count OTHER roads near a sample point (3x3 cells). Density proxy for city/main/rural.
        static int NearbyRoadCount(Vector3 sample, int self)
        {
            float inv = 1f / roadCell;
            int cx = Mathf.FloorToInt(sample.x * inv), cz = Mathf.FloorToInt(sample.z * inv);
            nearbyScratch.Clear();
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (roadGrid.TryGetValue(CellKey(cx + dx, cz + dz), out var set))
                        foreach (int r in set) if (r != self) nearbyScratch.Add(r);
            return nearbyScratch.Count;
        }

        static void RegenTexture()
        {
            bCore = RealisticNightPlugin.StreetLightCore.Value;
            bFalloff = RealisticNightPlugin.StreetLightFalloff.Value;
            Texture2D old = tex;
            tex = GlowFX.RadialTex(128, bCore, bFalloff);
            // Re-point BOTH orb materials (sodium is a separate Instantiate; skip it and its orbs vanish).
            if (mat != null) { mat.SetTexture(GlowFX.MainTexID, tex); mat.SetTexture(GlowFX.BaseMapID, tex); }
            if (orbYellowMat != null) { orbYellowMat.SetTexture(GlowFX.MainTexID, tex); orbYellowMat.SetTexture(GlowFX.BaseMapID, tex); }
            if (old != null) UnityEngine.Object.Destroy(old);
        }

        static void Build()
        {
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (li == null) return;
            RoadNetwork net = li.roadNetwork;
            bool exists = net != null && net.Exists();
            if (!exists) return;

            // Mission-start race: build once, but only when the road set is actually streamed in.
            // Empty reads just take the throttled retry above; after a successful build this costs nothing.
            // NOTE: deliberately NOT gated on Terrain.activeTerrains: this game's ground is not a Unity
            // Terrain (activeTerrains stays empty all mission — proven by waiting-to-build logs), so that
            // gate blocked every build forever. Ground seating + the over-water skip run on physics
            // raycasts (the primary path), with the terrain heightfield used only as a reference when present.
            // (A road-less map builds nothing either way — the building pass keys off road lamps.)
            int roadTotal = 0;
            try { roadTotal = (net.roads != null) ? net.roads.Count : 0; } catch { }
            if (roadTotal <= 0) return;
            // Floating-origin frame: container-space MUST equal raw-global (orb bake, pool feed,
            // ground seating and the water test all assume it). Building before Datum.origin spawns
            // would bake world-as-raw with no self-heal (built stays true), permanently offsetting
            // pools/orbs and corrupting the water check once the origin appears.
            try { if (Datum.origin == null) return; } catch { return; }

            bCore = RealisticNightPlugin.StreetLightCore.Value;
            bFalloff = RealisticNightPlugin.StreetLightFalloff.Value;
            if (tex == null) tex = GlowFX.RadialTex(128, bCore, bFalloff);
            // GPU-billboard orb shader preferred; Sprites/Default fallback needs per-frame CPU work.
            Material orb = StreetLightFX.OrbMat;
            if (orb != null && orb.shader != null && orb.shader.isSupported)
            {
                mat = orb;
                mat.SetTexture(GlowFX.MainTexID, tex);
                if (orbYellowMat == null) orbYellowMat = UnityEngine.Object.Instantiate(mat); // 2nd colour batch (sodium)
                orbYellowMat.SetTexture(GlowFX.MainTexID, tex);
                orbActive = true;
            }
            else
            {
                orbActive = false;
                if (mat == null || mat == orb) mat = GlowFX.SoftGlow(tex, "RN_StreetLightMat");
                else { mat.SetTexture(GlowFX.MainTexID, tex); mat.SetTexture(GlowFX.BaseMapID, tex); }
            }
            if (mat == null) { RealisticNightPlugin.Log.LogWarning("[StreetLights] no material available."); return; }
            Mesh qm = GlowFX.Billboard();

            container = new GameObject("RN_StreetLights");
            if (Datum.origin != null) container.transform.SetParent(Datum.origin, worldPositionStays: false);

            bSpacing = RealisticNightPlugin.StreetLightSpacing.Value;
            bHeight = RealisticNightPlugin.StreetLightHeight.Value;
            bSide = RealisticNightPlugin.StreetLightSideOffset.Value;
            bPoles = RealisticNightPlugin.StreetLightPoles.Value;
            bSkipWater = RealisticNightPlugin.StreetLightSkipWater.Value;
            bBuildSearch = Mathf.Max(0f, RealisticNightPlugin.StreetBuildingLampSearch.Value);
            waterRefTerrain = null; waterTHas = false; // true-ground reference for the water skip
            try
            {
                Terrain[] allT = Terrain.activeTerrains;
                Terrain t = (allT != null && allT.Length > 0) ? allT[0] : null;
                if (t != null && t.terrainData != null)
                {
                    Vector3 ts = Vector3.Scale(t.terrainData.size, t.transform.lossyScale);
                    if (ts.x > 0f && ts.z > 0f) { waterRefTerrain = t; waterTPos = t.GetPosition(); waterTSize = ts; waterTHas = true; }
                }
            }
            catch { waterTHas = false; }
            bPoleW = RealisticNightPlugin.StreetLightPoleWidth.Value;
            float spacing = Mathf.Max(2f, bSpacing);
            float h = bHeight, side = bSide;
            // Cantilever pole: shaft stands `armReach` further out than the head; the arm reaches over to it.
            float armReach = Mathf.Clamp(side * 0.4f, 1.5f, 5f);
            // ONE combined mesh per pole (shaft + foot + arm + head), baked with this height/arm/width.
            if (poleMeshRef != null) { UnityEngine.Object.Destroy(poleMeshRef); poleMeshRef = null; }
            Mesh poleMesh = (bPoles && h > 0.5f) ? (poleMeshRef = GlowFX.PoleMesh(h, armReach, bPoleW)) : null;
            // Solid steel, metallic-lit (URP/Lit) so it reads as real metal and is never see-through.
            if (poleMat == null) poleMat = GlowFX.PoleMetal(new Color(0.34f, 0.35f, 0.39f, 1f));
            // Tuck the lens just under the cobra-head luminaire so it reads as the lamp lens.
            float ballDrop = poleMesh != null ? (h * 0.05f + bPoleW * 1.1f + 0.1f) : 0f;
            const int cap = 100000; // safety ceiling so a degenerate road can't hang the build
            int count = 0;
            markers.Clear(); renderers.Clear(); lampWarm.Clear();
            ballLocal.Clear(); ballAlive.Clear(); poleLocalPos.Clear(); poleLocalRot.Clear(); poleAlive.Clear();

            // Shared lamp placer for the arc-length walk AND the one-lamp fallback below.
            void PlaceLamp(Vector3 p, Vector3 perp, float warm, Material ballMat)
            {
                float sign = (count % 2 == 0) ? 1f : -1f;
                Vector3 headXZ = p + perp * side * sign;    // lamp head horizontal position

                // Seat on the REAL ground via raycast (road Y is centre-line only); fall back to road Y.
                float groundY = p.y;
                bool gotGround = false;
                Collider groundCol = null;
                Vector3 probeW = container.transform.TransformPoint(new Vector3(headXZ.x, p.y, headXZ.z));
                if (Physics.Raycast(probeW + Vector3.up * 8f, Vector3.down, out RaycastHit gh, 90f, ~0, QueryTriggerInteraction.Ignore))
                { groundY = container.transform.InverseTransformPoint(gh.point).y; gotGround = true; groundCol = gh.collider; }

                if (IsOverWater(probeW, groundY, gotGround, groundCol)) return; // no record, no count++ (side alternation stays clean)

                // Instanced records (positions drive orbs, pools AND poles; standing poles need no GameObjects).
                Vector3 ballLp = new Vector3(headXZ.x, groundY + (h - ballDrop), headXZ.z); // ball hangs under the head
                ballLocal.Add(ballLp); ballAlive.Add(true);
                lampWarm.Add(warm);
                bool hasPole = poleMesh != null && poleMat != null;
                if (hasPole)
                {
                    // Shaft base sits `armReach` outboard of the head; mesh +Z (the arm) points inboard to the head.
                    Vector3 shaftBaseXZ = p + perp * (side + armReach) * sign;
                    poleLocalPos.Add(new Vector3(shaftBaseXZ.x, groundY, shaftBaseXZ.z)); // seated on real ground
                    poleLocalRot.Add(Quaternion.LookRotation(-perp * sign, Vector3.up));
                }
                else { poleLocalPos.Add(Vector3.zero); poleLocalRot.Add(Quaternion.identity); }
                poleAlive.Add(hasPole);

                if (!orbActive)
                {
                    // Fallback (no orb shader): per-lamp ball GameObjects, CPU-tinted per frame (frozen path).
                    GameObject go = new GameObject("sl");
                    go.transform.SetParent(container.transform, worldPositionStays: false);
                    go.transform.localPosition = ballLp;
                    go.AddComponent<MeshFilter>().sharedMesh = qm;
                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = ballMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    markers.Add(go.transform);
                    renderers.Add(mr);
                }
                count++;
            }

            // Classify roads by local road density (grid count of nearby OTHER roads).
            bClassifyRadius = RealisticNightPlugin.StreetClassifyRadius.Value;
            bMainMult = RealisticNightPlugin.StreetSpacingMainMult.Value;
            bRuralMult = RealisticNightPlugin.StreetSpacingRuralMult.Value;
            bCityMin = RealisticNightPlugin.StreetCityMinRoads.Value;
            bMainMin = RealisticNightPlugin.StreetMainMinRoads.Value;

            BuildRoadGrid(net);
            float mainMult = Mathf.Max(1f, bMainMult);
            float ruralMult = Mathf.Max(1f, bRuralMult);
            int cityMin = bCityMin;
            int mainMin = bMainMin;

            // Walk each polyline by arc length, dropping a lamp every `spacing` metres (class-scaled).
            for (int ri = 0; ri < net.roads.Count && count < cap; ri++)
            {
                Road road = net.roads[ri];
                if (road == null || road.points == null || road.points.Count < 2) continue;

                // Classify this road from a sample near its middle.
                int nearby = NearbyRoadCount(road.points[road.points.Count / 2].AsVector3(), ri);
                float mult; float warm;
                if (nearby >= cityMin) { mult = 1f; warm = 0f; }            // city: base spacing, WHITE LED
                else if (nearby >= mainMin) { mult = mainMult; warm = 1f; } // main: wider, YELLOW sodium
                else { mult = ruralMult; warm = 1f; }                      // rural: widest, YELLOW sodium
                float roadSpacing = spacing * mult;
                Material ballMat = (warm > 0.5f && orbYellowMat != null) ? orbYellowMat : mat;

                int placedBefore = count;
                float nextAt = roadSpacing * 0.5f;   // first lamp half a span in
                float traveled = 0f;
                for (int i = 1; i < road.points.Count && count < cap; i++)
                {
                    Vector3 a = road.points[i - 1].AsVector3();
                    Vector3 b = road.points[i].AsVector3();
                    Vector3 seg = b - a;
                    float segLen = seg.magnitude;
                    if (segLen < 1e-4f) continue;
                    Vector3 flat = seg; flat.y = 0f;
                    Vector3 dir = flat.sqrMagnitude < 1e-6f ? Vector3.forward : flat.normalized;
                    Vector3 perp = Vector3.Cross(Vector3.up, dir).normalized;

                    while (nextAt <= traveled + segLen && count < cap)
                    {
                        float t = (nextAt - traveled) / segLen;
                        Vector3 p = Vector3.Lerp(a, b, t);          // interpolated road point (raw global)
                        nextAt += roadSpacing;
                        PlaceLamp(p, perp, warm, ballMat);
                    }
                    traveled += segLen;
                }

                // Roads too short for even one span get a single lamp at the middle.
                if (count == placedBefore && count < cap)
                {
                    int mi = road.points.Count / 2;
                    Vector3 pm = road.points[mi].AsVector3();
                    Vector3 nA = road.points[Mathf.Max(0, mi - 1)].AsVector3();
                    Vector3 nB = road.points[Mathf.Min(road.points.Count - 1, mi + 1)].AsVector3();
                    Vector3 flat = nB - nA; flat.y = 0f;
                    Vector3 dir = flat.sqrMagnitude < 1e-6f ? Vector3.forward : flat.normalized;
                    Vector3 perp = Vector3.Cross(Vector3.up, dir).normalized;
                    PlaceLamp(pm, perp, warm, ballMat);
                }
                if (count >= cap) break;
            }
            // Second pass: dark buildings get one perimeter lamp (shared emitter dedups by proximity).
            void PlaceBuildingLamps()
            {
                if (bBuildSearch < 1f || ballLocal.Count == 0) return; // 0 = off; nothing to measure against
                MapBuilding[] all = null;
                try { all = UnityEngine.Object.FindObjectsOfType<MapBuilding>(true); } catch { return; }
                if (all == null) return;
                foreach (MapBuilding mb in all)
                {
                    if (count >= cap) break;
                    if (mb == null) continue;
                    Transform t = null;
                    try { t = mb.transform; } catch { continue; }
                    if (t == null) continue;
                    if (HasMobileUnitParent(t) || IsNoLampKey(t.name)) continue;
                    float radius = 0f;
                    try { radius = BuildingRadius(mb); } catch { continue; }
                    if (radius < 4f || IsThinFootprint(t)) continue;
                    Vector3 centerLp;
                    try { centerLp = container.transform.InverseTransformPoint(t.position); } catch { continue; }
                    if (TryPlaceBuildingLamp(centerLp, t.position, radius) == BuildingLampResult.Placed) count++;
                }
                // Stationary structure units (all Building types + Scenery); late spawns via rescan.
                List<Unit> units = null;
                try { units = UnitRegistry.allUnits; } catch { units = null; }
                if (units != null)
                {
                    Unit[] snap = null;
                    try { snap = units.ToArray(); } catch { snap = null; }
                    if (snap != null) foreach (Unit u in snap)
                    {
                        if (count >= cap) break;
                        if (u == null) continue;
                        bool dis = false;
                        try { dis = u.disabled; } catch { continue; }
                        if (dis) continue;
                        if (!(u is Building) && !(u is Scenery)) continue;
                        if (IsNoLampUnit(u)) continue;
                        Vector3 wpos;
                        try { wpos = u.transform.position; } catch { continue; }
                        float fr = UnitRadius(u);
                        if (fr < 4f || fr > 200f) continue;
                        Vector3 centerLp;
                        try { centerLp = container.transform.InverseTransformPoint(wpos); } catch { continue; }
                        if (TryPlaceBuildingLamp(centerLp, wpos, fr) == BuildingLampResult.Placed) count++;
                    }
                }
            }

            PlaceBuildingLamps();
            built = true;
            try { if (RealisticNightPlugin.DiagOn()) RealisticNightPlugin.Log.LogInfo($"[StreetLights] built {count} lamps ({ballLocal.Count} orbs) from {roadTotal} roads (waterRef={waterTHas}, skipWater={bSkipWater})."); } catch { }
            nextUnitRescan = Time.realtimeSinceStartup + 5f;
        }

        static void Teardown(string why, bool instant)
        {
            if (container != null) UnityEngine.Object.Destroy(container);
            container = null;
            if (poleMeshRef != null) { UnityEngine.Object.Destroy(poleMeshRef); poleMeshRef = null; }
            if (whiteMesh != null) { UnityEngine.Object.Destroy(whiteMesh); whiteMesh = null; }
            if (yellowMesh != null) { UnityEngine.Object.Destroy(yellowMesh); yellowMesh = null; }
            if (poleMergedMesh != null) { UnityEngine.Object.Destroy(poleMergedMesh); poleMergedMesh = null; }
            whiteGO = yellowGO = poleGO = null; // died with the container
            poleSrcCached = null; poleVertSrc = poleNrmSrc = null; poleTriSrc = null;
            markers.Clear(); renderers.Clear(); lampWarm.Clear();
            ballLocal.Clear(); ballAlive.Clear(); poleLocalPos.Clear(); poleLocalRot.Clear(); poleAlive.Clear();
            // Container destroy already took topple debris; hand the pole collision matrix back.
            RestorePoleLayer();
            built = false;
            if (instant) nextBuildTry = 0f;
        }

        // Blast destruction: static poles grow a rigidbody + terrain-only collider when a blast reaches them, topple, go dark, get cleaned up.
        const float ToppleLife = 6f;         // seconds a fallen pole lingers before cleanup
        const float PoleMass = 250f;          // steel-ish, so the topple reads with weight (not a ping-pong ball)
        const float ToppleForceK = 0.45f;     // overpressure -> impulse scale (tuned by feel; no user knob)
        const float ToppleForceMin = 400f;    // floor so a pole in range always clearly goes over (not a half-tip)
        const float ToppleForceMax = 6500f;   // cap so even a nuke's core doesn't fling poles to orbit
        // Overpressure floor for toppling: devastation radius = power * (25000/OP_MIN)^(1/3).
        const float ToppleOpMin = 150f;
        static readonly float ToppleRadiusFactor = Mathf.Pow(25000f / ToppleOpMin, 1f / 3f); // dist = power * this

        static bool Destruct => RealisticNightPlugin.ModEnabled.Value && RealisticNightPlugin.StreetDestructible.Value && built;

        // Big weapons (>200 yield): topple poles as the live Shockwave front (340 m/s) passes them.
        public static void OnShockwaveFront(Vector3 origin, float propagation, float power)
        {
            if (!Destruct) return;
            power = Mathf.Max(1f, power);
            float maxR = power * ToppleRadiusFactor;      // devastation edge (op == OP_MIN)
            if (propagation > maxR * 1.05f) return;       // front already past the edge — nothing left to topple
            SetupPoleLayer();
            ToppleWithin(origin, Mathf.Min(propagation, maxR), power, ToppleOpMin);
        }

        // Small weapons (<=200 yield): no Shockwave, topple instantly within the BlastFrag radius.
        public static void OnBlastFrag(Vector3 origin, float yield)
        {
            if (!Destruct) return;
            float power = Mathf.Pow(Mathf.Max(0f, yield), 1f / 3f);
            float radius = power * 20f;
            if (radius <= 0.5f) return;
            SetupPoleLayer();
            ToppleWithin(origin, radius, power, 0f);
        }

        // Topple every standing pole within `radius` whose overpressure >= opMin.
        static int ToppleWithin(Vector3 origin, float radius, float power, float opMin)
        {
            if (radius <= 0f || container == null) return 0;
            float r2 = radius * radius;
            float invPow = 1f / Mathf.Max(1f, power);
            Matrix4x4 root = container.transform.localToWorldMatrix;
            Vector3 sideFallback;
            try { sideFallback = container.transform.right; } catch { sideFallback = Vector3.right; }
            int n = 0;
            for (int k = 0; k < poleAlive.Count; k++)
            {
                if (!poleAlive[k]) continue;                       // no pole here / already toppled
                Vector3 pw = root.MultiplyPoint3x4(poleLocalPos[k]);
                Vector3 d = pw - origin;
                if (d.sqrMagnitude > r2) continue;
                float dist = d.magnitude;
                float num = Mathf.Max(dist * invPow, 1f);
                float op = 25000f / (num * num * num);          // game's overpressure at this distance
                if (op < opMin) continue;                       // too weak to knock this pole down
                float mag = Mathf.Clamp(op * ToppleForceK, ToppleForceMin, ToppleForceMax);
                Vector3 outward = d; outward.y = 0f;
                if (outward.sqrMagnitude < 1e-4f) outward = sideFallback; // directly under the blast: shove sideways
                TopplePole(k, (outward.normalized + Vector3.up * 0.25f).normalized * mag);
                n++;
            }
            return n;
        }

        // Knock one pole down: orb out, standing instance dropped, temp falling twin with real physics.
        static void TopplePole(int k, Vector3 impulse)
        {
            if (k < ballAlive.Count) ballAlive[k] = false; // light out (instance dropped next LOD tick)
            if (!orbActive && k < markers.Count && markers[k] != null) // fallback ball GO
            {
                UnityEngine.Object.Destroy(markers[k].gameObject);
                markers[k] = null; if (k < renderers.Count) renderers[k] = null;
            }
            if (container == null || poleMeshRef == null) return;
            if (k >= poleAlive.Count || !poleAlive[k]) return;
            poleAlive[k] = false;
            float w = Mathf.Max(0.4f, bPoleW);
            float hgt = Mathf.Max(1f, bHeight);
            GameObject pole = new GameObject("slpole_fall");
            pole.transform.SetParent(container.transform, worldPositionStays: false);
            pole.transform.localPosition = poleLocalPos[k];
            pole.transform.localRotation = poleLocalRot[k];
            pole.layer = PoleLayer;
            pole.AddComponent<MeshFilter>().sharedMesh = poleMeshRef; // visible falling twin (was physics-only)
            if (poleMat != null)
            {
                MeshRenderer pmr = pole.AddComponent<MeshRenderer>();
                pmr.sharedMaterial = poleMat;
                pmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pmr.receiveShadows = true;
            }

            BoxCollider bc = pole.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, hgt * 0.5f, 0f);
            bc.size = new Vector3(w, hgt, w);

            Rigidbody rb = pole.AddComponent<Rigidbody>();
            rb.mass = PoleMass;
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.drag = 0.05f;
            rb.angularDrag = 0.15f;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

            rb.AddForceAtPosition(impulse, pole.transform.position + Vector3.up * hgt, ForceMode.Impulse);

            UnityEngine.Object.Destroy(pole, ToppleLife);    // debris cleanup; container-destroy also covers it
        }

        // Pole layer collides with terrain only (never blocks aircraft/vehicles/shots); matrix restored on teardown.
        static void SetupPoleLayer()
        {
            if (layerMatrixSet) return;
            for (int i = 0; i < 32; i++)
            {
                savedIgnore[i] = Physics.GetIgnoreLayerCollision(PoleLayer, i);
                Physics.IgnoreLayerCollision(PoleLayer, i, true);
            }
            Physics.IgnoreLayerCollision(PoleLayer, PhysicsLayers.Statics, false); // ...except terrain, so poles land
            layerMatrixSet = true;
        }

        static void RestorePoleLayer()
        {
            if (!layerMatrixSet) return;
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(PoleLayer, i, savedIgnore[i]);
            layerMatrixSet = false;
        }
    }
    // Big weapons: hook the game's Shockwave Update, topple poles as the live front reaches them.
    [HarmonyPatch(typeof(Shockwave), "Update")]
    public static class Shockwave_Update_Patch
    {
        static readonly AccessTools.FieldRef<Shockwave, float> PropRef = AccessTools.FieldRefAccess<Shockwave, float>("blastPropagation");
        static readonly AccessTools.FieldRef<Shockwave, float> PowerRef = AccessTools.FieldRefAccess<Shockwave, float>("blastPower");
        static void Postfix(Shockwave __instance)
        {
            try { StreetLights.OnShockwaveFront(__instance.transform.position, PropRef(__instance), PowerRef(__instance)); }
            catch { }
        }
    }

    // Small weapons: hook BlastFrag (world position on every client), topple within frag radius.
    [HarmonyPatch(typeof(DamageEffects), "BlastFrag")]
    public static class BlastFrag_Patch
    {
        static void Postfix(float blastYield, Vector3 blastPosition)
        {
            StreetLights.OnBlastFrag(blastPosition, blastYield);
        }
    }
}
