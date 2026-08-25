using System;
using System.Collections.Generic;
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
    internal enum LightCat { City, Vehicle, Other }
    // Which street-light elements render. Shown as a dropdown in the config (ConfigurationManager renders
    // any enum as a list). Lets you A/B the glowing balls vs the ground light pools independently.
    public enum LampSources { BallsAndGround, BallsOnly, GroundOnly, None }

    // "Better Night" — the night / lighting / street-light half of the project. The rank-based night vision
    // (NVG / thermal / ENVG-B optics + unit silhouettes) is now a SEPARATE mod, "Tiered NVG"
    // (com.leech.tierednvg). This plugin keeps its original GUID so your tuned config carries over.
    [BepInPlugin(GUID, "Better Night", "0.9.39")]
    public class RealisticNightPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.leech.realisticnight";
        internal static ManualLogSource Log;

        // General
        public static ConfigEntry<bool> ModEnabled;
        public static ConfigEntry<bool> Diagnostics;

        // Night floor
        public static ConfigEntry<bool> NightEnabled;
        public static ConfigEntry<float> NightAmbientBoost;
        public static ConfigEntry<float> NightTintR, NightTintG, NightTintB;

        // Universal light boost (master) + per-category
        public static ConfigEntry<float> AllLightsIntensity;
        public static ConfigEntry<bool> CityWindowsEnabled;
        public static ConfigEntry<float> CityWindowIntensity;
        public static ConfigEntry<float> CityWindowWarmth;
        public static ConfigEntry<float> CityDrawDistanceMult;
        public static ConfigEntry<bool> VehicleLightsEnabled;
        public static ConfigEntry<float> VehicleLightIntensity;
        public static ConfigEntry<bool> OtherLightsEnabled;
        public static ConfigEntry<float> OtherLightIntensity;

        // Road street-lighting (spawned)
        public static ConfigEntry<bool> StreetLightsEnabled;
        public static ConfigEntry<bool> StreetLightsAlwaysOn;
        public static ConfigEntry<float> StreetLightIntensity;
        // Road-type classification (heuristic: local road density) -> spacing multiplier + colour.
        public static ConfigEntry<float> StreetSpacingMainMult, StreetSpacingRuralMult, StreetClassifyRadius;
        public static ConfigEntry<int> StreetCityMinRoads, StreetMainMinRoads;
        public static ConfigEntry<float> StreetLightSpacing;
        public static ConfigEntry<float> StreetLightHeight;
        public static ConfigEntry<float> StreetLightSize;
        public static ConfigEntry<float> StreetLightCore;
        public static ConfigEntry<float> StreetLightFalloff;
        public static ConfigEntry<float> StreetLightSideOffset;
        public static ConfigEntry<float> StreetLightAtten;
        public static ConfigEntry<bool> StreetLightPoles;
        public static ConfigEntry<float> StreetLightPoleWidth;
        public static ConfigEntry<float> StreetMaxRenderDist;
        public static ConfigEntry<float> StreetPoleDrawDist;
        public static ConfigEntry<bool> StreetDestructible;
        // Which elements render: glowing balls, ground light pools, both, or neither (dropdown).
        public static ConfigEntry<LampSources> StreetSources;
        public static ConfigEntry<float> StreetGroundIntensity, StreetGroundIntensityYellow, StreetGroundRange, StreetGroundShading, StreetGroundFalloff;
        public static ConfigEntry<float> StreetGroundColorPreserve;

        // Derived from the Light Sources dropdown — read by the renderers.
        public static bool StreetBallsOn => StreetSources.Value == LampSources.BallsAndGround || StreetSources.Value == LampSources.BallsOnly;
        public static bool StreetGroundOn => StreetSources.Value == LampSources.BallsAndGround || StreetSources.Value == LampSources.GroundOnly;

        // Extra world ambient while the game's night vision is active. This stays in Better Night (it only
        // READS the game's NightVision state, so there's no dependency on the Tiered NVG mod).
        public static ConfigEntry<float> NvgWorldAmbient;

        private void Awake()
        {
            Log = Logger;

            ModEnabled = Config.Bind("1. General", "Enabled", true, "Master switch. Off = fully vanilla (all effects revert live).");
            Diagnostics = Config.Bind("1. General", "Diagnostics Logging", true, "Log what the mod hooked to BepInEx/LogOutput.log.");

            NightEnabled = Config.Bind("2. Night", "Enabled", true, "Lift the near-black night via ambient light. World only; cockpit unaffected.");
            NightAmbientBoost = Config.Bind("2. Night", "Ambient Boost", 0f, new ConfigDescription("Strength of the night ambient floor.", new AcceptableValueRange<float>(0f, 5f)));
            NightTintR = Config.Bind("2. Night", "Tint R", 0.020f, new ConfigDescription("red", new AcceptableValueRange<float>(0f, 0.3f)));
            NightTintG = Config.Bind("2. Night", "Tint G", 0.028f, new ConfigDescription("green", new AcceptableValueRange<float>(0f, 0.3f)));
            NightTintB = Config.Bind("2. Night", "Tint B", 0.045f, new ConfigDescription("blue", new AcceptableValueRange<float>(0f, 0.3f)));
            NvgWorldAmbient = Config.Bind("2. Night", "Extra Ambient When NVG Active", 0f, new ConfigDescription("Extra world ambient ONLY while the game's night vision is active (amplifies the night for goggles). 0 = off. The NVG optics themselves are the separate 'Tiered NVG' mod.", new AcceptableValueRange<float>(0f, 5f)));

            AllLightsIntensity = Config.Bind("3. Lights", "Master Intensity", 1.0f,
                new ConfigDescription("Universal multiplier applied to EVERY emissive light source (windows, vehicle lights, windmills, antennas, beacons...). Multiplies the per-category knobs below.", new AcceptableValueRange<float>(0f, 10f)));
            CityWindowsEnabled = Config.Bind("3. Lights", "City Windows", true, "Boost city building windows at night (already-lit window materials only; dark roofs untouched).");
            CityWindowIntensity = Config.Bind("3. Lights", "City Intensity", 4f, new ConfigDescription("City window emission multiplier.", new AcceptableValueRange<float>(0f, 30f)));
            CityWindowWarmth = Config.Bind("3. Lights", "City Warmth", 0.5f, new ConfigDescription("0 = keep colours, 1 = warm amber.", new AcceptableValueRange<float>(0f, 1f)));
            CityDrawDistanceMult = Config.Bind("3. Lights", "Building Draw Distance x", 20f, new ConfigDescription("Keeps distant buildings (and their lit windows) drawn much farther out by multiplying the LOD bias at night — smaller buildings otherwise cull too soon. 1 = vanilla. Higher = see the city glow from farther, at some GPU cost (affects all LOD objects, incl. trees). Reverts to vanilla when off / daytime.", new AcceptableValueRange<float>(1f, 20f)));
            VehicleLightsEnabled = Config.Bind("3. Lights", "Vehicle Lights", true, "Brighten nav/position/running/engine lights on aircraft, ships and ground vehicles.");
            VehicleLightIntensity = Config.Bind("3. Lights", "Vehicle Intensity", 1f, new ConfigDescription("Vehicle light emission multiplier.", new AcceptableValueRange<float>(0f, 12f)));
            OtherLightsEnabled = Config.Bind("3. Lights", "Other Lights", true, "Brighten all OTHER emissive light sources at night: windmill lights, building antennas/blinking lights, beacons, mesh lights, etc.");
            OtherLightIntensity = Config.Bind("3. Lights", "Other Intensity", 1f, new ConfigDescription("Other light emission multiplier.", new AcceptableValueRange<float>(0f, 12f)));

            // --- 4. Street Lights: master switches + placement ---
            StreetLightsEnabled = Config.Bind("4. Street Lights", "Enabled", true, "Spawn street lights along roads (poles show day + night; the lights come on at night).");
            StreetLightsAlwaysOn = Config.Bind("4. Street Lights", "Always On (ignore day/night)", false, "Force the glowing balls + ground pools ON even in daytime. Normally OFF -> they auto-toggle: bare poles in day, full lights at night.");
            StreetSources = Config.Bind("4. Street Lights", "Light Sources", LampSources.BallsAndGround, "DROPDOWN — what each lamp shows at night: BallsAndGround = orb + ground pool; BallsOnly = just orbs; GroundOnly = just pools; None = neither (poles still show).");
            StreetLightPoles = Config.Bind("4. Street Lights", "Poles", true, "Add a cantilever lamp-post (shaft + arm over the road) under each light. Poles show day AND night.");
            StreetLightSpacing = Config.Bind("4. Street Lights", "Spacing (m)", 90f, new ConfigDescription("Base lamp spacing — used for CITY roads. Main/rural roads multiply this (see Road Types).", new AcceptableValueRange<float>(5f, 500f)));
            StreetLightSideOffset = Config.Bind("4. Street Lights", "Side Offset (m)", 9f, new ConfigDescription("How far the lamp head sits from the road centre-line (the pole stands further out and an arm reaches to the head). Lamps alternate sides.", new AcceptableValueRange<float>(0f, 80f)));
            StreetLightHeight = Config.Bind("4. Street Lights", "Height (m)", 8f, new ConfigDescription("Lamp head height above the (real, raycast) ground.", new AcceptableValueRange<float>(0f, 60f)));
            StreetLightPoleWidth = Config.Bind("4. Street Lights", "Pole Width (m)", 0.35f, new ConfigDescription("Thickness of the lamp posts.", new AcceptableValueRange<float>(0.02f, 5f)));
            StreetLightAtten = Config.Bind("4. Street Lights", "Atmospheric Range (m)", 100000f, new ConfigDescription("Haze distance: BOTH the orbs AND the ground pools fade to ~37% brightness at this range and keep fading beyond (no hard cutoff). Shared by balls + ground light.", new AcceptableValueRange<float>(100f, 100000f)));

            // --- 5. Road Types: how roads are classed City/Main/Rural (spacing + colour) ---
            StreetSpacingMainMult = Config.Bind("5. Street Lights - Road Types", "Main Road Spacing x", 2f, new ConfigDescription("Spacing multiplier for main roads (outside cities). 2 = twice the city spacing.", new AcceptableValueRange<float>(1f, 20f)));
            StreetSpacingRuralMult = Config.Bind("5. Street Lights - Road Types", "Rural Road Spacing x", 4f, new ConfigDescription("Spacing multiplier for rural roads. 4 = four times the city spacing.", new AcceptableValueRange<float>(1f, 40f)));
            StreetClassifyRadius = Config.Bind("5. Street Lights - Road Types", "Classify Radius (m)", 220f, new ConfigDescription("A road is classified City/Main/Rural by how many OTHER roads run within this distance of it.", new AcceptableValueRange<float>(30f, 2000f)));
            StreetCityMinRoads = Config.Bind("5. Street Lights - Road Types", "City: min nearby roads", 6, new ConfigDescription("At/above this many nearby roads -> CITY (base spacing, WHITE LED). Raise if too much reads as city.", new AcceptableValueRange<int>(1, 60)));
            StreetMainMinRoads = Config.Bind("5. Street Lights - Road Types", "Main: min nearby roads", 1, new ConfigDescription("At/above this (but below City) -> MAIN road (2x spacing, YELLOW). Below this -> RURAL (4x, YELLOW).", new AcceptableValueRange<int>(0, 30)));

            // --- 6. Appearance: the glowing orb look ---
            StreetLightIntensity = Config.Bind("6. Street Lights - Appearance", "Brightness", 1f, new ConfigDescription("Peak HDR brightness of the glow orb core (drives bloom). Independent of Glow Radius.", new AcceptableValueRange<float>(0f, 300f)));
            StreetLightSize = Config.Bind("6. Street Lights - Appearance", "Glow Radius (m)", 2.2f, new ConfigDescription("Diameter of the glow orb only. Independent of Brightness.", new AcceptableValueRange<float>(0.1f, 200f)));
            StreetLightCore = Config.Bind("6. Street Lights - Appearance", "Core (0-1)", 1f, new ConfigDescription("Fraction of the orb that is a solid-bright centre. LOWER = softer, no hard dot.", new AcceptableValueRange<float>(0f, 1f)));
            StreetLightFalloff = Config.Bind("6. Street Lights - Appearance", "Falloff", 0.1f, new ConfigDescription("How fast the halo fades out. Higher = tighter centre, bigger soft halo. Affects BOTH white and yellow orbs.", new AcceptableValueRange<float>(0.1f, 30f)));

            // --- 7. Ground Light: the per-lamp light volumes (custom shader) ---
            StreetGroundIntensity = Config.Bind("7. Street Lights - Ground Light", "Ground Intensity (White)", 1f, new ConfigDescription("Ground-pool brightness under WHITE (city) lamps. (Toggle pools on/off via 'Light Sources' in section 4.)", new AcceptableValueRange<float>(0f, 10f)));
            StreetGroundIntensityYellow = Config.Bind("7. Street Lights - Ground Light", "Ground Intensity (Yellow)", 8f, new ConfigDescription("Ground-pool brightness under YELLOW (main + rural) lamps. Separate from white because white reads brighter at the same value.", new AcceptableValueRange<float>(0f, 10f)));
            StreetGroundRange = Config.Bind("7. Street Lights - Ground Light", "Ground Range (m)", 250f, new ConfigDescription("Radius each lamp lights on the ground. Raise for much larger pools.", new AcceptableValueRange<float>(1f, 3000f)));
            StreetGroundShading = Config.Bind("7. Street Lights - Ground Light", "Ground Surface Shading", 0f, new ConfigDescription("0 = flat light pool (trees render cleaner), 1 = full N.L directional shading (can look pixelated on trees). At 0, raise 'Preserve Surface Colours' so surfaces keep their own colour instead of turning the lamp's colour.", new AcceptableValueRange<float>(0f, 1f)));
            StreetGroundFalloff = Config.Bind("7. Street Lights - Ground Light", "Ground Falloff", 0f, new ConfigDescription("Inverse-square softness of the pool. 0 = flat, fills the whole range evenly (biggest pool). The whole 0..1 range is usable now: ~0.1 is a subtle centre-weighting, ~0.5 noticeably tighter, 1 = tight bright centre with a dark rim. (Normalised to Ground Range, so it looks the same at any range.)", new AcceptableValueRange<float>(0f, 1f)));
            StreetGroundColorPreserve = Config.Bind("7. Street Lights - Ground Light", "Preserve Surface Colours", 1f, new ConfigDescription("0 = additive: the pool ADDS the lamp's colour, so at high Intensity (or low Surface Shading) surfaces wash out toward the lamp colour. 1 = multiplicative: the pool BRIGHTENS each surface while keeping its OWN colour (asphalt stays grey, grass stays green). Raise this if lit ground looks tinted like the lamp. Note: at high values pools get fainter on very dark ground, so pair with more Intensity.", new AcceptableValueRange<float>(0f, 1f)));

            // --- 8. Range & Destruction ---
            StreetMaxRenderDist = Config.Bind("8. Street Lights - Range & Destruction", "Max Render Distance (m)", 80000f, new ConfigDescription("Lamps within this distance of the camera render / feed the ground light. Set to your map size (some are 80km+).", new AcceptableValueRange<float>(1000f, 500000f)));
            StreetPoleDrawDist = Config.Bind("8. Street Lights - Range & Destruction", "Pole Draw Distance (m)", 5000f, new ConfigDescription("LOD: beyond this, only the light ball + ground pool show (no pole mesh). Poles are the heaviest geometry, so this is the main perf lever on huge maps.", new AcceptableValueRange<float>(0f, 500000f)));
            StreetDestructible = Config.Bind("8. Street Lights - Range & Destruction", "Destructible", true, "Poles are physically knocked down by explosion shockwaves. Each hit pole gets a rigidbody and topples/falls with real physics using the game's own blast overpressure, then its light goes out. Turn off to make poles indestructible.");

            new Harmony(GUID).PatchAll();
            Log.LogInfo("Better Night 0.9.39 loaded.");
        }
    }

    // Universal emissive-light booster. One scan categorises every emissive LIGHT material
    // (windows / vehicle lights / other), then each frame sets emission = orig × master × category.
    // Shared materials => scales to all instances. Fully revertible (mult=1 restores original).
    internal static class LightBoost
    {
        struct Entry { public Material m; public Color orig; public LightCat cat; }
        static readonly int EmiID = Shader.PropertyToID("_EmissionColor");
        static List<Entry> entries;
        static bool searched;

        static readonly string[] CityP = { "highrise", "blocks_city", "commercial", "downtown", "office", "storefront", "apartment", "skyscraper", "window",
            // more building families the user found unlit (commercial_3a, residential_f1/f1a, midrise_1a, solarpanels1, ...)
            "residential", "midrise", "lowrise", "solarpanel", "rooftop", "block_", "building" };
        static readonly string[] VehP = { "navlight", "lights_red_nav", "lights_green_nav", "lights_amber", "lights_green", "lights_red", "engineglow", "nozzle_glow", "position_light", "running_light" };
        static readonly string[] ExcludeName = { "afterburner", "tracer", "muzzle", "explos", "fireball", "mushroom", "crater", "scorch", "smoke", "distort", "heatdist", "font", "glyph", "hud", "icon", "cursor", "reticle", "debug", "_ui", "runway", "taxi",
            // cockpit / instrument surfaces — boosting these makes them cross the bloom threshold and strobe
            "cockpit", "mfd", "screen", "display", "instrument", "gauge", "dial", "monitor", "canopy", "rwr", "hsi", "mfcd", "panel_light", "warning", "caution",
            // water FX — ship wakes / splashes / spray are emissive-ish; boosting them looks wrong
            "splash", "spray", "wake", "foam", "bubble", "ripple", "wetdeck" };
        static readonly string[] ExcludeShader = { "stars", "water", "cloud", "moon", "sky", "afterburner", "tracer", "smoke", "distort" };

        static void Find()
        {
            searched = true;
            entries = new List<Entry>();
            int nc = 0, nv = 0, no = 0;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || !m.HasProperty(EmiID)) continue;
                string sn = m.shader.name.ToLowerInvariant();
                bool skip = false;
                foreach (string x in ExcludeShader) if (sn.Contains(x)) { skip = true; break; }
                if (skip) continue;
                bool isLight = m.IsKeywordEnabled("_EMISSION") || sn.Contains("meshlight");
                if (!isLight) continue;
                Color e = m.GetColor(EmiID);
                if (e.maxColorComponent <= 0.5f) continue; // only real light sources
                string n = m.name.ToLowerInvariant();
                foreach (string x in ExcludeName) if (n.Contains(x)) { skip = true; break; }
                if (skip) continue;

                LightCat cat = LightCat.Other;
                foreach (string p in CityP) if (n.Contains(p)) { cat = LightCat.City; break; }
                if (cat == LightCat.Other) foreach (string p in VehP) if (n.Contains(p)) { cat = LightCat.Vehicle; break; }

                entries.Add(new Entry { m = m, orig = e, cat = cat });
                if (cat == LightCat.City) nc++; else if (cat == LightCat.Vehicle) nv++; else no++;
            }
            if (RealisticNightPlugin.Diagnostics.Value)
                RealisticNightPlugin.Log.LogInfo($"[LightBoost] matched {entries.Count} light materials (city {nc}, vehicle {nv}, other {no}).");
        }

        public static void Apply(bool modOn, bool night)
        {
            if (!searched || (entries != null && entries.Count == 0)) Find();
            if (entries == null || entries.Count == 0) return;

            float master = RealisticNightPlugin.AllLightsIntensity.Value;
            foreach (Entry en in entries)
            {
                if (en.m == null) continue;
                bool catOn; float catI; bool nightGate;
                switch (en.cat)
                {
                    case LightCat.City: catOn = RealisticNightPlugin.CityWindowsEnabled.Value; catI = RealisticNightPlugin.CityWindowIntensity.Value; nightGate = true; break;
                    case LightCat.Vehicle: catOn = RealisticNightPlugin.VehicleLightsEnabled.Value; catI = RealisticNightPlugin.VehicleLightIntensity.Value; nightGate = false; break;
                    default: catOn = RealisticNightPlugin.OtherLightsEnabled.Value; catI = RealisticNightPlugin.OtherLightIntensity.Value; nightGate = true; break;
                }
                bool active = modOn && catOn && (!nightGate || night);
                float mult = active ? master * catI : 1f;
                Color c;
                if (en.cat == LightCat.City)
                {
                    float w = RealisticNightPlugin.CityWindowWarmth.Value;
                    c = new Color(en.orig.r * mult, en.orig.g * mult * Mathf.Lerp(1f, 0.72f, w), en.orig.b * mult * Mathf.Lerp(1f, 0.4f, w), en.orig.a);
                }
                else c = new Color(en.orig.r * mult, en.orig.g * mult, en.orig.b * mult, en.orig.a);
                en.m.SetColor(EmiID, c);
            }
        }
    }

    // Distant-building draw distance. Small city buildings (and thus their lit windows) LOD-cull too soon;
    // raising QualitySettings.lodBias makes all LOD objects survive to a greater distance. Global but fully
    // revertible: cache the user's value, apply the multiplier at night, restore when off/day.
    internal static class DrawDistance
    {
        static bool cached;
        static float origLodBias;

        public static void Apply(bool active)
        {
            float mult = RealisticNightPlugin.CityDrawDistanceMult.Value;
            if (active && mult > 1.001f)
            {
                if (!cached) { origLodBias = QualitySettings.lodBias; cached = true; }
                float want = origLodBias * mult;
                if (!Mathf.Approximately(QualitySettings.lodBias, want)) QualitySettings.lodBias = want;
            }
            else if (cached)
            {
                QualitySettings.lodBias = origLodBias; // truthful revert
                cached = false;
            }
        }
    }

    // Shared glow-rendering helpers. Two material paths, both chosen for RUNTIME RELIABILITY:
    //  * SoftGlow  -> "Sprites/Default": a built-in always-included shader with a single premultiplied
    //    alpha-blend pass (Blend One OneMinusSrcAlpha, c.rgb*=c.a, ZWrite Off, Cull Off). No shader
    //    variants to be stripped, so it ALWAYS blends — unlike URP/Unlit-transparent, whose stripped
    //    variant fell back to opaque and rendered solid white squares.
    //  * Solid     -> "Universal Render Pipeline/Unlit" OPAQUE (the variant that DOES survive), used for
    //    the thermal white-hot silhouette and the ENVG-B inverted-hull outline.

    internal static class GlowFX
    {
        public static readonly int BaseColID = Shader.PropertyToID("_BaseColor");
        public static readonly int BaseMapID = Shader.PropertyToID("_BaseMap");
        public static readonly int MainTexID = Shader.PropertyToID("_MainTex");
        public static readonly int ColorID = Shader.PropertyToID("_Color");
        static Mesh billboard;

        // Unit quad in the XY plane (normal -Z). Billboards by setting transform.rotation = camera.rotation.
        public static Mesh Billboard()
        {
            if (billboard != null) return billboard;
            billboard = new Mesh { name = "RN_Billboard" };
            billboard.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            billboard.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            // Sprites/Default multiplies by vertex colour; a mesh with no colour channel can feed
            // (0,0,0,0) on some drivers -> invisible. Force white so tint comes only from _Color.
            billboard.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            billboard.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            billboard.RecalculateBounds();
            return billboard;
        }

        static Mesh groundQuad;
        // Horizontal quad in the XZ plane (normal +Y). Flat "light pool" laid on the ground.
        public static Mesh GroundQuad()
        {
            if (groundQuad != null) return groundQuad;
            groundQuad = new Mesh { name = "RN_GroundQuad" };
            groundQuad.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(-0.5f, 0, 0.5f) };
            groundQuad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            groundQuad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            groundQuad.triangles = new[] { 0, 2, 1, 0, 3, 2 }; // faces up
            groundQuad.RecalculateBounds();
            return groundQuad;
        }

        static Mesh box;
        // Unit cube centred on origin (12 tris). Used for lamp-post poles.
        public static Mesh Box()
        {
            if (box != null) return box;
            box = new Mesh { name = "RN_Box" };
            Vector3[] v = {
                new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,-.5f,.5f), new Vector3(-.5f,-.5f,.5f),
                new Vector3(-.5f,.5f,-.5f),  new Vector3(.5f,.5f,-.5f),  new Vector3(.5f,.5f,.5f),  new Vector3(-.5f,.5f,.5f) };
            int[] tri = {
                0,2,1, 0,3,2,  4,5,6, 4,6,7,  0,1,5, 0,5,4,
                2,3,7, 2,7,6,  1,2,6, 1,6,5,  0,4,7, 0,7,3 };
            box.vertices = v; box.triangles = tri; box.RecalculateNormals(); box.RecalculateBounds();
            return box;
        }

        // Cantilever street-lamp post as ONE mesh (built in local space: shaft up +Y, arm out +Z to the
        // lamp head, plus a base foot and a small head drop). One GameObject per pole; per-instance we only
        // set position (shaft base) + rotation (align +Z to the arm direction). Height/arm/width baked in.
        public static Mesh PoleMesh(float height, float armLen, float width)
        {
            const int sides = 6;                 // hexagonal tubes read as round but stay cheap (~110 tris/pole)
            float w = Mathf.Max(0.02f, width);
            var verts = new List<Vector3>();
            var tris = new List<int>();

            // Append a tapered round tube (frustum) from A (radius rA) to B (radius rB). The pole material is
            // double-sided (cull off) so winding never matters for visibility.
            void Tube(Vector3 A, Vector3 B, float rA, float rB, bool capA, bool capB)
            {
                Vector3 axis = B - A; float len = axis.magnitude;
                if (len < 1e-5f) return;
                Vector3 up = axis / len;
                Vector3 refv = Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.forward;
                Vector3 ex = Vector3.Normalize(Vector3.Cross(up, refv));
                Vector3 ez = Vector3.Cross(up, ex);
                int b0 = verts.Count;
                for (int s = 0; s < sides; s++)
                {
                    float a = (s / (float)sides) * Mathf.PI * 2f;
                    Vector3 dir = ex * Mathf.Cos(a) + ez * Mathf.Sin(a);
                    verts.Add(A + dir * rA);   // even = ring A
                    verts.Add(B + dir * rB);   // odd  = ring B
                }
                for (int s = 0; s < sides; s++)
                {
                    int s0 = b0 + s * 2, s1 = b0 + ((s + 1) % sides) * 2;
                    tris.Add(s0); tris.Add(s0 + 1); tris.Add(s1 + 1);
                    tris.Add(s0); tris.Add(s1 + 1); tris.Add(s1);
                }
                if (capB) { int c = verts.Count; verts.Add(B); for (int s = 0; s < sides; s++) { tris.Add(b0 + s * 2 + 1); tris.Add(c); tris.Add(b0 + ((s + 1) % sides) * 2 + 1); } }
                if (capA) { int c = verts.Count; verts.Add(A); for (int s = 0; s < sides; s++) { tris.Add(b0 + ((s + 1) % sides) * 2); tris.Add(c); tris.Add(b0 + s * 2); } }
            }

            Tube(new Vector3(0, 0, 0), new Vector3(0, w * 1.6f, 0), w * 1.25f, w * 0.95f, true, false); // base foot
            Tube(new Vector3(0, 0, 0), new Vector3(0, height, 0), w * 0.55f, w * 0.42f, false, true);   // tapered shaft

            if (armLen > 0.05f)
            {
                // Swept mast arm: quadratic bezier P0(shaft top) -> C(hump up & out) -> P2(tip, slightly below
                // top) — the classic cobra curve. Sampled into tapering tube segments.
                float rise = Mathf.Min(armLen * 0.45f, height * 0.28f);
                float drop = height * 0.05f;
                Vector3 P0 = new Vector3(0, height, 0);
                Vector3 C = new Vector3(0, height + rise, armLen * 0.35f);
                Vector3 P2 = new Vector3(0, height - drop, armLen);
                const int seg = 6;
                Vector3 prev = P0; float prevR = w * 0.42f;
                for (int i = 1; i <= seg; i++)
                {
                    float t = i / (float)seg, u = 1f - t;
                    Vector3 pt = u * u * P0 + 2f * u * t * C + t * t * P2;
                    float r = Mathf.Lerp(w * 0.42f, w * 0.30f, t);
                    Tube(prev, pt, prevR, r, false, false);
                    prev = pt; prevR = r;
                }
                // Cobra-head luminaire: a rounded housing along the arm tangent at the tip, nosing slightly down.
                Vector3 tan = (P2 - C); tan = tan.sqrMagnitude < 1e-6f ? Vector3.forward : tan.normalized;
                float lumLen = Mathf.Clamp(armLen * 0.55f, 0.8f, 1.7f);
                Tube(P2 - tan * lumLen * 0.35f, P2 + tan * lumLen * 0.65f, w * 1.05f, w * 0.5f, true, true);
            }

            var m = new Mesh { name = "RN_PoleMesh" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // Soft, alpha-blended glow. Tint via _Color (supports HDR values -> bloom). Per-marker tint
        // through a MaterialPropertyBlock on _Color. Texture provides the round soft falloff.
        public static Material SoftGlow(Texture tex, string name)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) return null;
            Material m = new Material(sh) { name = name };
            if (tex != null)
            {
                if (m.HasProperty(MainTexID)) m.SetTexture(MainTexID, tex);
                if (m.HasProperty(BaseMapID)) m.SetTexture(BaseMapID, tex);
            }
            if (m.HasProperty(ColorID)) m.SetColor(ColorID, Color.white);
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return m;
        }

        static bool loggedSolid;
        // Opaque unlit HDR colour. cull: 0=Off, 1=Front (inverted-hull outline), 2=Back (solid fill).
        public static Material Solid(Color hdr, int cull, string name)
        {
            // Try several shaders that render an opaque solid colour, in case URP/Unlit isn't findable.
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Hidden/Internal-Colored");
            if (!loggedSolid && RealisticNightPlugin.Diagnostics.Value)
            {
                loggedSolid = true;
                RealisticNightPlugin.Log.LogInfo($"[GlowFX] Solid shader = '{(sh != null ? sh.name : "NULL — no opaque shader found!")}'.");
            }
            if (sh == null) return null;
            Material m = new Material(sh) { name = name };
            if (m.HasProperty(BaseColID)) m.SetColor(BaseColID, hdr);
            if (m.HasProperty(ColorID)) m.SetColor(ColorID, hdr);     // Unlit/Color & Sprites use _Color
            if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", hdr); }
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", cull);
            return m;
        }

        static bool loggedPole;
        // Opaque, metallic lamp-post material. The game exposes URP/Lit via Shader.Find (it shades the whole
        // world) but NOT URP/Unlit or Unlit/Color -> the old Solid() fell through to "Sprites/Default", a
        // TRANSPARENT alpha-blend/ZWrite-Off shader = the "see-through poles" bug. URP/Lit is opaque, writes
        // depth, and shades metallic so posts read as steel under the moon + our ground light.
        public static Material PoleMetal(Color baseCol)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            bool lit = sh != null;
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) return null;
            Material m = new Material(sh) { name = "RN_PoleMat" };
            if (m.HasProperty(BaseColID)) m.SetColor(BaseColID, baseCol);
            if (m.HasProperty(ColorID)) m.SetColor(ColorID, baseCol);
            if (lit)
            {
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.65f);   // steel; some diffuse so it stays visible at night
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.5f);
                // Faint emission floor so posts are never pure black on a moonless night.
                if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", new Color(0.02f, 0.021f, 0.026f)); }
                // Double-sided: the procedural cobra-head tubes are generated on the fly, so don't rely on
                // winding for visibility — render both faces (poles are thin + LOD-culled, cost is trivial).
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                if (m.HasProperty("_RenderFace")) m.SetFloat("_RenderFace", 0f);
            }
            ForceOpaque(m);
            if (!loggedPole && RealisticNightPlugin.Diagnostics.Value)
            {
                loggedPole = true;
                RealisticNightPlugin.Log.LogInfo($"[GlowFX] Pole shader = '{sh.name}' lit={lit} (opaque metallic).");
            }
            return m;
        }

        // Force a material into an OPAQUE, depth-writing state regardless of shader defaults or which fallback
        // shader we land on (defeats the transparent Sprites/Default fallback).
        static void ForceOpaque(Material m)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f);          // URP: 0 = Opaque
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
            m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        }

        // Radial soft glow: white RGB, alpha = 1 inside the core, smooth power falloff to 0 at the edge.
        // Mipmapped so distant markers blur/soften instead of shimmering.
        public static Texture2D RadialTex(int size, float core, float falloff)
        {
            core = Mathf.Clamp01(core); falloff = Mathf.Max(0.1f, falloff);
            var t = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true, linear: false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            float half = (size - 1) * 0.5f, maxr = size * 0.5f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float r = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / maxr;
                    float a = r <= core ? 1f : (r >= 1f ? 0f : Mathf.Pow(1f - (r - core) / (1f - core), falloff));
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px); t.Apply(true, false);
            return t;
        }

        // Hollow ring: alpha peaks in a band at radius r0 (0..1), width w. For ENVG-B target outlines.
        public static Texture2D RingTex(int size, float r0, float w)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true, linear: false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            float half = (size - 1) * 0.5f, maxr = size * 0.5f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float r = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / maxr;
                    float a = Mathf.Clamp01(1f - Mathf.Abs(r - r0) / w); a *= a;
                    if (r > 1f) a = 0f;
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px); t.Apply(true, false);
            return t;
        }
    }

    // Street lights: billboarded soft-glow orbs placed along road sides at night (map ships none).
    // Each is a camera-facing additive quad (a "ball of light" from any angle) whose brightness is
    // attenuated by distance to mimic light scattering through air. Positions bake once; billboard
    // orientation, size, colour and distance fade update live every frame.
    internal static class StreetLights
    {
        static GameObject container;
        static Material mat;           // orb material for WHITE-LED lamps (city/main)
        static Material orbYellowMat;  // orb material for SODIUM lamps (rural)
        static Material poleMat;
        static Mesh poleMeshRef;   // combined cantilever mesh, rebuilt per Build (bakes height/arm/width)
        static Texture2D tex;
        static readonly List<Transform> markers = new List<Transform>();
        static readonly List<MeshRenderer> renderers = new List<MeshRenderer>();      // ball renderers (parallel to markers)
        static readonly List<Transform> poles = new List<Transform>();                // parallel to markers (null if no pole)
        static readonly List<MeshRenderer> poleRends = new List<MeshRenderer>();      // parallel to markers (null if no pole) — for LOD/destruction
        static readonly List<float> lampWarm = new List<float>();                     // parallel to markers: 0 = white LED, 1 = sodium (rural)
        static bool orbActive;                                                        // true = GPU-billboard orb shader (no per-frame ball updates)
        static float nextLod;
        static readonly int POrbSize = Shader.PropertyToID("_Size");
        static readonly int POrbColor = Shader.PropertyToID("_Color");
        static readonly int POrbAtmos = Shader.PropertyToID("_AtmosRange");
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
        // Road-classification params the current placement/colours were built with (change => respawn:
        // these decide spacing AND city/main/rural colour, all baked at Build time).
        static float bMainMult, bRuralMult, bClassifyRadius;
        static int bCityMin, bMainMin;
        static float nextBuildTry;

        // --- Physics destruction (poles are knocked down by real shockwaves) ---
        // Poles stay pure static meshes (zero physics cost) until a blast reaches them; only THEN do we add a
        // rigidbody + collider and let the pole topple/fall with real physics. Colliders live on a dedicated
        // layer whose collision matrix we set so toppling poles rest on the terrain (Statics) but pass through
        // aircraft / vehicles / bullets — so they can never become invisible walls that crash a landing plane.
        const int PoleLayer = 30;                 // unused by the game (its PhysicsLayers enum stops at 18)
        static bool layerMatrixSet;
        static readonly bool[] savedIgnore = new bool[32];

        // Cool white LED (city/main roads) vs warm sodium (rural), scaled by intensity for HDR bloom.
        static Color LedCol(float i) { return new Color(0.90f * i, 0.95f * i, 1.0f * i, 1f); }
        static Color SodiumCol(float i) { return new Color(1.0f * i, 0.72f * i, 0.36f * i, 1f); }

        // on = spawn/keep the system (day AND night, so POLES stay up in daytime). lightsActive = the glowing
        // balls + ground pools are on (night, or Always On). Poles show whenever built regardless.
        public static void Apply(bool on, bool lightsActive)
        {
            if (!on) { StreetLightFX.Apply(false); StreetLightFX.ReleaseBuffer(); if (built) Teardown("disabled", instant: true); return; }

            // If the game destroyed our container (mission restart / scene reload) while we still think we're
            // built, the poles are gone but we'd never rebuild — the user had to toggle the mod. Detect the
            // dead container and reset so we respawn automatically.
            if (built && container == null)
            {
                built = false; nextBuildTry = 0f;
                markers.Clear(); renderers.Clear(); poles.Clear(); poleRends.Clear(); lampWarm.Clear();
                if (RealisticNightPlugin.Diagnostics.Value)
                    RealisticNightPlugin.Log.LogInfo("[StreetLights] container was destroyed externally (scene reload?) — rebuilding.");
            }

            // Position/geometry/classification params changed => respawn (these are all baked at Build:
            // placement, poles, AND which roads count as city/main/rural + their spacing and colour).
            if (built &&
                (bSpacing != RealisticNightPlugin.StreetLightSpacing.Value ||
                 bHeight != RealisticNightPlugin.StreetLightHeight.Value ||
                 bSide != RealisticNightPlugin.StreetLightSideOffset.Value ||
                 bPoles != RealisticNightPlugin.StreetLightPoles.Value ||
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
                return;
            }

            // Texture shape (core/falloff) is a live regen — no respawn needed.
            if (mat != null && (bCore != RealisticNightPlugin.StreetLightCore.Value || bFalloff != RealisticNightPlugin.StreetLightFalloff.Value))
                RegenTexture();

            UpdateFrame(lightsActive);
        }

        // The glow orb renders at this fraction of the Glow Radius knob, so it reads as a lamp LENS tucked in the
        // cobra head rather than a big obvious floating ball. Raise Glow Radius if you want more altitude glow.
        const float OrbSizeScale = 0.5f;

        static void UpdateFrame(bool lightsActive)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;
            float size = RealisticNightPlugin.StreetLightSize.Value * OrbSizeScale;
            float range = Mathf.Max(1f, RealisticNightPlugin.StreetLightAtten.Value);
            float maxDist = RealisticNightPlugin.StreetMaxRenderDist.Value;

            float bi = RealisticNightPlugin.StreetLightIntensity.Value;
            if (orbActive)
            {
                // GPU billboard: the shader billboards + fades every ball. We just push the shared uniforms
                // once (cheap, live config) — NO per-lamp per-frame CPU work. Two materials: white + sodium.
                mat.SetFloat(POrbSize, size); mat.SetColor(POrbColor, LedCol(bi)); mat.SetFloat(POrbAtmos, range); mat.SetFloat(POrbMax, maxDist);
                if (orbYellowMat != null)
                { orbYellowMat.SetFloat(POrbSize, size); orbYellowMat.SetColor(POrbColor, SodiumCol(bi)); orbYellowMat.SetFloat(POrbAtmos, range); orbYellowMat.SetFloat(POrbMax, maxDist); }
            }
            else
            {
                // Fallback (no orb shader): billboard/fade every visible ball on the CPU (the old path).
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
                    float atten = Mathf.Exp(-Mathf.Sqrt(d2) / range);
                    Color c = emi * atten; c.a = 1f;
                    mpb.SetColor(GlowFX.ColorID, c); mpb.SetColor(GlowFX.BaseColID, c);
                    renderers[k].SetPropertyBlock(mpb);
                }
            }

            LodPass(camPos, maxDist, lightsActive);
            UpdateGroundLight(camPos, lightsActive);
        }

        // Throttled LOD/culling pass: disable pole meshes past Pole Draw Distance (they're the heavy
        // geometry) and ball renderers past Max Render Distance, so distant lamps cost nothing to draw.
        // Poles show whenever built (day + night); balls only when lightsActive (night / Always On).
        static void LodPass(Vector3 camPos, float maxDist, bool lightsActive)
        {
            if (Time.realtimeSinceStartup < nextLod) return;
            nextLod = Time.realtimeSinceStartup + 0.2f;   // ~5 Hz; lamps are static
            float poleDist = RealisticNightPlugin.StreetPoleDrawDist.Value;
            float maxD2 = maxDist * maxDist;
            float poleD2 = poleDist * poleDist;
            bool ballsOn = lightsActive && RealisticNightPlugin.StreetBallsOn; // off in daytime; dropdown can hide too
            for (int k = 0; k < markers.Count; k++)
            {
                Transform t = markers[k];
                if (t == null) continue;
                float d2 = (t.position - camPos).sqrMagnitude;
                MeshRenderer br = renderers[k];
                if (br != null) { bool on = ballsOn && d2 <= maxD2; if (br.enabled != on) br.enabled = on; }
                MeshRenderer pr = k < poleRends.Count ? poleRends[k] : null;
                if (pr != null) { bool on = d2 <= poleD2; if (pr.enabled != on) pr.enabled = on; }
            }
        }

        // Feed the nearest N lamp world positions to the screen-space ground-light shader.
        // Always uses the maximum the shader supports (StreetLightFX.MaxLamps) — no user cap.
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
                // Collect up to `cap` lamps in range. The shader sums them unordered, so we DON'T sort —
                // just append (O(n) while under cap, the normal case now that cap=8192 > lamp count) and,
                // only if we overflow, replace the current farthest (nearest-N selection without an O(n^2) sort).
                int found = 0;
                float farDist = -1f; int farSlot = -1;
                for (int k = 0; k < markers.Count; k++)
                {
                    Transform t = markers[k];
                    if (t == null) continue;
                    float d2 = (t.position - camPos).sqrMagnitude;
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
                // Feed RAW GLOBAL positions (localPosition, i.e. relative to Datum.origin) so the buffer is
                // floating-origin-invariant; the shader subtracts _RN_OriginPos from the reconstructed ground
                // to match. (Selection above uses .position/world — correct for 'nearest to camera'.)
                for (int i = 0; i < found; i++) { int k = gidx[i]; Vector3 p = markers[k].localPosition; gbuf[i] = new Vector4(p.x, p.y, p.z, k < lampWarm.Count ? lampWarm[k] : 0f); }
                StreetLightFX.SetLamps(gbuf, found);
            }
            StreetLightFX.Apply(true);
        }

        static long CellKey(int x, int z) { return ((long)x << 32) ^ (uint)z; }

        // Bin every road point into a grid cell (size = classify radius), recording which roads touch each cell.
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

        // How many OTHER roads run near this sample point (3x3 cells ~ classify radius). Density proxy.
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
            // BOTH orb materials must get the new texture — the sodium (rural/main) material is a separate
            // Instantiate of the white one, so if we only re-point `mat` the sodium orbs keep the OLD (now
            // destroyed) texture and vanish.
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
            if (RealisticNightPlugin.Diagnostics.Value)
                RealisticNightPlugin.Log.LogInfo($"[StreetLights] build: roadNetwork={(net == null ? "null" : "ok")} exists={exists} roads={(net != null && net.roads != null ? net.roads.Count : 0)}");
            if (!exists) return;

            bCore = RealisticNightPlugin.StreetLightCore.Value;
            bFalloff = RealisticNightPlugin.StreetLightFalloff.Value;
            if (tex == null) tex = GlowFX.RadialTex(128, bCore, bFalloff);
            // Prefer the GPU-billboard orb shader (no per-frame CPU). Fall back to Sprites/Default (per-frame
            // billboarding) only if the bundle/material is unavailable.
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
            if (RealisticNightPlugin.Diagnostics.Value)
                RealisticNightPlugin.Log.LogInfo($"[StreetLights] orb shader = '{(mat.shader != null ? mat.shader.name : "null")}' orbActive={orbActive} (GPU billboard = no per-frame CPU).");
            Mesh qm = GlowFX.Billboard();

            container = new GameObject("RN_StreetLights");
            if (Datum.origin != null) container.transform.SetParent(Datum.origin, worldPositionStays: false);

            bSpacing = RealisticNightPlugin.StreetLightSpacing.Value;
            bHeight = RealisticNightPlugin.StreetLightHeight.Value;
            bSide = RealisticNightPlugin.StreetLightSideOffset.Value;
            bPoles = RealisticNightPlugin.StreetLightPoles.Value;
            bPoleW = RealisticNightPlugin.StreetLightPoleWidth.Value;
            float spacing = Mathf.Max(2f, bSpacing);
            float h = bHeight, side = bSide;
            // Cantilever pole: shaft stands `armReach` further out than the head; the arm reaches over to it.
            float armReach = Mathf.Clamp(side * 0.4f, 1.5f, 5f);
            // ONE combined mesh per pole (shaft + foot + arm + head), baked with this height/arm/width.
            if (poleMeshRef != null) { UnityEngine.Object.Destroy(poleMeshRef); poleMeshRef = null; }
            Mesh poleMesh = (bPoles && h > 0.5f) ? (poleMeshRef = GlowFX.PoleMesh(h, armReach, bPoleW)) : null;
            // Solid steel, metallic-lit (URP/Lit) so it reads as real metal and is never see-through.
            if (bPoles && poleMat == null) poleMat = GlowFX.PoleMetal(new Color(0.34f, 0.35f, 0.39f, 1f));
            // Tuck the glowing lens just UNDER the cobra-head luminaire (which sits ~height*0.05 below the shaft
            // top and is ~bPoleW*1.05 in radius) so the orb reads as the lamp's lit lens, not a floating ball.
            // Independent of orb size now, so changing Glow Radius won't move it off the fixture.
            float ballDrop = poleMesh != null ? (h * 0.05f + bPoleW * 1.1f + 0.1f) : 0f;
            if (RealisticNightPlugin.Diagnostics.Value)
                RealisticNightPlugin.Log.LogInfo($"[StreetLights] poles: enabled={bPoles} poleMat={(poleMat == null ? "NULL" : poleMat.shader.name)} width={bPoleW} h={h} arm={armReach}.");
            int poleCount = 0;
            int groundHits = 0; // how many lamps found real ground via raycast (rest fell back to road Y)
            const int cap = 100000; // effectively uncapped — safety ceiling so a degenerate road can't hang the build
            int count = 0;
            markers.Clear(); renderers.Clear(); poles.Clear(); poleRends.Clear(); lampWarm.Clear();

            // Places one lamp (ball + optional pole) at road point p, offset to the side by `perp`. Extracted so
            // BOTH the arc-length walk AND the "every road gets at least one lamp" fallback share the exact same
            // seating/pole logic. Captures the outer locals (side/h/ballDrop/qm/poleMesh/poleMat/container) and
            // mutates the outer counters (count/poleCount/groundHits) + the parallel lists.
            void PlaceLamp(Vector3 p, Vector3 perp, float warm, Material ballMat)
            {
                float sign = (count % 2 == 0) ? 1f : -1f;
                Vector3 headXZ = p + perp * side * sign;    // lamp head horizontal position

                // Seat on the REAL ground: roads carry only their centre-line height, but the lamp/pole sit `side`
                // metres out where the terrain can differ. Raycast down from JUST ABOVE the road point (building
                // roofs are far above this start, so we can't land on them). Fall back to the road Y if nothing hit.
                float groundY = p.y;
                Vector3 probeW = container.transform.TransformPoint(new Vector3(headXZ.x, p.y, headXZ.z));
                if (Physics.Raycast(probeW + Vector3.up * 8f, Vector3.down, out RaycastHit gh, 90f, ~0, QueryTriggerInteraction.Ignore))
                { groundY = container.transform.InverseTransformPoint(gh.point).y; groundHits++; }

                GameObject go = new GameObject("sl");
                go.transform.SetParent(container.transform, worldPositionStays: false);
                go.transform.localPosition = new Vector3(headXZ.x, groundY + (h - ballDrop), headXZ.z); // ball hangs under the head
                go.AddComponent<MeshFilter>().sharedMesh = qm;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = ballMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                markers.Add(go.transform);
                renderers.Add(mr);
                lampWarm.Add(warm);

                Transform poleT = null;
                MeshRenderer poleR = null;
                if (poleMesh != null && poleMat != null)
                {
                    // Shaft base sits `armReach` outboard of the head; mesh +Z (the arm) points inboard to the head.
                    Vector3 shaftBaseXZ = p + perp * (side + armReach) * sign;
                    GameObject pole = new GameObject("slpole");
                    pole.transform.SetParent(container.transform, worldPositionStays: false);
                    pole.transform.localPosition = new Vector3(shaftBaseXZ.x, groundY, shaftBaseXZ.z); // seated on real ground
                    pole.transform.localRotation = Quaternion.LookRotation(-perp * sign, Vector3.up);
                    pole.AddComponent<MeshFilter>().sharedMesh = poleMesh;
                    MeshRenderer pmr = pole.AddComponent<MeshRenderer>();
                    pmr.sharedMaterial = poleMat;
                    pmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    pmr.receiveShadows = false;
                    poleT = pole.transform;
                    poleR = pmr;
                    poleCount++;
                }
                poles.Add(poleT); poleRends.Add(poleR); // parallel with markers (null if no pole)
                count++;
            }

            // Classify roads by LOCAL DENSITY: build a grid of which roads touch each cell, then count how
            // many OTHER roads run near each road. Dense = city, some = main, isolated = rural.
            // Cache RAW config values so Apply()'s change-detection compares like-for-like and respawns
            // live when any classification knob is tuned.
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
            int cCity = 0, cMain = 0, cRural = 0;
            int zeroRoads = 0; float zeroMaxLen = 0f;   // roads the arc-walk placed NO lamp on (got only the fallback)

            // UNIFORM placement: walk each road's polyline by ARC LENGTH and drop a lamp every `spacing`
            // metres, interpolating WITHIN segments. Spacing/colour depend on the road's class.
            for (int ri = 0; ri < net.roads.Count && count < cap; ri++)
            {
                Road road = net.roads[ri];
                if (road == null || road.points == null || road.points.Count < 2) continue;

                // Classify this road from a sample near its middle.
                int nearby = NearbyRoadCount(road.points[road.points.Count / 2].AsVector3(), ri);
                float mult; float warm;
                if (nearby >= cityMin) { mult = 1f; warm = 0f; cCity++; }            // city: base spacing, WHITE LED
                else if (nearby >= mainMin) { mult = mainMult; warm = 1f; cMain++; } // main: wider, YELLOW sodium
                else { mult = ruralMult; warm = 1f; cRural++; }                      // rural: widest, YELLOW sodium
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

                // EVERY road gets at least one lamp. Short/isolated roads (esp. rural, 4x spacing) whose entire
                // length is under half a span otherwise placed NONE. Drop one at the middle. (This is a floor, not
                // the fix for the user's "long roads with no lamps" — those long roads are NOT in net.roads at all;
                // see the zeroRoads/zeroMaxLen diagnostic: if a LONG road ever lands here, the arc-walk has a bug.)
                if (count == placedBefore && count < cap)
                {
                    zeroRoads++; zeroMaxLen = Mathf.Max(zeroMaxLen, road.length);
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
            built = true;
            if (RealisticNightPlugin.Diagnostics.Value)
                RealisticNightPlugin.Log.LogInfo($"[StreetLights] spawned {count} lamps, {poleCount} poles ({groundHits} ground-raycast hits) on {net.roads.Count} roads. Classified: city={cCity} main={cMain} rural={cRural}. Roads that needed the 1-lamp fallback: {zeroRoads} (longest {zeroMaxLen:0}m). base spacing {spacing}m x main {mainMult}/rural {ruralMult}.");
        }

        static void Teardown(string why, bool instant)
        {
            if (container != null) UnityEngine.Object.Destroy(container);
            container = null;
            if (poleMeshRef != null) { UnityEngine.Object.Destroy(poleMeshRef); poleMeshRef = null; }
            markers.Clear(); renderers.Clear(); poles.Clear(); poleRends.Clear(); lampWarm.Clear();
            // Destroying the container above already took any in-progress topple debris (they're its children).
            // Hand the pole layer's collision matrix back to the game (truthful revert).
            RestorePoleLayer();
            built = false;
            if (instant) nextBuildTry = 0f;
            if (RealisticNightPlugin.Diagnostics.Value)
                RealisticNightPlugin.Log.LogInfo($"[StreetLights] torn down ({why}).");
        }

        // === Physics destruction ==========================================================================
        // Poles are knocked down by the game's REAL explosions, using its own overpressure model. A pole is a
        // pure static mesh (no physics) until a blast reaches it; then it lazily grows a rigidbody + collider and
        // topples/falls under real physics, its light goes out, and after a few seconds the debris is destroyed.
        // The game's HP damage is server-authoritative and never touches our client-local poles, so we drive the
        // topple from the game's OWN blast: for big weapons we read the live Shockwave front each frame (so the
        // collapse is synced to the visible wave), for small ones we use BlastFrag. Client-side, MP-safe.
        const float ToppleLife = 6f;         // seconds a fallen pole lingers before cleanup
        const float PoleMass = 250f;          // steel-ish, so the topple reads with weight (not a ping-pong ball)
        const float ToppleForceK = 0.45f;     // overpressure -> impulse scale (tuned by feel; no user knob)
        const float ToppleForceMin = 400f;    // floor so a pole in range always clearly goes over (not a half-tip)
        const float ToppleForceMax = 6500f;   // cap so even a nuke's core doesn't fling poles to orbit
        // A pole only falls where the game's overpressure exceeds this — so the FAR, WEAK edge of a huge nuke's
        // shockwave (which reaches ~8 km but is trivially weak out there) leaves poles standing. This sets the
        // "devastation radius": op = 25000/(dist/power)^3 >= OP_MIN  ->  radius = power * (25000/OP_MIN)^(1/3).
        // At OP_MIN=150 that's power*5.5 (a 250 kt nuke, power 630, topples to ~3.5 km, not 8 km). Raise to
        // tighten the kill radius, lower to widen it.
        const float ToppleOpMin = 150f;
        static readonly float ToppleRadiusFactor = Mathf.Pow(25000f / ToppleOpMin, 1f / 3f); // dist = power * this
        static float nextShockLog;

        static bool Destruct => RealisticNightPlugin.ModEnabled.Value && RealisticNightPlugin.StreetDestructible.Value && built;

        // A big weapon (>200 yield) spawns the game's Shockwave, whose Update() advances the real front
        // (blastPropagation) at 340 m/s. We hook that Update, so each frame we get the TRUE current front radius
        // + power. Poles fall as the real front passes them, but only out to the overpressure-limited devastation
        // radius — so the collapse is synced to the visible wave and the weak far edge leaves poles standing.
        public static void OnShockwaveFront(Vector3 origin, float propagation, float power)
        {
            if (!Destruct) return;
            power = Mathf.Max(1f, power);
            float maxR = power * ToppleRadiusFactor;      // devastation edge (op == OP_MIN)
            if (propagation > maxR * 1.05f) return;       // front already past the edge — nothing left to topple
            SetupPoleLayer();
            int n = ToppleWithin(origin, Mathf.Min(propagation, maxR), power, ToppleOpMin);
            if (n > 0 && RealisticNightPlugin.Diagnostics.Value && Time.realtimeSinceStartup >= nextShockLog)
            {
                nextShockLog = Time.realtimeSinceStartup + 0.5f;
                RealisticNightPlugin.Log.LogInfo($"[StreetLights] shockwave front {propagation:0}m/{maxR:0}m (power {power:0}) toppled {n} poles this frame.");
            }
        }

        // A small weapon (<=200 yield) has no Shockwave — the game handles it via BlastFrag. Topple instantly
        // within the frag radius (yield^(1/3)*20), toppling everything inside (no overpressure floor — these are
        // point weapons and this radius already reads well).
        public static void OnBlastFrag(Vector3 origin, float yield)
        {
            if (!Destruct) return;
            float power = Mathf.Pow(Mathf.Max(0f, yield), 1f / 3f);
            float radius = power * 20f;
            if (radius <= 0.5f) return;
            SetupPoleLayer();
            int n = ToppleWithin(origin, radius, power, 0f);
            if (n > 0 && RealisticNightPlugin.Diagnostics.Value)
                RealisticNightPlugin.Log.LogInfo($"[StreetLights] blastfrag: {n} poles toppled within {radius:0}m.");
        }

        // Topple every still-standing pole within `radius` whose overpressure >= opMin. Returns how many fell.
        static int ToppleWithin(Vector3 origin, float radius, float power, float opMin)
        {
            if (radius <= 0f) return 0;
            float r2 = radius * radius;
            float invPow = 1f / Mathf.Max(1f, power);
            int n = 0;
            for (int k = 0; k < poles.Count; k++)
            {
                Transform pt = poles[k];
                if (pt == null) continue;                       // no pole here / already toppled
                Vector3 d = pt.position - origin;
                if (d.sqrMagnitude > r2) continue;
                float dist = d.magnitude;
                float num = Mathf.Max(dist * invPow, 1f);
                float op = 25000f / (num * num * num);          // game's overpressure at this distance
                if (op < opMin) continue;                       // too weak to knock this pole down
                float mag = Mathf.Clamp(op * ToppleForceK, ToppleForceMin, ToppleForceMax);
                Vector3 outward = d; outward.y = 0f;
                if (outward.sqrMagnitude < 1e-4f) outward = pt.right; // directly under the blast: shove sideways
                TopplePole(k, (outward.normalized + Vector3.up * 0.25f).normalized * mag);
                n++;
            }
            return n;
        }

        // Knock one pole down with real physics: extinguish its orb, lazily add a rigidbody + collider on the
        // pole layer (which only collides with terrain), apply `impulse` at the head so it TOPPLES (torque about
        // the base), and schedule cleanup. Nulls the pole/orb slots so the render + ground-light loops skip it.
        static void TopplePole(int k, Vector3 impulse)
        {
            if (k < markers.Count && markers[k] != null)     // light out
            {
                UnityEngine.Object.Destroy(markers[k].gameObject);
                markers[k] = null; if (k < renderers.Count) renderers[k] = null;
            }
            Transform pt = poles[k];
            if (pt == null) return;
            GameObject pole = pt.gameObject;
            pole.layer = PoleLayer;

            float w = Mathf.Max(0.4f, bPoleW);
            float hgt = Mathf.Max(1f, bHeight);
            BoxCollider bc = pole.GetComponent<BoxCollider>();
            if (bc == null) bc = pole.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, hgt * 0.5f, 0f);
            bc.size = new Vector3(w, hgt, w);

            Rigidbody rb = pole.GetComponent<Rigidbody>();
            if (rb == null) rb = pole.AddComponent<Rigidbody>();
            rb.mass = PoleMass;
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.drag = 0.05f;
            rb.angularDrag = 0.15f;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

            rb.AddForceAtPosition(impulse, pt.position + Vector3.up * hgt, ForceMode.Impulse);

            UnityEngine.Object.Destroy(pole, ToppleLife);    // debris cleanup; container-destroy also covers it
            poles[k] = null; if (k < poleRends.Count) poleRends[k] = null;
        }

        // Give our pole layer a collision matrix that lets a falling pole rest on the terrain (Statics) but pass
        // through EVERYTHING else — aircraft, vehicles, bullets, missiles — so poles can never become invisible
        // walls that crash a landing plane or block a shot. Original matrix saved for a truthful revert.
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

    // Big weapons (>200 yield) spawn the game's real Shockwave MonoBehaviour. Its Update() advances the true
    // front (blastPropagation, 340 m/s) and runs on every client. We hook Update so each frame we read the LIVE
    // front radius + power and topple the poles it has actually reached — synced to the visible wave, and only
    // out to the overpressure-limited devastation radius (see ToppleOpMin) so the weak far edge is left standing.
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

    // Small weapons (<=200 yield) get no Shockwave — the game applies their blast through DamageEffects.BlastFrag
    // (runs on every client with a WORLD position). Hook it to topple poles within the frag radius.
    [HarmonyPatch(typeof(DamageEffects), "BlastFrag")]
    public static class BlastFrag_Patch
    {
        static void Postfix(float blastYield, Vector3 blastPosition)
        {
            StreetLights.OnBlastFrag(blastPosition, blastYield);
        }
    }

    // Screen-space deferred street lighting. Loads the StreetLightFX shader from our AssetBundle and
    // injects a URP fullscreen pass that reconstructs world position from depth and adds warm light from
    // the nearest lamp positions (fed by StreetLights). Bypasses URP's 8-light-per-surface cap.
    internal static class StreetLightFX
    {
        public static volatile bool Active;
        static Material mat;
        static bool triedLoad, installed;
        static StreetLightRendererFeature feature;
        // Lamps now live in a StructuredBuffer (no 64KB cbuffer cap) so the WHOLE map can be fed. This is the
        // feed ceiling (nearest N to camera); loop cost is bounded by the Ground Light Downsample knob.
        public const int MaxLamps = 8192;
        static readonly Vector4[] lamps = new Vector4[MaxLamps];
        static ComputeBuffer lampBuf;
        static int lampCount;
        public static int LampCount => lampCount; // instance count for the light-volume draw

        static readonly int PIntensity = Shader.PropertyToID("_Intensity");
        static readonly int PIntensityWarm = Shader.PropertyToID("_IntensityWarm");
        static readonly int PAtmos = Shader.PropertyToID("_AtmosRange");
        static readonly int PRange = Shader.PropertyToID("_Range");
        static readonly int PFalloff = Shader.PropertyToID("_FalloffK");
        static readonly int PNdotL = Shader.PropertyToID("_NdotL");
        static readonly int PColor = Shader.PropertyToID("_Color");
        static readonly int PColorWarm = Shader.PropertyToID("_ColorWarm");
        static readonly int PLampsBuf = Shader.PropertyToID("_LampsBuf");
        static readonly int PLampCount = Shader.PropertyToID("_LampCount");

        public static Material Mat => mat;
        static Material orbMat;
        public static Material OrbMat { get { EnsureLoaded(); return orbMat; } } // shared billboard orb material (same bundle)

        static bool lampsDirty;
        public static void SetLamps(Vector4[] src, int count)
        {
            count = Mathf.Clamp(count, 0, MaxLamps);
            for (int i = 0; i < count; i++) lamps[i] = src[i];
            lampCount = count;
            lampsDirty = true;
        }

        // Free the GPU lamp buffer (call when the feature fully shuts off). Active is cleared first by the
        // caller so the render pass won't touch a disposed buffer.
        public static void ReleaseBuffer()
        {
            if (lampBuf != null) { lampBuf.Release(); lampBuf = null; }
            lampsDirty = true; // force a re-upload if we come back
        }

        static void EnsureLoaded()
        {
            if (triedLoad) return;
            triedLoad = true;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(dir, "realisticnight_fx");
                AssetBundle ab = AssetBundle.LoadFromFile(path);
                if (ab == null) { RealisticNightPlugin.Log.LogWarning($"[StreetLightFX] could not load bundle at {path}"); return; }
                mat = ab.LoadAsset<Material>("Assets/StreetLightMat.mat");
                orbMat = ab.LoadAsset<Material>("Assets/StreetOrbMat.mat");
                RealisticNightPlugin.Log.LogInfo($"[StreetLightFX] loaded groundMat={(mat != null)} orbMat={(orbMat != null)} " +
                    $"(ground supported={mat != null && mat.shader != null && mat.shader.isSupported}, orb supported={orbMat != null && orbMat.shader != null && orbMat.shader.isSupported}).");
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
            mat.SetFloat(PRange, RealisticNightPlugin.StreetGroundRange.Value);
            // Ground Falloff (0..1) -> shader _FalloffK. The shader does att /= (1 + d^2 * _FalloffK) with d in
            // METRES, so feeding the raw value made even 0.01 divide the light by hundreds (it "ate" the pool).
            // Remap gently AND normalise by range^2 so the curve is the same at any Ground Range: at the pool
            // edge the inverse-square term becomes 1/(1 + v^2*25) — v=1 => ~4% (tight), v=0.1 => ~80% (subtle),
            // v=0.01 => ~99.75% (fine-tuning). The v^2 gives extra resolution near 0. v=0 = perfectly flat pool.
            float gf = Mathf.Clamp01(RealisticNightPlugin.StreetGroundFalloff.Value);
            float gRange = Mathf.Max(1f, RealisticNightPlugin.StreetGroundRange.Value);
            mat.SetFloat(PFalloff, (gf * gf * 25f) / (gRange * gRange));
            mat.SetFloat(PNdotL, RealisticNightPlugin.StreetGroundShading.Value);
            // Per-lamp: white LED (city) vs warm sodium (main + rural), selected by _LampsBuf[i].w in the shader.
            mat.SetColor(PColor, new Color(0.90f, 0.95f, 1.0f, 1f));
            mat.SetColor(PColorWarm, new Color(1.0f, 0.72f, 0.36f, 1f));
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
                if (a == null) return;
                var fList = AccessTools.Field(typeof(UniversalRenderPipelineAsset), "m_RendererDataList");
                var fIdx = AccessTools.Field(typeof(UniversalRenderPipelineAsset), "m_DefaultRendererIndex");
                var list = fList?.GetValue(a) as ScriptableRendererData[];
                int idx = fIdx != null ? (int)fIdx.GetValue(a) : 0;
                if (list == null || idx < 0 || idx >= list.Length || list[idx] == null) return;
                feature = ScriptableObject.CreateInstance<StreetLightRendererFeature>();
                feature.name = "RN_StreetLightFeature";
                list[idx].rendererFeatures.Add(feature);
                list[idx].SetDirty();
                installed = true;
                RealisticNightPlugin.Log.LogInfo("[StreetLightFX] fullscreen render feature installed.");
            }
            catch (Exception e) { RealisticNightPlugin.Log.LogWarning($"[StreetLightFX] install error: {e.Message}"); }
        }
    }

    internal class StreetLightRendererFeature : ScriptableRendererFeature
    {
        StreetLightPass pass;
        public override void Create() { pass = new StreetLightPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing }; }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
        {
            if (!StreetLightFX.Active || StreetLightFX.Mat == null) return;
            if (data.cameraData.cameraType != CameraType.Game) return;
            if (data.cameraData.renderType != CameraRenderType.Base) return;
            pass.Setup(StreetLightFX.Mat);
            renderer.EnqueuePass(pass);
        }
    }

    internal class StreetLightPass : ScriptableRenderPass
    {
        Material mat;
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
        const int VolumePass = 0;    // RN_StreetLight_Volume
        const int CompositePass = 1; // RN_StreetLight_Composite
        public void Setup(Material m)
        {
            mat = m;
            ConfigureInput(ScriptableRenderPassInput.Depth); // need _CameraDepthTexture for world reconstruction
        }
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
        {
            RenderTextureDescriptor d = data.cameraData.cameraTargetDescriptor;
            d.depthBufferBits = 0;
            RenderingUtils.ReAllocateIfNeeded(ref temp, d, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_RN_StreetTemp");
            // Full-res volume light buffer (RGBA-half HDR). The lamps draw into this, then we composite.
            RenderTextureDescriptor vd = d;
            vd.colorFormat = RenderTextureFormat.ARGBHalf;
            RenderingUtils.ReAllocateIfNeeded(ref volRT, vd, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_RN_StreetVolRT");
        }
        public override void Execute(ScriptableRenderContext context, ref RenderingData data)
        {
            if (mat == null) return;
            int count = StreetLightFX.LampCount;
            if (count <= 0) return;

            Camera cam = data.cameraData.camera;
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            mat.SetMatrix(PInvVP, vp.inverse); // world reconstruction: ComputeWorldSpacePosition(uv, rawDepth, _RN_InvVP)
            mat.SetMatrix(PVP, vp);
            mat.SetVector(PCamRight, cam.transform.right);
            mat.SetVector(PCamUp, cam.transform.up);
            mat.SetVector(PCamPos, cam.transform.position);
            // Floating origin: subtract the origin so the reconstructed ground is in RAW GLOBAL, matching the
            // raw-global lamp buffer -> pools never desync when the world recenters (read at render time so
            // it's the same epoch as the depth + _RN_InvVP).
            mat.SetVector(POriginPos, Datum.origin != null ? Datum.origin.position : Vector3.zero);
            mat.SetFloat(PPreserve, RealisticNightPlugin.StreetGroundColorPreserve.Value);

            CommandBuffer cmd = CommandBufferPool.Get("RN_StreetLightFX");
            RTHandle src = data.cameraData.renderer.cameraColorTargetHandle;

            // Pass 0: draw each lamp's pool as a full-res additive volume into a cleared light buffer.
            CoreUtils.SetRenderTarget(cmd, volRT, ClearFlag.Color, Color.clear);
            cmd.DrawProcedural(Matrix4x4.identity, mat, VolumePass, MeshTopology.Triangles, 6, count);

            // Pass 1: composite the light onto the scene (additive <-> multiplicative via Preserve).
            mat.SetTexture(PVolLight, volRT);
            Blitter.BlitCameraTexture(cmd, src, temp);                    // scene -> temp (full res copy)
            Blitter.BlitCameraTexture(cmd, temp, src, mat, CompositePass); // temp(scene) + volRT -> src
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
    }

    [HarmonyPatch(typeof(LevelInfo), "UpdateTimeOfDayLighting")]
    public static class LevelInfo_Lighting_Patch
    {
        static readonly AccessTools.FieldRef<LevelInfo, float> AmbientIntensityRef = AccessTools.FieldRefAccess<LevelInfo, float>("ambientIntensity");
        static readonly AccessTools.FieldRef<LevelInfo, float> LightingLastRef = AccessTools.FieldRefAccess<LevelInfo, float>("lightingLastUpdated");
        static readonly AccessTools.FieldRef<NightVision, bool> NvgActiveRef = AccessTools.FieldRefAccess<NightVision, bool>("nightVisActive");

        static float lastSeen = -1f;

        static void Postfix(LevelInfo __instance)
        {
            bool on = RealisticNightPlugin.ModEnabled.Value;
            bool night = !__instance.isDayLight;

            LightBoost.Apply(on, night);
            DrawDistance.Apply(on && night && RealisticNightPlugin.CityWindowsEnabled.Value);
            // Spawn/keep the system (poles) whenever the mod + street lights are enabled — day OR night. The
            // glowing balls + ground pools only light up at night (or with Always On). So daytime = bare poles.
            StreetLights.Apply(on && RealisticNightPlugin.StreetLightsEnabled.Value,
                               night || RealisticNightPlugin.StreetLightsAlwaysOn.Value);
            // (Destruction is fully event-driven now: the game's Shockwave.Update + BlastFrag hooks topple poles;
            // fallen debris self-destructs on a timer. Nothing to poll here.)

            if (!on || !RealisticNightPlugin.NightEnabled.Value) return;

            float stamp = LightingLastRef(__instance);
            if (stamp == lastSeen) return;
            lastSeen = stamp;

            float ai = AmbientIntensityRef(__instance);
            float nightFactor = 1f - Mathf.InverseLerp(0.02f, 0.20f, ai);
            if (nightFactor <= 0f) return;

            bool nvgActive = false;
            try { nvgActive = NightVision.i != null && NvgActiveRef(NightVision.i); } catch { }
            float nvgAmb = nvgActive ? RealisticNightPlugin.NvgWorldAmbient.Value : 0f;

            float boost = (RealisticNightPlugin.NightAmbientBoost.Value + nvgAmb) * nightFactor;
            Color lift = new Color(RealisticNightPlugin.NightTintR.Value, RealisticNightPlugin.NightTintG.Value, RealisticNightPlugin.NightTintB.Value) * boost;
            RenderSettings.ambientSkyColor += lift;
            RenderSettings.ambientEquatorColor += lift;
            RenderSettings.ambientGroundColor += lift;
            RenderSettings.ambientIntensity += 0.15f * boost;
        }
    }

}
